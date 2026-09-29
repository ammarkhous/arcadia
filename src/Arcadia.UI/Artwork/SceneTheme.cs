using System.Windows.Media;

namespace Arcadia.UI.Artwork;

[Flags]
public enum SceneFeature
{
    None = 0,
    Castle = 1,
    City = 2,
    Mesa = 4,
    Blocks = 8,
    Forest = 16,
    Planet = 32,
    Town = 64,
    Wanderer = 128,
}

/// <summary>
/// Palette and composition parameters for a procedurally painted landscape.
/// Themes are original Arcadia development artwork — they stand in wherever licensed imagery is absent.
/// </summary>
public sealed record SceneTheme(
    Color SkyTop,
    Color SkyMid,
    Color Horizon,
    Color Sun,
    double SunX,
    double SunY,
    double SunSize,
    Color RidgeFar,
    Color RidgeNear,
    Color Haze,
    Color Accent,
    SceneFeature Features = SceneFeature.None,
    bool Water = false,
    double Stars = 0,
    int Layers = 5,
    double HorizonY = 0.62,
    double Cloudiness = 0.4,
    double Roughness = 0.5);

public static class SceneThemes
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static readonly Dictionary<string, SceneTheme> Themes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arcadia-header"] = new(C("#0B1838"), C("#3D66A8"), C("#F4A868"), C("#FFD49A"), 0.47, 0.6, 0.05,
            C("#7E97CC"), C("#060913"), C("#9FB6E0"), C("#FFC56E"), SceneFeature.Wanderer, Water: true, Stars: 0.35, Layers: 5, HorizonY: 0.64, Cloudiness: 0.55, Roughness: 0.65),
        ["sidebar-forest"] = new(C("#0B1226"), C("#2C3F6B"), C("#E7A06A"), C("#FFD2A0"), 0.62, 0.5, 0.04,
            C("#5D6F99"), C("#05070C"), C("#C98A6A"), C("#FFC56E"), SceneFeature.Forest, Water: true, Stars: 0.6, Layers: 4, HorizonY: 0.55, Cloudiness: 0.3),
        ["mountains-dusk"] = new(C("#101A38"), C("#4A5A8A"), C("#F2A15E"), C("#FFD08A"), 0.55, 0.58, 0.05,
            C("#6E7BA3"), C("#0A0C14"), C("#D98E68"), C("#FFC56E")),
        ["castle-dusk"] = new(C("#161B3A"), C("#6E4A63"), C("#F29A55"), C("#FFC27A"), 0.68, 0.55, 0.06,
            C("#6B5C7E"), C("#0B0910"), C("#D0806A"), C("#FFC66B"), SceneFeature.Castle, Stars: 0.2, HorizonY: 0.6, Cloudiness: 0.6),
        ["castle-violet"] = new(C("#100A24"), C("#4A2B6E"), C("#C66FC9"), C("#FFB3E6"), 0.35, 0.52, 0.05,
            C("#5A447E"), C("#08060E"), C("#A266B8"), C("#FFB86B"), SceneFeature.Castle, Stars: 0.5, HorizonY: 0.6),
        ["castle-night"] = new(C("#050918"), C("#172750"), C("#3F639C"), C("#E8F0FF"), 0.24, 0.24, 0.025,
            C("#33476E"), C("#04060C"), C("#4A6A9A"), C("#FFC66B"), SceneFeature.Castle, Stars: 1, HorizonY: 0.6, Cloudiness: 0.3),
        ["mountains-storm"] = new(C("#181E26"), C("#39434F"), C("#8793A0"), C("#C9D4DE"), 0.3, 0.45, 0.03,
            C("#5A6573"), C("#0A0D11"), C("#7C8894"), C("#FFC66B"), Layers: 5, Cloudiness: 1, Roughness: 0.7),
        ["mountains-crimson"] = new(C("#240A10"), C("#7A1E24"), C("#F29F4A"), C("#FFE0A0"), 0.42, 0.55, 0.06,
            C("#7A3A3A"), C("#0C0506"), C("#C8604A"), C("#FFD27A"), SceneFeature.Forest, Cloudiness: 0.3),
        ["mountains-snow"] = new(C("#0E1C31"), C("#4D6B90"), C("#D0E4F6"), C("#FFFFFF"), 0.62, 0.5, 0.04,
            C("#A9C0D9"), C("#101826"), C("#BFD4EA"), C("#FFE3A6"), Stars: 0.1, Roughness: 0.75),
        ["golden-realm"] = new(C("#2A2110"), C("#8A6A2A"), C("#FFE09A"), C("#FFF4CC"), 0.5, 0.5, 0.09,
            C("#7A6644"), C("#110D07"), C("#E7C27A"), C("#FFE8A0"), SceneFeature.Castle, Cloudiness: 0.7),
        ["desert-sunset"] = new(C("#2A1428"), C("#B24F3A"), C("#FFB35C"), C("#FFE28E"), 0.6, 0.6, 0.07,
            C("#9A4B3C"), C("#1A0C09"), C("#D9784E"), C("#FFD27A"), SceneFeature.Mesa, Layers: 4, HorizonY: 0.64, Cloudiness: 0.35),
        ["neon-city"] = new(C("#090616"), C("#2A0F40"), C("#FF3FA4"), C("#FF7FC8"), 0.5, 0.62, 0.0,
            C("#2A1840"), C("#06040C"), C("#8A2A7A"), C("#27E1FF"), SceneFeature.City, Water: true, Stars: 0.15, Layers: 2, HorizonY: 0.66, Cloudiness: 0.2),
        ["blocks-day"] = new(C("#2B79D2"), C("#7DBEEE"), C("#D4F0FF"), C("#FFFBE0"), 0.75, 0.25, 0.04,
            C("#7FAFD0"), C("#23551F"), C("#BFE3F5"), C("#FFFFFF"), SceneFeature.Blocks, Water: true, Layers: 4, Cloudiness: 0.7),
        ["blocks-night"] = new(C("#050A1A"), C("#13244A"), C("#2E4F86"), C("#E6EEFF"), 0.2, 0.22, 0.03,
            C("#2C4468"), C("#060A12"), C("#3A5A8A"), C("#8FE3FF"), SceneFeature.Blocks, Stars: 1, Layers: 4),
        ["forest-night"] = new(C("#03060C"), C("#0E1B26"), C("#2D4959"), C("#DDEBFF"), 0.72, 0.28, 0.025,
            C("#23394A"), C("#020406"), C("#35556A"), C("#FFD27A"), SceneFeature.Forest, Stars: 0.7, Layers: 4, Cloudiness: 0.5),
        ["forest-ember"] = new(C("#130707"), C("#3A150C"), C("#D9652B"), C("#FFB060"), 0.45, 0.62, 0.05,
            C("#5A2A1E"), C("#070303"), C("#A0482A"), C("#FFB060"), SceneFeature.Forest, Stars: 0.3, Layers: 4, Cloudiness: 0.3),
        ["lake-sunset"] = new(C("#1A2250"), C("#B85A74"), C("#FFC07A"), C("#FFE6B0"), 0.5, 0.56, 0.06,
            C("#6A5A86"), C("#0B0A14"), C("#E08A7A"), C("#FFC56E"), SceneFeature.Town, Water: true, Layers: 3, HorizonY: 0.56),
        ["lake-storm"] = new(C("#1A2028"), C("#3C4854"), C("#7F8B96"), C("#D0D8E0"), 0.7, 0.4, 0.02,
            C("#4E5A66"), C("#0B0E12"), C("#6C7884"), C("#FFC66B"), Water: true, Layers: 3, HorizonY: 0.56, Cloudiness: 1),
        ["space-nebula"] = new(C("#04030C"), C("#140C30"), C("#2E1A5A"), C("#FFE6C0"), 0.25, 0.3, 0.02,
            C("#2A2448"), C("#07060E"), C("#B04CFF"), C("#2FA8FF"), SceneFeature.Planet, Stars: 1.6, Layers: 1, HorizonY: 0.8, Cloudiness: 0),
        ["space-planet"] = new(C("#02040C"), C("#0A1430"), C("#1C3466"), C("#FFFFFF"), 0.8, 0.2, 0.015,
            C("#22304C"), C("#05070D"), C("#4FA3FF"), C("#8FD8FF"), SceneFeature.Planet, Stars: 1.4, Layers: 1, HorizonY: 0.82, Cloudiness: 0),
    };

    public static IReadOnlyCollection<string> Names => Themes.Keys;

    public static SceneTheme Get(string? name) =>
        name != null && Themes.TryGetValue(name, out var theme) ? theme : Themes["mountains-dusk"];
}
