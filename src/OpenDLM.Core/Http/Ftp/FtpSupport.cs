using System.Buffers;
using System.Net;
using Microsoft.Win32.SafeHandles;
using OpenDLM.Core.Http;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Http.Ftp;

/// <summary>
/// FTP downloads: probe, resume and transfer.
///
/// Built on <see cref="FtpWebRequest"/> from the framework rather than on a
/// hand-written protocol implementation. That is a deliberate call: the control
/// channel, passive mode, type negotiation, restart markers and the many ways real
/// servers disagree about them are a large surface, and the framework has already
/// solved it. OpenDLM's own code stays focused on the parts that are actually its
/// responsibility — resuming, accounting, cancellation and the speed governor.
///
/// FTP is transferred over a single connection. Ranged parallel download is not
/// offered because restarting several streams on one control channel is not
/// something servers agree on, and a wrong guess corrupts the file.
/// </summary>
public static class FtpSupport
{
    /// <summary>Stream buffer size, matching the HTTP path.</summary>
    private const int BufferSize = 128 * 1024;

    public static bool IsFtpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeFtp, StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------------- probe

    public static async Task<ProbeResult> ProbeAsync(
        string url, DownloadContext context, DownloadItem? item, CancellationToken cancellationToken)
    {
        if (!IsFtpUrl(url))
        {
            return ProbeResult.Failed("The address is not an ftp:// URL.", DownloadErrorKind.InvalidUrl);
        }

        try
        {
            using var request = CreateRequest(url, context, item, WebRequestMethods.Ftp.GetFileSize);

            using var response = await GetResponseAsync(request, context, cancellationToken).ConfigureAwait(false);

            var length = response.ContentLength;

            return new ProbeResult
            {
                Success = true,
                ContentLength = length > 0 ? length : -1,
                // Restart markers are how FTP resumes; the transfer itself falls back
                // to a full restart if the server turns out not to support them.
                SupportsRanges = true,
                SuggestedFileName = FileNameResolver.Sanitize(FileNameResolver.FromUrl(url) ?? "download"),
                MimeType = GuessMimeType(url),
                FinalUrl = url,
                StatusCode = (int)response.StatusCode,
                ErrorMessage = null
            };
        }
        catch (OperationCanceledException)
        {
            return ProbeResult.Failed("The probe was cancelled.", DownloadErrorKind.Cancelled);
        }
        catch (Exception ex)
        {
            var (kind, message) = ErrorMapper.Map(ex);
            return ProbeResult.Failed(message, kind);
        }
    }

