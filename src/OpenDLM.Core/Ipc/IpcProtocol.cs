using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenDLM.Core.Http;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Ipc;

/// <summary>
/// The wire format shared by OpenDLM and its native messaging host.
///
/// Framing is a 4-byte little-endian length prefix followed by UTF-8 JSON, which is
/// byte-for-byte the same framing Chromium uses for native messaging. Keeping both
/// ends on one format means the host is a pure pass-through with no translation
/// layer to get out of sync.
/// </summary>
public static class IpcProtocol
{
    public const int ProtocolVersion = 1;

    /// <summary>Chromium refuses host-to-browser messages larger than 1 MB.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] EncodeFrame(JsonNode message)
    {
        var json = message.ToJsonString(PayloadOptions);
        var payload = Encoding.UTF8.GetBytes(json);

        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException(
                $"IPC message of {payload.Length} bytes exceeds the {MaxMessageBytes} byte limit.");
        }

        var frame = new byte[4 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Reads exactly one frame. Returns null on a clean end of stream.</summary>
    public static async Task<JsonNode?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }
        if (read < 4)
        {
            throw new InvalidDataException("Truncated IPC frame header.");
        }

        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0)
        {
            // An empty frame is not an error; treat it as a no-op message.
            return new JsonObject();
        }
        if (length > MaxMessageBytes)
        {
            throw new InvalidDataException($"IPC frame of {length} bytes exceeds the {MaxMessageBytes} byte limit.");
        }

        var payload = new byte[length];
        var received = await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (received < length)
        {
            throw new InvalidDataException("Truncated IPC frame payload.");
        }

        return JsonNode.Parse(payload);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    /// <summary>Convenience: send one request and read one response over an open stream.</summary>
    public static async Task<JsonNode?> RoundTripAsync(
        Stream stream, JsonNode request, CancellationToken cancellationToken)
    {
        var frame = EncodeFrame(request);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ message builders

    public static JsonObject Error(string? requestType, string message) => new()
    {
        ["type"] = "error",
        ["requestType"] = requestType,
        ["message"] = message
    };

    public static JsonObject Ack(string requestType, Guid? itemId = null, int? count = null)
    {
        var node = new JsonObject
        {
            ["type"] = "ack",
            ["requestType"] = requestType
        };
        if (itemId.HasValue)
        {
            node["itemId"] = itemId.Value.ToString();
        }
        if (count.HasValue)
        {
            node["count"] = count.Value;
        }
        return node;
    }
}

