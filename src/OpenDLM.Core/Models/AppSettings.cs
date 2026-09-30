using System.Text.Json.Serialization;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Models;

/// <summary>How a finished download should be handed off.</summary>
public enum PostDownloadAction
{
    None = 0,
    OpenFile = 1,
    OpenFolder = 2,
    OpenWith = 3,
    AntivirusScan = 4,
    ShutdownComputer = 5,
    ExitApplication = 6,
    DisconnectDialUp = 7
}

/// <summary>Behaviour when the destination file already exists.</summary>
public enum ExistingFileAction
{
    Ask = 0,
    Overwrite = 1,
    Rename = 2,
    Resume = 3
}

public enum ProxyMode
{
    None = 0,
    System = 1,
    Manual = 2,
    Auto = 3
}

/// <summary>How much progress UI to show for an active download.</summary>
public enum ProgressDialogMode
{
    Hidden = 0,
    Compact = 1,
    Full = 2
}

public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>
/// The complete persisted configuration. Grouped exactly the way the options
/// dialog's tabs are, so the two never drift apart.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("general")]
    public GeneralSettings General { get; set; } = new();

    [JsonPropertyName("downloads")]
    public DownloadsSettings Downloads { get; set; } = new();

    [JsonPropertyName("connection")]
    public ConnectionSettings Connection { get; set; } = new();

    [JsonPropertyName("scheduler")]
    public SchedulerSettings Scheduler { get; set; } = new();

    [JsonPropertyName("sounds")]
    public SoundSettings Sounds { get; set; } = new();

    [JsonPropertyName("browserIntegration")]
    public BrowserIntegrationSettings BrowserIntegration { get; set; } = new();

    [JsonPropertyName("interface")]
    public InterfaceSettings Interface { get; set; } = new();

    [JsonPropertyName("advanced")]
    public AdvancedSettings Advanced { get; set; } = new();

    /// <summary>Deep copy, used by the options dialog so Cancel really cancels.</summary>
    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, SettingsService.JsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsService.JsonOptions)!;
    }

    /// <summary>Clamps every value into a sane range. Called after loading a possibly hand-edited file.</summary>
    public void Normalize()
    {
        Connection.MaxConnectionsPerFile = Math.Clamp(Connection.MaxConnectionsPerFile, 1, 32);
        Connection.MaxConcurrentDownloads = Math.Clamp(Connection.MaxConcurrentDownloads, 1, 50);
        Connection.RetryCount = Math.Clamp(Connection.RetryCount, 0, 100);
        Connection.RetryDelaySeconds = Math.Clamp(Connection.RetryDelaySeconds, 1, 600);
        Connection.TimeoutSeconds = Math.Clamp(Connection.TimeoutSeconds, 5, 3600);
        Connection.SpeedLimitKbPerSecond = Math.Clamp(Connection.SpeedLimitKbPerSecond, 0, 10_000_000);

        Interface.RefreshIntervalMs = Math.Clamp(Interface.RefreshIntervalMs, 100, 5000);
        Downloads.ClipboardMinSizeBytes = Math.Max(0, Downloads.ClipboardMinSizeBytes);

        if (string.IsNullOrWhiteSpace(Downloads.DefaultDownloadDirectory))
        {
            Downloads.DefaultDownloadDirectory = AppPaths.DefaultDownloadDirectory;
        }

        Downloads.Normalize();
        General.Normalize(this);
    }

    public static AppSettings CreateDefault() => new();
}

public sealed class GeneralSettings
{
    /// <summary>Master switch for browser download takeover.</summary>
    [JsonPropertyName("captureDownloads")]
    public bool CaptureDownloads { get; set; } = true;

    /// <summary>Show the "start download" dialog that lets the user pick folder/name.</summary>
    [JsonPropertyName("showStartDialog")]
    public bool ShowStartDialog { get; set; } = true;

    /// <summary>Show the "download complete" dialog.</summary>
    [JsonPropertyName("showCompleteDialog")]
    public bool ShowCompleteDialog { get; set; } = true;

    [JsonPropertyName("showErrorDialog")]
    public bool ShowErrorDialog { get; set; } = true;

