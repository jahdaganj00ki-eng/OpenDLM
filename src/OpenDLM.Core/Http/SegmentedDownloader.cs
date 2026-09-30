using System.Buffers;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Http;

/// <summary>
/// Thread-safe byte counter shared by all segment workers of one download.
/// The manager polls it on a timer to update the grid, which keeps property
/// change notifications out of the hot read/write path entirely.
/// </summary>
public sealed class DownloadProgressSink
{
    private long _bytes;
    private int _activeConnections;

    /// <summary>Bytes written across all segments.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Number of segment requests currently streaming.</summary>
    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public void Add(long delta)
    {
        if (delta > 0)
        {
            Interlocked.Add(ref _bytes, delta);
        }
    }

    /// <summary>Seeds the counter, e.g. with the bytes already present when resuming.</summary>
    public void Seed(long value) => Interlocked.Exchange(ref _bytes, Math.Max(0, value));

    internal void EnterConnection() => Interlocked.Increment(ref _activeConnections);

    internal void LeaveConnection() => Interlocked.Decrement(ref _activeConnections);
}

/// <summary>The result of one attempt to run a download plan.</summary>
public sealed class DownloadOutcome
{
    public bool Success { get; private init; }
    public bool Cancelled { get; private init; }
    public bool RangeFallbackRequired { get; private init; }
    public bool AuthenticationRequired { get; private init; }
    public DownloadErrorKind ErrorKind { get; private init; } = DownloadErrorKind.None;
    public string? ErrorMessage { get; private init; }
    public long TotalBytes { get; private init; } = -1;

    public static DownloadOutcome Ok(long totalBytes) => new() { Success = true, TotalBytes = totalBytes };

    public static DownloadOutcome CancelledByUser() => new() { Cancelled = true, ErrorKind = DownloadErrorKind.Cancelled };

    public static DownloadOutcome Failed(DownloadErrorKind kind, string message) =>
        new() { ErrorKind = kind, ErrorMessage = message };

    public static DownloadOutcome NeedsRangeFallback(string message) =>
        new() { RangeFallbackRequired = true, ErrorKind = DownloadErrorKind.RangeNotSupported, ErrorMessage = message };

    public static DownloadOutcome NeedsCredentials(string message) =>
        new() { AuthenticationRequired = true, ErrorKind = DownloadErrorKind.AuthenticationRequired, ErrorMessage = message };
}

/// <summary>Immutable description of how a download should be executed.</summary>
public sealed class DownloadPlan
{
    public required List<Segment> Segments { get; init; }

    /// <summary>Total size in bytes, or -1 when the server did not disclose one.</summary>
    public required long TotalBytes { get; init; }

    public required bool SupportsRanges { get; init; }

    /// <summary>Upper bound on simultaneously open segment requests.</summary>
    public int MaxParallelSegments { get; init; } = 1;

    /// <summary>Invoked periodically so the caller can flush the resume sidecar.</summary>
    public Action? PersistResume { get; init; }
}

/// <summary>Divides a file into byte ranges.</summary>
public static class SegmentPlanner
{
    /// <summary>Never create a segment smaller than this, to avoid pointless connection churn.</summary>
    public const long MinimumSegmentSize = 1024 * 1024;

    public static List<Segment> Create(long totalBytes, int connections)
    {
        var segments = new List<Segment>();

        // Unknown size, or a single connection requested: one segment covering everything.
        if (totalBytes <= 0 || connections <= 1)
        {
            segments.Add(new Segment
            {
                Index = 0,
                Start = 0,
                End = totalBytes > 0 ? totalBytes - 1 : long.MaxValue - 1,
                Position = 0
            });
            return segments;
        }

        connections = Math.Clamp(connections, 1, 32);

        // A 3 MB file does not benefit from 8 connections.
        var sizeLimited = (int)Math.Max(1, totalBytes / MinimumSegmentSize);
        var count = Math.Clamp(Math.Min(connections, sizeLimited), 1, 32);

        var partSize = totalBytes / count;
        var remainder = totalBytes % count;

        long start = 0;
        for (var index = 0; index < count; index++)
        {
            var length = partSize + (index < remainder ? 1 : 0);
            var end = start + length - 1;
            segments.Add(new Segment
            {
                Index = index,
                Start = start,
                End = end,
                Position = start
            });
            start = end + 1;
        }

        return segments;
    }

