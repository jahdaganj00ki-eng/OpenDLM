using System.Threading;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Services;

/// <summary>
/// A named-mutex guard that makes OpenDLM a single-instance application.
///
/// A second launch is not an error: the command line is forwarded to the running
/// instance over the existing local pipe, so "Open with OpenDLM" and browser hand-
/// offs work no matter how many times the executable is started.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\OpenDLM.SingleInstance.v1";

    private readonly Mutex _mutex;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);

        if (created)
        {
            _ownsMutex = true;
            IsFirstInstance = true;
            return;
        }

        // The mutex already exists. If the previous owner crashed, acquiring it here
        // succeeds immediately after the abandoned-mutex exception.
        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero, exitContext: false);
            IsFirstInstance = _ownsMutex;
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
            IsFirstInstance = true;
            Log.Warn("Recovered an abandoned single-instance mutex.");
        }
        catch (Exception ex)
        {
            Log.Warn("Single-instance check failed, continuing as the primary instance: " + ex.Message);
            IsFirstInstance = true;
        }
    }

    public bool IsFirstInstance { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not release the single-instance mutex: " + ex.Message);
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
