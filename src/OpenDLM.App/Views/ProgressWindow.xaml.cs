using System.Windows;
using System.Windows.Threading;
using OpenDLM.App.Services;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Views;

/// <summary>
/// A live progress window for one download, with a compact and a full view.
///
/// It binds straight to the <see cref="DownloadItem"/>, which already raises change
/// notifications, so no polling of the text is needed: only the throughput graph is
/// fed on a timer, because it has to accumulate samples.
/// </summary>
public partial class ProgressWindow : Window
{
    private readonly DownloadItem _item;
    private readonly DownloadManager _manager;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _graphTimer;
    private bool _compact;

    public ProgressWindow(DownloadItem item, DownloadManager manager, SettingsService settingsService)
    {
        _item = item;
        _manager = manager;
        _settingsService = settingsService;

        InitializeComponent();

        DataContext = item;
        Title = "OpenDLM - " + item.FileName;

        // Start in whichever view the user configured.
        if (settingsService.Current.Downloads.ProgressDialog == ProgressDialogMode.Compact)
        {
            SetCompact(true);
        }

        _graphTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _graphTimer.Tick += OnGraphTick;
        _graphTimer.Start();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Icon = AppIcons.CreateImageSource(16);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not set the progress window icon: " + ex.Message);
        }

        Graph.AxisBrush = TryFindResource("Brush.BorderStrong") as System.Windows.Media.Brush
                          ?? System.Windows.Media.Brushes.Gray;
        Graph.LineBrush = TryFindResource("Brush.Accent") as System.Windows.Media.Brush
                          ?? System.Windows.Media.Brushes.SteelBlue;
        Graph.AreaBrush = TryFindResource("Brush.Selection") as System.Windows.Media.Brush
                          ?? System.Windows.Media.Brushes.LightGray;

        RefreshButtons();
    }

    private void OnGraphTick(object? sender, EventArgs e)
    {
        Graph.Push(_item.Speed);
        RefreshButtons();
    }

    /// <summary>Switches between the compact bar and the full statistics view.</summary>
    private void SetCompact(bool compact)
    {
        _compact = compact;

        DetailsArea.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ViewButton.Content = compact ? "Full view" : "Compact view";
        MinHeight = compact ? 132 : 210;
        Height = compact ? 150 : Math.Max(Height, 212);
    }

    private void OnToggleView(object sender, RoutedEventArgs e) => SetCompact(!_compact);

    private void OnPauseOrResume(object sender, RoutedEventArgs e)
    {
        if (_item.CanPause)
        {
            _manager.Pause(_item);
            return;
        }

        if (_item.CanStart)
        {
            _ = _manager.StartAsync(_item);
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => _manager.Stop(_item);

    private void OnOpenFolder(object sender, RoutedEventArgs e)
        => PostDownloadActions.RevealInExplorer(_item.FullPath);

    /// <summary>Keeps the action buttons in step with the item's state.</summary>
    private void RefreshButtons()
    {
        PauseButton.Content = _item.CanPause ? "Pause" : _item.CanStart ? "Resume" : "Pause";
        PauseButton.IsEnabled = _item.CanPause || _item.CanStart;

        // A finished download has nothing left to control.
        if (_item.IsFinished)
        {
            PauseButton.IsEnabled = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _graphTimer.Stop();
        _graphTimer.Tick -= OnGraphTick;
        Log.Info($"Progress window closed for '{_item.FileName}'.");
    }
}