    /// <summary>Total bytes covered by a segment list, or -1 if any segment is open ended.</summary>
    public static long TotalOf(IEnumerable<Segment> segments)
    {
        long total = 0;
        foreach (var segment in segments)
        {
            if (segment.End >= long.MaxValue - 1)
            {
                return -1;
            }
            total += segment.Length;
        }
        return total;
    }
}

internal enum SegmentStatus
{
    Complete,
    Failed,
    Cancelled,
    RangeIgnored,
    AuthenticationRequired
}

internal readonly record struct SegmentResult(SegmentStatus Status, DownloadErrorKind Kind, string? Message)
{
    public static SegmentResult Complete => new(SegmentStatus.Complete, DownloadErrorKind.None, null);
    public static SegmentResult Cancelled => new(SegmentStatus.Cancelled, DownloadErrorKind.Cancelled, null);
    public static SegmentResult Failed(DownloadErrorKind kind, string message) => new(SegmentStatus.Failed, kind, message);
}

/// <summary>
/// Downloads one file over several parallel HTTP range requests, writing each
/// stream directly into its final offset of the partial file.
///
/// Design notes:
/// - All workers share a single <see cref="SafeFileHandle"/> and use
///   <c>RandomAccess</c> writes with explicit offsets, so no locking and no seek
///   races are possible.
/// - The partial file is preallocated to the full size, which means the final
///   rename is instant even for multi-gigabyte files.
/// - A server that answers a ranged request with <c>200 OK</c> is detected
///   immediately and the plan is retried with a single connection.
/// </summary>
public sealed class SegmentedDownloader
{
    /// <summary>Stream buffer size. 128 KB keeps syscall overhead low on fast links.</summary>
    private const int BufferSize = 128 * 1024;

    /// <summary>How often the resume sidecar is flushed while downloading.</summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(3);

    public async Task<DownloadOutcome> RunAsync(
        DownloadItem item,
        DownloadContext context,
        DownloadPlan plan,
        DownloadProgressSink sink,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(item.Directory);
        var partialPath = item.PartialPath;
        var total = plan.TotalBytes;

        try
        {
            PreparePartialFile(partialPath, total);
        }
        catch (Exception ex)
        {
            var (kind, message) = ErrorMapper.Map(ex);
            return DownloadOutcome.Failed(kind, "Could not create the destination file: " + message);
        }

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(partialPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.None);
        }
        catch (Exception ex)
        {
            var (kind, message) = ErrorMapper.Map(ex);
            return DownloadOutcome.Failed(kind, "Could not open the destination file: " + message);
        }

        using (handle)
        using (var internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var parallelism = new SemaphoreSlim(Math.Max(1, plan.MaxParallelSegments)))
        {
            var results = new SegmentResult[plan.Segments.Count];
            var fallbackFlagged = 0;

            using var persistCts = new CancellationTokenSource();
            var persistTask = Task.Run(() => PersistLoopAsync(plan, persistCts.Token), CancellationToken.None);

            try
            {
                var workers = new Task[plan.Segments.Count];
                for (var index = 0; index < plan.Segments.Count; index++)
                {
                    var captured = index;
                    workers[index] = Task.Run(async () =>
                    {
                        await parallelism.WaitAsync(internalCts.Token).ConfigureAwait(false);
                        try
                        {
                            var result = await DownloadSegmentAsync(
                                    plan.Segments[captured], item, context, plan, handle, sink, internalCts.Token)
                                .ConfigureAwait(false);

                            results[captured] = result;

                            if (result.Status == SegmentStatus.RangeIgnored &&
                                Interlocked.Exchange(ref fallbackFlagged, 1) == 0)
                            {
                                // Stop the other workers immediately: their byte
                                // offsets assumed a range-capable server.
                                internalCts.Cancel();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            results[captured] = SegmentResult.Cancelled;
                        }
                        catch (Exception ex)
                        {
                            var (kind, message) = ErrorMapper.Map(ex);
                            results[captured] = SegmentResult.Failed(kind, message);
                        }
                        finally
                        {
                            parallelism.Release();
                        }
                    }, CancellationToken.None);
                }

                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            finally
            {
                await persistCts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await persistTask.ConfigureAwait(false);
                }
                catch
                {
                    // The persistence loop is best effort.
                }

                // Always flush the final segment positions before returning.
                plan.PersistResume?.Invoke();
            }

            return Interpret(item, plan, sink, results, cancellationToken, Volatile.Read(ref fallbackFlagged) == 1);
        }
    }

