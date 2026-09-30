using System.Text;
using System.Windows;
using OpenDLM.App.Services;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Views;

/// <summary>
/// Shows what is inside a ZIP before the whole file has arrived, which is what the
/// reference's archive preview is for.
/// </summary>
public partial class ZipPreviewWindow : Window
{
    private readonly string _archivePath;
    private readonly IReadOnlyList<ZipEntryInfo> _entries;

    public ZipPreviewWindow(string archivePath)
    {
        _archivePath = archivePath;
        _entries = ZipPreview.Read(archivePath);

        InitializeComponent();

        Title = "Contents of " + System.IO.Path.GetFileName(archivePath);
        TitleText.Text = System.IO.Path.GetFileName(archivePath);
        SummaryText.Text = ZipPreview.Summarise(_entries);

        EntryList.ItemsSource = _entries;
    }

    private void OnSaveList(object sender, RoutedEventArgs e)
    {
        var target = Dialogs.PickSaveLocation(
            System.IO.Path.GetFileNameWithoutExtension(_archivePath) + "-contents.txt",
            System.IO.Path.GetDirectoryName(_archivePath));

        if (target is null)
        {
            return;
        }

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine(ZipPreview.Summarise(_entries));
            builder.AppendLine();

            foreach (var entry in _entries)
            {
                builder.AppendLine(
                    $"{entry.Length,15:N0}  {entry.CompressedLength,15:N0}  {entry.Modified:yyyy-MM-dd}  {entry.Name}");
            }

            System.IO.File.WriteAllText(target, builder.ToString());
            Log.Info("Saved the archive listing to " + target);
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "The list could not be saved:\n" + ex.Message);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
