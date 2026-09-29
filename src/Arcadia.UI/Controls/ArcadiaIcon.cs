using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Arcadia.UI.Media;

namespace Arcadia.UI.Controls;

/// <summary>
/// Duotone vector icon. Set <see cref="Kind"/> to an icon name from the Arcadia set (e.g. "Home"),
/// or supply <see cref="Data"/>/<see cref="AccentData"/> directly. Primary tone = Foreground.
/// </summary>
public class ArcadiaIcon : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(ArcadiaIcon), new PropertyMetadata(null, OnKindChanged));

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(ArcadiaIcon));

    public static readonly DependencyProperty AccentDataProperty = DependencyProperty.Register(
        nameof(AccentData), typeof(Geometry), typeof(ArcadiaIcon));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(ArcadiaIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public string? Kind { get => (string?)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Geometry? AccentData { get => (Geometry?)GetValue(AccentDataProperty); set => SetValue(AccentDataProperty, value); }
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    static ArcadiaIcon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ArcadiaIcon), new FrameworkPropertyMetadata(typeof(ArcadiaIcon)));
        FocusableProperty.OverrideMetadata(typeof(ArcadiaIcon), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ArcadiaIcon), new FrameworkPropertyMetadata(false));
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (ArcadiaIcon)d;
        var kind = e.NewValue as string;
        icon.Data = IconCatalog.Get(kind);
        icon.AccentData = IconCatalog.GetAccent(kind);
        // Objects (gamepad, play, wrench…) keep their shape in right-to-left layouts; only directional glyphs mirror.
        if (kind != null && !DirectionalKinds.Contains(kind))
            icon.SetCurrentValue(FlowDirectionProperty, FlowDirection.LeftToRight);
        else
            icon.ClearValue(FlowDirectionProperty);
    }

    private static readonly HashSet<string> DirectionalKinds = ["ArrowRight", "ChevronRight", "ChevronLeft"];
}
