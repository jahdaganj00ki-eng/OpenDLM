using System.Windows;
using Microsoft.Win32;
using OpenDLM.Core.Models;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Services;

/// <summary>
/// Swaps the palette dictionary at runtime.
///
/// Every control references its colours with DynamicResource, so replacing the
/// dictionary re-colours the whole application with no window reload. Note that the
/// commercial download manager this project is modelled on has no theme setting at
/// all; a dark mode is an addition here, not a parity feature.
/// </summary>
public static class ThemeManager
{
    private const string LightSource = "Theme/Light.xaml";
    private const string DarkSource = "Theme/Dark.xaml";

    /// <summary>The palette actually in force (never <see cref="AppTheme.System"/>).</summary>
    public static AppTheme EffectiveTheme { get; private set; } = AppTheme.Light;

    public static void Apply(AppTheme requested)
    {
        var resolved = requested == AppTheme.System
            ? (IsSystemDark() ? AppTheme.Dark : AppTheme.Light)
            : requested;

        EffectiveTheme = resolved;

        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        try
        {
            var dictionaries = application.Resources.MergedDictionaries;
            var source = resolved == AppTheme.Dark ? DarkSource : LightSource;
            var replacement = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/{source}", UriKind.Absolute)
            };

            var existing = dictionaries.FirstOrDefault(IsPalette);
            if (existing is null)
            {
                // Must come after the control styles so DynamicResource lookups see it.
                dictionaries.Insert(0, replacement);
            }
            else
            {
                dictionaries[dictionaries.IndexOf(existing)] = replacement;
            }

            Log.Info($"Theme applied: {resolved}.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply the theme.", ex);
        }
    }

    private static bool IsPalette(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null &&
               (source.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads the Windows "app mode" preference so the System theme can follow it.</summary>
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int useLight && useLight == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the system theme preference: " + ex.Message);
            return false;
        }
    }
}
