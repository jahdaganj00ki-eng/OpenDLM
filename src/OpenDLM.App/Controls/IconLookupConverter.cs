using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OpenDLM.App.Controls;

/// <summary>
/// Resolves an icon key such as "Icon.Folder" to the <see cref="Geometry"/> the
/// category tree needs for its <c>Path.Data</c>.
///
/// The icon resources are declared as Geometry, so the usual case is a straight
/// handover; a string is still accepted and parsed, because the toolbar stores its
/// icon in a Button's Tag and a Tag can hold either.
/// </summary>
public sealed class IconLookupConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Geometry geometry)
        {
            return geometry;
        }

        if (value is not string key || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        try
        {
            switch (Application.Current?.TryFindResource(key))
            {
                case Geometry resource:
                    return resource;
                case string data when data.Length > 0:
                    return Geometry.Parse(data);
                default:
                    return null;
            }
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
