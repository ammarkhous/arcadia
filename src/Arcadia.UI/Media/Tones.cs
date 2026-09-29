using System.Windows.Media;

namespace Arcadia.UI.Media;

/// <summary>Derives the family of brushes a "toned" control needs from one base colour.</summary>
public static class Tones
{
    public static readonly Color Blue = Color.FromRgb(0x2F, 0x7B, 0xFF);
    public static readonly Color Green = Color.FromRgb(0x34, 0xC7, 0x6A);
    public static readonly Color Amber = Color.FromRgb(0xF2, 0xA6, 0x23);
    public static readonly Color Purple = Color.FromRgb(0x8E, 0x5B, 0xFF);
    public static readonly Color Red = Color.FromRgb(0xEE, 0x44, 0x49);
    public static readonly Color Cyan = Color.FromRgb(0x45, 0xD2, 0xFF);
    public static readonly Color Steel = Color.FromRgb(0x6F, 0x7C, 0x90);

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    public static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>Glossy horizontal bar fill (progress, spark bars).</summary>
    public static Brush BarFill(Color c) => Frozen(new LinearGradientBrush(
        [new GradientStop(Mix(c, Colors.White, 0.35), 0), new GradientStop(c, 0.5), new GradientStop(Mix(c, Colors.Black, 0.25), 1)], 90));

    /// <summary>Deep tinted slab (stat rows, badges).</summary>
    public static Brush Slab(Color c) => Frozen(new LinearGradientBrush(
        [new GradientStop(Mix(Color.FromRgb(0x10, 0x15, 0x1E), c, 0.42), 0), new GradientStop(Mix(Color.FromRgb(0x0A, 0x0E, 0x14), c, 0.18), 1)], 0));

    public static Brush Border(Color c) => Frozen(new SolidColorBrush(Mix(c, Colors.Black, 0.1)));

    public static Brush Text(Color c) => Frozen(new SolidColorBrush(Mix(c, Colors.White, 0.25)));
}
