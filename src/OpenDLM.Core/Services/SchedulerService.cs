using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Services;

/// <summary>What the scheduler should do when a window opens or closes, or when everything finishes.</summary>
public enum ScheduledAction
{
    None = 0,
    StartQueue = 1,
    StopQueue = 2,
    ExitApplication = 3,
    ShutdownComputer = 4,
    DisconnectDialUp = 5
}

public sealed class ScheduledEventArgs : EventArgs
{
    public required ScheduledAction Action { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Watches the clock and the download list, and raises the actions configured on
/// the scheduler tab: opening/closing a queue's window, and the "when finished"
/// behaviour (exit, shut down, disconnect).
///
/// It intentionally does not perform the side effects itself — the UI layer owns
/// process shutdown and signage — it only decides *when*.
/// </summary>
public sealed class SchedulerService : IDisposable
{
    private readonly DownloadManager _manager;
    private readonly SettingsService _settingsService;
    private readonly Timer _timer;

    private bool _queueWindowOpen;
    private bool _allCompletedRaised;
    private DateTime? _finishedSince;
    private bool _disposed;

    // Daily ceiling bookkeeping.
    private DateTime _dailyDate = DateTime.Today;
    private double _dailySeconds;
    private long _dailyBytes;
    private long _lastBytesSnapshot = -1;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    private bool _limitReported;

    public SchedulerService(DownloadManager manager, SettingsService settingsService)
    {
        _manager = manager;
        _settingsService = settingsService;
        _timer = new Timer(_ => Evaluate(), null, 5000, 5000);
    }

    /// <summary>Raised when a scheduled action should be carried out.</summary>
    public event EventHandler<ScheduledEventArgs>? ActionRequired;

    /// <summary>Raised once when a daily time or volume ceiling is reached.</summary>
    public event EventHandler<string>? DailyLimitReached;

    /// <summary>Seconds of downloading accumulated today.</summary>
    public double DailySeconds => _dailySeconds;

    /// <summary>Bytes downloaded today since the scheduler started.</summary>
    public long DailyBytes => _dailyBytes;

    /// <summary>True while the daily ceiling is stopping new downloads.</summary>
    public bool IsDailyLimitActive { get; private set; }

    /// <summary>Clears today's counters, for example after the user raises the limit.</summary>
    public void ResetDailyCounters()
    {
        _dailyDate = DateTime.Today;
        _dailySeconds = 0;
        _dailyBytes = 0;
        _lastBytesSnapshot = -1;
        _limitReported = false;
        IsDailyLimitActive = false;
        Log.Info("The daily download counters were reset.");
    }

    /// <summary>The queue the scheduler controls. Defaults to the main queue.</summary>
    public int ManagedQueueId { get; set; }

    private void Evaluate()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var settings = _settingsService.Current;
            var scheduler = settings.Scheduler;

            EvaluateDailyLimit(scheduler);
            EvaluateQueueWindow(scheduler);
            EvaluateFinishedActions(scheduler);
        }
        catch (Exception ex)
        {
            Log.Error("Scheduler evaluation failed.", ex);
        }
    }

    /// <summary>
    /// Tracks how long and how much has been downloaded today, and stops everything
    /// once a ceiling is crossed. The counters reset at midnight.
    /// </summary>
    private void EvaluateDailyLimit(SchedulerSettings scheduler)
    {
        var now = DateTime.UtcNow;

        if (_dailyDate != DateTime.Today)
        {
            ResetDailyCounters();
        }

        var elapsed = (now - _lastTickUtc).TotalSeconds;
        _lastTickUtc = now;

        // A huge gap means the machine was asleep; do not bill the user for it.
        if (elapsed < 0 || elapsed > 120)
        {
            elapsed = 0;
        }

        var totalBytes = _manager.Snapshot().Sum(item => Math.Max(0, item.DownloadedBytes));

        if (_lastBytesSnapshot < 0 || totalBytes < _lastBytesSnapshot)
        {
            // First tick, or items were removed: rebase without counting a delta.
            _lastBytesSnapshot = totalBytes;
        }
        else
        {
            _dailyBytes += totalBytes - _lastBytesSnapshot;
            _lastBytesSnapshot = totalBytes;
        }

        if (_manager.HasActiveDownloads)
        {
            _dailySeconds += elapsed;
        }

        if (!scheduler.DailyLimitEnabled)
        {
            IsDailyLimitActive = false;
            return;
        }

        var timeExceeded = scheduler.DailyLimitHours > 0 &&
                           _dailySeconds >= scheduler.DailyLimitHours * 3600;

        var volumeExceeded = scheduler.DailyLimitMegabytes > 0 &&
                             _dailyBytes >= scheduler.DailyLimitMegabytes * 1024L * 1024L;

        if (!timeExceeded && !volumeExceeded)
        {
            IsDailyLimitActive = false;
            _limitReported = false;
            return;
        }

        IsDailyLimitActive = true;

        if (_limitReported)
        {
            return;
        }

        _limitReported = true;

        var reason = timeExceeded && volumeExceeded
            ? $"the daily limit of {scheduler.DailyLimitHours:0.#} hours and {scheduler.DailyLimitMegabytes} MB"
            : timeExceeded
                ? $"the daily limit of {scheduler.DailyLimitHours:0.#} hours"
                : $"the daily limit of {scheduler.DailyLimitMegabytes} MB";

        var message = $"OpenDLM reached {reason} of downloading today. " +
                      "Active downloads have been stopped; they resume tomorrow or when you reset the counter.";

        Log.Info(message);

        if (scheduler.ShowLimitExceededWarning)
        {
            DailyLimitReached?.Invoke(this, message);
        }

        Raise(ScheduledAction.StopQueue, message);
    }

