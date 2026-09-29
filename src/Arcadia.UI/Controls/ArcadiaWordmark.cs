using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Arcadia.UI.Controls;

/// <summary>
/// Letter-spaced metallic wordmark (WPF TextBlock has no tracking). Used for the ARCADIA brand and
/// for large typographic game titles over artwork.
/// </summary>
public sealed class ArcadiaWordmark : FrameworkElement
{
    private const FrameworkPropertyMetadataOptions Layout = FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ArcadiaWordmark), new FrameworkPropertyMetadata("ARCADIA", Layout, OnGlyphsChanged));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(ArcadiaWordmark), new FrameworkPropertyMetadata(40.0, Layout, OnGlyphsChanged));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(ArcadiaWordmark), new FrameworkPropertyMetadata(new FontFamily("Bahnschrift"), Layout, OnGlyphsChanged));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(ArcadiaWordmark), new FrameworkPropertyMetadata(FontWeights.Bold, Layout, OnGlyphsChanged));

    /// <summary>Extra spacing between letters as a fraction of the font size.</summary>
    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(
        nameof(Tracking), typeof(double), typeof(ArcadiaWordmark), new FrameworkPropertyMetadata(0.08, Layout, OnGlyphsChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ArcadiaWordmark), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutlineProperty = DependencyProperty.Register(
        nameof(Outline), typeof(Brush), typeof(ArcadiaWordmark), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowColorProperty = DependencyProperty.Register(
        nameof(GlowColor), typeof(Color), typeof(ArcadiaWordmark), new PropertyMetadata(Color.FromRgb(0x3F, 0x8C, 0xFF), OnGlowChanged));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Outline { get => (Brush?)GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }
    public Color GlowColor { get => (Color)GetValue(GlowColorProperty); set => SetValue(GlowColorProperty, value); }

    private Geometry? _glyphs;

    public ArcadiaWordmark()
    {
        Effect = new DropShadowEffect { Color = GlowColor, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 };
        FlowDirection = FlowDirection.LeftToRight; // brand marks never mirror
    }

    private static void OnGlyphsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ArcadiaWordmark)d)._glyphs = null;

    private static void OnGlowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (((ArcadiaWordmark)d).Effect is DropShadowEffect fx) fx.Color = (Color)e.NewValue;
    }

    private Geometry BuildGlyphs()
    {
        if (_glyphs != null) return _glyphs;
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double x = 0;
        var text = Text ?? string.Empty;
        for (int i = 0; i < text.Length; i++)
        {
            var ft = new FormattedText(text[i].ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, Brushes.White, dpi);
            group.Children.Add(ft.BuildGeometry(new Point(x, 0)));
            x += ft.WidthIncludingTrailingWhitespace + (i < text.Length - 1 ? Tracking * FontSize : 0);
        }
        group.Freeze();
        _glyphs = group;
        return group;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var b = BuildGlyphs().Bounds;
        return b.IsEmpty ? new Size() : new Size(b.Right + 2, b.Bottom + 3);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var glyphs = BuildGlyphs();
        if (glyphs.Bounds.IsEmpty) return;
        var fill = Fill ?? DefaultChrome(glyphs.Bounds);
        dc.PushTransform(new TranslateTransform(0, Math.Max(1, FontSize / 28)));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), null, glyphs);
        dc.Pop();
        dc.DrawGeometry(fill, Outline != null ? new Pen(Outline, Math.Max(0.6, FontSize / 60)) : null, glyphs);
    }

    private static Brush DefaultChrome(Rect b)
    {
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, b.Top), EndPoint = new Point(0, b.Bottom) };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE4, 0xEB, 0xF5), 0.46));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x9C, 0xAE, 0xC8), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xC9, 0xD6, 0xE8), 0.78));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF4, 0xF8, 0xFF), 1));
        brush.Freeze();
        return brush;
    }
}
