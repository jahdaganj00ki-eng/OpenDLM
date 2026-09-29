using System.Windows;
using OpenDLM.App.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Views;

/// <summary>A plain read-only text viewer, used for the log and for checksum listings.</summary>
public partial class TextWindow : Window
{
    public TextWindow(string title, string content, string? path = null)
    {
        InitializeComponent();

        Title = title;
        PathText.Text = path ?? string.Empty;
        ContentBox.Text = content;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ContentBox.Text ?? string.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not copy to the clipboard: " + ex.Message);
        }
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        var target = Dialogs.PickSaveLocation(
            Path.GetFileName(PathText.Text) is { Length: > 0 } name ? name : "opendlm.txt",
            Path.GetDirectoryName(PathText.Text));

        if (target is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(target, ContentBox.Text ?? string.Empty);
            Log.Info("Saved a text report to " + target);
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Could not save the file:\n" + ex.Message);
        }
    }
}