/// <summary>
/// Listens on the local named pipe that the browser extension's native messaging
/// host connects to, and translates requests into engine calls.
///
/// The pipe is created with <see cref="PipeOptions.CurrentUserOnly"/>, so no other
/// Windows account (and nothing off-machine) can enqueue downloads.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly DownloadManager _manager;
    private readonly SettingsService _settingsService;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Concurrent pipe instances are served. A single instance would refuse a second
    /// browser request while the first is being handled, and the native messaging
    /// host would then report "OpenDLM is not running" to the user.
    /// </summary>
    private readonly List<Task> _listeners = new();

    private const int ListenerCount = 4;

    private bool _disposed;

    public IpcServer(DownloadManager manager, SettingsService settingsService)
    {
        _manager = manager;
        _settingsService = settingsService;
    }

    /// <summary>Raised so the UI can show the "start download" dialog. Return false to veto the request.</summary>
    public Func<AddDownloadRequest, bool>? ConfirmAddRequest { get; set; }

    /// <summary>Raised when the extension changed a setting the UI displays.</summary>
    public event EventHandler? SettingsChangedByExtension;

    public event EventHandler<string>? Message;

    /// <summary>Raised when a second launch asked the running instance to show its window.</summary>
    public event EventHandler? ActivateRequested;

    public bool IsRunning => _listeners.Exists(task => !task.IsCompleted);

    public void Start()
    {
        if (!_settingsService.Current.Advanced.EnableBrowserIpc)
        {
            Log.Info("Browser IPC is disabled in the settings; the pipe was not opened.");
            return;
        }

        lock (_listeners)
        {
            if (_listeners.Count > 0)
            {
                return;
            }

            for (var index = 0; index < ListenerCount; index++)
            {
                _listeners.Add(Task.Run(() => ListenAsync(_shutdown.Token), CancellationToken.None));
            }
        }

        Log.Info($"IPC pipe '{AppPaths.PipeName}' is listening on {ListenerCount} concurrent instances.");
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    AppPaths.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleConnectionAsync(server, cancellationToken);
                server = null; // ownership passed to the handler
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                Log.Warn("IPC listener error: " + ex.Message);
                // Back off briefly so a persistent failure cannot spin the CPU.
                try
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using (server.ConfigureAwait(false))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                var request = await IpcProtocol.ReadFrameAsync(server, timeout.Token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                var response = await DispatchAsync(request, timeout.Token).ConfigureAwait(false);
                var frame = IpcProtocol.EncodeFrame(response);
                await server.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
                await server.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller went away; nothing to report.
            }
            catch (Exception ex)
            {
                Log.Warn("IPC request failed: " + ex.Message);
                try
                {
                    var frame = IpcProtocol.EncodeFrame(
                        IpcProtocol.Error(null, "The request could not be processed: " + ex.Message));
                    await server.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
                    await server.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The pipe is already gone.
                }
            }
        }
    }

    private async Task<JsonNode> DispatchAsync(JsonNode request, CancellationToken cancellationToken)
    {
        var type = request["type"]?.GetValue<string>() ?? string.Empty;

        switch (type)
        {
            case "hello":
                return new JsonObject
                {
                    ["type"] = "hello",
                    ["appVersion"] = AppPaths.Version,
                    ["protocol"] = IpcProtocol.ProtocolVersion,
                    ["running"] = true
                };

            case "ping":
                return new JsonObject
                {
                    ["type"] = "pong",
                    ["running"] = true,
                    ["appVersion"] = AppPaths.Version
                };

            case "getSettings":
                return new JsonObject
                {
                    ["type"] = "settings",
                    ["settings"] = BuildIntegrationSettings()
                };

            case "setSetting":
                return ApplySetting(request);

            case "activate":
                ActivateRequested?.Invoke(this, EventArgs.Empty);
                return IpcProtocol.Ack("activate");

            case "addDownload":
            {
                var addRequest = ParseAddRequest(request);
                if (addRequest is null)
                {
                    return IpcProtocol.Error("addDownload", "The message did not contain a usable url.");
                }

                if (ConfirmAddRequest is not null && !ConfirmAddRequest(addRequest))
                {
                    return IpcProtocol.Error("addDownload", "The download was declined in OpenDLM.");
                }

                var item = await _manager.AddAndStartAsync(addRequest, cancellationToken).ConfigureAwait(false);
                Message?.Invoke(this, $"Browser request added: {item.FileName}");
                return IpcProtocol.Ack("addDownload", item.Id);
            }

            case "addBatch":
            {
                var items = request["items"]?.AsArray();
                if (items is null || items.Count == 0)
                {
                    return IpcProtocol.Error("addBatch", "The batch did not contain any items.");
                }

                var added = 0;
                foreach (var node in items)
                {
                    if (node is null)
                    {
                        continue;
                    }

                    var addRequest = ParseAddRequest(node);
                    if (addRequest is null)
                    {
                        continue;
                    }

                    if (ConfirmAddRequest is not null && !ConfirmAddRequest(addRequest))
                    {
                        continue;
                    }

                    await _manager.AddAndStartAsync(addRequest, cancellationToken).ConfigureAwait(false);
                    added++;
                }

                Message?.Invoke(this, $"Browser batch request added {added} download(s).");
                return IpcProtocol.Ack("addBatch", count: added);
            }

            default:
                return IpcProtocol.Error(type, $"Unknown message type '{type}'.");
        }
    }

    private static AddDownloadRequest? ParseAddRequest(JsonNode node)
    {
        var url = node["url"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var request = new AddDownloadRequest
        {
            Url = url.Trim(),
            PageUrl = GetString(node, "pageUrl"),
            Referer = GetString(node, "referer"),
            FileName = GetString(node, "filename"),
            Description = GetString(node, "description"),
            MimeType = GetString(node, "mimeType"),
            Cookies = GetString(node, "cookies"),
            UserAgent = GetString(node, "userAgent"),
            StartNow = node["startNow"]?.GetValue<bool>() ?? true,
            AddToQueue = node["addToQueue"]?.GetValue<bool>() ?? false
        };

        if (node["totalBytes"] is { } total && long.TryParse(total.ToJsonString(), out var bytes))
        {
            request.TotalBytes = bytes;
        }

        if (node["queueId"] is { } queue && int.TryParse(queue.ToJsonString(), out var queueId))
        {
            request.QueueId = queueId;
        }

        var category = GetString(node, "category");
        if (!string.IsNullOrWhiteSpace(category))
        {
            var normalized = category!.Trim().ToLowerInvariant();
            request.Category = normalized switch
            {
                "video" => DownloadCategory.Video,
                "music" or "audio" => DownloadCategory.Music,
                "program" or "programs" or "software" => DownloadCategory.Program,
                "document" or "documents" => DownloadCategory.Document,
                "compressed" or "archive" => DownloadCategory.Compressed,
                "other" => DownloadCategory.Other,
                _ => null
            };
        }

        return request;
    }

    private static string? GetString(JsonNode node, string name)
    {
        var value = node[name];
        if (value is null)
        {
            return null;
        }
        try
        {
            var text = value.GetValue<string>();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The subset of settings the browser extension reads and writes.
    ///
    /// The primary keys are the ones the extension uses natively
    /// (<c>enabled</c>, <c>mediaSniffing</c>, <c>forceTakeoverKey</c>,
    /// <c>keepDownloadKey</c>, <c>takeoverExtensions</c>); desktop-style aliases are
    /// also returned so either side can consume the same payload.
    /// </summary>
    private JsonObject BuildIntegrationSettings()
    {
        var settings = _settingsService.Current;
        var browser = settings.BrowserIntegration;
        var integrationEnabled = browser.Enabled && settings.General.CaptureDownloads;

        var takeOver = NormalizeToBareList(browser.TakeOverExtensions.Count > 0
            ? browser.TakeOverExtensions
            : _manager.FileTypes.TakeOverMasks());

        var excluded = NormalizeToBareList(browser.ExcludedExtensions.Count > 0
            ? browser.ExcludedExtensions
            : _manager.FileTypes.ExcludedMasks());

        return new JsonObject
        {
            ["enabled"] = integrationEnabled,
            ["mediaSniffing"] = browser.MediaOverlay,
            ["forceTakeoverKey"] = settings.General.TakeOverModifier,
            ["keepDownloadKey"] = settings.General.BypassModifier,
            ["takeoverExtensions"] = ToJsonArray(takeOver),

            ["contextMenu"] = browser.ContextMenu,
            ["takeOverBrowserDownloads"] = browser.TakeOverBrowserDownloads,
            ["minSizeBytes"] = browser.MinSizeBytes,
            ["excludedExtensions"] = ToJsonArray(excluded),
            ["showStartDialog"] = settings.General.ShowStartDialog,
            // Takeover behaviour the extension can honour.
            ["skipHtml"] = settings.General.SkipHtml,
            ["checkMouse"] = settings.General.CheckMouse,
            ["enableForceKey"] = settings.General.EnableForceKey,
            ["enablePreventKey"] = settings.General.EnablePreventKey,
            ["defaultDownloadDirectory"] = settings.Downloads.DefaultDownloadDirectory,
            ["maxConnectionsPerFile"] = settings.Connection.MaxConnectionsPerFile,
            ["appVersion"] = AppPaths.Version,
            ["protocol"] = IpcProtocol.ProtocolVersion,

            // Aliases kept so an alternative client keeps working.
            ["integrationEnabled"] = integrationEnabled,
            ["mediaOverlay"] = browser.MediaOverlay,
            ["takeOverModifier"] = settings.General.TakeOverModifier,
            ["bypassModifier"] = settings.General.BypassModifier
        };
    }

    /// <summary>Converts "*.ZIP", ".zip" and "zip" all to the bare lowercase "zip".</summary>
    private static string ToBareExtension(string value)
        => FileTypeRule.Normalize(value).TrimStart('.');

    private static List<string> NormalizeToBareList(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var value in values)
        {
            var bare = ToBareExtension(value);
            if (bare.Length > 0 && seen.Add(bare))
            {
                result.Add(bare);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
        => new(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());

    private JsonNode ApplySetting(JsonNode request)
    {
        var key = request["key"]?.GetValue<string>();
        var value = request["value"];

        if (string.IsNullOrWhiteSpace(key) || value is null)
        {
            return IpcProtocol.Error("setSetting", "Both 'key' and 'value' are required.");
        }

        try
        {
            _settingsService.Update(settings =>
            {
                var browser = settings.BrowserIntegration;
                switch (key)
                {
                    // ---- the browser extension's own vocabulary ------------------
                    case "enabled":
                    case "integrationEnabled":
                        browser.Enabled = value.GetValue<bool>();
                        settings.General.CaptureDownloads = browser.Enabled;
                        break;

                    case "mediaSniffing":
                    case "mediaOverlay":
                        browser.MediaOverlay = value.GetValue<bool>();
                        break;

                    case "forceTakeoverKey":
                    case "takeOverModifier":
                        settings.General.TakeOverModifier = value.GetValue<string>();
                        break;

                    case "keepDownloadKey":
                    case "bypassModifier":
                        settings.General.BypassModifier = value.GetValue<string>();
                        break;

                    case "takeoverExtensions":
                    case "takeOverExtensions":
                        browser.TakeOverExtensions = ReadExtensionList(value);
                        break;

                    case "excludedExtensions":
                        browser.ExcludedExtensions = ReadExtensionList(value);
                        break;

                    // ---- additional keys the app exposes --------------------------
                    case "skipHtml":
                        settings.General.SkipHtml = value.GetValue<bool>();
                        break;

                    case "checkMouse":
                        settings.General.CheckMouse = value.GetValue<bool>();
                        break;

                    case "enableForceKey":
                        settings.General.EnableForceKey = value.GetValue<bool>();
                        break;

                    case "enablePreventKey":
                        settings.General.EnablePreventKey = value.GetValue<bool>();
                        break;

                    case "takeOverBrowserDownloads":
                        browser.TakeOverBrowserDownloads = value.GetValue<bool>();
                        break;

                    case "contextMenu":
                        browser.ContextMenu = value.GetValue<bool>();
                        break;

                    case "minSizeBytes":
                        browser.MinSizeBytes = value.GetValue<long>();
                        break;

                    default:
                        throw new ArgumentException($"Unknown setting '{key}'.");
                }
            });
        }
        catch (Exception ex)
        {
            return IpcProtocol.Error("setSetting", ex.Message);
        }

        SettingsChangedByExtension?.Invoke(this, EventArgs.Empty);
        return IpcProtocol.Ack("setSetting");
    }

    /// <summary>
    /// Reads an extension list from either a JSON array or a delimited string and
    /// normalizes every entry to the bare lowercase form the extension uses, so a
    /// value typed as "*.ZIP" in the desktop options and "zip" in the browser agree.
    /// </summary>
    private static List<string> ReadExtensionList(JsonNode value)
        => NormalizeToBareList(ReadStringList(value));

    private static List<string> ReadStringList(JsonNode value)
    {
        var list = new List<string>();
        if (value is JsonArray array)
        {
            foreach (var node in array)
            {
                var text = node?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text.Trim());
                }
            }
        }
        else
        {
            var text = value.GetValue<string>();
            foreach (var part in text.Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    list.Add(trimmed);
                }
            }
        }
        return list;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            _shutdown.Cancel();
        }
        catch (Exception ex)
        {
            Log.Warn("IPC shutdown error: " + ex.Message);
        }
        _shutdown.Dispose();
    }
}

/// <summary>Client side of the IPC protocol, used by tests and by the command-line helper mode.</summary>
public static class IpcClient
{
    public static async Task<JsonNode?> SendAsync(JsonNode request, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            await client.ConnectAsync(3000, timeout.Token).ConfigureAwait(false);
            return await IpcProtocol.RoundTripAsync(client, request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"IPC client could not reach the app: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when an OpenDLM instance is listening on the pipe.</summary>
    public static async Task<bool> IsAppRunningAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new JsonObject { ["type"] = "ping" }, cancellationToken).ConfigureAwait(false);
        return response?["running"]?.GetValue<bool>() == true;
    }
}
