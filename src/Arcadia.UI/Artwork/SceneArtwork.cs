using System.Windows;
using System.Windows.Media;

namespace Arcadia.UI.Artwork;

/// <summary>
/// Procedurally painted landscape used as original placeholder artwork (header, covers, screenshots).
/// </summary>
public sealed class SceneArtwork : FrameworkElement
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(string), typeof(SceneArtwork), new FrameworkPropertyMetadata("mountains-dusk", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(
        nameof(Seed), typeof(int), typeof(SceneArtwork), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VignetteProperty = DependencyProperty.Register(
        nameof(Vignette), typeof(double), typeof(SceneArtwork), new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BottomShadeProperty = DependencyProperty.Register(
        nameof(BottomShade), typeof(double), typeof(SceneArtwork), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Theme { get => (string)GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
    public int Seed { get => (int)GetValue(SeedProperty); set => SetValue(SeedProperty, value); }
    public double Vignette { get => (double)GetValue(VignetteProperty); set => SetValue(VignetteProperty, value); }
    public double BottomShade { get => (double)GetValue(BottomShadeProperty); set => SetValue(BottomShadeProperty, value); }

    static SceneArtwork()
    {
        // Decorative: hide from screen readers; the owning control supplies the accessible name.
        System.Windows.Automation.AutomationProperties.IsOffscreenBehaviorProperty.OverrideMetadata(typeof(SceneArtwork),
            new FrameworkPropertyMetadata(System.Windows.Automation.IsOffscreenBehavior.Offscreen));
    }

    protected override void OnRender(DrawingContext dc)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        ScenePainter.Paint(dc, RenderSize, SceneThemes.Get(Theme), Seed, Vignette, BottomShade);
        RenderStats.AddScene(System.Diagnostics.Stopwatch.GetTimestamp() - start);
    }
}
