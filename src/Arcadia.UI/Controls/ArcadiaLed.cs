using System.Windows;
using System.Windows.Media;
using Arcadia.UI.Media;

namespace Arcadia.UI.Controls;

/// <summary>
/// A small glass status lamp (green/amber/red/blue) with a soft halo — the equipment-panel light used across
/// IT Tools. Off lamps are drawn dark but still visible, so the state is readable without colour alone
/// when paired with text.
/// </summary>
public sealed class ArcadiaLed : FrameworkElement
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Color), typeof(ArcadiaLed), new FrameworkPropertyMetadata(Tones.Green, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(ArcadiaLed), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public Color Tone { get => (Color)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

    public ArcadiaLed()
    {
        Width = 10;
        Height = 10;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2;
        var tone = IsOn ? Tone : Tones.Mix(Tone, Color.FromRgb(0x10, 0x14, 0x1C), 0.8);

        if (IsOn)
        {
            var halo = new RadialGradientBrush(Color.FromArgb(0x70, tone.R, tone.G, tone.B), Color.FromArgb(0, tone.R, tone.G, tone.B));
            halo.Freeze();
            dc.DrawEllipse(halo, null, c, r * 1.9, r * 1.9);
        }

        var bulb = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.3),
            Center = new Point(0.45, 0.4),
            RadiusX = 0.7,
            RadiusY = 0.7,
            GradientStops =
            [
                new GradientStop(Tones.Mix(tone, Colors.White, IsOn ? 0.65 : 0.15), 0),
                new GradientStop(tone, 0.45),
                new GradientStop(Tones.Mix(tone, Colors.Black, 0.55), 1),
            ],
        };
        bulb.Freeze();
        var rim = new Pen(new SolidColorBrush(Color.FromRgb(0x04, 0x06, 0x0A)), Math.Max(1, size / 10));
        rim.Freeze();
        dc.DrawEllipse(bulb, rim, c, r - rim.Thickness / 2, r - rim.Thickness / 2);
    }
}