    /// <summary>Watch the clipboard for download URLs and offer to grab them.</summary>
    [JsonPropertyName("clipboardMonitoring")]
    public bool ClipboardMonitoring { get; set; }

    /// <summary>Hold this modifier while clicking a link to force takeover.</summary>
    [JsonPropertyName("takeOverModifier")]
    public string TakeOverModifier { get; set; } = "Alt";

    /// <summary>Hold this modifier while clicking a link to force the browser to keep the download.</summary>
    [JsonPropertyName("bypassModifier")]
    public string BypassModifier { get; set; } = "Shift";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; }

    /// <summary>Closing the main window hides it to the notification area instead of exiting.</summary>
    [JsonPropertyName("minimizeToTrayOnClose")]
    public bool MinimizeToTrayOnClose { get; set; } = true;

    [JsonPropertyName("confirmOnExit")]
    public bool ConfirmOnExit { get; set; }

    [JsonPropertyName("runClipboardMonitorsInBackground")]
    public bool MonitorClipboardInBackground { get; set; } = true;

    [JsonPropertyName("warnOnDuplicateDownload")]
    public bool WarnOnDuplicateDownload { get; set; } = true;

    /// <summary>Honour the "force takeover" key while clicking a link.</summary>
    [JsonPropertyName("enableForceKey")]
    public bool EnableForceKey { get; set; } = true;

    /// <summary>Honour the "let the browser keep it" key while clicking a link.</summary>
    [JsonPropertyName("enablePreventKey")]
    public bool EnablePreventKey { get; set; } = true;

    /// <summary>Also react to middle-click and modifier-click takeover gestures.</summary>
    [JsonPropertyName("checkMouse")]
    public bool CheckMouse { get; set; }

    /// <summary>Never take over plain web page navigations.</summary>
    [JsonPropertyName("skipHtml")]
    public bool SkipHtml { get; set; } = true;

    /// <summary>Leave the last used folder selected the next time a download is added.</summary>
    [JsonPropertyName("rememberLastSave")]
    public bool RememberLastSave { get; set; } = true;

    /// <summary>The folder remembered by <see cref="RememberLastSave"/>.</summary>
    [JsonPropertyName("lastUsedDirectory")]
    public string? LastUsedDirectory { get; set; }

    internal void Normalize(AppSettings owner)
    {
        TakeOverModifier = NormalizeModifier(TakeOverModifier, "Alt");
        BypassModifier = NormalizeModifier(BypassModifier, "Shift");

        if (string.IsNullOrWhiteSpace(Language))
        {
            Language = "en";
        }
    }

    /// <summary>
    /// Validates a modifier specification and puts it in canonical form.
    ///
    /// A specification may be a single modifier or a group such as "Alt+Ctrl", so a
    /// user can force a takeover with one modifier and prevent it with another. The
    /// accepted order is fixed so two equivalent entries compare equal, and "None"
    /// wins outright because it means "no modifier at all".
    /// </summary>
    public static string NormalizeModifier(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var allowed = new[] { "Alt", "Ctrl", "Shift", "None" };
        var picked = new List<string>();

        foreach (var part in value.Split(new[] { '+', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = allowed.FirstOrDefault(candidate =>
                string.Equals(candidate, part, StringComparison.OrdinalIgnoreCase));

            if (match is not null && !picked.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                picked.Add(match);
            }
        }

        if (picked.Contains("None", StringComparer.OrdinalIgnoreCase))
        {
            return "None";
        }

        // Keep a stable order regardless of how the value was typed.
        return picked.Count > 0
            ? string.Join("+", allowed.Where(candidate => picked.Contains(candidate, StringComparer.OrdinalIgnoreCase)))
            : fallback;
    }
}

public sealed class DownloadsSettings
{
    [JsonPropertyName("defaultDownloadDirectory")]
    public string DefaultDownloadDirectory { get; set; } = AppPaths.DefaultDownloadDirectory;

    /// <summary>When true, files are filed into a per-category subfolder of the default directory.</summary>
    [JsonPropertyName("useCategoryFolders")]
    public bool UseCategoryFolders { get; set; } = true;

    [JsonPropertyName("categoryFolders")]
    public Dictionary<string, string> CategoryFolders { get; set; } = new()
    {
        ["Video"] = Path.Combine(AppPaths.DefaultDownloadDirectory, "Video"),
        ["Music"] = Path.Combine(AppPaths.DefaultDownloadDirectory, "Music"),
        ["Programs"] = Path.Combine(AppPaths.DefaultDownloadDirectory, "Programs"),
        ["Documents"] = Path.Combine(AppPaths.DefaultDownloadDirectory, "Documents"),
        ["Compressed"] = Path.Combine(AppPaths.DefaultDownloadDirectory, "Compressed"),
        ["Other"] = Path.Combine(AppPaths.DefaultDownloadDirectory, "Other")
    };

    /// <summary>Start new downloads immediately rather than leaving them queued.</summary>
    [JsonPropertyName("startDownloadsAutomatically")]
    public bool StartDownloadsAutomatically { get; set; } = true;

    /// <summary>Resume unfinished downloads from the previous session at startup.</summary>
    [JsonPropertyName("resumeUnfinishedOnStartup")]
    public bool ResumeUnfinishedOnStartup { get; set; } = true;

    [JsonPropertyName("progressDialog")]
    public ProgressDialogMode ProgressDialog { get; set; } = ProgressDialogMode.Compact;

    [JsonPropertyName("postDownloadAction")]
    public PostDownloadAction PostDownloadAction { get; set; } = PostDownloadAction.None;

    [JsonPropertyName("openWithPath")]
    public string OpenWithPath { get; set; } = string.Empty;

    [JsonPropertyName("antivirusPath")]
    public string AntivirusPath { get; set; } = string.Empty;

    [JsonPropertyName("antivirusArguments")]
    public string AntivirusArguments { get; set; } = "\"%1\"";

    [JsonPropertyName("existingFileAction")]
    public ExistingFileAction ExistingFileAction { get; set; } = ExistingFileAction.Ask;

    /// <summary>Verify the received byte count against Content-Length before completing.</summary>
    [JsonPropertyName("verifyFileSize")]
    public bool VerifyFileSize { get; set; } = true;

    [JsonPropertyName("computeChecksums")]
    public bool ComputeChecksums { get; set; }

    [JsonPropertyName("autoRetryOnFailure")]
    public bool AutoRetryOnFailure { get; set; } = true;

    [JsonPropertyName("autoRemoveCompleted")]
    public bool AutoRemoveCompleted { get; set; }

    /// <summary>Remove finished entries from the list after this many days. 0 = never.</summary>
    [JsonPropertyName("keepCompletedDays")]
    public int KeepCompletedDays { get; set; }

    /// <summary>Reuse the folder of the previous download as the default for the next one.</summary>
    [JsonPropertyName("rememberLastSave")]
    public bool RememberLastSave { get; set; } = true;

    /// <summary>The folder remembered by <see cref="RememberLastSave"/>.</summary>
    [JsonPropertyName("lastUsedDirectory")]
    public string? LastUsedDirectory { get; set; }

    /// <summary>Default action for FTP transfers that support it.</summary>
    [JsonPropertyName("ftpPassive")]
    public bool FtpPassive { get; set; } = true;

    /// <summary>Do not take over downloads smaller than this. 0 = no minimum.</summary>
    [JsonPropertyName("minTakeOverSizeBytes")]
    public long MinTakeOverSizeBytes { get; set; }

    /// <summary>Clipboard monitoring only reacts to URLs matching these extensions.</summary>
    [JsonPropertyName("clipboardMinSizeBytes")]
    public long ClipboardMinSizeBytes { get; set; }

    [JsonPropertyName("addToQueueByDefault")]
    public bool AddToQueueByDefault { get; set; }

    [JsonPropertyName("defaultQueueId")]
    public int DefaultQueueId { get; set; }

    internal void Normalize()
    {
        if (CategoryFolders is null || CategoryFolders.Count == 0)
        {
            CategoryFolders = new Dictionary<string, string>
            {
                ["Video"] = Path.Combine(DefaultDownloadDirectory, "Video"),
                ["Music"] = Path.Combine(DefaultDownloadDirectory, "Music"),
                ["Programs"] = Path.Combine(DefaultDownloadDirectory, "Programs"),
                ["Documents"] = Path.Combine(DefaultDownloadDirectory, "Documents"),
                ["Compressed"] = Path.Combine(DefaultDownloadDirectory, "Compressed"),
                ["Other"] = DefaultDownloadDirectory
            };
        }
        else
        {
            foreach (var key in CategoryFolders.Keys.ToList())
            {
                if (string.IsNullOrWhiteSpace(CategoryFolders[key]))
                {
                    CategoryFolders[key] = DefaultDownloadDirectory;
                }
            }
        }
    }
}

public sealed class ConnectionSettings
{
    /// <summary>Simultaneous connections per file. Download managers call this "segments".</summary>
    [JsonPropertyName("maxConnectionsPerFile")]
    public int MaxConnectionsPerFile { get; set; } = 8;

    [JsonPropertyName("maxConcurrentDownloads")]
    public int MaxConcurrentDownloads { get; set; } = 3;

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; } = 10;

    [JsonPropertyName("retryDelaySeconds")]
    public int RetryDelaySeconds { get; set; } = 5;

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 60;

    [JsonPropertyName("speedLimitEnabled")]
    public bool SpeedLimitEnabled { get; set; }

    /// <summary>0 means unlimited.</summary>
    [JsonPropertyName("speedLimitKbPerSecond")]
    public int SpeedLimitKbPerSecond { get; set; } = 0;

    /// <summary>Run a speed test style "auto" probe and use the best connection count.</summary>
    [JsonPropertyName("autoTuneConnections")]
    public bool AutoTuneConnections { get; set; } = true;

    [JsonPropertyName("useCustomUserAgent")]
    public bool UseCustomUserAgent { get; set; }

    [JsonPropertyName("customUserAgent")]
    public string CustomUserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    [JsonPropertyName("proxyMode")]
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;

    [JsonPropertyName("proxyAddress")]
    public string ProxyAddress { get; set; } = string.Empty;

    [JsonPropertyName("proxyPort")]
    public int ProxyPort { get; set; } = 8080;

    [JsonPropertyName("proxyUsername")]
    public string ProxyUsername { get; set; } = string.Empty;

    /// <summary>Ciphertext produced by <c>CredentialProtector</c>.</summary>
    [JsonPropertyName("proxyPasswordProtected")]
    public string? ProxyPasswordProtected { get; set; }

    [JsonPropertyName("proxyBypassLocal")]
    public bool ProxyBypassLocal { get; set; } = true;

    /// <summary>Per-protocol switches: each scheme can opt out of the proxy separately.</summary>
    [JsonPropertyName("useHttpProxy")]
    public bool UseHttpProxy { get; set; } = true;

    [JsonPropertyName("useHttpsProxy")]
    public bool UseHttpsProxy { get; set; } = true;

    [JsonPropertyName("useFtpProxy")]
    public bool UseFtpProxy { get; set; } = true;

    /// <summary>Dialect for the manual proxy. A SOCKS setting wins over plain HTTP CONNECT.</summary>
    [JsonPropertyName("socksType")]
    public SocksType SocksType { get; set; } = SocksType.None;

    /// <summary>
    /// Ask a SOCKS5 proxy to resolve the name. .NET always sends the host name for
    /// socks5, so this is recorded for the options dialog and for the documentation.
    /// </summary>
    [JsonPropertyName("socks5ProxyDns")]
    public bool Socks5ProxyDns { get; set; } = true;

    /// <summary>Hosts that must connect directly, one entry per host or domain.</summary>
    [JsonPropertyName("proxyExceptions")]
    public List<string> ProxyExceptions { get; set; } = new();

    /// <summary>Use passive mode for FTP transfers.</summary>
    [JsonPropertyName("ftpPassive")]
    public bool FtpPassive { get; set; } = true;

    /// <summary>Accept invalid/self-signed TLS certificates. Off by default; surfaced with a warning in the UI.</summary>
    [JsonPropertyName("ignoreCertificateErrors")]
    public bool IgnoreCertificateErrors { get; set; }

    /// <summary>Send a HEAD request first to learn the size, then a GET. Some servers dislike HEAD.</summary>
    [JsonPropertyName("useHeadProbe")]
    public bool UseHeadProbe { get; set; } = true;

    [JsonPropertyName("maxRedirects")]
    public int MaxRedirects { get; set; } = 10;

    /// <summary>Behaviour when an FTP or plain-HTTP server rejects range requests.</summary>
    [JsonPropertyName("singleConnectionFallback")]
    public bool SingleConnectionFallback { get; set; } = true;
}

