using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OpenDLM.App.Services;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Views;

/// <summary>
/// The options dialog.
///
/// It edits a clone of the settings and only replaces the live configuration on OK,
/// so Cancel genuinely cancels. The file-type table and the site logins are edited
/// as collections and pushed to the engine on save.
/// </summary>
public partial class OptionsWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly SettingsService _settingsService;

    private readonly AppSettings _working;
    private readonly ObservableCollection<FileTypeRule> _fileTypes;
    private readonly ObservableCollection<SiteLogin> _logins;
    private readonly ObservableCollection<SiteException> _siteExceptions;

    private bool _loading = true;

    public OptionsWindow(DownloadManager manager, SettingsService settingsService)
    {
        _manager = manager;
        _settingsService = settingsService;

        InitializeComponent();

        _working = settingsService.Current.Clone();
        _fileTypes = new ObservableCollection<FileTypeRule>(manager.FileTypes.Rules.Select(CloneRule));
        _logins = new ObservableCollection<SiteLogin>(manager.SiteLogins.Select(CloneLogin));
        _siteExceptions = new ObservableCollection<SiteException>(
            manager.SiteExceptions.Select(entry => entry.Clone()));

        FileTypeList.ItemsSource = _fileTypes;
        LoginList.ItemsSource = _logins;
        SiteExceptionList.ItemsSource = _siteExceptions;

        BuildChoiceLists();
        LoadFromSettings();

        _loading = false;
    }

    /// <summary>True once the user confirmed a change.</summary>
    public bool SettingsChanged { get; private set; }

    // ---------------------------------------------------------------- choice lists

    private void BuildChoiceLists()
    {
        foreach (var value in Enum.GetValues<FileTypeAction>())
        {
            FileTypeActionBox.Items.Add(value);
        }
        FileTypeActionBox.SelectedIndex = 0;

        foreach (var value in Enum.GetValues<DownloadCategory>())
        {
            FileTypeCategoryBox.Items.Add(value);
        }
        FileTypeCategoryBox.SelectedIndex = 0;

        foreach (var value in Enum.GetValues<ExistingFileAction>())
        {
            ExistingFileBox.Items.Add(value);
        }

        foreach (var value in Enum.GetValues<ProgressDialogMode>())
        {
            ProgressDialogBox.Items.Add(value);
        }

        foreach (var value in Enum.GetValues<PostDownloadAction>())
        {
            PostActionBox.Items.Add(value);
        }

        foreach (var value in Enum.GetValues<ProxyMode>())
        {
            ProxyModeBox.Items.Add(value);
        }

        foreach (var value in Enum.GetValues<AppTheme>())
        {
            ThemeBox.Items.Add(value);
        }

        foreach (var value in Enum.GetValues<ToolbarStyle>())
        {
            ToolbarStyleBox.Items.Add(value);
        }

        foreach (var value in Enum.GetValues<SocksType>())
        {
            SocksTypeBox.Items.Add(value);
        }
    }

    /// <summary>Reads "Alt+Ctrl" into a set of part names.</summary>
    private static List<string> SplitModifiers(string? value)
        => (value ?? string.Empty)
            .Split(new[] { '+', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Builds a canonical specification, or "None" when nothing is selected.</summary>
    private static string JoinModifiers(params bool?[] states)
    {
        var parts = new List<string>();
        if (states.Length > 0 && states[0] == true) parts.Add("Alt");
        if (states.Length > 1 && states[1] == true) parts.Add("Ctrl");
        if (states.Length > 2 && states[2] == true) parts.Add("Shift");
        return parts.Count == 0 ? "None" : string.Join("+", parts);
    }

    private static void SetModifierBoxes(CheckBox alt, CheckBox ctrl, CheckBox shift, string? specification)
    {
        var parts = SplitModifiers(specification);
        alt.IsChecked = parts.Contains("Alt", StringComparer.OrdinalIgnoreCase);
        ctrl.IsChecked = parts.Contains("Ctrl", StringComparer.OrdinalIgnoreCase) ||
                         parts.Contains("Control", StringComparer.OrdinalIgnoreCase);
        shift.IsChecked = parts.Contains("Shift", StringComparer.OrdinalIgnoreCase);
    }

    private static void SelectEnum<T>(ComboBox box, T value) where T : struct, Enum
    {
        for (var index = 0; index < box.Items.Count; index++)
        {
            if (box.Items[index] is T candidate && candidate.Equals(value))
            {
                box.SelectedIndex = index;
                return;
            }
        }

        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private static T ReadEnum<T>(ComboBox box, T fallback) where T : struct, Enum
        => box.SelectedItem is T value ? value : fallback;

    // ------------------------------------------------------------------- loading

    private void LoadFromSettings()
    {
        var general = _working.General;
        CaptureDownloadsBox.IsChecked = general.CaptureDownloads;
        WarnDuplicateBox.IsChecked = general.WarnOnDuplicateDownload;
        RememberDuplicateBox.IsChecked = general.RememberDuplicateAnswers;
        RememberDuplicateBox.IsEnabled = general.WarnOnDuplicateDownload;
        ClipboardBox.IsChecked = general.ClipboardMonitoring;
        ShowStartBox.IsChecked = general.ShowStartDialog;
        ShowCompleteBox.IsChecked = general.ShowCompleteDialog;
        ShowErrorBox.IsChecked = general.ShowErrorDialog;
        ConfirmExitBox.IsChecked = general.ConfirmOnExit;
        StartWithWindowsBox.IsChecked = general.StartWithWindows;
        StartMinimizedBox.IsChecked = general.StartMinimized;
        MinimizeToTrayBox.IsChecked = general.MinimizeToTrayOnClose;
        EnableForceKeyBox.IsChecked = general.EnableForceKey;
        EnablePreventKeyBox.IsChecked = general.EnablePreventKey;
        CheckMouseBox.IsChecked = general.CheckMouse;
        SkipHtmlBox.IsChecked = general.SkipHtml;
        SetModifierBoxes(ForceAltBox, ForceCtrlBox, ForceShiftBox, general.TakeOverModifier);
        SetModifierBoxes(PreventAltBox, PreventCtrlBox, PreventShiftBox, general.BypassModifier);

        var downloads = _working.Downloads;
        DefaultFolderBox.Text = downloads.DefaultDownloadDirectory;
        UseCategoryFoldersBox.IsChecked = downloads.UseCategoryFolders;
        FolderVideoBox.Text = Folder(downloads, "Video");
        FolderMusicBox.Text = Folder(downloads, "Music");
        FolderProgramsBox.Text = Folder(downloads, "Programs");
        FolderDocumentsBox.Text = Folder(downloads, "Documents");
        FolderCompressedBox.Text = Folder(downloads, "Compressed");
        FolderOtherBox.Text = Folder(downloads, "Other");
        StartAutomaticallyBox.IsChecked = downloads.StartDownloadsAutomatically;
        AddToQueueBox.IsChecked = downloads.AddToQueueByDefault;
        ResumeOnStartupBox.IsChecked = downloads.ResumeUnfinishedOnStartup;
        AutoRetryBox.IsChecked = downloads.AutoRetryOnFailure;
        VerifySizeBox.IsChecked = downloads.VerifyFileSize;
        ChecksumsBox.IsChecked = downloads.ComputeChecksums;
        RememberLastSaveBox.IsChecked = downloads.RememberLastSave;
        SelectEnum(ExistingFileBox, downloads.ExistingFileAction);
        SelectEnum(ProgressDialogBox, downloads.ProgressDialog);
        SelectEnum(PostActionBox, downloads.PostDownloadAction);
        MinSizeBox.Text = (downloads.MinTakeOverSizeBytes / 1024).ToString(CultureInfo.InvariantCulture);
        OpenWithBox.Text = downloads.OpenWithPath;
        AntivirusBox.Text = downloads.AntivirusPath;

        var connection = _working.Connection;
        MaxConnectionsBox.Text = connection.MaxConnectionsPerFile.ToString(CultureInfo.InvariantCulture);
        MaxConcurrentBox.Text = connection.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture);
        RetryCountBox.Text = connection.RetryCount.ToString(CultureInfo.InvariantCulture);
        RetryDelayBox.Text = connection.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture);
        TimeoutBox.Text = connection.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        MaxRedirectsBox.Text = connection.MaxRedirects.ToString(CultureInfo.InvariantCulture);
        LimitSpeedBox.IsChecked = connection.SpeedLimitEnabled;
        SpeedLimitBox.Text = connection.SpeedLimitKbPerSecond.ToString(CultureInfo.InvariantCulture);
        CustomAgentBox.IsChecked = connection.UseCustomUserAgent;
        AgentBox.Text = connection.CustomUserAgent;
        HeadProbeBox.IsChecked = connection.UseHeadProbe;
        FallbackBox.IsChecked = connection.SingleConnectionFallback;
        IgnoreCertsBox.IsChecked = connection.IgnoreCertificateErrors;

        SelectEnum(ProxyModeBox, connection.ProxyMode);
        ProxyAddressBox.Text = connection.ProxyAddress;
        ProxyPortBox.Text = connection.ProxyPort.ToString(CultureInfo.InvariantCulture);
        ProxyUserBox.Text = connection.ProxyUsername;
        ProxyPasswordInput.Password = CredentialProtector.Unprotect(connection.ProxyPasswordProtected) ?? string.Empty;
        ProxyBypassLocalBox.IsChecked = connection.ProxyBypassLocal;
        ProxyHttpBox.IsChecked = connection.UseHttpProxy;
        ProxyHttpsBox.IsChecked = connection.UseHttpsProxy;
        ProxyFtpBox.IsChecked = connection.UseFtpProxy;
        SocksDnsBox.IsChecked = connection.Socks5ProxyDns;
        SelectEnum(SocksTypeBox, connection.SocksType);
        PacUrlBox.Text = connection.ProxyPacUrl;
        ProxyExceptionsBox.Text = string.Join(Environment.NewLine,
            connection.ProxyExceptions ?? new List<string>());

        var browser = _working.BrowserIntegration;
        BrowserEnabledBox.IsChecked = browser.Enabled;
        ChromeBox.IsChecked = browser.Chrome;
        EdgeBox.IsChecked = browser.Edge;
        FirefoxBox.IsChecked = browser.Firefox;
        BraveBox.IsChecked = browser.Brave;
        OperaBox.IsChecked = browser.Opera;
        VivaldiBox.IsChecked = browser.Vivaldi;
        TakeOverBrowserBox.IsChecked = browser.TakeOverBrowserDownloads;
        ContextMenuBox.IsChecked = browser.ContextMenu;
        ContextMenuLinkBox.IsChecked = browser.ContextMenuDownloadWith;
        ContextMenuAllBox.IsChecked = browser.ContextMenuDownloadAll;
        ContextMenuMediaBox.IsChecked = browser.ContextMenuMedia;
        MediaOverlayBox.IsChecked = browser.MediaOverlay;
        BrowserMinSizeBox.Text = (browser.MinSizeBytes / 1024).ToString(CultureInfo.InvariantCulture);

        TakeOverExtensionsBox.Text = browser.TakeOverExtensions.Count > 0
            ? string.Join(Environment.NewLine, browser.TakeOverExtensions)
            : string.Join(Environment.NewLine, _manager.FileTypes.TakeOverMasks());

        ExcludedExtensionsBox.Text = browser.ExcludedExtensions.Count > 0
            ? string.Join(Environment.NewLine, browser.ExcludedExtensions)
            : string.Join(Environment.NewLine, _manager.FileTypes.ExcludedMasks());

        var ui = _working.Interface;
        SelectEnum(ThemeBox, ui.Theme);
        ShowCategoryPaneBox.IsChecked = ui.ShowCategoryPane;
        ShowDetailsPaneBox.IsChecked = ui.ShowDetailsPane;
        ShowToolbarBox.IsChecked = ui.ShowToolbar;
        ShowStatusBarBox.IsChecked = ui.ShowStatusBar;
        SelectEnum(ToolbarStyleBox, ui.ToolbarStyle);
        RefreshIntervalBox.Text = ui.RefreshIntervalMs.ToString(CultureInfo.InvariantCulture);

        var advanced = _working.Advanced;
        LoggingBox.IsChecked = advanced.EnableLogging;
        SendRefererBox.IsChecked = advanced.SendReferer;
        SendCookiesBox.IsChecked = advanced.SendCookies;
        BrowserIpcBox.IsChecked = advanced.EnableBrowserIpc;
        ProbeWithGetBox.IsChecked = advanced.ProbeWithGetFallback;

        var sounds = _working.Sounds;
        NotifyCompleteBox.IsChecked = sounds.NotifyOnComplete;
        NotifyErrorBox.IsChecked = sounds.NotifyOnError;
        NotifyQueueStartBox.IsChecked = sounds.NotifyOnQueueStart;
        NotifyQueueFinishBox.IsChecked = sounds.NotifyOnQueueFinish;

        PathsBox.Text =
            $"Settings and list: {AppPaths.RoamingRoot}{Environment.NewLine}" +
            $"Logs: {AppPaths.LogDirectory}{Environment.NewLine}" +
            $"Incomplete downloads: {AppPaths.TempDirectory}{Environment.NewLine}" +
            $"Portable mode: {(AppPaths.PortableRoot is null ? "off" : AppPaths.PortableRoot)}";

        FootNote.Text = "Changed values are applied when you choose OK.";

        LoadSelectedLogin();
    }

    private static string Folder(DownloadsSettings downloads, string key)
        => downloads.CategoryFolders.TryGetValue(key, out var value) ? value : downloads.DefaultDownloadDirectory;

    // -------------------------------------------------------------------- saving

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!ValidateInput())
        {
            return;
        }

        SaveToSettings();

        _settingsService.Replace(_working);
        _manager.FileTypes.ReplaceAll(_fileTypes);
        _manager.SaveSiteExceptions(_siteExceptions);
        ApplyLoginChanges();

        SettingsChanged = true;
        DialogResult = true;
    }

    private bool ValidateInput()
    {
        if (!TryInt(MaxConnectionsBox, 1, 32, "Connections per download") ||
            !TryInt(MaxConcurrentBox, 1, 50, "Downloads at the same time") ||
            !TryInt(RetryCountBox, 0, 1000, "Retries per download") ||
            !TryInt(RetryDelayBox, 1, 600, "Seconds between retries") ||
            !TryInt(TimeoutBox, 5, 3600, "Server timeout") ||
            !TryInt(MaxRedirectsBox, 1, 50, "Maximum redirects") ||
            !TryInt(SpeedLimitBox, 0, 10_000_000, "Maximum speed") ||
            !TryInt(ProxyPortBox, 1, 65535, "Proxy port") ||
            !TryInt(RefreshIntervalBox, 100, 5000, "Status refresh interval") ||
            !TryLong(MinSizeBox, 0, long.MaxValue, "Minimum takeover size") ||
            !TryLong(BrowserMinSizeBox, 0, long.MaxValue, "Minimum browser download size"))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(DefaultFolderBox.Text))
        {
            Dialogs.Warn(this, "Choose a default download folder.");
            return false;
        }

        return true;
    }

    private bool TryInt(TextBox box, int min, int max, string what)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            value >= min && value <= max)
        {
            return true;
        }

        Dialogs.Warn(this, $"\"{what}\" must be a whole number between {min} and {max}.");
        box.Focus();
        box.SelectAll();
        return false;
    }

    private bool TryLong(TextBox box, long min, long max, string what)
    {
        if (long.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            value >= min && value <= max)
        {
            return true;
        }

        Dialogs.Warn(this, $"\"{what}\" must be a whole number of at least {min}.");
        box.Focus();
        box.SelectAll();
        return false;
    }

    private void SaveToSettings()
    {
        var general = _working.General;
        general.CaptureDownloads = CaptureDownloadsBox.IsChecked == true;
        general.WarnOnDuplicateDownload = WarnDuplicateBox.IsChecked == true;
        general.RememberDuplicateAnswers =
            general.WarnOnDuplicateDownload && RememberDuplicateBox.IsChecked == true;
        general.ClipboardMonitoring = ClipboardBox.IsChecked == true;
        general.ShowStartDialog = ShowStartBox.IsChecked == true;
        general.ShowCompleteDialog = ShowCompleteBox.IsChecked == true;
        general.ShowErrorDialog = ShowErrorBox.IsChecked == true;
        general.ConfirmOnExit = ConfirmExitBox.IsChecked == true;
        general.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        general.StartMinimized = StartMinimizedBox.IsChecked == true;
        general.MinimizeToTrayOnClose = MinimizeToTrayBox.IsChecked == true;
        general.EnableForceKey = EnableForceKeyBox.IsChecked == true;
        general.EnablePreventKey = EnablePreventKeyBox.IsChecked == true;
        general.CheckMouse = CheckMouseBox.IsChecked == true;
        general.SkipHtml = SkipHtmlBox.IsChecked == true;
        general.TakeOverModifier = JoinModifiers(
            ForceAltBox.IsChecked, ForceCtrlBox.IsChecked, ForceShiftBox.IsChecked);
        general.BypassModifier = JoinModifiers(
            PreventAltBox.IsChecked, PreventCtrlBox.IsChecked, PreventShiftBox.IsChecked);

        var downloads = _working.Downloads;
        downloads.DefaultDownloadDirectory = DefaultFolderBox.Text.Trim();
        downloads.UseCategoryFolders = UseCategoryFoldersBox.IsChecked == true;
        downloads.CategoryFolders["Video"] = FolderVideoBox.Text.Trim();
        downloads.CategoryFolders["Music"] = FolderMusicBox.Text.Trim();
        downloads.CategoryFolders["Programs"] = FolderProgramsBox.Text.Trim();
        downloads.CategoryFolders["Documents"] = FolderDocumentsBox.Text.Trim();
        downloads.CategoryFolders["Compressed"] = FolderCompressedBox.Text.Trim();
        downloads.CategoryFolders["Other"] = FolderOtherBox.Text.Trim();
        downloads.StartDownloadsAutomatically = StartAutomaticallyBox.IsChecked == true;
        downloads.AddToQueueByDefault = AddToQueueBox.IsChecked == true;
        downloads.ResumeUnfinishedOnStartup = ResumeOnStartupBox.IsChecked == true;
        downloads.AutoRetryOnFailure = AutoRetryBox.IsChecked == true;
        downloads.VerifyFileSize = VerifySizeBox.IsChecked == true;
        downloads.ComputeChecksums = ChecksumsBox.IsChecked == true;
        downloads.RememberLastSave = RememberLastSaveBox.IsChecked == true;
        downloads.ExistingFileAction = ReadEnum(ExistingFileBox, ExistingFileAction.Ask);
        downloads.ProgressDialog = ReadEnum(ProgressDialogBox, ProgressDialogMode.Compact);
        downloads.PostDownloadAction = ReadEnum(PostActionBox, PostDownloadAction.None);
        downloads.MinTakeOverSizeBytes = ReadLong(MinSizeBox) * 1024;
        downloads.OpenWithPath = OpenWithBox.Text.Trim();
        downloads.AntivirusPath = AntivirusBox.Text.Trim();

        var connection = _working.Connection;
        connection.MaxConnectionsPerFile = ReadInt(MaxConnectionsBox, 8);
        connection.MaxConcurrentDownloads = ReadInt(MaxConcurrentBox, 3);
        connection.RetryCount = ReadInt(RetryCountBox, 10);
        connection.RetryDelaySeconds = ReadInt(RetryDelayBox, 5);
        connection.TimeoutSeconds = ReadInt(TimeoutBox, 60);
        connection.MaxRedirects = ReadInt(MaxRedirectsBox, 10);
        connection.SpeedLimitEnabled = LimitSpeedBox.IsChecked == true;
        connection.SpeedLimitKbPerSecond = ReadInt(SpeedLimitBox, 0);
        connection.UseCustomUserAgent = CustomAgentBox.IsChecked == true;
        connection.CustomUserAgent = AgentBox.Text.Trim();
        connection.UseHeadProbe = HeadProbeBox.IsChecked == true;
        connection.SingleConnectionFallback = FallbackBox.IsChecked == true;
        connection.IgnoreCertificateErrors = IgnoreCertsBox.IsChecked == true;

        connection.ProxyMode = ReadEnum(ProxyModeBox, ProxyMode.System);
        connection.ProxyAddress = ProxyAddressBox.Text.Trim();
        connection.ProxyPort = ReadInt(ProxyPortBox, 8080);
        connection.ProxyUsername = ProxyUserBox.Text.Trim();
        var proxyPassword = ProxyPasswordInput.Password;
        connection.ProxyPasswordProtected = string.IsNullOrEmpty(proxyPassword)
            ? null
            : CredentialProtector.Protect(proxyPassword);
        connection.ProxyBypassLocal = ProxyBypassLocalBox.IsChecked == true;
        connection.UseHttpProxy = ProxyHttpBox.IsChecked == true;
        connection.UseHttpsProxy = ProxyHttpsBox.IsChecked == true;
        connection.UseFtpProxy = ProxyFtpBox.IsChecked == true;
        connection.SocksType = ReadEnum(SocksTypeBox, SocksType.None);
        connection.Socks5ProxyDns = SocksDnsBox.IsChecked == true;
        connection.ProxyPacUrl = PacUrlBox.Text.Trim();
        connection.ProxyExceptions = SplitLines(ProxyExceptionsBox.Text);

        var browser = _working.BrowserIntegration;
        browser.Enabled = BrowserEnabledBox.IsChecked == true;
        browser.Chrome = ChromeBox.IsChecked == true;
        browser.Edge = EdgeBox.IsChecked == true;
        browser.Firefox = FirefoxBox.IsChecked == true;
        browser.Brave = BraveBox.IsChecked == true;
        browser.Opera = OperaBox.IsChecked == true;
        browser.Vivaldi = VivaldiBox.IsChecked == true;
        browser.TakeOverBrowserDownloads = TakeOverBrowserBox.IsChecked == true;
        browser.ContextMenu = ContextMenuBox.IsChecked == true;
        browser.ContextMenuDownloadWith = ContextMenuLinkBox.IsChecked == true;
        browser.ContextMenuDownloadAll = ContextMenuAllBox.IsChecked == true;
        browser.ContextMenuMedia = ContextMenuMediaBox.IsChecked == true;
        browser.MediaOverlay = MediaOverlayBox.IsChecked == true;
        browser.MinSizeBytes = ReadLong(BrowserMinSizeBox) * 1024;
        browser.TakeOverExtensions = SplitLines(TakeOverExtensionsBox.Text);
        browser.ExcludedExtensions = SplitLines(ExcludedExtensionsBox.Text);

        var ui = _working.Interface;
        ui.Theme = ReadEnum(ThemeBox, AppTheme.System);
        ui.ShowCategoryPane = ShowCategoryPaneBox.IsChecked == true;
        ui.ShowDetailsPane = ShowDetailsPaneBox.IsChecked == true;
        ui.ShowToolbar = ShowToolbarBox.IsChecked == true;
        ui.ShowStatusBar = ShowStatusBarBox.IsChecked == true;
        ui.ToolbarStyle = ReadEnum(ToolbarStyleBox, ToolbarStyle.IconsAndText);
        ui.RefreshIntervalMs = ReadInt(RefreshIntervalBox, 500);

        var advanced = _working.Advanced;
        advanced.EnableLogging = LoggingBox.IsChecked == true;
        advanced.SendReferer = SendRefererBox.IsChecked == true;
        advanced.SendCookies = SendCookiesBox.IsChecked == true;
        advanced.EnableBrowserIpc = BrowserIpcBox.IsChecked == true;
        advanced.ProbeWithGetFallback = ProbeWithGetBox.IsChecked == true;

        var sounds = _working.Sounds;
        sounds.NotifyOnComplete = NotifyCompleteBox.IsChecked == true;
        sounds.NotifyOnError = NotifyErrorBox.IsChecked == true;
        sounds.NotifyOnQueueStart = NotifyQueueStartBox.IsChecked == true;
        sounds.NotifyOnQueueFinish = NotifyQueueFinishBox.IsChecked == true;
    }

    private static int ReadInt(TextBox box, int fallback)
        => int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static long ReadLong(TextBox box)
        => long.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static List<string> SplitLines(string? text)
        => (text ?? string.Empty)
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // -------------------------------------------------------------- file types

    private void OnFileTypeActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FileTypeActionBox.SelectedItem is not FileTypeAction action)
        {
            return;
        }

        foreach (var rule in FileTypeList.SelectedItems.Cast<FileTypeRule>().ToList())
        {
            rule.Action = action;
        }

        FileTypeList.Items.Refresh();
    }

    private void OnFileTypeCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FileTypeCategoryBox.SelectedItem is not DownloadCategory category)
        {
            return;
        }

        foreach (var rule in FileTypeList.SelectedItems.Cast<FileTypeRule>().ToList())
        {
            rule.Category = category;
        }

        FileTypeList.Items.Refresh();
    }

    private void OnAddFileType(object sender, RoutedEventArgs e)
    {
        var extension = FileTypeRule.Normalize(NewExtensionBox.Text);

        if (extension.Length <= 1)
        {
            Dialogs.Warn(this, "Enter a file extension, for example: iso");
            return;
        }

        if (_fileTypes.Any(rule => string.Equals(rule.Extension, extension, StringComparison.OrdinalIgnoreCase)))
        {
            Dialogs.Info(this, $"\"{extension}\" is already in the list.");
            return;
        }

        _fileTypes.Add(new FileTypeRule
        {
            Extension = extension,
            Action = FileTypeAction.Ask,
            Category = _manager.FileTypes.CategoryOf("file" + extension),
            Description = "Added by the user"
        });

        NewExtensionBox.Clear();
        Resequence();
    }

    private void OnRemoveFileType(object sender, RoutedEventArgs e)
    {
        var selected = FileTypeList.SelectedItems.Cast<FileTypeRule>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (var rule in selected)
        {
            _fileTypes.Remove(rule);
        }

        Resequence();
    }

    private void OnRestoreFileTypes(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.ConfirmYesNo(this, "Restore the built-in file type rules? Your edits to this table are lost."))
        {
            return;
        }

        var defaults = new FileTypeRegistry();
        _fileTypes.Clear();
        foreach (var rule in defaults.Rules)
        {
            _fileTypes.Add(CloneRule(rule));
        }

        Resequence();
    }

    private void Resequence()
    {
        var ordered = _fileTypes.OrderBy(rule => rule.Extension, StringComparer.OrdinalIgnoreCase).ToList();
        _fileTypes.Clear();
        foreach (var rule in ordered)
        {
            _fileTypes.Add(rule);
        }

        FileTypeList.Items.Refresh();
    }

    private static FileTypeRule CloneRule(FileTypeRule rule) => new()
    {
        Extension = rule.Extension,
        Action = rule.Action,
        Category = rule.Category,
        Description = rule.Description,
        Folder = rule.Folder
    };

    /// <summary>Points the selected file types at their own folder, or clears that override.</summary>
    private void OnSetFileTypeFolder(object sender, RoutedEventArgs e)
    {
        var selected = FileTypeList.SelectedItems.Cast<FileTypeRule>().ToList();
        if (selected.Count == 0)
        {
            Dialogs.Info(this, "Select one or more file types in the list first.");
            return;
        }

        var current = selected[0].Folder ?? DefaultFolderBox.Text;
        var chosen = Dialogs.PickFolder(current, "Folder for the selected file types");

        if (chosen is null)
        {
            // Cancelling offers to clear an override, which is otherwise impossible to undo.
            if (selected.Any(rule => rule.Folder is not null) &&
                Dialogs.ConfirmYesNo(this, "Clear the per-type folder override for the selected entries?"))
            {
                foreach (var rule in selected)
                {
                    rule.Folder = null;
                }

                FileTypeList.Items.Refresh();
            }

            return;
        }

        foreach (var rule in selected)
        {
            rule.Folder = chosen;
        }

        FileTypeList.Items.Refresh();
    }

    // ---------------------------------------------------------- per-site rules

    private void OnSiteExceptionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SiteExceptionList.SelectedItem is SiteException selected)
        {
            SiteExceptionHostBox.Text = selected.Host;
            SiteExceptionSingleBox.IsChecked = selected.UseSingleConnection;
        }
    }

    private void OnAddSiteException(object sender, RoutedEventArgs e)
    {
        var host = HostMatcher.Normalize(SiteExceptionHostBox.Text);

        if (host.Length == 0)
        {
            Dialogs.Warn(this, "Enter a host, for example files.example.com or .example.com");
            return;
        }

        if (_siteExceptions.Any(entry => string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase)))
        {
            Dialogs.Info(this, $"\"{host}\" is already in the list.");
            return;
        }

        _siteExceptions.Add(new SiteException
        {
            Host = host,
            UseSingleConnection = SiteExceptionSingleBox.IsChecked == true,
            Note = "Added by the user"
        });

        SiteExceptionHostBox.Clear();
        SiteExceptionList.Items.Refresh();
    }

    private void OnRemoveSiteException(object sender, RoutedEventArgs e)
    {
        var selected = SiteExceptionList.SelectedItems.Cast<SiteException>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (var entry in selected)
        {
            _siteExceptions.Remove(entry);
        }

        SiteExceptionList.Items.Refresh();
    }

    // ------------------------------------------------------------ site logins

    private SiteLogin? SelectedLogin => LoginList.SelectedItem as SiteLogin;

    private void OnLoginSelected(object sender, SelectionChangedEventArgs e) => LoadSelectedLogin();

    private void LoadSelectedLogin()
    {
        var login = SelectedLogin;

        LoginServerBox.Text = login?.Server ?? string.Empty;
        LoginUserBox.Text = login?.Username ?? string.Empty;
        LoginPasswordInput.Password = login?.PlainPassword ?? string.Empty;
        LoginAllPathsBox.IsChecked = login?.UseForAllPaths ?? true;
    }

    private void OnNewLogin(object sender, RoutedEventArgs e)
    {
        LoginList.SelectedItem = null;
        LoadSelectedLogin();
        LoginServerBox.Focus();
    }

    private void OnSaveLogin(object sender, RoutedEventArgs e)
    {
        var server = LoginServerBox.Text.Trim();

        if (server.Length == 0)
        {
            Dialogs.Warn(this, "Enter the server this login belongs to, for example: files.example.com");
            return;
        }

        if (LoginUserBox.Text.Trim().Length == 0)
        {
            Dialogs.Warn(this, "Enter the user name for this server.");
            return;
        }

        var login = SelectedLogin;

        if (login is null)
        {
            login = new SiteLogin { Server = server };
            _logins.Add(login);
        }

        login.Server = server;
        login.Username = LoginUserBox.Text.Trim();
        login.PlainPassword = LoginPasswordInput.Password;
        login.UseForAllPaths = LoginAllPathsBox.IsChecked == true;

        LoginList.Items.Refresh();
    }

    private void OnRemoveLogin(object sender, RoutedEventArgs e)
    {
        if (SelectedLogin is not { } login)
        {
            return;
        }

        _logins.Remove(login);
        LoadSelectedLogin();
    }

    private void CloneLoginsFromEngine()
    {
        _logins.Clear();
        foreach (var login in _manager.SiteLogins)
        {
            _logins.Add(CloneLogin(login));
        }
    }

    private void ApplyLoginChanges()
    {
        var kept = _logins.Select(login => login.Id).ToHashSet();

        foreach (var existing in _manager.SiteLogins.Where(login => !kept.Contains(login.Id)).ToList())
        {
            _manager.RemoveSiteLogin(existing);
        }

        foreach (var login in _logins)
        {
            login.PasswordProtected = CredentialProtector.Protect(login.PlainPassword);
            _manager.UpsertSiteLogin(login);
        }
    }

    private static SiteLogin CloneLogin(SiteLogin login) => new()
    {
        Id = login.Id,
        Server = login.Server,
        Realm = login.Realm,
        Username = login.Username,
        PasswordProtected = login.PasswordProtected,
        PlainPassword = login.PlainPassword ?? CredentialProtector.Unprotect(login.PasswordProtected),
        UseForAllPaths = login.UseForAllPaths
    };

    // -------------------------------------------------------------------- browse

    private void OnBrowseDefaultFolder(object sender, RoutedEventArgs e) => Browse(DefaultFolderBox);

    private void OnBrowseFolderVideo(object sender, RoutedEventArgs e) => Browse(FolderVideoBox);

    private void OnBrowseFolderMusic(object sender, RoutedEventArgs e) => Browse(FolderMusicBox);

    private void OnBrowseFolderPrograms(object sender, RoutedEventArgs e) => Browse(FolderProgramsBox);

    private void OnBrowseFolderDocuments(object sender, RoutedEventArgs e) => Browse(FolderDocumentsBox);

    private void OnBrowseFolderCompressed(object sender, RoutedEventArgs e) => Browse(FolderCompressedBox);

    private void OnBrowseFolderOther(object sender, RoutedEventArgs e) => Browse(FolderOtherBox);

    private void Browse(TextBox target)
    {
        var chosen = Dialogs.PickFolder(target.Text, "Select a folder");
        if (chosen is not null)
        {
            target.Text = chosen;
        }
    }

    private void OnBrowseOpenWith(object sender, RoutedEventArgs e)
    {
        var chosen = Dialogs.PickExecutable(OpenWithBox.Text);
        if (chosen is not null)
        {
            OpenWithBox.Text = chosen;
        }
    }

    private void OnBrowseAntivirus(object sender, RoutedEventArgs e)
    {
        var chosen = Dialogs.PickExecutable(AntivirusBox.Text);
        if (chosen is not null)
        {
            AntivirusBox.Text = chosen;
        }
    }

    // --------------------------------------------------------------------- reset

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.ConfirmYesNo(this,
                "Reset every setting and the file type table to their defaults?\n\n" +
                "Your download list and files are not touched."))
        {
            return;
        }

        _settingsService.ResetToDefaults();
        _manager.FileTypes.RestoreDefaults();

        var fresh = _settingsService.Current.Clone();
        CopyInto(_working, fresh);

        _fileTypes.Clear();
        foreach (var rule in _manager.FileTypes.Rules)
        {
            _fileTypes.Add(CloneRule(rule));
        }

        CloneLoginsFromEngine();
        LoadFromSettings();
        Resequence();

        FootNote.Text = "Defaults restored. Choose OK to close.";
    }

    /// <summary>Copies every group from one settings object into another.</summary>
    private static void CopyInto(AppSettings target, AppSettings source)
    {
        target.General = source.General;
        target.Downloads = source.Downloads;
        target.Connection = source.Connection;
        target.Scheduler = source.Scheduler;
        target.Sounds = source.Sounds;
        target.BrowserIntegration = source.BrowserIntegration;
        target.Interface = source.Interface;
        target.Advanced = source.Advanced;
    }
}
