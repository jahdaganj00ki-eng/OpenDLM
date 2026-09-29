using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OpenDLM.App.Controls;

/// <summary>
/// Turns an icon resource key such as "Icon.Folder" into a parsed
/// <see cref="Geometry"/> for a <c>Path</c>.
///
/// Returning a parsed Geometry rather than the raw string matters: a binding result
/// is not guaranteed to pass through the target property's type converter, so a bare
/// string would appear to work in some places and silently draw nothing in others.
/// </summary>
public sealed class IconLookupConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        try
        {
            return Application.Current?.TryFindResource(key) is string data && data.Length > 0
                ? Geometry.Parse(data)
                : null;
        }
        catch (Exception)
        {
            // A malformed geometry must never take the window down.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Icon keys are read-only.");
}
