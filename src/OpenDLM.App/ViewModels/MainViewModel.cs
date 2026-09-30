using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using OpenDLM.App.Services;
using OpenDLM.Core.Cli;
using OpenDLM.Core.Http;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.App.ViewModels;

/// <summary>
/// Everything the main window binds to: the download list, the category tree, the
/// status bar text and the commands behind the menus and the toolbar.
///
/// The view model deliberately creates its own dialogs. With a single window and a
/// handful of modal dialogs, a service locator would add indirection without buying
/// anything testable — the engine, which is where the logic lives, is already fully
/// covered by the test suite.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DownloadManager _manager;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _statusTimer;
    private readonly DownloadSearcher _searcher;

    /// <summary>One progress window per running download, keyed by item id.</summary>
    private readonly Dictionary<Guid, Views.ProgressWindow> _progressWindows = new();

    /// <summary>Guards against a queue of completions piling up dialogs.</summary>
    private bool _completeDialogOpen;

    private DownloadItem? _selectedItem;
    private CategoryNode? _selectedCategory;
    private string _searchText = string.Empty;
    private string _statusText = "Ready";
    private bool _disposed;

    public MainViewModel(DownloadManager manager, SettingsService settingsService)
    {
        _manager = manager;
        _settingsService = settingsService;
        _searcher = new DownloadSearcher(manager);

        Items = new ObservableCollection<DownloadItem>(manager.Snapshot());
        Categories = CategoryNode.BuildDefaultTree();

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;

        _selectedCategory = Categories.FirstOrDefault();

        _manager.ItemAdded += OnItemAdded;
        _manager.ItemRemoved += OnItemRemoved;
        _manager.ItemStatusChanged += OnItemStatusChanged;
        _manager.ItemCompleted += OnItemCompleted;
        _manager.ItemStarted += OnItemStarted;
        _manager.ItemFailed += OnItemFailed;
        _manager.Message += OnManagerMessage;
        _manager.CredentialsRequired += OnCredentialsRequired;

        AddUrlCommand = new RelayCommand(AddUrl);
        OptionsCommand = new RelayCommand(ShowOptions);
        SchedulerCommand = new RelayCommand(ShowScheduler);
        AboutCommand = new RelayCommand(ShowAbout);
        ShowLogCommand = new RelayCommand(ShowLog);
        ExitCommand = new RelayCommand(RequestExit);

        StartCommand = new RelayCommand(StartSelected, () => SelectedItem is { } item && item.CanStart);
        PauseCommand = new RelayCommand(PauseSelected, () => SelectedItem?.CanPause == true);
        StopCommand = new RelayCommand(StopSelected, () => SelectedItem?.CanStop == true);
        RestartCommand = new RelayCommand(RestartSelected, () => SelectedItem is not null);
        RemoveCommand = new RelayCommand(RemoveSelected, () => SelectedItem is not null);
        RemoveCompletedCommand = new RelayCommand(RemoveCompleted, () => Items.Any(i => i.IsFinished));
        OpenFileCommand = new RelayCommand(OpenSelectedFile, () => SelectedItem?.IsFinished == true);
        OpenFolderCommand = new RelayCommand(OpenSelectedFolder, () => SelectedItem is not null);
        CopyUrlCommand = new RelayCommand(CopySelectedUrl, () => SelectedItem is not null);
        MoveUpCommand = new RelayCommand(() => Move(SelectedItem, -1), () => SelectedItem is not null);
        MoveDownCommand = new RelayCommand(() => Move(SelectedItem, 1), () => SelectedItem is not null);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        AddAllFromFileCommand = new RelayCommand(AddFromBatchFile);

        FindCommand = new RelayCommand(ShowFind);
        ImportCommand = new RelayCommand(ImportDownloads);
        CleanUpCommand = new RelayCommand(CleanUp, () => Items.Any(i => i.IsFinished));
        RecoverCommand = new RelayCommand(RecoverInterrupted);
        DialUpCommand = new RelayCommand(ShowDialUp);
        ExportCommand = new RelayCommand(ExportDownloads);
        LoadNowCommand = new RelayCommand(LoadNow, () => SelectedItem is not null);

        StartAllCommand = new RelayCommand(() => _ = StartAllAsync(), () => Items.Any(i => i.CanStart));
        StopAllCommand = new RelayCommand(StopAll, () => _manager.HasActiveDownloads);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        RefreshCounts();
        UpdateStatus();

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(250, _settingsService.Current.Interface.RefreshIntervalMs))
        };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
    }

    // ---------------------------------------------------------------- collections

    public ObservableCollection<DownloadItem> Items { get; }

    public ICollectionView ItemsView { get; }

    public ObservableCollection<CategoryNode> Categories { get; }

    // -------------------------------------------------------------------- selection

    public DownloadItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value))
            {
                return;
            }

            Raise(nameof(SelectionSummary));
            Raise(nameof(HasSelection));
            RefreshCommandStates();
        }
    }

    public bool HasSelection => _selectedItem is not null;

    public CategoryNode? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (Set(ref _selectedCategory, value))
            {
                ItemsView.Refresh();
                Raise(nameof(CategorySummary));
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty))
            {
                ItemsView.Refresh();
                Raise(nameof(CategorySummary));
            }
        }
    }

    // ------------------------------------------------------------------ status bar

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string TotalSpeedText
    {
        get
        {
            var active = Items.Count(i => i.IsActive);
            if (active == 0)
            {
                return "No active downloads";
            }

            var speed = _manager.TotalSpeed;
            return $"{active} active - {Fmt.Speed(speed)}";
        }
    }

    public string CategorySummary
    {
        get
        {
            var total = ItemsView.Cast<object>().Count();
            var size = Items.Where(PassesFilter).Sum(i => Math.Max(0, i.TotalBytes));
            return size > 0
                ? $"{total} item(s), {Fmt.Bytes(size)}"
                : $"{total} item(s)";
        }
    }

    public string SelectionSummary
    {
        get
        {
            if (_selectedItem is not { } item)
            {
                return "No download selected";
            }

            var parts = new List<string> { item.FileName, item.StatusText };
            if (item.TotalBytes > 0)
            {
                parts.Add(item.DownloadedWithTotalText);
                parts.Add($"{item.Progress:0.0} %");
            }
            if (item.IsActive)
            {
                parts.Add(item.SpeedText);
                parts.Add("left " + item.TimeLeftText);
            }
            if (item.Connections > 1)
            {
                parts.Add(item.ConnectionsText + " connections");
            }

            return string.Join("  |  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    // -------------------------------------------------------------------- commands

    public RelayCommand AddUrlCommand { get; }
    public RelayCommand AddAllFromFileCommand { get; }
    public RelayCommand OptionsCommand { get; }
    public RelayCommand SchedulerCommand { get; }
    public RelayCommand AboutCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand RemoveCompletedCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand StartAllCommand { get; }
    public RelayCommand StopAllCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }

    public RelayCommand FindCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CleanUpCommand { get; }
    public RelayCommand RecoverCommand { get; }
    public RelayCommand DialUpCommand { get; }
    public RelayCommand LoadNowCommand { get; }

    /// <summary>Raised when the user asks to close the window, so the view can decide how.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when a download finished, so the view can run the post-download action.</summary>
    public event EventHandler<DownloadItem>? DownloadFinished;

    /// <summary>Raised when the theme setting changed and the view should re-apply it.</summary>
    public event EventHandler? ThemeChanged;

    public bool AreCategoryFoldersEnabled => _settingsService.Current.Downloads.UseCategoryFolders;

    /// <summary>True while at least one download occupies a slot.</summary>
    public bool HasActiveDownload => _manager.HasActiveDownloads;

    /// <summary>Combined throughput of every running download, in bytes per second.</summary>
    public double CurrentTotalSpeed => _manager.TotalSpeed;

    /// <summary>Adds a request and starts it. Used by drag and drop and by the dialog.</summary>
    public async Task AddRequestAsync(AddDownloadRequest request)
        => await _manager.AddAndStartAsync(request).ConfigureAwait(true);

    // ------------------------------------------------------------------ list logic

    private bool PassesFilter(DownloadItem item)
    {
        var categoryFilter = _selectedCategory?.Filter;
        if (categoryFilter is not null && !categoryFilter(item))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_searchText))
        {
            return true;
        }

        var needle = _searchText.Trim();
        return Contains(item.FileName, needle)
               || Contains(item.Url, needle)
               || Contains(item.Description, needle)
               || Contains(item.Directory, needle);

        static bool Contains(string? haystack, string needle)
            => !string.IsNullOrEmpty(haystack) &&
               haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterItem(object candidate) => candidate is DownloadItem item && PassesFilter(item);

    /// <summary>Recomputes the "(n)" counts shown in the category tree.</summary>
    public void RefreshCounts()
    {
        foreach (var category in Categories)
        {
            if (category.Filter is null)
            {
                category.Count = Items.Count;
            }
            else
            {
                category.Count = Items.Count(category.Filter);
            }

            foreach (var child in category.Children)
            {
                child.Count = child.Filter is null ? Items.Count : Items.Count(child.Filter);
            }
        }
    }

    // ------------------------------------------------------------------- mutations

    private void AddUrl()
    {
        var window = new Views.AddUrlWindow(_manager, _settingsService)
        {
            Owner = Application.Current?.MainWindow
        };

        if (window.ShowDialog() == true)
        {
            StatusText = window.ResultMessage ?? "Download added";
        }
    }

    private void ShowFind()
    {
        var settings = _settingsService.Current.Search;
        var window = new Views.FindWindow(_searcher, settings)
        {
            Owner = Application.Current?.MainWindow
        };

        window.Show();

        if (window.Found is { } found)
        {
            SelectedItem = found;
        }
    }

    private void ShowDialUp()
    {
        var settings = _settingsService.Current;
        var window = new Views.DialUpWindow(settings.DialUp)
        {
            Owner = Application.Current?.MainWindow
        };

        window.Show();

        if (window.Saved)
        {
            _settingsService.Update(current => current.DialUp = window.Result);
            StatusText = "Dial-up / VPN settings saved";
        }
    }

    private void ExportDownloads()
    {
        var items = Snapshot();
        if (items.Count == 0)
        {
            Dialogs.Info(Application.Current?.MainWindow, "There is nothing to export.");
            return;
        }

        var target = Dialogs.PickSaveLocation(
            $"opendlm-{DateTime.Now:yyyyMMdd-HHmm}.dlm",
            AppPaths.RoamingRoot);

        if (target is null)
        {
            return;
        }

        try
        {
            if (target.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                DownloadTransfer.ExportUrls(target, items);
            }
            else
            {
                DownloadTransfer.Export(target, items);
            }

            StatusText = $"Exported {items.Count} download(s)";
        }
        catch (Exception ex)
        {
            Dialogs.Error(Application.Current?.MainWindow, "The export failed:\n" + ex.Message);
        }
    }

    private void ImportDownloads()
    {
        var file = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a download list",
            Filter = "OpenDLM list (*.dlm)|*.dlm|Text file (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (file.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var result = DownloadTransfer.Import(file.FileName, url => new AddDownloadRequest
            {
                Url = url,
                Description = "Imported"
            });

            if (result.Requests.Count == 0)
            {
                Dialogs.Warn(Application.Current?.MainWindow, "The file did not contain any usable addresses.");
                return;
            }

            var added = 0;
            foreach (var request in result.Requests)
            {
                var item = _manager.CreateItem(request);
                _manager.Add(item);
                added++;
            }

            var skipped = result.SkippedLines > 0 ? $" {result.SkippedLines} line(s) skipped." : string.Empty;
            StatusText = $"Imported {added} download(s).{skipped}";
        }
        catch (Exception ex)
        {
            Dialogs.Error(Application.Current?.MainWindow, "The import failed:\n" + ex.Message);
        }
    }

    private void CleanUp()
    {
        var removed = _manager.CleanUp();
        StatusText = removed == 0
            ? "There was nothing to clean up"
            : $"Cleaned up {removed} download(s)";
    }

    private void RecoverInterrupted()
    {
        var recovered = _manager.RecoverInterrupted();
        RefreshCounts();

        StatusText = recovered.Count == 0
            ? "No interrupted downloads were found"
            : $"Recovered {recovered.Count} interrupted download(s)";
    }

    private void LoadNow()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        _ = _manager.StartFirstSegmentOnlyAsync(item);
        StatusText = $"Loading the first block of {item.FileName}";
    }

    private void AddFromBatchFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a text file with one URL per line",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var options = new CliOptions { BatchFile = dialog.FileName };
        var requests = options.ExpandBatchFile();

        if (requests.Count == 0)
        {
            Dialogs.Warn(Application.Current?.MainWindow, "That file did not contain any usable URLs.");
            return;
        }

        foreach (var request in requests)
        {
            _ = _manager.AddAndStartAsync(request);
        }

        StatusText = $"Queued {requests.Count} download(s) from {Path.GetFileName(dialog.FileName)}";
    }

    private void ShowOptions()
    {
        var window = new Views.OptionsWindow(_manager, _settingsService)
        {
            Owner = Application.Current?.MainWindow
        };

        window.ShowDialog();

        if (window.SettingsChanged)
        {
            _settingsService.Save();
            ApplySettingsSideEffects();
            StatusText = "Settings saved";
        }
    }

    /// <summary>Pushes settings that are not read live into the runtime.</summary>
    public void ApplySettingsSideEffects()
    {
        var settings = _settingsService.Current;

        _manager.Governor.ApplyLimit(
            settings.Connection.SpeedLimitEnabled,
            settings.Connection.SpeedLimitKbPerSecond);

        ShellIntegration.SetStartWithWindows(settings.General.StartWithWindows);
        Log.Enabled = settings.Advanced.EnableLogging;

        Raise(nameof(AreCategoryFoldersEnabled));
        ItemsView.Refresh();
        RefreshCounts();
    }

    private void ShowScheduler()
    {
        var window = new Views.SchedulerWindow(_manager, _settingsService)
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
        StatusText = "Scheduler updated";
    }

    private void ShowAbout()
    {
        var window = new Views.AboutWindow { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
    }

    private void ShowLog()
    {
        var window = new Views.TextWindow(
            "OpenDLM log",
            Log.Read(800),
            AppPaths.LogFile)
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
    }

    private void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void ToggleTheme()
    {
        var next = ThemeManager.EffectiveTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _settingsService.Update(settings => settings.Interface.Theme = next);
        ThemeManager.Apply(next);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
        StatusText = $"Switched to the {next.ToString().ToLowerInvariant()} theme";
    }

    // ------------------------------------------------------------- command bodies

    private void StartSelected()
    {
        if (SelectedItem is { } item)
        {
            _ = _manager.StartAsync(item);
            StatusText = $"Starting {item.FileName}";
        }
    }

    private void PauseSelected()
    {
        if (SelectedItem is { } item)
        {
            _manager.Pause(item);
            StatusText = $"Pausing {item.FileName}";
        }
    }

    private void StopSelected()
    {
        if (SelectedItem is { } item)
        {
            _manager.Stop(item);
            StatusText = $"Stopping {item.FileName}";
        }
    }

    private async void RestartSelected()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        if (Dialogs.ConfirmYesNo(Application.Current?.MainWindow,
                $"Restart '{item.FileName}' from the beginning?\n\n" +
                "Any bytes already downloaded for it are discarded."))
        {
            await _manager.RestartAsync(item);
            RefreshCounts();
        }
    }

    private void RemoveSelected()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var deleteFile = false;
        if (item.Status is DownloadStatus.Complete or DownloadStatus.Paused or DownloadStatus.Stopped)
        {
            deleteFile = Dialogs.Confirm(Application.Current?.MainWindow,
                $"Also delete '{item.FileName}' from the disk?\n\n" +
                "Choose No to remove it from the list only.");
        }

        _manager.Remove(item, deleteFile);
        StatusText = $"Removed {item.FileName}";
    }

    private void RemoveCompleted()
    {
        var deleteFiles = Dialogs.Confirm(Application.Current?.MainWindow,
            "Delete the finished files from the disk as well?\n\n" +
            "Choose No to clear the list only.");

        _manager.RemoveCompleted(deleteFiles);
        StatusText = "Cleared finished downloads";
    }

    private void OpenSelectedFile()
    {
        if (SelectedItem is { } item)
        {
            PostDownloadActions.OpenFile(item.FullPath, Application.Current?.MainWindow);
        }
    }

    private void OpenSelectedFolder()
    {
        if (SelectedItem is { } item)
        {
            PostDownloadActions.RevealInExplorer(item.FullPath);
        }
    }

    private void CopySelectedUrl()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        try
        {
            Clipboard.SetText(item.Url);
            StatusText = "URL copied to the clipboard";
        }
        catch (Exception ex)
        {
            Log.Warn("Could not copy the URL: " + ex.Message);
        }
    }

    private void Move(DownloadItem? item, int delta)
    {
        if (item is null)
        {
            return;
        }

        var index = Items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Items.Count)
        {
            return;
        }

        Items.Move(index, target);
    }

    private async Task StartAllAsync()
    {
        await _manager.StartAllAsync();
        StatusText = "Starting all downloads";
    }

    private void StopAll()
    {
        _manager.StopAll();
        StatusText = "Stopping all downloads";
    }

    // ------------------------------------------------------------------- engine glue

    private void OnItemAdded(object? sender, DownloadItem item) => OnDispatcher(() =>
    {
        if (!Items.Contains(item))
        {
            Items.Insert(0, item);
        }
        RefreshCounts();
        ItemsView.Refresh();
    });

    private void OnItemRemoved(object? sender, DownloadItem item) => OnDispatcher(() =>
    {
        Items.Remove(item);
        if (ReferenceEquals(_selectedItem, item))
        {
            SelectedItem = null;
        }
        RefreshCounts();
    });

    private void OnItemStatusChanged(object? sender, DownloadItem item) => OnDispatcher(() =>
    {
        RefreshCommandStates();
        Raise(nameof(SelectionSummary));
    });

    private void OnItemCompleted(object? sender, DownloadItem item) => OnDispatcher(() =>
    {
        CloseProgressWindow(item);
        RefreshCounts();
        DownloadFinished?.Invoke(this, item);
        ShowCompleteDialogIfEnabled(item);
    });

    /// <summary>Opens a progress window for a download the engine just started.</summary>
    private void OnItemStarted(object? sender, DownloadItem item) => OnDispatcher(() =>
    {
        if (_settingsService.Current.Downloads.ProgressDialog == ProgressDialogMode.Hidden)
        {
            return;
        }

        ShowProgressWindow(item);
    });

    private void ShowProgressWindow(DownloadItem item)
    {
        if (_progressWindows.ContainsKey(item.Id))
        {
            return;
        }

        try
        {
            var window = new Views.ProgressWindow(item, _manager, _settingsService);
            window.Closed += (_, _) => _progressWindows.Remove(item.Id);

            _progressWindows[item.Id] = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open the progress window for '{item.FileName}'.", ex);
        }
    }

    private void CloseProgressWindow(DownloadItem item)
    {
        if (!_progressWindows.Remove(item.Id, out var window))
        {
            return;
        }

        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not close a progress window: " + ex.Message);
        }
    }

    /// <summary>
    /// Shows the completion dialog, at most one at a time so a queue of finished
    /// downloads does not stack a wall of dialogs. The owner is only attached when
    /// the main window is actually visible, because an owned window whose owner is
    /// hidden would stay hidden itself.
    /// </summary>
    private void ShowCompleteDialogIfEnabled(DownloadItem item)
    {
        if (!_settingsService.Current.General.ShowCompleteDialog || _completeDialogOpen)
        {
            return;
        }

        try
        {
            var owner = Application.Current?.MainWindow;

            var window = new Views.DownloadCompleteWindow(item);
            if (owner is { IsVisible: true })
            {
                window.Owner = owner;
            }

            _completeDialogOpen = true;
            window.Closed += (_, _) => _completeDialogOpen = false;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open the download complete dialog for '{item.FileName}'.", ex);
        }
    }

    private void OnItemFailed(object? sender, DownloadItem item) => OnDispatcher(() =>
    {
        RefreshCounts();
        StatusText = $"{item.FileName} failed: {item.ErrorMessage}";
    });

    private void OnManagerMessage(object? sender, string message) => OnDispatcher(() => StatusText = message);

    /// <summary>
    /// The engine raises this from a background thread and waits for the answer, so
    /// the dialog has to be shown with a blocking dispatcher call.
    /// </summary>
    private void OnCredentialsRequired(object? sender, CredentialsRequiredEventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            ShowCredentialsDialog(args);
        }
        else
        {
            dispatcher.Invoke(() => ShowCredentialsDialog(args));
        }
    }

    private void ShowCredentialsDialog(CredentialsRequiredEventArgs args)
    {
        var window = new Views.CredentialsWindow(args.Host, args.Item.Url)
        {
            Owner = Application.Current?.MainWindow
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        args.CredentialsSupplied = true;
        args.Username = window.UserName;
        args.Password = window.Password;
        args.Remember = window.Remember;
        StatusText = $"Credentials supplied for {args.Host}";
    }

    private void OnDispatcher(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    // ------------------------------------------------------------------- housekeeping

    private void UpdateStatus()
    {
        Raise(nameof(TotalSpeedText));

        // Per-item speed and time-left text are refreshed by the engine's own tick,
        // so nothing needs to be poked here.

        if (!_manager.HasActiveDownloads)
        {
            var finished = Items.Count(i => i.IsFinished);
            if (finished > 0 && StatusText.StartsWith("Ready", StringComparison.Ordinal))
            {
                StatusText = $"{finished} finished";
            }
        }
    }

    public void RefreshCommandStates()
    {
        StartCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        RemoveCompletedCommand.RaiseCanExecuteChanged();
        OpenFileCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        CopyUrlCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        StartAllCommand.RaiseCanExecuteChanged();
        StopAllCommand.RaiseCanExecuteChanged();
        LoadNowCommand.RaiseCanExecuteChanged();
        CleanUpCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _statusTimer.Stop();

        _manager.ItemAdded -= OnItemAdded;
        _manager.ItemRemoved -= OnItemRemoved;
        _manager.ItemStatusChanged -= OnItemStatusChanged;
        _manager.ItemCompleted -= OnItemCompleted;
        _manager.ItemStarted -= OnItemStarted;
        _manager.ItemFailed -= OnItemFailed;
        _manager.Message -= OnManagerMessage;
        _manager.CredentialsRequired -= OnCredentialsRequired;

        foreach (var window in _progressWindows.Values.ToList())
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not close a progress window during shutdown: " + ex.Message);
            }
        }

        _progressWindows.Clear();
    }
}