    private static async Task PersistLoopAsync(DownloadPlan plan, CancellationToken cancellationToken)
    {
        if (plan.PersistResume is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PersistInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                plan.PersistResume();
            }
            catch (Exception ex)
            {
                Log.Warn("Resume state flush failed: " + ex.Message);
            }
        }
    }

    private static DownloadOutcome Interpret(
        DownloadItem item,
        DownloadPlan plan,
        DownloadProgressSink sink,
        SegmentResult[] results,
        CancellationToken callerToken,
        bool rangeFallbackFlagged)
    {
        if (rangeFallbackFlagged)
        {
            var message = results.FirstOrDefault(r => r.Status == SegmentStatus.RangeIgnored).Message
                          ?? "The server does not support byte ranges.";
            return DownloadOutcome.NeedsRangeFallback(message);
        }

        // Authentication failures deserve their own outcome so the UI can prompt.
        var auth = results.FirstOrDefault(r => r.Status == SegmentStatus.AuthenticationRequired);
        if (auth.Status == SegmentStatus.AuthenticationRequired)
        {
            return DownloadOutcome.NeedsCredentials(auth.Message ?? "The server requires credentials.");
        }

        // A real failure outranks a cancellation caused by that same failure.
        var failure = results.FirstOrDefault(r => r.Status == SegmentStatus.Failed);
        if (failure.Status == SegmentStatus.Failed)
        {
            return DownloadOutcome.Failed(failure.Kind, failure.Message ?? "The download failed.");
        }

        if (callerToken.IsCancellationRequested)
        {
            return DownloadOutcome.CancelledByUser();
        }

        if (results.Any(r => r.Status == SegmentStatus.Cancelled))
        {
            return DownloadOutcome.CancelledByUser();
        }

        if (!plan.Segments.All(s => s.IsComplete))
        {
            return DownloadOutcome.Failed(DownloadErrorKind.ConnectionReset,
                "The connection closed before the file was complete.");
        }

        // For an unknown-length download the real size is whatever we wrote.
        long actualTotal;
        try
        {
            actualTotal = new FileInfo(item.PartialPath).Length;
        }
        catch (Exception ex)
        {
            return DownloadOutcome.Failed(DownloadErrorKind.DiskError,
                "Could not read the size of the downloaded file: " + ex.Message);
        }

        if (plan.TotalBytes > 0 && actualTotal != plan.TotalBytes)
        {
            return DownloadOutcome.Failed(DownloadErrorKind.SizeMismatch,
                $"The file size does not match: expected {plan.TotalBytes} bytes but got {actualTotal}.");
        }

        if (sink.Bytes > 0)
        {
            item.DownloadedBytes = Math.Max(item.DownloadedBytes, sink.Bytes);
        }

        return DownloadOutcome.Ok(actualTotal);
    }

    private static void PreparePartialFile(string path, long total)
    {
        if (!File.Exists(path))
        {
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            if (total > 0)
            {
                created.SetLength(total);
            }
            return;
        }

        if (total <= 0)
        {
            return;
        }

        // Preallocation makes the final move instant and lets the OS lay the file
        // out contiguously, which measurably helps large sequential writes.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        if (stream.Length != total)
        {
            stream.SetLength(total);
        }
    }

    private async Task<SegmentResult> DownloadSegmentAsync(
        Segment segment,
        DownloadItem item,
        DownloadContext context,
        DownloadPlan plan,
        SafeFileHandle handle,
        DownloadProgressSink sink,
        CancellationToken cancellationToken)
    {
        var connection = context.Settings.Connection;
        var maxRetries = Math.Max(0, connection.RetryCount);
        var baseDelay = Math.Max(1, connection.RetryDelaySeconds);

        var url = string.IsNullOrWhiteSpace(item.FinalUrl) ? item.Url : item.FinalUrl!;
        var openEnded = segment.End >= long.MaxValue - 1;
        var attempt = 0;
        DownloadErrorKind lastKind = DownloadErrorKind.Unknown;
        var lastMessage = "The download failed.";

        while (!segment.IsComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = HttpRequestFactory.Create(
                    HttpMethod.Get,
                    url,
                    context,
                    item,
                    rangeFrom: plan.SupportsRanges ? segment.Position : null,
                    rangeTo: plan.SupportsRanges && !openEnded ? segment.End : null,
                    addRange: plan.SupportsRanges);

                using var response = await context.Client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // The resource shrank since the probe; a fresh start is required.
                    return new SegmentResult(SegmentStatus.RangeIgnored, DownloadErrorKind.RangeNotSupported,
                        "The server rejected the byte range; the file has probably changed. Restarting.");
                }

                if (plan.SupportsRanges && response.StatusCode == HttpStatusCode.OK && IsRangeIgnored(segment, plan, openEnded))
                {
                    return new SegmentResult(SegmentStatus.RangeIgnored, DownloadErrorKind.RangeNotSupported,
                        "The server ignored the byte range request.");
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ProxyAuthenticationRequired)
                {
                    return new SegmentResult(SegmentStatus.AuthenticationRequired,
                        DownloadErrorKind.AuthenticationRequired,
                        "The server requires a user name and password.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    // Throws HttpRequestException with StatusCode populated, which
                    // ErrorMapper turns into a precise, user-facing message.
                    response.EnsureSuccessStatusCode();
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                sink.EnterConnection();
                try
                {
                    await PumpAsync(stream, handle, segment, context, sink, openEnded, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    sink.LeaveConnection();
                }

                if (segment.IsComplete)
                {
                    return SegmentResult.Complete;
                }

                // Server closed the stream early; treat it as a retryable interruption.
                lastKind = DownloadErrorKind.ConnectionReset;
                lastMessage = "The server closed the connection before the range was complete.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var (kind, message) = ErrorMapper.Map(ex);
                lastKind = kind;
                lastMessage = message;
            }

            attempt++;

            if (!ErrorMapper.IsRetryable(lastKind) || attempt > maxRetries)
            {
                return SegmentResult.Failed(lastKind, lastMessage);
            }

            var delaySeconds = Math.Min(baseDelay * attempt, 60);
            context.Log($"Segment {segment.Index} retry {attempt}/{maxRetries} in {delaySeconds}s: {lastMessage}");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        return SegmentResult.Complete;
    }

    private static bool IsRangeIgnored(Segment segment, DownloadPlan plan, bool openEnded)
    {
        if (segment.Position > 0)
        {
            return true;
        }

        // A 200 for a range that does not reach the end of the file proves that the
        // range was ignored, not merely that it started at zero.
        if (!openEnded && plan.TotalBytes > 0 && segment.End < plan.TotalBytes - 1)
        {
            return true;
        }

        return false;
    }

    /// <summary>Reads the response body into the partial file at the segment's offsets.</summary>
    /// <remarks>
    /// Internal rather than private: the FTP path drives the same pump, so byte
    /// accounting, the bandwidth governor and cancellation behave identically on
    /// both transports and cannot drift apart.
    /// </remarks>
    internal static async Task PumpAsync(
        Stream source,
        SafeFileHandle handle,
        Segment segment,
        DownloadContext context,
        DownloadProgressSink sink,
        bool openEnded,
        CancellationToken cancellationToken)
    {
        var governor = context.Governor;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long position = segment.Position;

        try
        {
            while (openEnded || position <= segment.End)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var wanted = buffer.Length;
                if (!openEnded)
                {
                    var remaining = segment.End - position + 1;
                    if (remaining <= 0)
                    {
                        break;
                    }
                    wanted = (int)Math.Min(buffer.Length, remaining);
                }

                int read;
                if (governor.IsEnabled)
                {
                    var allowance = await governor.AcquireAsync(wanted, cancellationToken).ConfigureAwait(false);
                    if (allowance <= 0)
                    {
                        continue;
                    }
                    read = await source.ReadAsync(buffer.AsMemory(0, allowance), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                }

                if (read <= 0)
                {
                    break; // end of stream
                }

                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, cancellationToken)
                    .ConfigureAwait(false);

                position += read;
                segment.Position = position;
                sink.Add(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
