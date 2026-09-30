using System.Windows;
using OpenDLM.App.Services;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Views;

/// <summary>
/// The "download complete" dialog, with the numbers a user actually wants to see and
/// the two things they almost always do next: open the file, or open its folder.
/// </summary>
public partial class DownloadCompleteWindow : Window
{
    private readonly DownloadItem _item;

    public DownloadCompleteWindow(DownloadItem item)
    {
        _item = item;

        InitializeComponent();

        NameText.Text = item.FileName;
        PathText.Text = item.FullPath;
        SizeText.Text = item.TotalBytes > 0 ? Fmt.Bytes(item.TotalBytes) : "Unknown";
        ElapsedText.Text = item.ElapsedSeconds > 0 ? Fmt.Elapsed(TimeSpan.FromSeconds(item.ElapsedSeconds)) : "Unknown";
        AverageText.Text = item.AverageSpeed > 0 ? Fmt.Speed(item.AverageSpeed) : "Unknown";
        ChecksumText.Text = string.IsNullOrEmpty(item.ChecksumMd5) ? "(not computed)" : item.ChecksumMd5;
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        PostDownloadActions.OpenFile(_item.FullPath, this);
        Close();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        PostDownloadActions.RevealInExplorer(_item.FullPath);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (OpenWhenClosedBox.IsChecked == true)
        {
            PostDownloadActions.OpenFile(_item.FullPath, this);
        }

        Close();
    }
}
