using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenDLM.Tests;

/// <summary>
/// A deliberately small passive-mode FTP server used to exercise the FTP path for real.
///
/// Built on a raw <see cref="TcpListener"/> so the tests need no elevation and no
/// external dependency. It can be told to misbehave in the ways real servers do:
/// drop the connection mid-transfer, refuse restart markers, or ignore them while
/// pretending to honour them (which is what makes resuming dangerous).
///
/// Commands implemented: USER, PASS, TYPE, PWD, CWD, SYST, NOOP, SIZE, REST,
/// PASV, EPSV, RETR, QUIT, FEAT. Anything else gets a 500, which is correct: the
/// client must not assume an unknown verb exists.
/// </summary>
public sealed class TestFtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    private int _truncated;
    private int _authChallenges;

    public TestFtpServer(int payloadSize = 1024 * 1024, int seed = 20260416)
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

    public bool RequiresAuth { get; set; }

    public string AuthUser { get; set; } = "alice";

    public string AuthPassword { get; set; } = "s3cret";

    /// <summary>Number of transfers to cut short, to exercise resume and retry.</summary>
    public int TruncateTimes { get; set; }

    /// <summary>When false the server answers REST with 501, as many servers do.</summary>
    public bool SupportsRestart { get; set; } = true;

    /// <summary>
    /// Answers REST as if it worked but always sends the file from byte zero.
    /// This is the dangerous case: a naive resume silently corrupts the file.
    /// </summary>
    public bool IgnoreRestart { get; set; }

    public int ControlRequestCount => Volatile.Read(ref _controlRequests);

    public int TransferCount => Volatile.Read(ref _transfers);

    public int RestCount => Volatile.Read(ref _restRequests);

    private int _controlRequests;
    private int _transfers;

    public string Url(string path = "/file.bin") => $"ftp://127.0.0.1:{Port}{path}";

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

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                await ReplyAsync(stream, "220 OpenDLM FTP test server ready.", cancellationToken);

                var dataListener = new TcpListener(IPAddress.Loopback, 0);
                var dataListenerTask = (Task<TcpClient>?)null;
                var pendingRest = 0L;
                var authenticated = !RequiresAuth;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        return;
                    }

                    if (line.Length == 0)
                    {
                        continue;
                    }

                    Interlocked.Increment(ref _controlRequests);

                    var separator = line.IndexOf(' ');
                    var command = (separator < 0 ? line : line[..separator]).ToUpperInvariant();
                    var argument = separator < 0 ? string.Empty : line[(separator + 1)..];

                    switch (command)
                    {
                        case "USER":
                            await ReplyAsync(stream, "331 Password required.", cancellationToken);
                            break;

                        case "PASS":
                            var ok = authenticated ||
                                     (argument == AuthPassword && AuthPassword.Length > 0);
                            if (RequiresAuth)
                            {
                                Interlocked.Increment(ref _authChallenges);
                                // Accept the first two attempts so a single retry works.
                                ok = argument == AuthPassword;
                            }

                            authenticated = ok;
                            await ReplyAsync(stream, ok ? "230 Login successful." : "530 Login incorrect.", cancellationToken);
                            break;

                        case "TYPE":
                            await ReplyAsync(stream, "200 Type set to I.", cancellationToken);
                            break;

                        case "PWD":
                            await ReplyAsync(stream, "257 \"/\" is the working directory.", cancellationToken);
                            break;

                        case "CWD":
                            await ReplyAsync(stream, "250 Directory changed.", cancellationToken);
                            break;

                        case "SYST":
                            await ReplyAsync(stream, "215 UNIX Type: L8", cancellationToken);
                            break;

                        case "NOOP":
                            await ReplyAsync(stream, "200 OK.", cancellationToken);
                            break;

                        case "OPTS":
                        case "FEAT":
                            await ReplyAsync(stream, "200 OK.", cancellationToken);
                            break;

                        case "SIZE":
                            await ReplyAsync(stream, "213 " + Payload.Length, cancellationToken);
                            break;

                        case "REST":
                            Interlocked.Increment(ref _restRequests);

                            if (!SupportsRestart)
                            {
                                pendingRest = 0;
                                await ReplyAsync(stream, "501 Restart not supported.", cancellationToken);
                            }
                            else if (long.TryParse(argument, out var offset) && offset >= 0)
                            {
                                pendingRest = IgnoreRestart ? 0 : offset;
                                await ReplyAsync(stream, "350 Restarting at " + offset + ".", cancellationToken);
                            }
                            else
                            {
                                pendingRest = 0;
                                await ReplyAsync(stream, "501 Bad restart position.", cancellationToken);
                            }

                            break;

                        case "PASV":
                        {
                            if (dataListenerTask is not null)
                            {
                                dataListenerTask = null;
                                dataListener.Stop();
                                dataListener = new TcpListener(IPAddress.Loopback, 0);
                            }

                            dataListener.Start();
                            var dataPort = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                            dataListenerTask = dataListener.AcceptTcpClientAsync(cancellationToken);

                            var portHigh = dataPort / 256;
                            var portLow = dataPort % 256;
                            await ReplyAsync(stream,
                                $"227 Entering Passive Mode (127,0,0,1,{portHigh},{portLow}).",
                                cancellationToken);
                            break;
                        }

                        case "EPSV":
                        {
                            if (dataListenerTask is not null)
                            {
                                dataListenerTask = null;
                                dataListener.Stop();
                                dataListener = new TcpListener(IPAddress.Loopback, 0);
                            }

                            dataListener.Start();
                            var dataPort = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                            dataListenerTask = dataListener.AcceptTcpClientAsync(cancellationToken);

                            await ReplyAsync(stream,
                                $"229 Entering Extended Passive Mode (|||{dataPort}|)",
                                cancellationToken);
                            break;
                        }

                        case "RETR":
                        {
                            if (dataListenerTask is null)
                            {
                                await ReplyAsync(stream, "425 Use PASV first.", cancellationToken);
                                break;
                            }

                            if (!authenticated)
                            {
                                await ReplyAsync(stream, "530 Please log in first.", cancellationToken);
                                break;
                            }

                            TcpClient dataClient;
                            try
                            {
                                dataClient = await dataListenerTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                await ReplyAsync(stream, "425 Could not open the data connection.", cancellationToken);
                                break;
                            }

                            await ReplyAsync(stream,
                                $"150 Opening binary mode data connection for {argument} ({Payload.Length} bytes).",
                                cancellationToken);

                            try
                            {
                                using (dataClient)
                                {
                                    await SendDataAsync(dataClient, pendingRest, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }
                            catch (Exception)
                            {
                                // The client hung up mid-transfer; that is the point of the fixture.
                            }

                            dataListenerTask = null;
                            Interlocked.Increment(ref _transfers);

                            await ReplyAsync(stream, "226 Transfer complete.", cancellationToken);
                            break;
                        }

                        case "QUIT":
                            await ReplyAsync(stream, "221 Goodbye.", cancellationToken);
                            return;

                        default:
                            await ReplyAsync(stream, $"500 Unknown command {command}.", cancellationToken);
                            break;
                    }
                }

                dataListener.Stop();
            }
            catch
            {
                // Client disconnected or the fixture is shutting down.
            }
        }
    }

    private async Task SendDataAsync(TcpClient dataClient, long offset, CancellationToken cancellationToken)
    {
        var stream = dataClient.GetStream();
        var chunk = Math.Max(1, ChunkSize);
        var position = offset;
        var truncate = TruncateTimes > 0 && Interlocked.Increment(ref _truncated) <= TruncateTimes;

        while (position < Payload.Length)
        {
            var count = (int)Math.Min(chunk, Payload.Length - position);

            if (truncate)
            {
                count = Math.Max(1, count / 4);
            }

            await stream.WriteAsync(Payload.AsMemory((int)position, count), cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            position += count;

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

    /// <summary>Milliseconds to hold each data chunk, used to create a window for pausing.</summary>
    public int ChunkDelayMs { get; set; }

    /// <summary>Bytes written per write call on the data connection.</summary>
    public int ChunkSize { get; set; } = 64 * 1024;

    private static async Task ReplyAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(text + "\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];

        while (builder.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return builder.Length > 0 ? builder.ToString() : null;
            }

            var character = (char)buffer[0];
            if (character == '\n')
            {
                return builder.ToString().TrimEnd('\r');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    public int AuthChallengeCount => Volatile.Read(ref _authChallenges);

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
