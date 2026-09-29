using System.Drawing;
using System.Windows;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Services;

/// <summary>
/// The notification-area icon and its context menu.
///
/// WinForms types are fully qualified on purpose: this project references both WPF
/// and WinForms (for the tray icon and native folder pickers), and a blanket
/// <c>using System.Windows.Forms</c> would make Application, MessageBox and Brush
/// ambiguous throughout the file.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayIconService(Action showWindow, Action addUrl, Action options, Action exit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(CreateItem("Open OpenDLM", showWindow));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(CreateItem("Add URL...", addUrl));
        menu.Items.Add(CreateItem("Options...", options));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(CreateItem("Exit", exit));

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = AppIcons.CreateTrayIcon(32),
            Text = "OpenDLM - free download manager",
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => showWindow();
    }

    private static System.Windows.Forms.ToolStripMenuItem CreateItem(string text, Action action)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error($"Tray menu action '{text}' failed.", ex);
            }
        };
        return item;
    }

    /// <summary>Shows a balloon notification. Ignored silently when notifications are unavailable.</summary>
    public void Notify(string title, string message, bool isError = false)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = isError
                ? System.Windows.Forms.ToolTipIcon.Error
                : System.Windows.Forms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(5000);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not show a tray notification: " + ex.Message);
        }
    }

    public void SetTooltip(string text)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // The Win32 NOTIFYICONDATA limit is 63 characters.
            _notifyIcon.Text = text.Length <= 63 ? text : text[..60] + "...";
        }
        catch (Exception ex)
        {
            Log.Warn("Could not update the tray tooltip: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Error while disposing the tray icon: " + ex.Message);
        }
    }
}
