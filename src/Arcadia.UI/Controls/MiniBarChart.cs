using System.Collections;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Arcadia.UI.Media;

namespace Arcadia.UI.Controls;

/// <summary>Tiny equaliser-style bar chart, like the level meters of classic media players.</summary>
public sealed class MiniBarChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(MiniBarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Color), typeof(MiniBarChart), new FrameworkPropertyMetadata(Tones.Blue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GrowProperty = DependencyProperty.Register(
        nameof(Grow), typeof(double), typeof(MiniBarChart), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Color Tone { get => (Color)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public double Grow { get => (double)GetValue(GrowProperty); set => SetValue(GrowProperty, value); }

    public MiniBarChart()
    {
        Loaded += (_, _) =>
        {
            if (Motion.AnimationsEnabled)
                BeginAnimation(GrowProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = Motion.EaseOut, BeginTime = TimeSpan.FromMilliseconds(150) });
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values?.Cast<object>().Select(Convert.ToDouble).ToArray();
        if (values == null || values.Length == 0) return;
        double w = ActualWidth, h = ActualHeight;
        double max = Math.Max(values.Max(), 0.0001);
        const double gap = 2;
        double bw = Math.Max(1.5, (w - gap * (values.Length - 1)) / values.Length);
        var lit = Tones.BarFill(Tone);
        var dim = Tones.Frozen(new SolidColorBrush(Tones.WithAlpha(Tone, 0.28)));
        int litCount = (int)Math.Ceiling(values.Length * 0.75);
        for (int i = 0; i < values.Length; i++)
        {
            double bh = Math.Max(2, (0.18 + 0.82 * values[i] / max) * h * Grow);
            dc.DrawRectangle(i < litCount ? lit : dim, null, new Rect(i * (bw + gap), h - bh, bw, bh));
        }
    }
}
