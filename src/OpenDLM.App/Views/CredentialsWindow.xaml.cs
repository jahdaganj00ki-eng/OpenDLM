using System.Windows;

namespace OpenDLM.App.Views;

/// <summary>
/// Asks for the user name and password of an HTTP authentication realm.
/// Shown from the engine's credential callback, which runs on a worker thread and is
/// therefore marshalled onto the dispatcher by the view model.
/// </summary>
public partial class CredentialsWindow : Window
{
    public CredentialsWindow(string host, string url)
    {
        InitializeComponent();

        IntroText.Text =
            $"The server \"{host}\" is asking for a user name and password before it will " +
            "send the file.";

        ToolTip = url;

        Loaded += (_, _) => UserBox.Focus();
    }

    public string UserName => UserBox.Text.Trim();

    public string Password => PasswordInput.Password;

    public bool Remember => RememberBox.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserBox.Text))
        {
            // An empty user name is almost certainly a mistake; keep the dialog open.
            UserBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