public sealed class SchedulerSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("startQueueOnSchedule")]
    public bool StartQueueOnSchedule { get; set; } = true;

    /// <summary>Stop all downloads when the scheduled window ends.</summary>
    [JsonPropertyName("stopQueueOnSchedule")]
    public bool StopQueueOnSchedule { get; set; } = true;

    [JsonPropertyName("exitWhenQueueFinished")]
    public bool ExitWhenQueueFinished { get; set; }

    [JsonPropertyName("shutdownWhenQueueFinished")]
    public bool ShutdownWhenQueueFinished { get; set; }

    /// <summary>Turn off the machine once every download in the list is complete.</summary>
    [JsonPropertyName("shutdownWhenAllComplete")]
    public bool ShutdownWhenAllComplete { get; set; }

    /// <summary>Seconds to wait before acting on "when finished", so the user can cancel.</summary>
    [JsonPropertyName("finishedActionDelaySeconds")]
    public int FinishedActionDelaySeconds { get; set; } = 60;

    /// <summary>Master switch for the daily time and volume ceilings.</summary>
    [JsonPropertyName("dailyLimitEnabled")]
    public bool DailyLimitEnabled { get; set; }

    /// <summary>Stop downloading once this many hours have been spent today. 0 disables the time ceiling.</summary>
    [JsonPropertyName("dailyLimitHours")]
    public double DailyLimitHours { get; set; }

    /// <summary>Stop downloading once this much data has been fetched today. 0 disables the volume ceiling.</summary>
    [JsonPropertyName("dailyLimitMegabytes")]
    public long DailyLimitMegabytes { get; set; }

    /// <summary>Warn once when a daily ceiling is reached instead of stopping silently.</summary>
    [JsonPropertyName("showLimitExceededWarning")]
    public bool ShowLimitExceededWarning { get; set; } = true;
}

