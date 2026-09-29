using System.Runtime.InteropServices;
using System.Windows;

namespace OpenDLM.App.Services;

/// <summary>
/// Lets the windowed application still behave like a console program.
///
/// OpenDLM is built as a WinExe so launching it never flashes a console window, but
/// the command line interface has to be able to print. Attaching to the parent
/// console gives real stdout when the process was started from a shell; when there
/// is no console (double-clicked from Explorer) the text falls back to a dialog.
/// </summary>
internal static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    private static bool _attached;

    public static void WriteLine(string text, string? dialogTitle = null)
    {
        if (!_attached)
        {
            _attached = AttachConsole(AttachParentProcess);
        }

        if (_attached)
        {
            try
            {
                Console.Out.WriteLine(text);
                Console.Out.Flush();
                return;
            }
            catch
            {
                // Fall through to the dialog below.
            }
        }

        // No console to attach to: show it so the user is not left with nothing.
        try
        {
            MessageBox.Show(text, dialogTitle ?? "OpenDLM", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            // Nothing else we can do.
        }
    }

    /// <summary>Detaches from the borrowed console so the shell prompt is not held open.</summary>
    public static void Detach()
    {
        if (!_attached)
        {
            return;
        }

        try
        {
            FreeConsole();
        }
        catch
        {
            // Ignore.
        }
        _attached = false;
    }
}
