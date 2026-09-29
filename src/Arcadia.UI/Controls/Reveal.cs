using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Arcadia.UI.Controls;

/// <summary>
/// Attached behaviour: panels fade and settle into place when first loaded, optionally staggered.
/// </summary>
public static class Reveal
{
    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(double), typeof(Reveal), new PropertyMetadata(-1.0, OnDelayChanged));

    public static double GetDelay(DependencyObject d) => (double)d.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject d, double value) => d.SetValue(DelayProperty, value);

    private static void OnDelayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || (double)e.NewValue < 0) return;
        fe.Loaded -= OnLoaded;
        fe.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        fe.Loaded -= OnLoaded;
        if (!Motion.AnimationsEnabled) return;

        var delay = TimeSpan.FromMilliseconds(GetDelay(fe));
        var translate = new TranslateTransform(0, 12);
        fe.RenderTransform = translate;
        fe.Opacity = 0; // base value during the stagger delay; the animation holds at 1 afterwards
        fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion.Slow) { BeginTime = delay, EasingFunction = Motion.EaseOut });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, Motion.Slow) { BeginTime = delay, EasingFunction = Motion.EaseOut });
    }
}
