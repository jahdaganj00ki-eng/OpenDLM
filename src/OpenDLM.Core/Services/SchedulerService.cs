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

    public SchedulerService(DownloadManager manager, SettingsService settingsService)
    {
        _manager = manager;
        _settingsService = settingsService;
        _timer = new Timer(_ => Evaluate(), null, 5000, 5000);
    }

    /// <summary>Raised when a scheduled action should be carried out.</summary>
    public event EventHandler<ScheduledEventArgs>? ActionRequired;

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

            EvaluateQueueWindow(scheduler);
            EvaluateFinishedActions(scheduler);
        }
        catch (Exception ex)
        {
            Log.Error("Scheduler evaluation failed.", ex);
        }
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
                Raise(ScheduledAction.StartQueue, $"The scheduled window for the {(ManagedQueueId == 0 ? "main" : "selected")} queue has opened.");
            }
        }
        else if (!isOpen && _queueWindowOpen)
        {
            _queueWindowOpen = false;

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
