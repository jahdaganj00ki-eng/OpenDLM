using System.Windows;

namespace OpenDLM.App.Services;

/// <summary>
/// Thin wrappers around the common dialogs so call sites never have to think about
/// owner windows or the WPF/WinForms split.
/// </summary>
public static class Dialogs
{
    private const string DefaultTitle = "OpenDLM";

    /// <summary>Native folder picker. Returns null when the user cancels.</summary>
    public static string? PickFolder(string? initialDirectory, string description = "Select a folder")
    {
        try
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = description,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                AutoUpgradeEnabled = true
            };

            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.SelectedPath = initialDirectory;
            }

            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                ? dialog.SelectedPath
                : null;
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn("Folder picker failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Save-file picker used by "save as" for a single download.</summary>
    public static string? PickSaveLocation(string suggestedFileName, string? initialDirectory)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedFileName,
            Filter = "All files (*.*)|*.*",
            // The engine handles collisions itself, so do not nag here.
            OverwritePrompt = false,
            AddExtension = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static string? PickExecutable(string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static void Error(Window? owner, string message, string title = DefaultTitle)
        => Show(owner, message, title, MessageBoxImage.Error);

    public static void Warn(Window? owner, string message, string title = DefaultTitle)
        => Show(owner, message, title, MessageBoxImage.Warning);

    public static void Info(Window? owner, string message, string title = DefaultTitle)
        => Show(owner, message, title, MessageBoxImage.Information);

    public static bool Confirm(Window? owner, string message, string title = DefaultTitle)
        => Show(owner, message, title, MessageBoxImage.Question) == MessageBoxResult.OK;

    public static bool ConfirmYesNo(Window? owner, string message, string title = DefaultTitle)
        => Show(owner, message, title, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxImage icon)
    {
        var buttons = icon == MessageBoxImage.Question ? MessageBoxButton.YesNo : MessageBoxButton.OK;

        try
        {
            // Owner windows must already be realised; fall back to an unowned dialog.
            return owner is { IsLoaded: true }
                ? MessageBox.Show(owner, message, title, buttons, icon)
                : MessageBox.Show(message, title, buttons, icon);
        }
        catch (Exception ex)
        {
            Core.Util.Log.Error("Could not show a message box.", ex);
            return MessageBoxResult.None;
        }
    }

    /// <summary>Asks for the destination folder of a single download.</summary>
    public static string? AskForFolder(Window? owner, string current)
    {
        var chosen = PickFolder(current, "Where should OpenDLM save this file?");
        if (chosen is null)
        {
            return null;
        }

        if (!Directory.Exists(chosen))
        {
            Error(owner, "The folder does not exist:\n" + chosen);
            return null;
        }

        return chosen;
    }
}