    private static string GuessMimeType(string url)
    {
        var extension = Path.GetExtension(FileNameResolver.FromUrl(url) ?? string.Empty);

        return extension switch
        {
            ".zip" => "application/zip",
            ".gz" or ".tgz" => "application/gzip",
            ".7z" => "application/x-7z-compressed",
            ".rar" => "application/vnd.rar",
            ".iso" => "application/x-iso9660-image",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }

    // ------------------------------------------------------------------ transfer

    /// <summary>
    /// Runs one plan over FTP. Because FTP is single connection, the plan carries
    /// exactly one segment, and its resume position decides whether this is a fresh
    /// transfer or a continuation.
    /// </summary>
    /// <remarks>
    /// The resume sidecar is flushed on a timer while the transfer runs, so a closed
    /// or crashed application does not throw the bytes already fetched away.
    /// </remarks>
    public static async Task<DownloadOutcome> RunAsync(
        DownloadItem item,
        DownloadContext context,
        DownloadPlan plan,
        DownloadProgressSink sink,
        CancellationToken cancellationToken)
    {
        using var persistCts = new CancellationTokenSource();
        var persistTask = StartPersistLoopAsync(plan, persistCts.Token);

        try
        {
            return await RunCoreAsync(item, context, plan, sink, cancellationToken).ConfigureAwait(false);
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

            plan.PersistResume?.Invoke();
        }
    }

    private static async Task StartPersistLoopAsync(DownloadPlan plan, CancellationToken cancellationToken)
    {
        if (plan.PersistResume is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
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

    private static async Task<DownloadOutcome> RunCoreAsync(
        DownloadItem item,
        DownloadContext context,
        DownloadPlan plan,
        DownloadProgressSink sink,
        CancellationToken cancellationToken)
    {
        var connection = context.Settings.Connection;
        var maxRetries = Math.Max(0, connection.RetryCount);
        var baseDelay = Math.Max(1, connection.RetryDelaySeconds);
        var segment = plan.Segments[0];

        Directory.CreateDirectory(item.Directory);
        var path = item.PartialPath;

        try
        {
            PreparePartialFile(path, plan.TotalBytes, segment.Position);
        }
        catch (Exception ex)
        {
            var (kind, message) = ErrorMapper.Map(ex);
            return DownloadOutcome.Failed(kind, "Could not create the destination file: " + message);
        }

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.None);
        }
        catch (Exception ex)
        {
            var (kind, message) = ErrorMapper.Map(ex);
            return DownloadOutcome.Failed(kind, "Could not open the destination file: " + message);
        }

        using (handle)
        {
            var attempt = 0;
            var restartFromZero = segment.Position > 0;
            DownloadErrorKind lastKind = DownloadErrorKind.Unknown;
            var lastMessage = "The download failed.";

            while (!segment.IsComplete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (restartFromZero)
                    {
                        // The server refused a restart marker, so continuing from the
                        // old offset would splice two different copies of the file
                        // together. Start over instead.
                        segment.Position = 0;
                        sink.Seed(0);
                        item.DownloadedBytes = 0;
                        restartFromZero = false;
                        context.Log($"'{item.FileName}': restarting the FTP transfer from the beginning.");
                    }

                    await TransferSegmentAsync(item, context, plan, segment, handle, sink, cancellationToken)
                        .ConfigureAwait(false);

                    if (segment.IsComplete)
                    {
                        return ResolveOutcome(item, plan, sink);
                    }

                    // The stream ended early without an error: treat it as an interruption.
                    lastKind = DownloadErrorKind.ConnectionReset;
                    lastMessage = "The server closed the data connection before the file was complete.";
                }
                catch (OperationCanceledException)
                {
                    return DownloadOutcome.CancelledByUser();
                }
                catch (RestartNotSupportedException)
                {
                    // Restart markers are unsupported. Restarting from zero is the only
                    // safe way forward, but the attempt budget still applies so a
                    // flapping server cannot loop forever.
                    restartFromZero = true;
                    attempt++;
                    if (attempt > maxRetries)
                    {
                        return DownloadOutcome.Failed(
                            DownloadErrorKind.RangeNotSupported,
                            "The server does not support resuming, and the transfer failed again after a full restart.");
                    }

                    await DelayBeforeRetryAsync(baseDelay, attempt, cancellationToken).ConfigureAwait(false);
                    continue;
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
                    return DownloadOutcome.Failed(lastKind, lastMessage);
                }

                await DelayBeforeRetryAsync(baseDelay, attempt, cancellationToken).ConfigureAwait(false);
            }

            return ResolveOutcome(item, plan, sink);
        }
    }

    private static DownloadOutcome ResolveOutcome(DownloadItem item, DownloadPlan plan, DownloadProgressSink sink)
    {
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

        return DownloadOutcome.Ok(actualTotal);
    }

