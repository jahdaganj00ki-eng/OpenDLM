using System.Windows;
using OpenDLM.App.Services;
using OpenDLM.Core.Models;
using OpenDLM.Core.Services;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Views;

/// <summary>
/// The dial-up / VPN settings, the reference's "Dial Up / VPN" tab, kept as its own
/// window because it also has connect and disconnect actions that have nothing to do
/// with saving preferences.
/// </summary>
public partial class DialUpWindow : Window
{
    private readonly DialUpSettings _settings;

    public DialUpWindow(DialUpSettings settings)
    {
        _settings = settings.Clone();
        _settings.AvailableConnections = DialUpManager.ListConnections();

        InitializeComponent();

        EnabledBox.IsChecked = _settings.Enabled;
        UserBox.Text = _settings.UserName;
        PasswordInput.Password = CredentialProtector.Unprotect(_settings.PasswordProtected) ?? string.Empty;
        OnlyWhenNeededBox.IsChecked = _settings.DialOnlyWhenNeeded;
        HangUpBox.IsChecked = _settings.HangUpWhenFinished;
        AttemptsBox.Text = _settings.RedialAttempts.ToString();
        IntervalBox.Text = _settings.RedialIntervalSeconds.ToString();

        Populate();
    }

    /// <summary>True when the user confirmed, so the caller applies the values.</summary>
    public bool Saved { get; private set; }

    public DialUpSettings Result => _settings;

    private void Populate()
    {
        ConnectionBox.Items.Clear();

        if (!string.IsNullOrWhiteSpace(_settings.ConnectionName) &&
            !_settings.AvailableConnections.Contains(_settings.ConnectionName))
        {
            // Keep a connection that Windows no longer lists, rather than dropping it.
            _settings.AvailableConnections.Insert(0, _settings.ConnectionName);
        }

        foreach (var name in _settings.AvailableConnections)
        {
            ConnectionBox.Items.Add(name);
        }

        ConnectionBox.SelectedItem = _settings.ConnectionName;
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _settings.AvailableConnections = DialUpManager.ListConnections();
        Populate();
    }

    private void OnConnectNow(object sender, RoutedEventArgs e)
    {
        Capture();

        if (DialUpManager.Dial(_settings, out var message))
        {
            Dialogs.Info(this, message);
        }
        else
        {
            Dialogs.Warn(this, message);
        }
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        Capture();
        DialUpManager.HangUp(_settings);
        Dialogs.Info(this, "The connection was hung up.");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Capture();
        Saved = true;
        Close();
    }

    private void Capture()
    {
        _settings.Enabled = EnabledBox.IsChecked == true;
        _settings.ConnectionName = ConnectionBox.SelectedItem as string ?? string.Empty;
        _settings.UserName = UserBox.Text.Trim();
        var password = PasswordInput.Password;
        _settings.PasswordProtected = string.IsNullOrEmpty(password)
            ? null
            : CredentialProtector.Protect(password);
        _settings.DialOnlyWhenNeeded = OnlyWhenNeededBox.IsChecked == true;
        _settings.HangUpWhenFinished = HangUpBox.IsChecked == true;
        _settings.RedialAttempts = int.TryParse(AttemptsBox.Text.Trim(), out var attempts)
            ? Math.Clamp(attempts, 0, 1000)
            : 3;
        _settings.RedialIntervalSeconds = int.TryParse(IntervalBox.Text.Trim(), out var interval)
            ? Math.Clamp(interval, 1, 3600)
            : 30;
    }
}
