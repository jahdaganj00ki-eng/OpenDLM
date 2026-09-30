using System.Windows;
using System.Windows.Threading;
using OpenDLM.App.Services;
using OpenDLM.App.ViewModels;
using OpenDLM.App.Views;
using OpenDLM.Core.Cli;
using OpenDLM.Core.Ipc;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.App;

/// <summary>
/// Application bootstrap.
///
/// Order matters here: the single-instance check and the command line come first so a
/// second launch never builds a second engine, then the settings decide the theme and
/// the log level, and only then is the window created. Everything is torn down again
/// in <see cref="OnExit"/>, including a final flush of the download list.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private SettingsService? _settingsService;
    private DownloadManager? _manager;
    private IpcServer? _ipcServer;
    private SchedulerService? _scheduler;
    private TrayIconService? _tray;
    private ClipboardMonitor? _clipboardMonitor;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private CliOptions _cli = new();
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _cli = CliOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());

        if (_cli.ShowHelp)
        {
            ConsoleBridge.WriteLine(CliOptions.HelpText, "OpenDLM command line");
            ConsoleBridge.Detach();
            Shutdown();
            return;
        }

        if (_cli.ShowVersion)
        {
            ConsoleBridge.WriteLine($"{AppPaths.ProductName} {AppPaths.Version}", "OpenDLM version");
            ConsoleBridge.Detach();
            Shutdown();
            return;
        }

        foreach (var error in _cli.Errors)
        {
            Log.Warn("Command line: " + error);
        }

        // A second launch must not create a second engine. It forwards its command
        // line to the running instance over the local pipe instead.
        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsFirstInstance)
        {
            ForwardToRunningInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            StartEngine();
        }
        catch (Exception ex)
        {
            Log.Error("OpenDLM could not start.", ex);
            Dialogs.Error(null,
                "OpenDLM could not start.\n\n" + ex.Message +
                "\n\nA log was written to:\n" + AppPaths.LogFile);
            Shutdown();
        }
    }

    private void StartEngine()
    {
        _settingsService = new SettingsService();
        Log.Enabled = _settingsService.Current.Advanced.EnableLogging;
        Log.Info($"OpenDLM {AppPaths.Version} starting (portable: {AppPaths.PortableRoot is not null}).");

        ThemeManager.Apply(_settingsService.Current.Interface.Theme);

        // Publish the install path so the browser host can launch the app by itself.
        ShellIntegration.RegisterInstallPath();

        var fileTypes = new FileTypeRegistry();
        var store = new DownloadStore();

        _manager = new DownloadManager(_settingsService, fileTypes, store);
        _manager.Governor.ApplyLimit(
            _settingsService.Current.Connection.SpeedLimitEnabled,
            _settingsService.Current.Connection.SpeedLimitKbPerSecond);

        ShellIntegration.SetStartWithWindows(_settingsService.Current.General.StartWithWindows);

        _viewModel = new MainViewModel(_manager, _settingsService);
        _viewModel.ExitRequested += (_, _) => ExitApplication();
        _viewModel.DownloadFinished += OnDownloadFinished;

        _manager.ItemFailed += (_, item) =>
        {
            if (_settingsService.Current.Sounds.NotifyOnError)
            {
                Dispatcher.BeginInvoke(() => _tray?.Notify("Download failed", item.FileName, isError: true));
            }
        };

        _mainWindow = new MainWindow(_viewModel, _settingsService);
        MainWindow = _mainWindow;

        var startHidden = _cli.Silent
                          || _cli.StartMinimized
                          || _settingsService.Current.General.StartMinimized;

        if (startHidden)
        {
            _mainWindow.HideToTray(showBalloon: false);
        }
        else
        {
            _mainWindow.Show();
            _mainWindow.Activate();
        }

        _tray = new TrayIconService(
            showWindow: () => _mainWindow.ShowFromTray(),
            addUrl: () => _viewModel.AddUrlCommand.Execute(null),
            options: () => _viewModel.OptionsCommand.Execute(null),
            exit: ExitApplication);

        _clipboardMonitor = new ClipboardMonitor
        {
            Enabled = _settingsService.Current.General.ClipboardMonitoring,
            Accept = ShouldAcceptClipboardUrl
        };
        _clipboardMonitor.UrlDetected += OnClipboardUrlDetected;
        _clipboardMonitor.Start();

        StartBrowserBridge();
        StartScheduler();

        // Kick off anything that came in on the command line.
        foreach (var request in _cli.ExpandBatchFile())
        {
            _ = _manager.AddAndStartAsync(request);
        }

        if (_cli.ExitAfterDownload)
        {
            _settingsService.Update(settings => settings.Scheduler.ExitWhenQueueFinished = true);
        }

        Log.Info("OpenDLM is ready.");
    }

    private void StartBrowserBridge()
    {
        if (_manager is null || _settingsService is null)
        {
            return;
        }

        _ipcServer = new IpcServer(_manager, _settingsService)
        {
            ConfirmAddRequest = ConfirmBrowserAdd
        };

        _ipcServer.ActivateRequested += (_, _) => _mainWindow?.ShowFromTray();
        _ipcServer.SettingsChangedByExtension += (_, _) => _viewModel?.ApplySettingsSideEffects();
        _ipcServer.Message += (_, message) => Log.Info("Browser bridge: " + message);
        _ipcServer.Start();
    }

    private void StartScheduler()
    {
        if (_manager is null || _settingsService is null)
        {
            return;
        }

        _scheduler = new SchedulerService(_manager, _settingsService);
        _scheduler.ActionRequired += OnScheduledAction;
        _scheduler.DailyLimitReached += OnDailyLimitReached;
    }

    private void OnDailyLimitReached(object? sender, string message)
        => Dispatcher.BeginInvoke(() => _tray?.Notify("Daily download limit reached", message));

    // -------------------------------------------------------------- browser bridge

    /// <summary>
    /// Called on the pipe's own thread while the extension waits for an answer, so the
    /// dialog has to be shown through a blocking dispatcher call.
    /// </summary>
    private bool ConfirmBrowserAdd(AddDownloadRequest request)
    {
        if (_manager is null || _settingsService is null || _mainWindow is null)
        {
            return true;
        }

        if (!_settingsService.Current.General.ShowStartDialog)
        {
            return true;
        }

        return Dispatcher.Invoke(() =>
        {
            var dialog = new AddUrlWindow(_manager, _settingsService, request)
            {
                Owner = _mainWindow.IsVisible ? _mainWindow : null
            };

            // The dialog edits the request in place, then the bridge enqueues it.
            return dialog.ShowDialog() == true;
        });
    }

    private bool ShouldAcceptClipboardUrl(string url)
    {
        if (_settingsService is null)
        {
            return false;
        }

        var settings = _settingsService.Current;
        if (!settings.General.ClipboardMonitoring)
        {
            return false;
        }

        // Never react to something already in the list.
        return _manager?.FindByUrl(url) is null;
    }

    private void OnClipboardUrlDetected(object? sender, string url)
    {
        if (_manager is null || _settingsService is null || _mainWindow is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            var request = new AddDownloadRequest
            {
                Url = url,
                Description = "Copied to the clipboard",
                StartNow = !_settingsService.Current.Downloads.AddToQueueByDefault,
                AddToQueue = _settingsService.Current.Downloads.AddToQueueByDefault,
                QueueId = _settingsService.Current.Downloads.DefaultQueueId
            };

            if (_settingsService.Current.Downloads.AddToQueueByDefault)
            {
                _ = _manager.AddAndStartAsync(request);
                _tray?.Notify("OpenDLM", "Added to the queue: " + Fmt.Ellipsis(url, 60));
                return;
            }

            var dialog = new AddUrlWindow(_manager, _settingsService, request)
            {
                Owner = _mainWindow.IsVisible ? _mainWindow : null
            };

            if (dialog.ShowDialog() == true)
            {
                _ = _manager.AddAndStartAsync(request);
            }
        });
    }

    // --------------------------------------------------------------- notifications

    private void OnDownloadFinished(object? sender, DownloadItem item)
    {
        var settings = _settingsService?.Current;
        if (settings is null)
        {
            return;
        }

        if (settings.Sounds.NotifyOnComplete)
        {
            _tray?.Notify("Download complete", item.FileName);
        }

        PostDownloadActions.Run(settings.Downloads.PostDownloadAction, item, settings, _mainWindow);

        _viewModel?.RefreshCounts();
    }

    private void OnScheduledAction(object? sender, ScheduledEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Log.Info($"Scheduler requested: {e.Action} ({e.Reason}).");

            switch (e.Action)
            {
                case ScheduledAction.StartQueue:
                    _ = _manager?.StartAllAsync();
                    if (_settingsService?.Current.Sounds.NotifyOnQueueStart == true)
                    {
                        _tray?.Notify("OpenDLM scheduler", e.Reason);
                    }
                    break;

                case ScheduledAction.StopQueue:
                    _manager?.StopAll();
                    if (_settingsService?.Current.Sounds.NotifyOnQueueFinish == true)
                    {
                        _tray?.Notify("OpenDLM scheduler", e.Reason);
                    }
                    break;

                case ScheduledAction.ExitApplication:
                    _tray?.Notify("OpenDLM scheduler", "All downloads finished. Exiting.");
                    ExitApplication();
                    break;

                case ScheduledAction.ShutdownComputer:
                    _tray?.Notify("OpenDLM scheduler", "All downloads finished. Shutting down.");
                    PostDownloadActions.ScheduleShutdown(
                        Math.Max(30, _settingsService?.Current.Scheduler.FinishedActionDelaySeconds ?? 60));
                    break;

                case ScheduledAction.DisconnectDialUp:
                    PostDownloadActions.DisconnectDialUp();
                    break;
            }
        });
    }

    // --------------------------------------------------------------------- exiting

    private void ExitApplication()
    {
        if (_exiting || _settingsService is null)
        {
            return;
        }

        if (_settingsService.Current.General.ConfirmOnExit)
        {
            var active = _manager?.HasActiveDownloads == true;
            var message = active
                ? "Exit OpenDLM?\n\nDownloads that are still running keep their progress " +
                  "and can be resumed the next time OpenDLM starts."
                : "Exit OpenDLM?";

            if (!Dialogs.ConfirmYesNo(_mainWindow, message))
            {
                return;
            }
        }

        _exiting = true;
        Shutdown();
    }

    private void ForwardToRunningInstance()
    {
        try
        {
            var requests = _cli.ExpandBatchFile();

            if (requests.Count == 0)
            {
                // Plain second launch: just ask the running window to come forward.
                _ = IpcClient.SendAsync(new System.Text.Json.Nodes.JsonObject { ["type"] = "activate" });
                return;
            }

            foreach (var request in requests)
            {
                var message = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "addDownload",
                    ["url"] = request.Url,
                    ["startNow"] = request.StartNow,
                    ["addToQueue"] = request.AddToQueue
                };

                if (!string.IsNullOrWhiteSpace(request.FileName))
                {
                    message["filename"] = request.FileName;
                }
                if (!string.IsNullOrWhiteSpace(request.Directory))
                {
                    message["description"] = request.Directory;
                }
                if (!string.IsNullOrWhiteSpace(request.Description))
                {
                    message["description"] = request.Description;
                }

                IpcClient.SendAsync(message).GetAwaiter().GetResult();
            }

            ConsoleBridge.WriteLine($"Forwarded {requests.Count} download(s) to the running OpenDLM.");
            ConsoleBridge.Detach();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not forward the command line to the running instance: " + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Info("OpenDLM is shutting down.");

            _clipboardMonitor?.Dispose();
            _ipcServer?.Dispose();
            _scheduler?.Dispose();
            _tray?.Dispose();
            _viewModel?.Dispose();

            // Persist last so the final segment positions are on disk.
            _manager?.Dispose();
            _singleInstance?.Dispose();
            ConsoleBridge.Detach();
        }
        catch (Exception ex)
        {
            Log.Warn("Error during shutdown: " + ex.Message);
        }

        base.OnExit(e);
    }

    // ------------------------------------------------------------ error reporting

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("An unhandled UI exception occurred.", e.Exception);

        // Recover from a UI fault rather than killing the download engine with it.
        e.Handled = true;

        Dialogs.Error(_mainWindow,
            "Something went wrong in the interface.\n\n" + e.Exception.Message +
            "\n\nDownloads keep running. A log was written to:\n" + AppPaths.LogFile);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error("An unhandled exception occurred.", e.ExceptionObject as Exception);
    }
}
