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

    /// <summary>True while <c>--selftest</c> is running: never show a dialog, only report.</summary>
    private bool _selfTestMode;

    /// <summary>Self test failures, shared with the global exception handlers.</summary>
    private List<string>? _selfTestFailures;

    private bool _selfTestReported;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Registered before anything else, including the self test, so a failure
        // while the application is being assembled is reported rather than taking
        // the process down with no output at all.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // The self test runs before anything else, including the single-instance
        // guard: CI must be able to verify a build while a normal copy is running.
        if (Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest();
            return;
        }

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

    /// <summary>
    /// Constructs the whole application and renders its windows, then exits.
    ///
    /// A WPF application compiles happily with a missing resource key or a bad
    /// template and only fails when the window is first shown, so a green build
    /// alone proves very little about the GUI. This is the check that closes that
    /// gap: CI runs <c>OpenDLM.exe --selftest</c> and the exit code decides.
    /// </summary>
    private void RunSelfTest()
    {
        _selfTestMode = true;
        var failures = _selfTestFailures = new List<string>();

        try
        {
            RunSelfTestChecks(failures);
        }
        catch (Exception ex)
        {
            failures.Add($"unhandled: {ex.GetType().Name}: {ex.Message} {ex.StackTrace}");
        }

        ReportSelfTest(failures);
    }

    private void RunSelfTestChecks(List<string> failures)
    {
        void Check(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Check("settings service", () =>
        {
            var service = new SettingsService();
            service.Update(settings => settings.General.TakeOverModifier = "Alt");
            settingsServiceForWindows = service;
        });

        Check("file type registry", () => new FileTypeRegistry());
        Check("download store", () => new DownloadStore());
        Check("notification icon", () =>
        {
            using var icon = AppIcons.CreateTrayIcon(32);
            if (icon.Handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("The tray icon had no handle.");
            }

            var source = AppIcons.CreateImageSource(32);
            if (source.IsFrozen == false && source.CanFreeze)
            {
                source.Freeze();
            }
        });

        if (settingsServiceForWindows is null)
        {
            return;
        }

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.System })
        {
            Check($"theme {theme}", () => ThemeManager.Apply(theme));
        }

        Check("resources", () =>
        {
            foreach (var key in SelfTestResourceKeys)
            {
                if (TryFindResource(key) is null)
                {
                    failures.Add($"resource missing: {key}");
                }
            }
        });

        DownloadManager? manager = null;
        Check("engine", () =>
        {
            manager = new DownloadManager(settingsServiceForWindows!, new FileTypeRegistry(), new DownloadStore());
        });

        if (manager is null)
        {
            return;
        }

        var engine = manager;
        var settings = settingsServiceForWindows!;

        // Showing each window is what actually resolves DynamicResource, so merely
        // constructing them would pass even with a broken palette.
        Check("main window", () =>
        {
            var viewModel = new MainViewModel(engine, settings);
            var window = new MainWindow(viewModel, settings) { DataContext = viewModel };
            window.Show();
            window.UpdateLayout();
            window.Close();
            viewModel.Dispose();
        });

        Check("add URL dialog", () =>
        {
            var window = new AddUrlWindow(engine, settings) { Owner = null };
            window.Show();
            window.Close();
        });

        Check("options dialog", () =>
        {
            var window = new OptionsWindow(engine, settings) { Owner = null };
            window.Show();
            window.UpdateLayout();
            window.Close();
        });

        Check("scheduler dialog", () =>
        {
            var window = new SchedulerWindow(engine, settings) { Owner = null };
            window.Show();
            window.Close();
        });

        Check("about dialog", () =>
        {
            var window = new AboutWindow { Owner = null };
            window.Show();
            window.Close();
        });

        Check("text dialog", () =>
        {
            var window = new TextWindow("Self test", "sample text", AppPaths.LogFile) { Owner = null };
            window.Show();
            window.Close();
        });

        Check("credentials dialog", () =>
        {
            var window = new CredentialsWindow("example.com", "https://example.com/file.zip") { Owner = null };
            window.Show();
            window.Close();
        });

        Check("duplicate prompt dialog", () =>
        {
            var window = new DuplicatePromptWindow("https://example.com/a.zip", "Complete", true) { Owner = null };
            window.Show();
            window.Close();
        });

        Check("progress and complete dialogs", () =>
        {
            var item = engine.CreateItem(new AddDownloadRequest
            {
                Url = "https://example.com/files/archive.zip",
                FileName = "archive.zip",
                Directory = AppPaths.TempDirectory
            });
            item.TotalBytes = 1000;
            item.DownloadedBytes = 400;
            item.Status = DownloadStatus.Downloading;

            var progress = new ProgressWindow(item, engine, settings) { Owner = null };
            progress.Show();
            progress.UpdateLayout();
            progress.Close();

            var complete = new DownloadCompleteWindow(item) { Owner = null };
            complete.Show();
            complete.Close();
        });

        Check("find dialog", () =>
        {
            var searcher = new OpenDLM.Core.Services.DownloadSearcher(engine);
            var window = new FindWindow(searcher, new OpenDLM.Core.Models.SearchSettings()) { Owner = null };
            window.Show();
            window.Close();
        });

        Check("dial-up window", () =>
        {
            var window = new DialUpWindow(new OpenDLM.Core.Models.DialUpSettings()) { Owner = null };
            window.Show();
            window.Close();
        });

        Check("archive preview window", () =>
        {
            var item = engine.CreateItem(new AddDownloadRequest
            {
                Url = "https://example.com/archive.zip",
                FileName = "archive.zip",
                Directory = AppPaths.TempDirectory
            });
            item.Status = DownloadStatus.Complete;

            // No archive exists at that path, so the preview reports no entries; the
            // point here is that the window and its resources load.
            var window = new ZipPreviewWindow(item.FullPath) { Owner = null };
            window.Show();
            window.Close();
        });

        engine.Dispose();
    }

    /// <summary>Every resource key the windows rely on, checked by name.</summary>
    private static readonly string[] SelfTestResourceKeys =
    {
        "IconLookup",
        "Style.ToolButton", "Style.ToolButtonIconOnly", "Style.ToolButtonLarge",
        "Style.Muted", "Style.SectionHeading", "Style.Heading",
        "Icon.Add", "Icon.Start", "Icon.Pause", "Icon.Stop", "Icon.Remove",
        "Icon.Options", "Icon.Calendar", "Icon.Folder", "Icon.Queue", "Icon.Search",
        "Icon.About", "Icon.Up", "Icon.Down", "Icon.OpenFolder",
        "Brush.Window", "Brush.Panel", "Brush.PanelAlt", "Brush.Toolbar", "Brush.MenuBar",
        "Brush.StatusBar", "Brush.Header", "Brush.DetailsPane", "Brush.Border",
        "Brush.GridLine", "Brush.Separator", "Brush.Text", "Brush.TextMuted",
        "Brush.TextDisabled", "Brush.Accent", "Brush.Selection", "Brush.Hover",
        "Brush.Pressed", "Brush.Progress", "Brush.ProgressTrack", "Brush.ProgressText",
        "Brush.Error", "Brush.Success", "Brush.Warning",
        "Brush.ScrollTrack", "Brush.ScrollThumb", "Brush.ScrollThumbHover"
    };

    private SettingsService? settingsServiceForWindows;

    /// <summary>Writes the report where CI can upload it and sets the process exit code.</summary>
    private void ReportSelfTest(List<string> failures)
    {
        if (_selfTestReported)
        {
            return;
        }
        _selfTestReported = true;

        var report = failures.Count == 0
            ? $"SELFTEST OK - OpenDLM {AppPaths.Version}"
            : "SELFTEST FAILED" + Environment.NewLine +
              string.Join(Environment.NewLine, failures.Select(failure => " - " + failure));

        try
        {
            File.WriteAllText(Path.Combine(AppPaths.LogDirectory, "selftest.txt"), report);
        }
        catch (Exception ex)
        {
            report += Environment.NewLine + "(could not write the report: " + ex.Message + ")";
        }

        ConsoleBridge.WriteLine(report, "OpenDLM self test");

        var exitCode = failures.Count == 0 ? 0 : 1;
        Environment.ExitCode = exitCode;
        Shutdown(exitCode);
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

        if (_selfTestMode)
        {
            // Never show a dialog from CI: record the failure, stop the test and
            // let the exit code carry the verdict.
            e.Handled = true;
            _selfTestFailures ??= new List<string>();
            _selfTestFailures.Add($"dispatcher: {e.Exception.GetType().Name}: {e.Exception.Message}");
            ReportSelfTest(_selfTestFailures);
            return;
        }

        // Recover from a UI fault rather than killing the download engine with it.
        e.Handled = true;

        Dialogs.Error(_mainWindow,
            "Something went wrong in the interface.\n\n" + e.Exception.Message +
            "\n\nDownloads keep running. A log was written to:\n" + AppPaths.LogFile);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error("An unhandled exception occurred.", e.ExceptionObject as Exception);

        if (_selfTestMode && !_selfTestReported)
        {
            _selfTestFailures ??= new List<string>();
            _selfTestFailures.Add($"domain: {e.ExceptionObject?.GetType().Name}: {e.ExceptionObject}");
            ReportSelfTest(_selfTestFailures);
        }
    }
}
