using System.Windows;
using System.Windows.Media;

namespace OpenDLM.App.Controls;

/// <summary>
/// A rolling throughput graph, drawn directly with <see cref="DrawingContext"/>.
///
/// Written by hand rather than pulled from a charting library: this is a single
/// polyline behind a filled area, and a charting dependency would dwarf the rest of
/// the application for no benefit.
/// </summary>
public sealed class SpeedGraph : FrameworkElement
{
    private readonly Queue<double> _samples = new();

    private Brush _areaBrush = Brushes.Transparent;
    private Pen _linePen = new(Brushes.Gray, 1);

    /// <summary>How many samples are kept. At two samples per second this is one minute.</summary>
    public int Capacity { get; set; } = 120;

    /// <summary>Colour of the filled area under the curve.</summary>
    public Brush AreaBrush
    {
        get => _areaBrush;
        set
        {
            _areaBrush = value;
            InvalidateVisual();
        }
    }

    /// <summary>Colour of the curve itself.</summary>
    public Brush LineBrush
    {
        get => _linePen.Brush;
        set
        {
            _linePen = new Pen(value, 1.4);
            InvalidateVisual();
        }
    }

    /// <summary>Colour of the baseline.</summary>
    public Brush AxisBrush { get; set; } = Brushes.Gray;

    /// <summary>Adds one throughput reading, in bytes per second.</summary>
    public void Push(double bytesPerSecond)
    {
        if (Capacity < 2)
        {
            Capacity = 2;
        }

        _samples.Enqueue(Math.Max(0, bytesPerSecond));
        while (_samples.Count > Capacity)
        {
            _samples.Dequeue();
        }

        InvalidateVisual();
    }

    public void Clear()
    {
        _samples.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 2 || height <= 2)
        {
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Baseline plus a faint quarter line so the graph reads as a scale.
        drawingContext.DrawLine(new Pen(AxisBrush, 1), new Point(0, height - 1), new Point(width, height - 1));
        drawingContext.DrawLine(
            new Pen(AxisBrush, 1) { Brush = AxisBrush, Thickness = 1, DashStyle = DashStyles.Dot },
            new Point(0, height / 2), new Point(width, height / 2));

        if (_samples.Count < 2)
        {
            DrawPlaceholder(drawingContext, width, height, pixelsPerDip);
            return;
        }

        var peak = _samples.Max();
        if (peak < 1)
        {
            peak = 1;
        }

        var values = _samples.ToArray();
        var step = width / Math.Max(1, Capacity - 1);
        var offset = width - (values.Length - 1) * step;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var startX = offset;
            var startY = height - (values[0] / peak * (height - 3)) - 1;

            context.BeginFigure(new Point(startX, height - 1), isFilled: true, isClosed: true);
            context.LineTo(new Point(startX, startY), isStroked: true, isSmoothJoin: false);

            for (var index = 1; index < values.Length; index++)
            {
                var x = offset + index * step;
                var y = height - (values[index] / peak * (height - 3)) - 1;
                context.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: false);
            }

            context.LineTo(new Point(offset + (values.Length - 1) * step, height - 1), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(AreaBrush, _linePen, geometry);

        // Peak label, so the graph is readable without a legend.
        var label = new FormattedText(
            Core.Util.Fmt.Speed(peak),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            10,
            _linePen.Brush,
            pixelsPerDip);

        drawingContext.DrawText(label, new Point(4, 2));
    }

    private void DrawPlaceholder(DrawingContext drawingContext, double width, double height, double pixelsPerDip)
    {
        var text = new FormattedText(
            "Throughput appears here while downloading",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            AxisBrush,
            pixelsPerDip);

        drawingContext.DrawText(text, new Point(Math.Max(4, width / 2 - text.Width / 2), height / 2 - text.Height / 2));
    }
}
