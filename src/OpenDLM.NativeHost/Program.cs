using System.Text;
using System.Text.Json;

namespace OpenDLM.NativeHost;

/// <summary>
/// Entry point for the OpenDLM native messaging host.
///
/// Browser (stdio frames) &lt;-&gt; OpenDLMNativeHost.exe &lt;-&gt; IPC pipe &lt;-&gt; OpenDLM.exe
///
/// Hard rule observed everywhere in this assembly: stdout carries protocol
/// frames and nothing else. All diagnostics go to the log file.
/// </summary>
internal static class Program
{
    /// <summary>Wire protocol version shared with the app and the extension.</summary>
    private const int ProtocolVersion = 1;

    /// <summary>How long to wait between IPC connect attempts while the app starts up.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    private static async Task<int> Main()
    {
        Log.Info($"OpenDLM native host starting (pid {Environment.ProcessId}).");

        try
        {
            return await RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let an exception escape: Chrome would be left waiting.
            Log.Error("Fatal error in the native host.", ex);
            return 1;
        }
    }

    private static async Task<int> RunAsync()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        while (true)
        {
            byte[]? frame;

            try
            {
                frame = await NativeMessaging.ReadFrameAsync(stdin, CancellationToken.None).ConfigureAwait(false);
            }
            catch (NativeMessagingException ex)
            {
                Log.Error("Rejecting an unusable native messaging frame; exiting.", ex);
                return 1;
            }
            catch (IOException ex)
            {
                Log.Warn("stdin could not be read (" + ex.Message + "); treating it as a clean shutdown.");
                return 0;
            }

            if (frame is null)
            {
                Log.Info("stdin closed; exiting normally.");
                return 0;
            }

            byte[] reply;
            try
            {
                reply = await HandleAsync(frame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Handling the request failed; sending an error reply.", ex);
                reply = ErrorReply("(unknown)", "The native host failed to handle the request: " + ex.Message);
            }

            await NativeMessaging.WriteFrameAsync(stdout, reply, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> HandleAsync(byte[] frame)
    {
        var requestType = "(unknown)";

        string text;
        try
        {
            text = Encoding.UTF8.GetString(frame);
        }
        catch (Exception ex)
        {
            Log.Warn("Request bytes were not valid UTF-8: " + ex.Message);
            return ErrorReply(requestType, "The request was not valid UTF-8.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            Log.Warn("Request was not valid JSON: " + ex.Message);
            return ErrorReply(requestType, "The request was not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ErrorReply(requestType, "The request must be a JSON object.");
            }

            if (root.TryGetProperty("type", out var typeProperty) && typeProperty.ValueKind == JsonValueKind.String)
            {
                requestType = typeProperty.GetString() ?? "(unknown)";
            }

            switch (requestType)
            {
                case "ping":
                case "hello":
                    LogHandshake(requestType, root);
                    return await HandleHandshakeReplyAsync(requestType, frame).ConfigureAwait(false);

                case "addDownload":
                case "addBatch":
                case "getSettings":
                case "setSetting":
                    return await HandleRelayAsync(requestType, frame).ConfigureAwait(false);

                default:
                    Log.Warn("Unsupported request type: " + requestType);
                    return ErrorReply(requestType, $"Unsupported request type '{requestType}'.");
            }
        }
    }

    private static void LogHandshake(string requestType, JsonElement root)
    {
        try
        {
            if (requestType == "hello"
                && root.TryGetProperty("extensionVersion", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                Log.Info("Extension handshake from version " + version.GetString() + ".");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the extension version from the handshake: " + ex.Message);
        }
    }

    /// <summary>
    /// ping and hello are answered by the app when it is up, but when it is not
    /// the host still answers usefully instead of erroring: the extension uses
    /// these to render its connected/disconnected state.
    /// </summary>
    private static async Task<byte[]> HandleHandshakeReplyAsync(string requestType, byte[] frame)
    {
        var attempt = await TryRelayAsync(requestType, frame).ConfigureAwait(false);
        if (attempt.Reply is not null)
        {
            return attempt.Reply;
        }

        if (attempt.Retryable)
        {
            AppLauncher.TryLaunch(out var path);
            var reply = await WaitForRelayAsync(requestType, frame, AppLauncher.HandshakeLaunchWait).ConfigureAwait(false);
            if (reply is not null)
            {
                return reply;
            }

            Log.Info($"OpenDLM is not running; answering {requestType} locally. Last launch target: {path ?? "(none found)"}.");
        }

        // Not running: report it in-band rather than as an error, so the popup
        // can show a clean "disconnected" state.
        return requestType == "hello" ? HelloNotRunningReply() : PongNotRunningReply();
    }

    private static async Task<byte[]> HandleRelayAsync(string requestType, byte[] frame)
    {
        var attempt = await TryRelayAsync(requestType, frame).ConfigureAwait(false);
        if (attempt.Reply is not null)
        {
            return attempt.Reply;
        }

        if (!attempt.Retryable)
        {
            return ErrorReply(requestType, "OpenDLM did not answer the request.");
        }

        if (AppLauncher.TryLaunch(out var launchedPath))
        {
            Log.Info($"Waiting up to {AppLauncher.LaunchWait.TotalSeconds:0.#}s for OpenDLM after launching {launchedPath}.");
            var reply = await WaitForRelayAsync(requestType, frame, AppLauncher.LaunchWait).ConfigureAwait(false);
            if (reply is not null)
            {
                return reply;
            }
        }
        else
        {
            Log.Warn("OpenDLM is not running and its executable could not be located.");
        }

        return ErrorReply(requestType, "OpenDLM is not running");
    }

    /// <summary>
    /// One relay attempt. Retryable is true only when the pipe itself was
    /// unreachable, which is the one case where re-sending cannot duplicate a
    /// request the app already accepted.
    /// </summary>
    private static async Task<RelayAttempt> TryRelayAsync(string requestType, byte[] frame)
    {
        try
        {
            var reply = await PipeClient.RelayAsync(frame, CancellationToken.None).ConfigureAwait(false);
            return new RelayAttempt(NormalizeReply(requestType, reply), false);
        }
        catch (PipeUnavailableException ex)
        {
            Log.Debug($"Pipe unavailable for {requestType}: {ex.Message}");
            return new RelayAttempt(null, true);
        }
        catch (PipeProtocolException ex)
        {
            Log.Warn($"The app answered {requestType} incorrectly: {ex.Message}");
            return new RelayAttempt(null, false);
        }
        catch (Exception ex)
        {
            Log.Error($"Unexpected failure while relaying {requestType}.", ex);
            return new RelayAttempt(null, false);
        }
    }

    private static async Task<byte[]?> WaitForRelayAsync(string requestType, byte[] frame, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(RetryInterval).ConfigureAwait(false);

            var attempt = await TryRelayAsync(requestType, frame).ConfigureAwait(false);
            if (attempt.Reply is not null)
            {
                return attempt.Reply;
            }

            if (!attempt.Retryable)
            {
                // The app is up but the exchange broke; re-sending is not safe.
                return ErrorReply(requestType, "OpenDLM did not answer the request.");
            }
        }

        return null;
    }

    /// <summary>
    /// Guards the reply before it goes back to Chrome: it must be non-empty,
    /// valid JSON, and small enough for native messaging to deliver.
    /// </summary>
    private static byte[]? NormalizeReply(string requestType, byte[] reply)
    {
        if (reply.Length == 0)
        {
            Log.Warn($"OpenDLM sent an empty reply for {requestType}.");
            return ErrorReply(requestType, "OpenDLM sent an empty reply.");
        }

        if (reply.Length > NativeMessaging.MaxMessageBytes)
        {
            Log.Warn($"OpenDLM's reply for {requestType} was {reply.Length} bytes, which exceeds the native messaging limit.");
            return ErrorReply(requestType, "OpenDLM's reply was too large to deliver to the browser.");
        }

        try
        {
            using var _ = JsonDocument.Parse(reply);
        }
        catch (JsonException ex)
        {
            Log.Warn($"OpenDLM's reply for {requestType} was not valid JSON: {ex.Message}");
            return ErrorReply(requestType, "OpenDLM sent a malformed reply.");
        }

        return reply;
    }

    private static byte[] ErrorReply(string requestType, string message) =>
        Json(new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["requestType"] = requestType,
            ["message"] = message,
        });

    private static byte[] HelloNotRunningReply() =>
        Json(new Dictionary<string, object?>
        {
            ["type"] = "hello",
            ["appVersion"] = string.Empty,
            ["protocol"] = ProtocolVersion,
            ["running"] = false,
        });

    private static byte[] PongNotRunningReply() =>
        Json(new Dictionary<string, object?>
        {
            ["type"] = "pong",
            ["running"] = false,
            ["appVersion"] = string.Empty,
        });

    private static byte[] Json(Dictionary<string, object?> payload)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(payload);
        }
        catch (Exception ex)
        {
            Log.Error("Serializing a host reply failed.", ex);
            // Hand-built last resort so the browser still receives a frame.
            return Encoding.UTF8.GetBytes("{\"type\":\"error\",\"requestType\":\"(unknown)\",\"message\":\"The native host could not build a reply.\"}");
        }
    }

    private readonly record struct RelayAttempt(byte[]? Reply, bool Retryable);
}
