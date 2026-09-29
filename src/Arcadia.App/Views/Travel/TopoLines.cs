using System.Windows;
using System.Windows.Media;

namespace Arcadia.App.Views.Travel;

/// <summary>
/// Faint topographic contour lines (procedural, seeded) — Travel's quiet "map paper" texture behind headers and
/// panels. Drawn once per size into a frozen geometry.
/// </summary>
public sealed class TopoLines : FrameworkElement
{
    private Geometry? _cache;
    private Size _cacheSize;

    public TopoLines() => IsHitTestVisible = false;

    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(nameof(Seed), typeof(int), typeof(TopoLines),
        new FrameworkPropertyMetadata(7, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TopoLines)d)._cache = null));
    public int Seed { get => (int)GetValue(SeedProperty); set => SetValue(SeedProperty, value); }

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(TopoLines),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x26, 0x45, 0xD2, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Contour spacing in pixels.</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(TopoLines),
        new FrameworkPropertyMetadata(22.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TopoLines)d)._cache = null));
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 4 || size.Height < 4) return;
        if (_cache == null || _cacheSize != size)
        {
            _cache = Build(size);
            _cacheSize = size;
        }
        var pen = new Pen(Stroke, 1);
        pen.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(size)));
        dc.DrawGeometry(null, pen, _cache);
        dc.Pop();
    }

    private Geometry Build(Size size)
    {
        var rng = new Random(Seed);
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            // two or three "hills", each a family of wobbly closed contours
            var hills = 2 + rng.Next(2);
            for (int h = 0; h < hills; h++)
            {
                var cx = size.Width * (0.15 + rng.NextDouble() * 0.7);
                var cy = size.Height * (0.2 + rng.NextDouble() * 0.9);
                var phase = rng.NextDouble() * Math.PI * 2;
                var a1 = 0.10 + rng.NextDouble() * 0.12;
                var a2 = 0.05 + rng.NextDouble() * 0.08;
                var maxR = Math.Max(size.Width, size.Height) * (0.45 + rng.NextDouble() * 0.3);
                for (var r = Spacing; r < maxR; r += Spacing)
                {
                    const int steps = 90;
                    var pts = new List<Point>(steps);
                    for (int i = 0; i < steps; i++)
                    {
                        var t = i * Math.PI * 2 / steps;
                        var wobble = 1 + a1 * Math.Sin(3 * t + phase + r * 0.013) + a2 * Math.Sin(5 * t - phase * 1.7 + r * 0.021);
                        pts.Add(new Point(cx + Math.Cos(t) * r * wobble * 1.35, cy + Math.Sin(t) * r * wobble * 0.8));
                    }
                    ctx.BeginFigure(pts[0], false, true);
                    ctx.PolyBezierTo(Smooth(pts), true, true);
                }
            }
        }
        sg.Freeze();
        return sg;
    }

    /// <summary>Catmull-Rom → Bézier control points for a closed smooth curve.</summary>
    private static List<Point> Smooth(List<Point> p)
    {
        var result = new List<Point>(p.Count * 3);
        int n = p.Count;
        for (int i = 0; i < n; i++)
        {
            Point p0 = p[(i - 1 + n) % n], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[(i + 2) % n];
            result.Add(new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6));
            result.Add(new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6));
            result.Add(p2);
        }
        return result;
    }
}
