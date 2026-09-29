using System.Windows;
using System.Windows.Controls;

namespace Arcadia.UI.Controls;

public enum ArcadiaButtonVariant
{
    /// <summary>Bevelled blue plate — the one obvious action in an area.</summary>
    Primary,
    /// <summary>Graphite plate for secondary actions.</summary>
    Secondary,
    /// <summary>Text-only link style ("View All").</summary>
    Link,
    /// <summary>Icon-only tool button for toolbars and the header.</summary>
    Tool,
}

public class ArcadiaButton : Button
{
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(ArcadiaButtonVariant), typeof(ArcadiaButton), new PropertyMetadata(ArcadiaButtonVariant.Secondary));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(ArcadiaButton));
    public static readonly DependencyProperty TrailingIconProperty = DependencyProperty.Register(nameof(TrailingIcon), typeof(string), typeof(ArcadiaButton));
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(ArcadiaButton), new PropertyMetadata(16.0));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(ArcadiaButton), new PropertyMetadata(new CornerRadius(3)));

    public ArcadiaButtonVariant Variant { get => (ArcadiaButtonVariant)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? TrailingIcon { get => (string?)GetValue(TrailingIconProperty); set => SetValue(TrailingIconProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    static ArcadiaButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ArcadiaButton), new FrameworkPropertyMetadata(typeof(ArcadiaButton)));
    }
}
