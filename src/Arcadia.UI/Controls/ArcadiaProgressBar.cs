using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Arcadia.UI.Media;

namespace Arcadia.UI.Controls;

/// <summary>
/// Recessed progress groove with a glossy, lit fill. Animates to its value when first shown and on change.
/// </summary>
public sealed class ArcadiaProgressBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcadiaProgressBar), new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Color), typeof(ArcadiaProgressBar),
        new FrameworkPropertyMetadata(Tones.Green, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ArcadiaProgressBar)d)._fill = null));

    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(ArcadiaProgressBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0–100.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Color Tone { get => (Color)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }

    private Brush? _fill;
    private static readonly Brush Groove = Tones.Frozen(new LinearGradientBrush(Color.FromRgb(0x05, 0x07, 0x0B), Color.FromRgb(0x1A, 0x21, 0x2C), 90));
    private static readonly Pen GrooveEdge = FreezePen(new Pen(Tones.Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x2E, 0x3A, 0x4C))), 1));
    private static readonly Brush Specular = Tones.Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));

    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    public ArcadiaProgressBar()
    {
        Height = 8;
        Loaded += (_, _) => AnimateTo(Value, fromZero: true);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (ArcadiaProgressBar)d;
        if (bar.IsLoaded) bar.AnimateTo((double)e.NewValue, fromZero: false);
        else bar.DisplayValue = (double)e.NewValue;
    }

    private void AnimateTo(double value, bool fromZero)
    {
        value = Math.Clamp(value, 0, 100);
        if (!Motion.AnimationsEnabled)
        {
            BeginAnimation(DisplayValueProperty, null);
            DisplayValue = value;
            return;
        }
        var anim = new DoubleAnimation(fromZero ? 0 : DisplayValue, value, TimeSpan.FromMilliseconds(fromZero ? 700 : 300))
        {
            EasingFunction = Motion.EaseOut,
            BeginTime = fromZero ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero,
        };
        BeginAnimation(DisplayValueProperty, anim);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2) return;
        double r = Math.Min(2, h / 2);
        dc.DrawRoundedRectangle(Groove, GrooveEdge, new Rect(0.5, 0.5, w - 1, h - 1), r, r);

        double fw = (w - 2) * Math.Clamp(DisplayValue, 0, 100) / 100;
        if (fw < 0.5) return;
        _fill ??= Tones.BarFill(Tone);
        dc.DrawRoundedRectangle(_fill, null, new Rect(1, 1, fw, h - 2), r, r);
        dc.DrawRectangle(Specular, null, new Rect(2, 1.5, Math.Max(0, fw - 2), Math.Max(0.6, (h - 2) * 0.22)));
        dc.DrawRectangle(new SolidColorBrush(Tones.WithAlpha(Tones.Mix(Tone, Colors.White, 0.6), 0.9)), null, new Rect(Math.Max(1, fw - 0.5), 1, 1.5, h - 2));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(ArcadiaProgressBar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;
        protected override string GetClassNameCore() => nameof(ArcadiaProgressBar);
        protected override bool IsControlElementCore() => true;
        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            var pct = $"{Math.Round(owner.Value)}%";
            return string.IsNullOrEmpty(name) ? pct : $"{name} {pct}";
        }
    }
}