public sealed class SoundSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("startSound")]
    public string StartSound { get; set; } = string.Empty;

    [JsonPropertyName("completeSound")]
    public string CompleteSound { get; set; } = string.Empty;

    [JsonPropertyName("errorSound")]
    public string ErrorSound { get; set; } = string.Empty;

    /// <summary>Balloon/toast notification when a download finishes.</summary>
    [JsonPropertyName("notifyOnComplete")]
    public bool NotifyOnComplete { get; set; } = true;

    [JsonPropertyName("notifyOnError")]
    public bool NotifyOnError { get; set; } = true;

    /// <summary>Notify when a scheduled queue window opens.</summary>
    [JsonPropertyName("notifyOnQueueStart")]
    public bool NotifyOnQueueStart { get; set; } = true;

    /// <summary>Notify when a scheduled queue window closes or the queue drains.</summary>
    [JsonPropertyName("notifyOnQueueFinish")]
    public bool NotifyOnQueueFinish { get; set; } = true;
}

/// <summary>Per-browser integration switches. The extension and native host read these over IPC.</summary>
public sealed class BrowserIntegrationSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("chrome")]
    public bool Chrome { get; set; } = true;

    [JsonPropertyName("edge")]
    public bool Edge { get; set; } = true;

    [JsonPropertyName("firefox")]
    public bool Firefox { get; set; } = true;

    [JsonPropertyName("brave")]
    public bool Brave { get; set; } = true;

    [JsonPropertyName("opera")]
    public bool Opera { get; set; } = true;

    [JsonPropertyName("vivaldi")]
    public bool Vivaldi { get; set; } = true;

    /// <summary>Add the "Download with OpenDLM" entries to the browser context menu.</summary>
    [JsonPropertyName("contextMenu")]
    public bool ContextMenu { get; set; } = true;

    /// <summary>Show the floating "download this media" badge over detected video/audio elements.</summary>
    [JsonPropertyName("mediaOverlay")]
    public bool MediaOverlay { get; set; } = true;

    /// <summary>Take over downloads initiated by the browser's own download manager.</summary>
    [JsonPropertyName("takeOverBrowserDownloads")]
    public bool TakeOverBrowserDownloads { get; set; } = true;

    /// <summary>Do not take over files smaller than this many bytes.</summary>
    [JsonPropertyName("minSizeBytes")]
    public long MinSizeBytes { get; set; }

    /// <summary>Extensions the extension should claim, in "*.zip" form.</summary>
    [JsonPropertyName("takeOverExtensions")]
    public List<string> TakeOverExtensions { get; set; } = new();

    /// <summary>Extensions the extension must always leave to the browser.</summary>
    [JsonPropertyName("excludedExtensions")]
    public List<string> ExcludedExtensions { get; set; } = new() { "*.htm", "*.html", "*.php", "*.asp" };
}

