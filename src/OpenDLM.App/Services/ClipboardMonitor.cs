using System.Windows;
using System.Windows.Threading;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Services;

/// <summary>
/// Watches the clipboard for download URLs.
///
/// Implemented as a dispatcher poll rather than a Win32 clipboard-viewer chain:
/// viewer chains require a message-only window and can deadlock the shell, which is
/// far too much risk for a convenience feature.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private string? _lastSeen;
    private bool _disposed;

    public ClipboardMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _timer.Tick += OnTick;
    }

    /// <summary>Raised once per newly copied download URL.</summary>
    public event EventHandler<string>? UrlDetected;

    /// <summary>Master switch, normally bound to the clipboard setting.</summary>
    public bool Enabled { get; set; }

    /// <summary>Lets the caller veto a URL (for example below the minimum size).</summary>
    public Func<string, bool>? Accept { get; set; }

    public void Start()
    {
        if (!_disposed)
        {
            _timer.Start();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!Enabled)
        {
            return;
        }

        string? text;
        try
        {
            if (!Clipboard.ContainsText())
            {
                return;
            }

            // The clipboard can be locked by another process; failing is normal.
            text = Clipboard.GetText();
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        text = text.Trim();

        if (string.Equals(text, _lastSeen, StringComparison.Ordinal))
        {
            return;
        }

        _lastSeen = text;

        if (!LooksLikeDownloadUrl(text))
        {
            return;
        }

        if (Accept is not null && !Accept(text))
        {
            Log.Info("Clipboard URL was vetoed by the size gate.");
            return;
        }

        Log.Info("Clipboard download URL detected.");
        UrlDetected?.Invoke(this, text);
    }

    /// <summary>Accepts a single URL that has a file-like last path segment.</summary>
    private static bool LooksLikeDownloadUrl(string text)
    {
        // Reject multi-line clipboard content: that is text, not a URL.
        if (text.Contains('\n') || text.Contains('\r') || text.Length > 2048)
        {
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        return extension.Length > 1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
