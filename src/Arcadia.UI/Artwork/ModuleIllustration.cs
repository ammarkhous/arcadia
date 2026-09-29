using System.Windows;
using System.Windows.Media;
using Arcadia.UI.Media;

namespace Arcadia.UI.Artwork;

/// <summary>
/// Little lamp-lit desk still life for the Home module tiles: a hero object (rendered from the icon set
/// at large scale with sculpted shading), a secondary prop, a desk lamp and a plant.
/// </summary>
public sealed class ModuleIllustration : FrameworkElement
{
    private const FrameworkPropertyMetadataOptions Render = FrameworkPropertyMetadataOptions.AffectsRender;

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(ModuleIllustration), new FrameworkPropertyMetadata("Home", Render));
    public static readonly DependencyProperty SecondaryIconProperty = DependencyProperty.Register(nameof(SecondaryIcon), typeof(string), typeof(ModuleIllustration), new FrameworkPropertyMetadata(null, Render));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(Color), typeof(ModuleIllustration), new FrameworkPropertyMetadata(Tones.Blue, Render));
    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(nameof(Seed), typeof(int), typeof(ModuleIllustration), new FrameworkPropertyMetadata(1, Render));
    public static readonly DependencyProperty GlowProperty = DependencyProperty.Register(nameof(Glow), typeof(double), typeof(ModuleIllustration), new FrameworkPropertyMetadata(0.0, Render));

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? SecondaryIcon { get => (string?)GetValue(SecondaryIconProperty); set => SetValue(SecondaryIconProperty, value); }
    public Color Tone { get => (Color)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public int Seed { get => (int)GetValue(SeedProperty); set => SetValue(SeedProperty, value); }
    /// <summary>0–1 hover illumination, animated by the owning tile.</summary>
    public double Glow { get => (double)GetValue(GlowProperty); set => SetValue(GlowProperty, value); }

    private static readonly Color Lamp = Color.FromRgb(0xFF, 0xC9, 0x7A);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        var rng = new Random(Seed * 31 + 7);
        bool lampLeft = rng.NextDouble() < 0.5 == (Seed % 2 == 0);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));

        // Room: dark wall with faint panelling
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x15, 0x1D, 0x2B), Color.FromRgb(0x08, 0x0B, 0x11), 90), null, new Rect(0, 0, w, h));
        var panelPen = new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), 1);
        for (double x = w * 0.18; x < w; x += w * 0.26) dc.DrawLine(panelPen, new Point(x, 0), new Point(x, h * 0.72));

        // Window with night sky on the far side
        var win = new Rect(lampLeft ? w * 0.7 : w * 0.06, h * 0.1, w * 0.22, h * 0.36);
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x10, 0x2A, 0x55), Color.FromRgb(0x2A, 0x4E, 0x86), 90), new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x27, 0x36)), 2), win);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x27, 0x36)), 1.5), new Point(win.X + win.Width / 2, win.Top), new Point(win.X + win.Width / 2, win.Bottom));
        for (int i = 0; i < 6; i++)
            dc.DrawEllipse(Brushes.White, null, new Point(win.X + rng.NextDouble() * win.Width, win.Y + rng.NextDouble() * win.Height * 0.6), 0.6, 0.6);

        // Lamp light pool
        double lampX = lampLeft ? w * 0.14 : w * 0.86;
        var lampCenter = new Point(lampX, h * 0.34);
        double glow = 0.55 + Glow * 0.35;
        dc.DrawRectangle(Radial(lampCenter, w * 0.55, h * 0.8, (0, Tones.WithAlpha(Lamp, 0.36 * glow)), (1, Tones.WithAlpha(Lamp, 0))), null, new Rect(0, 0, w, h));

        // Tone glow behind the hero object
        var heroCenter = new Point(w * (lampLeft ? 0.5 : 0.44), h * 0.46);
        dc.DrawRectangle(Radial(heroCenter, w * 0.4, h * 0.55, (0, Tones.WithAlpha(Tone, 0.30 + Glow * 0.2)), (1, Tones.WithAlpha(Tone, 0))), null, new Rect(0, 0, w, h));

        // Desk
        double deskY = h * 0.74;
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2A, 0x20, 0x1A), Color.FromRgb(0x0C, 0x09, 0x07), 90), null, new Rect(0, deskY, w, h - deskY));
        dc.DrawRectangle(new SolidColorBrush(Tones.WithAlpha(Lamp, 0.35 * glow)), null, new Rect(0, deskY, w, 1.2));
        dc.DrawRectangle(Radial(new Point(lampX, deskY), w * 0.4, h * 0.12, (0, Tones.WithAlpha(Lamp, 0.25 * glow)), (1, Tones.WithAlpha(Lamp, 0))), null, new Rect(0, deskY, w, h - deskY));

        // Lamp
        var metal = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x38));
        double shadeW = w * 0.1;
        dc.DrawRectangle(metal, null, new Rect(lampX - 1.2, h * 0.26, 2.4, deskY - h * 0.26));
        dc.DrawEllipse(metal, null, new Point(lampX, deskY), shadeW * 0.5, 2.5);
        var shade = new StreamGeometry();
        using (var ctx = shade.Open())
        {
            ctx.BeginFigure(new Point(lampX - shadeW * 0.3, h * 0.14), true, true);
            ctx.LineTo(new Point(lampX + shadeW * 0.3, h * 0.14), true, false);
            ctx.LineTo(new Point(lampX + shadeW * 0.55, h * 0.28), true, false);
            ctx.LineTo(new Point(lampX - shadeW * 0.55, h * 0.28), true, false);
        }
        dc.DrawGeometry(new LinearGradientBrush(Color.FromRgb(0x3A, 0x33, 0x2C), Color.FromRgb(0x1A, 0x16, 0x12), 90), null, shade);
        dc.DrawEllipse(new SolidColorBrush(Tones.WithAlpha(Lamp, 0.9)), null, new Point(lampX, h * 0.285), shadeW * 0.5, 1.6);

        // Plant on the far side from the lamp
        double plantX = lampLeft ? w * 0.9 : w * 0.1;
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x4A, 0x3A, 0x30), Color.FromRgb(0x22, 0x1A, 0x15), 0), null, new Rect(plantX - w * 0.03, deskY - h * 0.11, w * 0.06, h * 0.11));
        var leaf = new SolidColorBrush(Color.FromRgb(0x2E, 0x5A, 0x3A));
        for (int i = 0; i < 6; i++)
        {
            double a = -60 + i * 24 + rng.NextDouble() * 10;
            var e = new EllipseGeometry(new Point(plantX, deskY - h * 0.2), w * 0.012, h * 0.1) { Transform = new RotateTransform(a, plantX, deskY - h * 0.11) };
            dc.DrawGeometry(i % 2 == 0 ? leaf : new SolidColorBrush(Color.FromRgb(0x3E, 0x74, 0x4A)), null, e);
        }

        // Secondary prop (metallic)
        var secondary = IconCatalog.Get(SecondaryIcon);
        if (secondary != null)
        {
            double s = h * 0.34;
            var at = new Point(heroCenter.X + h * 0.42, deskY - s * 0.92);
            DrawSculpted(dc, secondary, IconCatalog.GetAccent(SecondaryIcon), at, s, Color.FromRgb(0xB8, 0xC4, 0xD6), Tone, 0.9);
        }

        // Hero object
        var hero = IconCatalog.Get(Icon);
        if (hero != null)
        {
            double s = h * 0.62;
            var at = new Point(heroCenter.X - s / 2 - (secondary != null ? h * 0.08 : 0), deskY - s * 0.94);
            // contact shadow
            dc.DrawEllipse(Radial(new Point(at.X + s / 2, deskY + 1), s * 0.5, 5, (0, Color.FromArgb(0xB0, 0, 0, 0)), (1, Color.FromArgb(0, 0, 0, 0))), null, new Point(at.X + s / 2, deskY + 1), s * 0.5, 5);
            DrawSculpted(dc, hero, IconCatalog.GetAccent(Icon), at, s, Tone, Color.FromRgb(0xE8, 0xF3, 0xFF), 1);
        }

        // Bottom fade into the tile body
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 0x0B, 0x10, 0x18), Color.FromArgb(0xF0, 0x0B, 0x10, 0x18), 90), null, new Rect(0, h * 0.8, w, h * 0.2 + 1));
        dc.Pop();
    }

    private static void DrawSculpted(DrawingContext dc, Geometry primary, Geometry? accent, Point at, double size, Color body, Color accentColor, double opacity)
    {
        dc.PushOpacity(opacity);
        dc.PushTransform(new TranslateTransform(at.X, at.Y));
        dc.PushTransform(new ScaleTransform(size / 24, size / 24));
        var fill = new LinearGradientBrush(
            [new GradientStop(Tones.Mix(body, Colors.White, 0.55), 0), new GradientStop(body, 0.45), new GradientStop(Tones.Mix(body, Colors.Black, 0.5), 1)], 90);
        dc.DrawGeometry(fill, new Pen(new SolidColorBrush(Tones.WithAlpha(Colors.White, 0.35)), 0.25), primary);
        if (accent != null)
        {
            var accentFill = new LinearGradientBrush(
                [new GradientStop(Tones.Mix(accentColor, Colors.White, 0.5), 0), new GradientStop(accentColor, 1)], 90);
            dc.DrawGeometry(accentFill, null, accent);
        }
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static Brush Radial(Point c, double rx, double ry, params (double o, Color color)[] stops)
    {
        var b = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = c, GradientOrigin = c, RadiusX = rx, RadiusY = ry };
        foreach (var (o, color) in stops) b.GradientStops.Add(new GradientStop(color, o));
        b.Freeze();
        return b;
    }
}