    private void EvaluateQueueWindow(SchedulerSettings scheduler)
    {
        if (!scheduler.Enabled)
        {
            return;
        }

        var isOpen = _manager.IsQueueActive(ManagedQueueId, DateTime.Now);

        if (isOpen && !_queueWindowOpen)
        {
            _queueWindowOpen = true;

            if (scheduler.StartQueueOnSchedule)
            {
                // Bring the VPN or dial-up connection up before the queue starts, so
                // the first download does not fail on a dead tunnel.
                var dialUp = _settingsService.Current.DialUp;
                if (dialUp.Enabled && !string.IsNullOrWhiteSpace(dialUp.ConnectionName) &&
                    !DialUpManager.IsConnected(dialUp.ConnectionName))
                {
                    if (DialUpManager.Dial(dialUp, out var message))
                    {
                        Raise(ScheduledAction.StartQueue, message);
                    }
                    else
                    {
                        Log.Warn(message);
                    }
                }

                Raise(ScheduledAction.StartQueue, $"The scheduled window for the {(ManagedQueueId == 0 ? "main" : "selected")} queue has opened.");
            }
        }
        else if (!isOpen && _queueWindowOpen)
        {
            _queueWindowOpen = false;

            // Drop the VPN or dial-up connection again once the window closes.
            var dialUp = _settingsService.Current.DialUp;
            if (dialUp.Enabled && dialUp.HangUpWhenFinished)
            {
                DialUpManager.HangUp(dialUp);
            }

            if (scheduler.StopQueueOnSchedule)
            {
                Raise(ScheduledAction.StopQueue, "The scheduled window has closed.");
            }
        }
    }

    private void EvaluateFinishedActions(SchedulerSettings scheduler)
    {
        var hasWork = _manager.Snapshot().Any(i =>
            i.Status is DownloadStatus.Downloading or DownloadStatus.Connecting
                or DownloadStatus.Queued or DownloadStatus.Scheduled or DownloadStatus.Finalizing);

        if (hasWork)
        {
            _allCompletedRaised = false;
            _finishedSince = null;
            return;
        }

        if (_allCompletedRaised)
        {
            return;
        }

        var action = scheduler.ShutdownWhenAllComplete || scheduler.ShutdownWhenQueueFinished
            ? ScheduledAction.ShutdownComputer
            : scheduler.ExitWhenQueueFinished
                ? ScheduledAction.ExitApplication
                : ScheduledAction.None;

        if (action == ScheduledAction.None)
        {
            return;
        }

        // Only act if there was actually something to finish, so an idle start-up
        // with the setting enabled does not immediately shut the machine down.
        var anyFinished = _manager.Snapshot().Any(i => i.Status == DownloadStatus.Complete);
        if (!anyFinished)
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Max(0, scheduler.FinishedActionDelaySeconds));
        _finishedSince ??= DateTime.UtcNow;

        if (DateTime.UtcNow - _finishedSince.Value < delay)
        {
            return;
        }

        _allCompletedRaised = true;
        Raise(action, action == ScheduledAction.ShutdownComputer
            ? "All downloads are complete; shutting down."
            : "All downloads are complete; exiting.");
    }

    private void Raise(ScheduledAction action, string reason)
    {
        Log.Info($"Scheduler: {action} ({reason})");
        ActionRequired?.Invoke(this, new ScheduledEventArgs { Action = action, Reason = reason });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Dispose();
    }
}
