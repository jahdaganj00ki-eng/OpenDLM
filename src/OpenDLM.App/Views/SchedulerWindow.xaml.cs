using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OpenDLM.App.Services;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;


namespace OpenDLM.App.Views;

/// <summary>
/// Queue windows and the "when everything has finished" behaviour.
///
/// The day checkboxes drive the same bit mask the engine evaluates, and the window
/// is written back to the queue objects the manager is already using, so a change
/// takes effect on the scheduler's next tick without a restart.
/// </summary>
public partial class SchedulerWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly SettingsService _settingsService;
    private readonly List<DownloadQueue> _queues;
    private DownloadQueue? _loadedQueue;
    private bool _loading;

    public SchedulerWindow(DownloadManager manager, SettingsService settingsService)
    {
        _manager = manager;
        _settingsService = settingsService;

        InitializeComponent();

        _queues = manager.Queues.Select(q => q.Clone()).ToList();
        _loading = true;

        foreach (var queue in _queues)
        {
            QueueBox.Items.Add(new QueueChoice(queue));
        }

        if (QueueBox.Items.Count > 0)
        {
            QueueBox.SelectedIndex = 0;
        }

        var scheduler = settingsService.Current.Scheduler;
        EnabledBox.IsChecked = scheduler.Enabled;
        ExitBox.IsChecked = scheduler.ExitWhenQueueFinished;
        ShutdownBox.IsChecked = scheduler.ShutdownWhenQueueFinished || scheduler.ShutdownWhenAllComplete;
        DelayBox.Text = scheduler.FinishedActionDelaySeconds.ToString(CultureInfo.InvariantCulture);

        DailyLimitBox.IsChecked = scheduler.DailyLimitEnabled;
        DailyHoursBox.Text = scheduler.DailyLimitHours.ToString(CultureInfo.InvariantCulture);
        DailyMegabytesBox.Text = scheduler.DailyLimitMegabytes.ToString(CultureInfo.InvariantCulture);
        DailyWarnBox.IsChecked = scheduler.ShowLimitExceededWarning;

        _loading = false;
        LoadSelectedQueue();
    }

    private DownloadQueue? SelectedQueue => (QueueBox.SelectedItem as QueueChoice)?.Queue;

    private void OnQueueChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // Write the controls back into the queue that is being left behind before
        // switching, otherwise its edits would be silently discarded.
        CaptureQueue(_loadedQueue);
        LoadSelectedQueue();
    }

    private void LoadSelectedQueue()
    {
        if (SelectedQueue is not { } queue)
        {
            return;
        }

        _loading = true;
        _loadedQueue = queue;

        ScheduledBox.IsChecked = queue.Scheduled;
        StartBox.Text = queue.StartTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        StopBox.Text = queue.StopTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        SetDay(DaySunday, queue, DayOfWeek.Sunday);
        SetDay(DayMonday, queue, DayOfWeek.Monday);
        SetDay(DayTuesday, queue, DayOfWeek.Tuesday);
        SetDay(DayWednesday, queue, DayOfWeek.Wednesday);
        SetDay(DayThursday, queue, DayOfWeek.Thursday);
        SetDay(DayFriday, queue, DayOfWeek.Friday);
        SetDay(DaySaturday, queue, DayOfWeek.Saturday);

        _loading = false;
    }

    private static void SetDay(CheckBox box, DownloadQueue queue, DayOfWeek day)
        => box.IsChecked = queue.RunsOn(day);

    /// <summary>Writes the controls back into one queue.</summary>
    private void CaptureQueue(DownloadQueue? queue)
    {
        if (queue is null)
        {
            return;
        }

        queue.Scheduled = ScheduledBox.IsChecked == true;

        if (TryParseTime(StartBox.Text, out var start))
        {
            queue.StartTime = start;
        }

        if (TryParseTime(StopBox.Text, out var stop))
        {
            queue.StopTime = stop;
        }

        var days = 0;
        if (DaySunday.IsChecked == true) days |= 1 << (int)DayOfWeek.Sunday;
        if (DayMonday.IsChecked == true) days |= 1 << (int)DayOfWeek.Monday;
        if (DayTuesday.IsChecked == true) days |= 1 << (int)DayOfWeek.Tuesday;
        if (DayWednesday.IsChecked == true) days |= 1 << (int)DayOfWeek.Wednesday;
        if (DayThursday.IsChecked == true) days |= 1 << (int)DayOfWeek.Thursday;
        if (DayFriday.IsChecked == true) days |= 1 << (int)DayOfWeek.Friday;
        if (DaySaturday.IsChecked == true) days |= 1 << (int)DayOfWeek.Saturday;

        // An empty day selection would mean "never runs", which is almost never what
        // the user meant; treat it as every day.
        queue.Days = days == 0 ? 0x7F : days;
    }

    private static bool TryParseTime(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var format in new[] { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss", @"h\:mm\:ss" })
        {
            if (TimeSpan.TryParseExact(text.Trim(), format, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        return TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out value);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        CaptureQueue(_loadedQueue);

        if (!TryParseTime(StartBox.Text, out _) || !TryParseTime(StopBox.Text, out _))
        {
            Dialogs.Warn(this, "Enter the window times on the 24-hour clock, for example 23:30 and 07:00.");
            return;
        }

        if (!int.TryParse(DelayBox.Text.Trim(), out var delay) || delay < 0)
        {
            Dialogs.Warn(this, "The delay must be a whole number of seconds.");
            return;
        }

        if (!double.TryParse(DailyHoursBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dailyHours) ||
            dailyHours < 0 || dailyHours > 24)
        {
            Dialogs.Warn(this, "The daily hour ceiling must be a number between 0 and 24.");
            return;
        }

        if (!long.TryParse(DailyMegabytesBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dailyMegabytes) ||
            dailyMegabytes < 0)
        {
            Dialogs.Warn(this, "The daily volume ceiling must be a whole number of megabytes.");
            return;
        }

        _settingsService.Update(settings =>
        {
            settings.Scheduler.Enabled = EnabledBox.IsChecked == true;
            settings.Scheduler.StartQueueOnSchedule = true;
            settings.Scheduler.StopQueueOnSchedule = true;
            settings.Scheduler.ExitWhenQueueFinished = ExitBox.IsChecked == true;
            settings.Scheduler.ShutdownWhenQueueFinished = ShutdownBox.IsChecked == true;
            settings.Scheduler.ShutdownWhenAllComplete = ShutdownBox.IsChecked == true;
            settings.Scheduler.FinishedActionDelaySeconds = Math.Clamp(delay, 0, 3600);

            settings.Scheduler.DailyLimitEnabled = DailyLimitBox.IsChecked == true;
            settings.Scheduler.DailyLimitHours = dailyHours;
            settings.Scheduler.DailyLimitMegabytes = dailyMegabytes;
            settings.Scheduler.ShowLimitExceededWarning = DailyWarnBox.IsChecked == true;
        });

        _manager.SaveQueues(_queues);

        Log.Info("Scheduler settings saved.");
        DialogResult = true;
    }

    private sealed record QueueChoice(DownloadQueue Queue)
    {
        public override string ToString() => Queue.Name;
    }
}
