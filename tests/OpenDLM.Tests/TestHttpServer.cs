using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenDLM.Tests;

/// <summary>
/// A deliberately small HTTP/1.1 server used to exercise the download engine for real.
///
/// It is built on a raw <see cref="TcpListener"/> rather than <c>HttpListener</c> so
/// that the tests need no URL ACL reservation and can therefore run without
/// elevation. It can be told to lie in the specific ways real servers do:
/// refuse byte ranges, drop the connection mid-transfer, or demand credentials.
/// </summary>
public sealed class TestHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    private int _requestCount;
    private int _rangeRequestCount;
    private int _truncated;
    private int _authChallenges;

    public TestHttpServer(int payloadSize = 4 * 1024 * 1024, int seed = 20260416)
    {
        Payload = new byte[payloadSize];
        new Random(seed).NextBytes(Payload);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public byte[] Payload { get; }

    public int Port { get; }

    public string ContentType { get; set; } = "application/octet-stream";

    public string? ContentDisposition { get; set; }

    public string? ETag { get; set; }

    /// <summary>When false the server answers every request with 200 and ignores Range.</summary>
    public bool SupportsRanges { get; set; } = true;

    public int ChunkSize { get; set; } = 64 * 1024;

    /// <summary>Artificial per-chunk delay, used to create a window in which to pause.</summary>
    public int ChunkDelayMs { get; set; }

    /// <summary>How many body responses should be cut short to simulate a dropped link.</summary>
    public int TruncateTimes { get; set; }

    public bool RequiresAuth { get; set; }

    public string AuthUser { get; set; } = "user";

    public string AuthPassword { get; set; } = "secret";

    public int RequestCount => Volatile.Read(ref _requestCount);

    public int RangeRequestCount => Volatile.Read(ref _rangeRequestCount);

    public int AuthChallengeCount => Volatile.Read(ref _authChallenges);

    public string Url(string path = "/file.bin") => $"http://127.0.0.1:{Port}{path}";

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();

                var headerText = await ReadHeaderBlockAsync(stream, cancellationToken).ConfigureAwait(false);
                if (headerText is null)
                {
                    return;
                }

                Interlocked.Increment(ref _requestCount);

                var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var parts = lines[0].Split(' ');
                var method = parts[0].ToUpperInvariant();
                var path = parts.Length > 1 ? parts[1] : "/";

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 1; index < lines.Length; index++)
                {
                    var colon = lines[index].IndexOf(':');
                    if (colon > 0)
                    {
                        headers[lines[index][..colon].Trim()] = lines[index][(colon + 1)..].Trim();
                    }
                }

                if (path.Contains("/404", StringComparison.Ordinal))
                {
                    await WriteHeadAsync(stream, "404 Not Found",
                        new Dictionary<string, string> { ["Content-Length"] = "0" }, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (RequiresAuth && !HasValidAuthorization(headers))
                {
                    Interlocked.Increment(ref _authChallenges);
                    await WriteHeadAsync(stream, "401 Unauthorized", new Dictionary<string, string>
                    {
                        ["Content-Length"] = "0",
                        ["WWW-Authenticate"] = "Basic realm=\"OpenDLM test\""
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var allowRanges = SupportsRanges && !path.Contains("/norange", StringComparison.Ordinal);
                var total = (long)Payload.Length;

                long? rangeFrom = null;
                long? rangeTo = null;
                if (headers.TryGetValue("Range", out var rangeHeader))
                {
                    var parsed = ParseRange(rangeHeader);
                    if (parsed is not null)
                    {
                        rangeFrom = parsed.Value.From;
                        rangeTo = parsed.Value.To;
                    }
                }

                if (method == "HEAD")
                {
                    var headResponse = new Dictionary<string, string>
                    {
                        ["Content-Length"] = total.ToString(),
                        ["Content-Type"] = ContentType
                    };
                    if (allowRanges)
                    {
                        headResponse["Accept-Ranges"] = "bytes";
                    }
                    if (ETag is not null)
                    {
                        headResponse["ETag"] = ETag;
                    }

                    await WriteHeadAsync(stream, "200 OK", headResponse, cancellationToken).ConfigureAwait(false);
                    return;
                }

                long start = 0;
                long end = total - 1;
                var partial = false;

                if (rangeFrom.HasValue && allowRanges)
                {
                    start = rangeFrom.Value;
                    end = rangeTo.HasValue ? Math.Min(rangeTo.Value, total - 1) : total - 1;

                    if (start > end || start >= total)
                    {
                        await WriteHeadAsync(stream, "416 Range Not Satisfiable", new Dictionary<string, string>
                        {
                            ["Content-Length"] = "0",
                            ["Content-Range"] = $"bytes */{total}"
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    partial = true;
                    Interlocked.Increment(ref _rangeRequestCount);
                }

                var length = end - start + 1;
                var responseHeaders = new Dictionary<string, string>
                {
                    ["Content-Length"] = length.ToString(),
                    ["Content-Type"] = ContentType
                };
                if (allowRanges)
                {
                    responseHeaders["Accept-Ranges"] = "bytes";
                }
                if (ETag is not null)
                {
                    responseHeaders["ETag"] = ETag;
                }
                if (ContentDisposition is not null)
                {
                    responseHeaders["Content-Disposition"] = ContentDisposition;
                }
                if (partial)
                {
                    responseHeaders["Content-Range"] = $"bytes {start}-{end}/{total}";
                }

                await WriteHeadAsync(stream, partial ? "206 Partial Content" : "200 OK", responseHeaders, cancellationToken)
                    .ConfigureAwait(false);

                var truncate = TruncateTimes > 0 && Interlocked.Increment(ref _truncated) <= TruncateTimes;

                var offset = start;
                var remaining = length;
                var chunk = Math.Max(1, ChunkSize);

                while (remaining > 0)
                {
                    var count = (int)Math.Min(chunk, remaining);
                    if (truncate)
                    {
                        // Send a token amount and then disappear.
                        count = Math.Max(1, count / 8);
                    }

                    await stream.WriteAsync(Payload.AsMemory((int)offset, count), cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                    offset += count;
                    remaining -= count;

                    if (ChunkDelayMs > 0)
                    {
                        await Task.Delay(ChunkDelayMs, cancellationToken).ConfigureAwait(false);
                    }

                    if (truncate)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // The client hung up, or the fixture is shutting down. Either is fine.
            }
        }
    }

    private bool HasValidAuthorization(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Authorization", out var value))
        {
            return false;
        }

        const string prefix = "Basic ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value[prefix.Length..].Trim()));
            return decoded == $"{AuthUser}:{AuthPassword}";
        }
        catch
        {
            return false;
        }
    }

    private static (long From, long? To)? ParseRange(string value)
    {
        var equals = value.IndexOf('=');
        if (equals < 0)
        {
            return null;
        }

        var spec = value[(equals + 1)..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var fromText = spec[..dash].Trim();
        var toText = spec[(dash + 1)..].Trim();

        if (!long.TryParse(fromText, out var from))
        {
            return null;
        }

        return (from, long.TryParse(toText, out var to) ? to : null);
    }

    private static async Task WriteHeadAsync(
        NetworkStream stream, string status, Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        foreach (var pair in headers)
        {
            builder.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
        }
        builder.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadHeaderBlockAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();

        while (builder.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return builder.Length > 0 ? builder.ToString() : null;
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return builder.ToString();
            }
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _listener.Stop();
        }
        catch
        {
            // Nothing to do.
        }

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore.
        }

        _cts.Dispose();
    }
}