public sealed class InterfaceSettings
{
    [JsonPropertyName("theme")]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Show the category tree pane.</summary>
    [JsonPropertyName("showCategoryPane")]
    public bool ShowCategoryPane { get; set; } = true;

    /// <summary>Show the description/log pane at the bottom.</summary>
    [JsonPropertyName("showDetailsPane")]
    public bool ShowDetailsPane { get; set; } = true;

    [JsonPropertyName("showToolbar")]
    public bool ShowToolbar { get; set; } = true;

    [JsonPropertyName("showStatusBar")]
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>Always on top of other windows.</summary>
    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; }

    /// <summary>How often the list is refreshed while downloads run.</summary>
    [JsonPropertyName("refreshIntervalMs")]
    public int RefreshIntervalMs { get; set; } = 500;

    /// <summary>How much detail the toolbar shows.</summary>
    [JsonPropertyName("toolbarStyle")]
    public ToolbarStyle ToolbarStyle { get; set; } = ToolbarStyle.IconsAndText;

    /// <summary>Which grid columns are visible, in display order.</summary>
    [JsonPropertyName("visibleColumns")]
    public List<string> VisibleColumns { get; set; } = new()
    {
        "FileName", "Size", "Status", "Downloaded", "Speed", "TimeLeft", "Description"
    };

    [JsonPropertyName("windowWidth")]
    public double WindowWidth { get; set; } = 980;

    [JsonPropertyName("windowHeight")]
    public double WindowHeight { get; set; } = 620;

    [JsonPropertyName("windowLeft")]
    public double WindowLeft { get; set; } = double.NaN;

    [JsonPropertyName("windowTop")]
    public double WindowTop { get; set; } = double.NaN;

    [JsonPropertyName("windowMaximized")]
    public bool WindowMaximized { get; set; }

    /// <summary>Sort column key and direction for the main list.</summary>
    [JsonPropertyName("sortColumn")]
    public string SortColumn { get; set; } = "CreatedAt";

    [JsonPropertyName("sortAscending")]
    public bool SortAscending { get; set; } = false;
}