    private static async Task DelayBeforeRetryAsync(int baseDelay, int attempt, CancellationToken cancellationToken)
    {
        var seconds = Math.Min(baseDelay * Math.Max(1, attempt), 60);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static async Task TransferSegmentAsync(
        DownloadItem item,
        DownloadContext context,
        DownloadPlan plan,
        Segment segment,
        SafeFileHandle handle,
        DownloadProgressSink sink,
        CancellationToken cancellationToken)
    {
        var url = string.IsNullOrWhiteSpace(item.FinalUrl) ? item.Url : item.FinalUrl!;

        using var request = CreateRequest(url, context, item, WebRequestMethods.Ftp.DownloadFile);
        request.ContentOffset = segment.Position;

        using var response = await GetResponseAsync(request, context, cancellationToken).ConfigureAwait(false);

        // Check BEFORE reading anything. A server that ignores ContentOffset starts
        // sending byte zero while we write it at the restart offset, which would
        // splice a fresh copy over the tail of the file; the damage is done by then.
        if (segment.Position > 0 &&
            response.ContentLength >= segment.Length &&
            plan.TotalBytes > 0 &&
            response.ContentLength >= plan.TotalBytes)
        {
            throw new RestartNotSupportedException();
        }

        var stream = response.GetResponseStream();
        if (stream is null)
        {
            throw new WebException("The server did not open a data connection.");
        }

        using (stream)
        {
            sink.EnterConnection();
            try
            {
                await SegmentedDownloader.PumpAsync(
                    stream, handle, segment, context, sink,
                    openEnded: segment.End >= long.MaxValue - 1,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sink.LeaveConnection();
            }
        }
    }

    /// <summary>Thrown when the server ignored a restart marker and sent the file from the start.</summary>
    private sealed class RestartNotSupportedException : Exception
    {
        public RestartNotSupportedException()
            : base("The server ignored the FTP restart marker.")
        {
        }
    }

    // ----------------------------------------------------------------- plumbing

    private static void PreparePartialFile(string path, long total, long offset)
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

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        if (total > 0 && stream.Length != total)
        {
            stream.SetLength(total);
        }

        // Resuming onto a file shorter than the restart point would leave a hole.
        if (offset > 0 && stream.Length < offset)
        {
            stream.SetLength(offset);
        }
    }

    private static FtpWebRequest CreateRequest(string url, DownloadContext context, DownloadItem? item, string method)
    {
        var settings = context.Settings;

        var request = (FtpWebRequest)WebRequest.Create(url);
        request.Method = method;
        request.UseBinary = true;
        request.UsePassive = settings.Connection.FtpPassive;
        request.KeepAlive = false;
        request.EnableSsl = false;

        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.Connection.TimeoutSeconds, 5, 3600));
        request.Timeout = (int)timeout.TotalMilliseconds;
        request.ReadWriteTimeout = (int)timeout.TotalMilliseconds;

        ApplyProxy(request, context);
        ApplyCredentials(request, url, context, item);

        return request;
    }

    private static void ApplyProxy(FtpWebRequest request, DownloadContext context)
    {
        switch (context.Settings.Connection.ProxyMode)
        {
            case ProxyMode.None:
                request.Proxy = null;
                break;

            case ProxyMode.Manual:
            {
                var proxy = new OpenDLMProxy(context.Settings.Connection,
                    Services.CredentialProtector.Unprotect(context.Settings.Connection.ProxyPasswordProtected));

                request.Proxy = proxy.IsUsable ? proxy : null;
                break;
            }

            // System and Auto both mean "whatever Windows is configured with",
            // which is the framework's default, so nothing needs setting.
        }
    }

    private static void ApplyCredentials(FtpWebRequest request, string url, DownloadContext context, DownloadItem? item)
    {
        if (!string.IsNullOrWhiteSpace(item?.Username))
        {
            request.Credentials = new NetworkCredential(item.Username, item.Password ?? string.Empty);
            return;
        }

        if (context.SiteLogins.Count == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        foreach (var login in context.SiteLogins)
        {
            if (!login.Matches(uri))
            {
                continue;
            }

            var password = login.PlainPassword
                           ?? Services.CredentialProtector.Unprotect(login.PasswordProtected);
            request.Credentials = new NetworkCredential(login.Username, password ?? string.Empty);
            context.Log($"Using stored FTP login for {uri.Host} as '{login.Username}'.");
            return;
        }
    }

    /// <summary>
    /// Sends a request and makes cancellation actually interrupt it. <see cref="FtpWebRequest"/>
    /// has no cancellation-token overload, so the token aborts the request instead,
    /// which is what makes a pause feel immediate rather than after a timeout.
    /// </summary>
    private static async Task<FtpWebResponse> GetResponseAsync(
        FtpWebRequest request, DownloadContext context, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(context.Settings.Connection.TimeoutSeconds, 5, 3600));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        using var registration = linked.Token.Register(static state => ((FtpWebRequest)state!).Abort(), request);

        return (FtpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
    }
}
