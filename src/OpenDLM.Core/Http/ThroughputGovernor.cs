using System.Diagnostics;

namespace OpenDLM.Core.Http;

/// <summary>
/// A global token bucket that enforces the optional download speed limit.
///
/// Every segment worker asks for an allowance before reading from the network, so
/// the limit applies to the sum of all connections rather than per connection,
/// which is what users expect from a "maximum speed" setting.
/// </summary>
public sealed class ThroughputGovernor
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _limitBytesPerSecond;
    private double _available;
    private long _lastRefillMs;

    /// <summary>Bytes per second cap. 0 (or negative) disables throttling.</summary>
    public long LimitBytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _limitBytesPerSecond;
            }
        }
        set
        {
            lock (_gate)
            {
                _limitBytesPerSecond = Math.Max(0, value);
                // Start with one second of credit so enabling the limit does not
                // stall the first transfer for a full second.
                _available = _limitBytesPerSecond;
                _lastRefillMs = _clock.ElapsedMilliseconds;
            }
        }
    }

    public bool IsEnabled => LimitBytesPerSecond > 0;

    /// <summary>Total bytes handed out since the process started, for the status bar.</summary>
    public long TotalGranted;

    /// <summary>Sets the cap from a kilobytes-per-second setting.</summary>
    public void ApplyLimit(bool enabled, int kilobytesPerSecond)
    {
        LimitBytesPerSecond = enabled && kilobytesPerSecond > 0
            ? (long)kilobytesPerSecond * 1024
            : 0;
    }

    /// <summary>
    /// Returns how many bytes the caller may transfer right now, waiting if the
    /// bucket is empty. The returned value is never larger than <paramref name="wanted"/>.
    /// </summary>
    public async Task<int> AcquireAsync(int wanted, CancellationToken cancellationToken)
    {
        if (wanted <= 0)
        {
            return 0;
        }

        while (true)
        {
            long toWaitMs;
            int granted;

            lock (_gate)
            {
                if (_limitBytesPerSecond <= 0)
                {
                    Interlocked.Add(ref TotalGranted, wanted);
                    return wanted;
                }

                Refill();

                // Burst allowance: never hand out more than one second's worth at once,
                // so a stalled connection cannot bank credit and then flood.
                var burstCap = (double)_limitBytesPerSecond;
                _available = Math.Min(_available, burstCap);

                if (_available >= 1)
                {
                    granted = (int)Math.Min(wanted, Math.Floor(_available));
                    _available -= granted;
                    Interlocked.Add(ref TotalGranted, granted);
                    return granted;
                }

                // Nothing available: wait for the next token.
                var deficit = 1 - _available;
                toWaitMs = (long)Math.Ceiling(deficit / _limitBytesPerSecond * 1000.0);
            }

            await Task.Delay((int)Math.Clamp(toWaitMs, 5, 250), cancellationToken).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = _clock.ElapsedMilliseconds;
        var elapsed = now - _lastRefillMs;
        if (elapsed <= 0)
        {
            return;
        }

        _lastRefillMs = now;
        _available += _limitBytesPerSecond * (elapsed / 1000.0);
        var cap = (double)_limitBytesPerSecond;
        if (_available > cap)
        {
            _available = cap;
        }
    }
}
