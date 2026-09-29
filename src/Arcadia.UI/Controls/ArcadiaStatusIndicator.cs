using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Arcadia.UI.Media;

namespace Arcadia.UI.Controls;

/// <summary>Small bevelled status plate ("Backlog", "Playing", "High").</summary>
public class ArcadiaStatusIndicator : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ArcadiaStatusIndicator));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Color), typeof(ArcadiaStatusIndicator), new PropertyMetadata(Tones.Blue, (d, e) => ((ArcadiaStatusIndicator)d).ApplyTone((Color)e.NewValue)));

    private static readonly DependencyPropertyKey PlateBrushKey = DependencyProperty.RegisterReadOnly(nameof(PlateBrush), typeof(Brush), typeof(ArcadiaStatusIndicator), new PropertyMetadata(null));
    private static readonly DependencyPropertyKey PlateBorderKey = DependencyProperty.RegisterReadOnly(nameof(PlateBorder), typeof(Brush), typeof(ArcadiaStatusIndicator), new PropertyMetadata(null));
    private static readonly DependencyPropertyKey PlateTextKey = DependencyProperty.RegisterReadOnly(nameof(PlateText), typeof(Brush), typeof(ArcadiaStatusIndicator), new PropertyMetadata(null));
    public static readonly DependencyProperty PlateBrushProperty = PlateBrushKey.DependencyProperty;
    public static readonly DependencyProperty PlateBorderProperty = PlateBorderKey.DependencyProperty;
    public static readonly DependencyProperty PlateTextProperty = PlateTextKey.DependencyProperty;

    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Color Tone { get => (Color)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public Brush? PlateBrush => (Brush?)GetValue(PlateBrushProperty);
    public Brush? PlateBorder => (Brush?)GetValue(PlateBorderProperty);
    public Brush? PlateText => (Brush?)GetValue(PlateTextProperty);

    static ArcadiaStatusIndicator()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ArcadiaStatusIndicator), new FrameworkPropertyMetadata(typeof(ArcadiaStatusIndicator)));
        FocusableProperty.OverrideMetadata(typeof(ArcadiaStatusIndicator), new FrameworkPropertyMetadata(false));
    }

    public ArcadiaStatusIndicator() => ApplyTone(Tone);

    private void ApplyTone(Color c)
    {
        SetValue(PlateBrushKey, Tones.Frozen(new LinearGradientBrush(
            [new GradientStop(Tones.Mix(c, Colors.White, 0.2), 0), new GradientStop(c, 0.5), new GradientStop(Tones.Mix(c, Colors.Black, 0.2), 1)], 90)));
        SetValue(PlateBorderKey, Tones.Frozen(new SolidColorBrush(Tones.Mix(c, Colors.Black, 0.45))));
        bool light = c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 150;
        SetValue(PlateTextKey, Tones.Frozen(new SolidColorBrush(light ? Color.FromRgb(0x1C, 0x13, 0x02) : Colors.White)));
    }
}