public sealed class AdvancedSettings
{
    /// <summary>Keep a bounded log file for troubleshooting.</summary>
    [JsonPropertyName("enableLogging")]
    public bool EnableLogging { get; set; } = true;

    [JsonPropertyName("sendReferer")]
    public bool SendReferer { get; set; } = true;

    /// <summary>Send the browser's cookies with the request. Required by many sites.</summary>
    [JsonPropertyName("sendCookies")]
    public bool SendCookies { get; set; } = true;

    /// <summary>Reuse one TCP connection for every segment instead of one per segment.</summary>
    [JsonPropertyName("reuseConnections")]
    public bool ReuseConnections { get; set; }

    /// <summary>Trust the "ZIP" / archive hint and probe with a GET when HEAD is refused.</summary>
    [JsonPropertyName("probeWithGetFallback")]
    public bool ProbeWithGetFallback { get; set; } = true;

    /// <summary>Listen for browser extension requests over the local named pipe.</summary>
    [JsonPropertyName("enableBrowserIpc")]
    public bool EnableBrowserIpc { get; set; } = true;

    /// <summary>Allow only these hosts for browser-initiated downloads; empty means "any host".</summary>
    [JsonPropertyName("allowedIpcHosts")]
    public List<string> AllowedIpcHosts { get; set; } = new();

    /// <summary>Maximum size of a partial file that may be kept for resume. 0 = unlimited.</summary>
    [JsonPropertyName("maxResumeFileSizeBytes")]
    public long MaxResumeFileSizeBytes { get; set; }
}
