using System.Diagnostics;
using System.Windows;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Services;

/// <summary>
/// The things that can happen once a download finishes: open it, reveal it, scan it,
/// or end the session. Kept out of the engine so the engine stays UI-free and testable.
/// </summary>
public static class PostDownloadActions
{
    /// <summary>Runs the configured action, if any.</summary>
    public static void Run(PostDownloadAction action, DownloadItem item, AppSettings settings, Window? owner)
    {
        if (action == PostDownloadAction.None)
        {
            return;
        }

        try
        {
            switch (action)
            {
                case PostDownloadAction.OpenFile:
                    OpenFile(item.FullPath, owner);
                    break;

                case PostDownloadAction.OpenFolder:
                    RevealInExplorer(item.FullPath);
                    break;

                case PostDownloadAction.OpenWith:
                    OpenWith(item.FullPath, settings.Downloads.OpenWithPath, owner);
                    break;

                case PostDownloadAction.AntivirusScan:
                    ScanWithAntivirus(item.FullPath, settings.Downloads.AntivirusPath,
                        settings.Downloads.AntivirusArguments, owner);
                    break;

                case PostDownloadAction.DisconnectDialUp:
                    DisconnectDialUp();
                    break;

                case PostDownloadAction.ShutdownComputer:
                    ScheduleShutdown(60);
                    break;

                case PostDownloadAction.ExitApplication:
                    Application.Current?.Shutdown();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Post-download action '{action}' failed for '{item.FileName}'.", ex);
            Dialogs.Warn(owner, $"The action '{action}' could not be completed:\n{ex.Message}");
        }
    }

    public static void OpenFile(string path, Window? owner)
    {
        if (!File.Exists(path))
        {
            Dialogs.Warn(owner, "The file is no longer there:\n" + path);
            return;
        }

        Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Opens Explorer with the file already selected.</summary>
    public static void RevealInExplorer(string path)
    {
        if (File.Exists(path))
        {
            Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return;
        }

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
    }

    public static void OpenWith(string path, string programPath, Window? owner)
    {
        if (string.IsNullOrWhiteSpace(programPath) || !File.Exists(programPath))
        {
            // Fall back to the shell's own "open with" picker.
            Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{path}\"")
            {
                UseShellExecute = true
            });
            return;
        }

        Start(new ProcessStartInfo(programPath, $"\"{path}\"") { UseShellExecute = false });
    }

    public static void ScanWithAntivirus(string path, string scannerPath, string arguments, Window? owner)
    {
        if (string.IsNullOrWhiteSpace(scannerPath) || !File.Exists(scannerPath))
        {
            Dialogs.Warn(owner,
                "No antivirus program is configured.\n\n" +
                "Set one under Options, Downloads, \"Run the following program after downloading\".");
            return;
        }

        var commandLine = string.IsNullOrWhiteSpace(arguments)
            ? $"\"{path}\""
            : arguments.Replace("%1", path, StringComparison.Ordinal);

        Start(new ProcessStartInfo(scannerPath, commandLine) { UseShellExecute = false });
        Log.Info($"Started an antivirus scan with {Path.GetFileName(scannerPath)}.");
    }

    /// <summary>Schedules a shutdown with a cancellable delay.</summary>
    public static void ScheduleShutdown(int delaySeconds)
    {
        var delay = Math.Clamp(delaySeconds, 10, 3600);
        Start(new ProcessStartInfo("shutdown.exe", $"/s /t {delay} /c \"OpenDLM: all downloads finished.\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Log.Info($"Computer shutdown scheduled in {delay} seconds.");
    }

    /// <summary>Cancels a pending shutdown started by OpenDLM.</summary>
    public static void CancelShutdown()
        => Start(new ProcessStartInfo("shutdown.exe", "/a")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });

    /// <summary>Hangs up a dial-up or VPN connection. Only meaningful for RAS links.</summary>
    public static void DisconnectDialUp()
    {
        Start(new ProcessStartInfo("rasdial.exe", "/disconnect")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Log.Info("Requested a RAS disconnect.");
    }

    private static void Start(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
    }
}
