using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenDLM.Core.Util;

namespace OpenDLM.App.Services;

/// <summary>
/// Generates the OpenDLM mark in code: a rounded blue tile with a white downward
/// arrow landing in a tray.
///
/// Drawing it rather than shipping a bitmap keeps the repository free of binary
/// image assets and guarantees the tray, taskbar and window icons are always
/// identical and DPI-correct.
/// </summary>
public static class AppIcons
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Creates a native icon of the requested pixel size.</summary>
    public static Icon CreateTrayIcon(int size = 32)
    {
        using var bitmap = Render(size);

        // Clone so the returned Icon owns its own copy and the HICON can be freed.
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Creates a WPF image source for a window icon.</summary>
    public static ImageSource CreateImageSource(int size = 32)
    {
        using var icon = CreateTrayIcon(size);
        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static Bitmap Render(int size)
    {
        size = Math.Clamp(size, 8, 256);

        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.Clear(System.Drawing.Color.Transparent);

        var bounds = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);

        using (var tile = RoundedRectangle(bounds, size * 0.22f))
        // Fully qualified: LinearGradientBrush exists in both System.Drawing and
        // System.Windows.Media, and both namespaces are imported in this file.
        using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(
                   new RectangleF(0, 0, size, size),
                   System.Drawing.Color.FromArgb(255, 31, 111, 196),
                   System.Drawing.Color.FromArgb(255, 66, 150, 232),
                   45f))
        {
            graphics.FillPath(fill, tile);
        }

        var width = (float)size;
        using var ink = new SolidBrush(System.Drawing.Color.White);

        // Downward arrow.
        using (var arrow = new GraphicsPath())
        {
            arrow.AddPolygon(new[]
            {
                new PointF(width * 0.435f, width * 0.14f),
                new PointF(width * 0.565f, width * 0.14f),
                new PointF(width * 0.565f, width * 0.47f),
                new PointF(width * 0.720f, width * 0.47f),
                new PointF(width * 0.500f, width * 0.72f),
                new PointF(width * 0.280f, width * 0.47f),
                new PointF(width * 0.435f, width * 0.47f)
            });
            graphics.FillPath(ink, arrow);
        }

        // Tray the arrow lands in.
        using (var tray = RoundedRectangle(
                   new RectangleF(width * 0.23f, width * 0.79f, width * 0.54f, width * 0.105f),
                   width * 0.05f))
        {
            graphics.FillPath(ink, tray);
        }

        return bitmap;
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(0.1f, radius * 2f);

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
