using System.Windows;
using System.Windows.Media;
using Arcadia.UI.Media;

namespace Arcadia.UI.Controls;

/// <summary>
/// Restrained live graph: a sunken oscilloscope-style well with a faint grid, one or two traces and a soft
/// fill under the primary trace. Values are drawn right-aligned (newest at the right edge) and the vertical
/// scale is fixed (<see cref="Maximum"/>) or follows the data. Rendering is a single cheap OnRender pass.
/// </summary>
public sealed class ArcadiaLineGraph : FrameworkElement
{
    private const FrameworkPropertyMetadataOptions Render = FrameworkPropertyMetadataOptions.AffectsRender;

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(ArcadiaLineGraph), new FrameworkPropertyMetadata(null, Render));

    public static readonly DependencyProperty SecondaryValuesProperty = DependencyProperty.Register(
        nameof(SecondaryValues), typeof(IReadOnlyList<double>), typeof(ArcadiaLineGraph), new FrameworkPropertyMetadata(null, Render));

    /// <summary>Fixed top of the scale (e.g. 100 for percentages); 0 = scale to the data.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ArcadiaLineGraph), new FrameworkPropertyMetadata(100.0, Render));

    /// <summary>How many samples span the full width.</summary>
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(ArcadiaLineGraph), new FrameworkPropertyMetadata(60, Render));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Color), typeof(ArcadiaLineGraph), new FrameworkPropertyMetadata(Tones.Cyan, Render));

    public static readonly DependencyProperty SecondaryToneProperty = DependencyProperty.Register(
        nameof(SecondaryTone), typeof(Color), typeof(ArcadiaLineGraph), new FrameworkPropertyMetadata(Tones.Amber, Render));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<double>? SecondaryValues { get => (IReadOnlyList<double>?)GetValue(SecondaryValuesProperty); set => SetValue(SecondaryValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public Color Tone { get => (Color)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public Color SecondaryTone { get => (Color)GetValue(SecondaryToneProperty); set => SetValue(SecondaryToneProperty, value); }

    private static readonly Brush Well = Tones.Frozen(new LinearGradientBrush(Color.FromRgb(0x05, 0x08, 0x0D), Color.FromRgb(0x09, 0x0E, 0x16), 90));
    private static readonly Pen WellEdge = FrozenPen(Color.FromRgb(0x1A, 0x25, 0x36), 1);
    private static readonly Pen GridLine = FrozenPen(Color.FromArgb(0x30, 0x4A, 0x6A, 0x96), 1);

    public ArcadiaLineGraph()
    {
        SnapsToDevicePixels = true;
        FlowDirection = FlowDirection.LeftToRight; // time runs left → right in every language
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width < 4 || size.Height < 4) return;
        var bounds = new Rect(size);
        dc.DrawRoundedRectangle(Well, WellEdge, new Rect(0.5, 0.5, size.Width - 1, size.Height - 1), 2, 2);
        dc.PushClip(new RectangleGeometry(new Rect(1, 1, size.Width - 2, size.Height - 2)));

        for (int i = 1; i < 4; i++)
        {
            double y = Math.Round(size.Height * i / 4) + 0.5;
            dc.DrawLine(GridLine, new Point(1, y), new Point(size.Width - 1, y));
        }
        int columns = Math.Max(2, (int)(size.Width / 48));
        for (int i = 1; i < columns; i++)
        {
            double x = Math.Round(size.Width * i / columns) + 0.5;
            dc.DrawLine(GridLine, new Point(x, 1), new Point(x, size.Height - 1));
        }

        double max = Maximum > 0 ? Maximum : Math.Max(1, Math.Max(Peak(Values), Peak(SecondaryValues)) * 1.15);
        if (SecondaryValues is { Count: > 1 } second) DrawTrace(dc, second, max, SecondaryTone, fill: false, bounds);
        if (Values is { Count: > 1 } first) DrawTrace(dc, first, max, Tone, fill: true, bounds);
        dc.Pop();
    }

    private static double Peak(IReadOnlyList<double>? values) => values is { Count: > 0 } ? values.Max() : 0;

    private void DrawTrace(DrawingContext dc, IReadOnlyList<double> values, double max, Color tone, bool fill, Rect bounds)
    {
        int capacity = Math.Max(2, Capacity);
        double step = (bounds.Width - 2) / (capacity - 1);
        int start = Math.Max(0, values.Count - capacity);
        double x0 = bounds.Right - 1 - (values.Count - start - 1) * step;
        double bottom = bounds.Bottom - 2, height = bounds.Height - 6;

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            for (int i = start; i < values.Count; i++)
            {
                var p = new Point(x0 + (i - start) * step, bottom - Math.Clamp(values[i] / max, 0, 1) * height);
                if (i == start) g.BeginFigure(p, false, false);
                else g.LineTo(p, true, true);
            }
        }
        line.Freeze();

        if (fill)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(x0, bottom), true, true);
                for (int i = start; i < values.Count; i++)
                    g.LineTo(new Point(x0 + (i - start) * step, bottom - Math.Clamp(values[i] / max, 0, 1) * height), true, true);
                g.LineTo(new Point(bounds.Right - 1, bottom), true, true);
            }
            area.Freeze();
            var brush = new LinearGradientBrush(Color.FromArgb(0x55, tone.R, tone.G, tone.B), Color.FromArgb(0x04, tone.R, tone.G, tone.B), 90);
            brush.Freeze();
            dc.DrawGeometry(brush, null, area);
        }

        var glow = FrozenPen(Color.FromArgb(0x40, tone.R, tone.G, tone.B), 4);
        dc.DrawGeometry(null, glow, line);
        dc.DrawGeometry(null, FrozenPen(Tones.Mix(tone, Colors.White, 0.25), 1.4), line);
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
