using System.Windows;
using System.Windows.Media.Animation;

namespace Arcadia.UI;

/// <summary>Central motion settings so every animation respects the user's preference.</summary>
public static class Motion
{
    public static bool AnimationsEnabled { get; set; } = true;

    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(120));
    public static readonly Duration Normal = new(TimeSpan.FromMilliseconds(200));
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(300));

    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseInOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });

    private static IEasingFunction Freeze(CubicEase e) { e.Freeze(); return e; }
}
