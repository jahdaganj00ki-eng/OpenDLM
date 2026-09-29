using System.Windows;
using OpenDLM.App.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Views;

/// <summary>Product information, licence and the build this window was started from.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = AppPaths.Version;
        TitleText.Text = AppPaths.ProductName;
        VersionText.Text = $"Version {version} - MIT licensed - free forever";

        BuildText.Text = ShellIntegration.DescribeBuild();

        Log.Info("About dialog opened.");
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => ShellIntegration.OpenDataFolder();

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => ShellIntegration.OpenLogFolder();
}
