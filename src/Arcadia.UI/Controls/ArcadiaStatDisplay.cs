using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Arcadia.UI.Media;

namespace Arcadia.UI.Controls;

/// <summary>
/// A tinted statistics slab: badge icon, big number, label, level meter and percentage.
/// </summary>
public class ArcadiaStatDisplay : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(ArcadiaStatDisplay));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(ArcadiaStatDisplay));
    public static readonly DependencyProperty PercentTextProperty = DependencyProperty.Register(nameof(PercentText), typeof(string), typeof(ArcadiaStatDisplay));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(ArcadiaStatDisplay));
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(nameof(Bars), typeof(IEnumerable), typeof(ArcadiaStatDisplay));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Color), typeof(ArcadiaStatDisplay), new PropertyMetadata(Tones.Blue, (d, e) => ((ArcadiaStatDisplay)d).ApplyTone((Color)e.NewValue)));

    private static readonly DependencyPropertyKey SlabBrushKey = DependencyProperty.RegisterReadOnly(nameof(SlabBrush), typeof(Brush), typeof(ArcadiaStatDisplay), new PropertyMetadata(null));
    private static readonly DependencyPropertyKey EdgeBrushKey = DependencyProperty.RegisterReadOnly(nameof(EdgeBrush), typeof(Brush), typeof(ArcadiaStatDisplay), new PropertyMetadata(null));
    private static readonly DependencyPropertyKey ToneTextBrushKey = DependencyProperty.RegisterReadOnly(nameof(ToneTextBrush), typeof(Brush), typeof(ArcadiaStatDisplay), new PropertyMetadata(null));
    private static readonly DependencyPropertyKey BadgeBrushKey = DependencyProperty.RegisterReadOnly(nameof(BadgeBrush), typeof(Brush), typeof(ArcadiaStatDisplay), new PropertyMetadata(null));
    public static readonly DependencyProperty SlabBrushProperty = SlabBrushKey.DependencyProperty;
    public static readonly DependencyProperty EdgeBrushProperty = EdgeBrushKey.DependencyProperty;
    public static readonly DependencyProperty ToneTextBrushProperty = ToneTextBrushKey.DependencyProperty;
    public static readonly DependencyProperty BadgeBrushProperty = BadgeBrushKey.DependencyProperty;

    public string? Value { get => (string?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? PercentText { get => (string?)GetValue(PercentTextProperty); set => SetValue(PercentTextProperty, value); }
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public IEnumerable? Bars { get => (IEnumerable?)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public Color Tone { get => (Color)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public Brush? SlabBrush => (Brush?)GetValue(SlabBrushProperty);
    public Brush? EdgeBrush => (Brush?)GetValue(EdgeBrushProperty);
    public Brush? ToneTextBrush => (Brush?)GetValue(ToneTextBrushProperty);
    public Brush? BadgeBrush => (Brush?)GetValue(BadgeBrushProperty);

    static ArcadiaStatDisplay()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ArcadiaStatDisplay), new FrameworkPropertyMetadata(typeof(ArcadiaStatDisplay)));
    }

    public ArcadiaStatDisplay() => ApplyTone(Tone);

    private void ApplyTone(Color c)
    {
        SetValue(SlabBrushKey, Tones.Slab(c));
        SetValue(EdgeBrushKey, Tones.Frozen(new SolidColorBrush(Tones.Mix(c, Colors.Black, 0.15))));
        SetValue(ToneTextBrushKey, Tones.Text(c));
        SetValue(BadgeBrushKey, Tones.Frozen(new RadialGradientBrush(Tones.Mix(c, Colors.White, 0.3), Tones.Mix(c, Colors.Black, 0.2))
        {
            GradientOrigin = new Point(0.35, 0.3),
        }));
    }
}
