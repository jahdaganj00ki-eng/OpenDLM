using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenDLM.App.Controls;
using OpenDLM.App.Services;
using OpenDLM.App.ViewModels;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;
namespace OpenDLM.App.Views;

/// <summary>
/// The main window: menu, toolbar, category tree, download list, details pane and
/// status bar.
///
/// The code-behind here is intentionally thin. Everything that is a business rule
/// lives in the engine or in <see cref="MainViewModel"/>; this file only handles the
/// things a view model genuinely cannot: window placement, sorting, drag and drop,
/// keyboard shortcuts and the view toggles.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _graphTimer;
    private bool _reallyClosing;

    public MainWindow(MainViewModel viewModel, SettingsService settingsService)
    {
        _viewModel = viewModel;
        _settingsService = settingsService;

        InitializeComponent();

        DataContext = _viewModel;

        _graphTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _graphTimer.Tick += OnGraphTick;
    }

    // -------------------------------------------------------------------- lifetime

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Icon = AppIcons.CreateImageSource(32);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not create the window icon: " + ex.Message);
        }

        ApplyViewSettings();
        ApplySortFromSettings();
        ApplyGraphBrushes();
        RestoreWindowPlacement();

        _graphTimer.Start();
        _viewModel.RefreshCounts();

        Log.Info("The main window is open.");
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (_reallyClosing)
        {
            return;
        }

        SaveWindowPlacement();

        var settings = _settingsService.Current;

        // Closing the window hides it to the notification area so downloads continue,
        // which is what users expect from a download manager.
        if (settings.General.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            HideToTray(showBalloon: true);
            return;
        }

        _reallyClosing = true;
        Application.Current?.Shutdown();
    }

    public void HideToTray(bool showBalloon)
    {
        Hide();

        if (showBalloon)
        {
            Log.Info("The main window was hidden to the notification area.");
        }
    }

    public void ShowFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // --------------------------------------------------------------- view settings

    private void ApplyViewSettings()
    {
        var settings = _settingsService.Current.Interface;

        SetCategoryPane(settings.ShowCategoryPane);
        SetDetailsPane(settings.ShowDetailsPane);
        SetToolbar(settings.ShowToolbar);
        SetStatusBar(settings.ShowStatusBar);
        ApplyToolbarStyle(settings.ToolbarStyle);
        UpdateThemeChecks(settings.Theme);
    }

    /// <summary>Swaps the toolbar button style between labelled, compact and large.</summary>
    private void ApplyToolbarStyle(ToolbarStyle style)
    {
        var resourceKey = style switch
        {
            ToolbarStyle.IconsOnly => "Style.ToolButtonIconOnly",
            ToolbarStyle.LargeIcons => "Style.ToolButtonLarge",
            _ => "Style.ToolButton"
        };

        if (TryFindResource(resourceKey) is not Style buttonStyle)
        {
            Log.Warn($"The toolbar style '{resourceKey}' was not found in the resources.");
            return;
        }

        foreach (var button in new[]
                 {
                     ToolAddUrl, ToolResume, ToolPause, ToolStop, ToolRemove,
                     ToolOptions, ToolScheduler, ToolFolder, ToolTheme
                 })
        {
            button.Style = buttonStyle;
        }

        MenuToolbarIconsAndText.IsChecked = style == ToolbarStyle.IconsAndText;
        MenuToolbarIconsOnly.IsChecked = style == ToolbarStyle.IconsOnly;
        MenuToolbarLarge.IsChecked = style == ToolbarStyle.LargeIcons;
    }

    private void OnToolbarStyleSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string tag ||
            !Enum.TryParse<ToolbarStyle>(tag, ignoreCase: true, out var style))
        {
            return;
        }

        _settingsService.Update(settings => settings.Interface.ToolbarStyle = style);
        ApplyToolbarStyle(style);
    }

    /// <summary>Applies one of the explicit "sort by" entries from the View menu.</summary>
    private void OnSortSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string property)
        {
            return;
        }

        _viewModel.ItemsView.SortDescriptions.Clear();
        _viewModel.ItemsView.SortDescriptions.Add(
            new SortDescription(property, ListSortDirection.Ascending));

        UpdateSortChecks(property, ascending: true);

        _settingsService.Update(settings =>
        {
            settings.Interface.SortColumn = property;
            settings.Interface.SortAscending = true;
        });
    }

    private void OnSortDirectionToggled(object sender, RoutedEventArgs e)
    {
        var ascending = !MenuSortDirection.IsChecked;
        MenuSortDirection.IsChecked = ascending;

        var current = _viewModel.ItemsView.SortDescriptions.FirstOrDefault();
        var property = string.IsNullOrEmpty(current.PropertyName)
            ? _settingsService.Current.Interface.SortColumn
            : current.PropertyName;

        _viewModel.ItemsView.SortDescriptions.Clear();
        _viewModel.ItemsView.SortDescriptions.Add(new SortDescription(property,
            ascending ? ListSortDirection.Ascending : ListSortDirection.Descending));

        _settingsService.Update(settings => settings.Interface.SortAscending = ascending);
    }

    private void UpdateSortChecks(string property, bool ascending)
    {
        MenuSortAdded.IsChecked = property == "DateAdded";
        MenuSortName.IsChecked = property == "FileName";
        MenuSortSize.IsChecked = property == "Size";
        MenuSortStatus.IsChecked = property == "Status";
        MenuSortTimeLeft.IsChecked = property == "TimeLeft";
        MenuSortRate.IsChecked = property == "Speed";
        MenuSortDescription.IsChecked = property == "Description";
        MenuSortDirection.IsChecked = ascending;
    }

    private void SetCategoryPane(bool visible)
    {
        CategoryPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CategoryColumn.Width = visible ? new GridLength(196) : new GridLength(0);
        MenuShowCategories.IsChecked = visible;
    }

    private void SetDetailsPane(bool visible)
    {
        DetailsPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DetailsRow.Height = visible ? new GridLength(132) : new GridLength(0);
        MenuShowDetails.IsChecked = visible;
    }

    private void SetToolbar(bool visible)
    {
        ToolbarBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MenuShowToolbar.IsChecked = visible;
    }

    private void SetStatusBar(bool visible)
    {
        StatusBarBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MenuShowStatusBar.IsChecked = visible;
    }

    /// <summary>
    /// Shows what is inside the selected archive without extracting it, which also
    /// works while the file is still downloading.
    /// </summary>
    private void OnShowZipPreview(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is not { } item)
        {
            Dialogs.Info(this, "Select a download first.");
            return;
        }

        // Prefer the partial file, so a running download can be inspected.
        var candidate = File.Exists(item.PartialPath) ? item.PartialPath : item.FullPath;

        if (!File.Exists(candidate))
        {
            Dialogs.Info(this, "The file is not on disk yet.");
            return;
        }

        if (!item.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            !item.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
            !item.FileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            Dialogs.Warn(this, "The contents can only be listed for a ZIP archive.");
            return;
        }

        new Views.ZipPreviewWindow(candidate) { Owner = this }.Show();
    }

    private void OnToggleCategoryPane(object sender, RoutedEventArgs e)
        => PersistView(settings => settings.ShowCategoryPane = MenuShowCategories.IsChecked);

    private void OnToggleDetailsPane(object sender, RoutedEventArgs e)
        => PersistView(settings => settings.ShowDetailsPane = MenuShowDetails.IsChecked);

    private void OnToggleToolbar(object sender, RoutedEventArgs e)
        => PersistView(settings => settings.ShowToolbar = MenuShowToolbar.IsChecked);

    private void OnToggleStatusBar(object sender, RoutedEventArgs e)
        => PersistView(settings => settings.ShowStatusBar = MenuShowStatusBar.IsChecked);

    private void PersistView(Action<InterfaceSettings> mutate)
    {
        _settingsService.Update(settings => mutate(settings.Interface));
        ApplyViewSettings();
    }

    // ------------------------------------------------------------------- theming

    private void OnThemeSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse<AppTheme>(tag, ignoreCase: true, out var theme))
        {
            return;
        }

        _settingsService.Update(settings => settings.Interface.Theme = theme);
        ThemeManager.Apply(theme);
        UpdateThemeChecks(theme);
        ApplyGraphBrushes();
        _viewModel.RefreshCounts();
    }

    private void UpdateThemeChecks(AppTheme theme)
    {
        MenuThemeLight.IsChecked = theme == AppTheme.Light;
        MenuThemeDark.IsChecked = theme == AppTheme.Dark;
        MenuThemeSystem.IsChecked = theme == AppTheme.System;
    }

    private void ApplyGraphBrushes()
    {
        Graph.AxisBrush = TryBrush("Brush.BorderStrong");
        Graph.LineBrush = TryBrush("Brush.Accent");
        Graph.AreaBrush = TryBrush("Brush.Selection");

        // The graph is drawn by hand, so it has to be told to redraw after a theme swap.
        Graph.InvalidateVisual();
    }

    private System.Windows.Media.Brush TryBrush(string key)
        => TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

    // ------------------------------------------------------------------ placement

    private void RestoreWindowPlacement()
    {
        var settings = _settingsService.Current.Interface;

        if (settings.WindowWidth >= MinWidth && settings.WindowHeight >= MinHeight)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }

        if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop))
        {
            // Only restore a position that is still on a connected screen.
            var left = settings.WindowLeft;
            var top = settings.WindowTop;

            if (left > SystemParameters.VirtualScreenLeft - 32 &&
                top > SystemParameters.VirtualScreenTop - 32 &&
                left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 64 &&
                top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 64)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var maximized = WindowState == WindowState.Maximized;
            var bounds = RestoreBounds;

            _settingsService.Update(settings =>
            {
                settings.Interface.WindowMaximized = maximized;

                if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
                {
                    settings.Interface.WindowWidth = bounds.Width;
                    settings.Interface.WindowHeight = bounds.Height;
                    settings.Interface.WindowLeft = bounds.Left;
                    settings.Interface.WindowTop = bounds.Top;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save the window placement: " + ex.Message);
        }
    }

    // -------------------------------------------------------------------- sorting

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } ||
            column.Header is not string header)
        {
            return;
        }

        var property = SortPropertyFor(header);
        if (property is null)
        {
            return;
        }

        var ascending = !string.Equals(_viewModel.ItemsView.SortDescriptions.FirstOrDefault().PropertyName,
                            property, StringComparison.Ordinal)
                        || !_viewModel.ItemsView.SortDescriptions.FirstOrDefault().Direction.Equals(ListSortDirection.Ascending);

        _viewModel.ItemsView.SortDescriptions.Clear();
        _viewModel.ItemsView.SortDescriptions.Add(new SortDescription(property, ascending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending));

        _settingsService.Update(settings =>
        {
            settings.Interface.SortColumn = property;
            settings.Interface.SortAscending = ascending;
        });
    }

    private void ApplySortFromSettings()
    {
        var settings = _settingsService.Current.Interface;
        var property = settings.SortColumn;

        if (string.IsNullOrWhiteSpace(property))
        {
            return;
        }

        _viewModel.ItemsView.SortDescriptions.Clear();
        _viewModel.ItemsView.SortDescriptions.Add(new SortDescription(property,
            settings.SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending));

        UpdateSortChecks(property, settings.SortAscending);
    }

    /// <summary>Maps a column header to the item property it sorts on.</summary>
    private static string? SortPropertyFor(string header) => header switch
    {
        "File Name" => nameof(DownloadItem.FileName),
        "Size" => nameof(DownloadItem.TotalBytes),
        "Status" => nameof(DownloadItem.Status),
        "Downloaded" => nameof(DownloadItem.DownloadedBytes),
        "Speed" => nameof(DownloadItem.Speed),
        "Time left" => nameof(DownloadItem.TimeLeft),
        "Connections" => nameof(DownloadItem.Connections),
        "Date added" => nameof(DownloadItem.CreatedAt),
        "Save to" => nameof(DownloadItem.Directory),
        "Queue" => nameof(DownloadItem.QueueName),
        "Referer" => nameof(DownloadItem.Referer),
        "Last try" => nameof(DownloadItem.LastTryAt),
        "Description" => nameof(DownloadItem.Description),
        _ => null
    };

    // ------------------------------------------------------------------ selection

    private void OnCategoryChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is CategoryNode node)
        {
            _viewModel.SelectedCategory = node;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedItem is not { } item)
        {
            return;
        }

        if (item.IsFinished && File.Exists(item.FullPath))
        {
            PostDownloadActions.OpenFile(item.FullPath, this);
        }
        else if (item.CanStart)
        {
            _ = _viewModel.StartCommand.CanExecute(null);
            _viewModel.StartCommand.Execute(null);
        }
    }

    // ------------------------------------------------------------------- shortcuts

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Insert:
                _viewModel.AddUrlCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.FindCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.F3:
                // The reference steps through matches with F3; ours re-runs the find
                // so the newest list is searched.
                _viewModel.FindCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Delete when _viewModel.SelectedItem is not null:
                _viewModel.RemoveCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.F5:
                _viewModel.RefreshCounts();
                e.Handled = true;
                break;

            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                _viewModel.OptionsCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------- drag drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.UnicodeText))
        {
            e.Effects = DragDropEffects.Copy;
        }

        e.Handled = true;
    }

    private async void OnFilesDropped(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                await AddDroppedFilesAsync(files);
                return;
            }

            var text = e.Data.GetData(DataFormats.UnicodeText) as string
                       ?? e.Data.GetData(DataFormats.Text) as string;

            if (!string.IsNullOrWhiteSpace(text))
            {
                await AddDroppedTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Drop handling failed.", ex);
            Dialogs.Error(this, "Those items could not be added:\n" + ex.Message);
        }
    }

    private async Task AddDroppedFilesAsync(string[] files)
    {
        var added = 0;

        foreach (var path in files)
        {
            // A dropped text file is treated as a list of URLs; anything else is an error.
            if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                var options = new Core.Cli.CliOptions { BatchFile = path };
                foreach (var request in options.ExpandBatchFile())
                {
                    await _viewModel.AddRequestAsync(request);
                    added++;
                }
                continue;
            }

            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                await _viewModel.AddRequestAsync(new AddDownloadRequest
                {
                    Url = path,
                    Description = "Dropped onto the window"
                });
                added++;
            }
        }

        if (added == 0)
        {
            Dialogs.Info(this,
                "Drop a download URL, a http(s) link or a text file containing one URL per line.");
            return;
        }

        _viewModel.RefreshCounts();
    }

    private async Task AddDroppedTextAsync(string text)
    {
        var urls = text
            .Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           part.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
        {
            Dialogs.Info(this, "That text did not contain a http(s) URL.");
            return;
        }

        foreach (var url in urls)
        {
            await _viewModel.AddRequestAsync(new AddDownloadRequest
            {
                Url = url,
                Description = "Dropped onto the window"
            });
        }

        _viewModel.RefreshCounts();
    }

    // ------------------------------------------------------------------ the graph

    private void OnGraphTick(object? sender, EventArgs e)
    {
        // Only feed the graph while something is running, so an idle window does not
        // scroll a flat line forever.
        if (!_settingsService.Current.Interface.ShowDetailsPane)
        {
            return;
        }

        Graph.Push(_viewModel.HasActiveDownload ? _viewModel.CurrentTotalSpeed : 0);
    }

    // ------------------------------------------------------------------ menu extras

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => ShellIntegration.OpenDataFolder();

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => ShellIntegration.OpenLogFolder();
}
