using System.Windows;

namespace OpenDLM.App.Views;

/// <summary>
/// Asked when a link that is already in the list is added again.
///
/// The dialog only reports the decision and whether the user asked to remember it;
/// storing the answer is the caller's job, so the engine stays the single place that
/// writes settings. Cancelling is deliberately distinct from "do not add", so a
/// dismissed dialog never silently records a decision.
/// </summary>
public partial class DuplicatePromptWindow : Window
{
    public DuplicatePromptWindow(string url, string existingStatus, bool canRemember)
    {
        InitializeComponent();

        IntroText.Text =
            "This address is already in the list, so it would be downloaded a second time:\n\n" + url;

        DetailText.Text = "The existing entry is currently \"" + existingStatus + "\".";

        // Remembering is only offered when the user has switched it on; the checkbox
        // is shown disabled and unchecked otherwise, so the dialog still explains it
        // without pretending the option is available.
        RememberBox.IsEnabled = canRemember;
        RememberBox.IsChecked = canRemember;
    }

    /// <summary>True when the user asked for this answer to be remembered.</summary>
    public bool ShouldRemember => RememberBox.IsEnabled && RememberBox.IsChecked == true;

    private void OnAdd(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnSkip(object sender, RoutedEventArgs e) => DialogResult = false;
}
