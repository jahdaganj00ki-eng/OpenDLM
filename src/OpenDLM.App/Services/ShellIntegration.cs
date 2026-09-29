using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Services;

/// <summary>
/// The few places where OpenDLM touches the Windows shell and the registry.
///
/// Everything is per-user (HKEY_CURRENT_USER) so the application never needs
/// administrator rights.
/// </summary>
public static class ShellIntegration
{
    private const string ProductKeyPath = @"Software\OpenDLM";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "OpenDLM";

    /// <summary>
    /// Publishes the installation folder so the browser native messaging host can
    /// find and start the application when it is not running yet.
    /// </summary>
    public static void RegisterInstallPath()
    {
        try
        {
            var directory = AppPaths.ApplicationDirectory;
            var hostPath = Path.Combine(directory, "OpenDLMNativeHost.exe");

            using var key = Registry.CurrentUser.CreateSubKey(ProductKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            key.SetValue("InstallPath", directory, RegistryValueKind.String);
            key.SetValue("ExecutablePath", Path.Combine(directory, "OpenDLM.exe"), RegistryValueKind.String);
            key.SetValue("NativeHostPath", hostPath, RegistryValueKind.String);
            key.SetValue("NativeHostName", AppPaths.NativeHostName, RegistryValueKind.String);
            key.SetValue("Version", AppPaths.Version, RegistryValueKind.String);
            key.SetValue("PipeName", AppPaths.PipeName, RegistryValueKind.String);

            Log.Info("Published the install path for the browser native host.");
        }
        catch (Exception ex)
        {
            // Not fatal: the extension can still reach a manually started app.
            Log.Warn("Could not publish the install path: " + ex.Message);
        }
    }

    /// <summary>Adds or removes the per-user "start with Windows" entry.</summary>
    public static bool SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var executable = Path.Combine(AppPaths.ApplicationDirectory, "OpenDLM.exe");
                key.SetValue(RunValueName, $"\"{executable}\" --minimized", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            Log.Info($"Start with Windows set to {enabled}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not change the start-with-Windows entry: " + ex.Message);
            return false;
        }
    }

    public static bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the start-with-Windows entry: " + ex.Message);
            return false;
        }
    }

    /// <summary>Opens a URL or folder with the default handler.</summary>
    public static void OpenInShell(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open '{target}': {ex.Message}");
        }
    }

    /// <summary>Reveals the folder that holds the settings and the download list.</summary>
    public static void OpenDataFolder() => OpenInShell(AppPaths.RoamingRoot);

    public static void OpenLogFolder() => OpenInShell(AppPaths.LogDirectory);

    /// <summary>Product name plus version and build location, for the About dialog.</summary>
    public static string DescribeBuild()
    {
        var assembly = Assembly.GetEntryAssembly();
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var version = AppPaths.Version;
        var location = AppPaths.ApplicationDirectory;
        var pointerSize = Environment.Is64BitProcess ? "64-bit" : "32-bit";

        return $"Version {version} ({pointerSize}){Environment.NewLine}" +
               $"Built from {location}{Environment.NewLine}" +
               (string.IsNullOrWhiteSpace(informational) ? string.Empty : $"Build: {informational}");
    }
}
