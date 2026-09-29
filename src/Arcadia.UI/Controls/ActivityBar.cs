using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Arcadia.UI.Media;

namespace Arcadia.UI.Controls;

/// <summary>
/// One column of an activity chart: a lit bar in a recessed slot, scaled against <see cref="MaxHours"/>.
/// Empty weeks show only the slot so the rhythm of play stays readable.
/// </summary>
public sealed class ActivityBar : FrameworkElement
{
    private const FrameworkPropertyMetadataOptions Render = FrameworkPropertyMetadataOptions.AffectsRender;

    public static readonly DependencyProperty HoursProperty = DependencyProperty.Register(nameof(Hours), typeof(double), typeof(ActivityBar), new FrameworkPropertyMetadata(0.0, Render));
    public static readonly DependencyProperty MaxHoursProperty = DependencyProperty.Register(nameof(MaxHours), typeof(double), typeof(ActivityBar), new FrameworkPropertyMetadata(1.0, Render));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(Color), typeof(ActivityBar), new FrameworkPropertyMetadata(Tones.Cyan, Render));
    public static readonly DependencyProperty GrowProperty = DependencyProperty.Register(nameof(Grow), typeof(double), typeof(ActivityBar), new FrameworkPropertyMetadata(1.0, Render));

    public double Hours { get => (double)GetValue(HoursProperty); set => SetValue(HoursProperty, value); }
    public double MaxHours { get => (double)GetValue(MaxHoursProperty); set => SetValue(MaxHoursProperty, value); }
    public Color Tone { get => (Color)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public double Grow { get => (double)GetValue(GrowProperty); set => SetValue(GrowProperty, value); }

    private static readonly Brush Slot = Tones.Frozen(new LinearGradientBrush(Color.FromRgb(0x05, 0x07, 0x0B), Color.FromRgb(0x11, 0x17, 0x21), 90));

    public ActivityBar()
    {
        Loaded += (_, _) =>
        {
            if (Motion.AnimationsEnabled)
                BeginAnimation(GrowProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = Motion.EaseOut });
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        dc.DrawRoundedRectangle(Slot, null, new Rect(0, 0, w, h), 2, 2);
        if (Hours <= 0) return;

        double ratio = Math.Clamp(Hours / Math.Max(MaxHours, 0.01), 0, 1);
        double bh = Math.Max(3, (h - 2) * ratio * Grow);
        var rect = new Rect(1, h - 1 - bh, w - 2, bh);
        var fill = new LinearGradientBrush(
            [new GradientStop(Tones.Mix(Tone, Colors.White, 0.35), 0), new GradientStop(Tone, 0.35), new GradientStop(Tones.Mix(Tone, Tones.Blue, 0.6), 1)], 90);
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, null, rect, 2, 2);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), null, new Rect(rect.X + 1, rect.Y, rect.Width - 2, 1));
    }
}
