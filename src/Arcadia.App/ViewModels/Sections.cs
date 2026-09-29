namespace Arcadia.App.ViewModels;

/// <summary>A top-level destination in the sidebar.</summary>
/// <param name="ModuleKey">Resource prefix for placeholder copy (Module_{ModuleKey}_Summary, _F1.._F3).</param>
/// <param name="Tone">Accent tone used by the placeholder illustration.</param>
/// <param name="ArtTheme">Procedural scene used on the placeholder banner.</param>
public sealed record SectionDescriptor(
    string Key,
    string Icon,
    string LabelKey,
    bool IsSecondary,
    string? ModuleKey = null,
    string Tone = "Blue",
    string ArtTheme = "mountains-dusk",
    string? SecondaryIcon = null);

public static class Sections
{
    public const string Home = "home";
    public const string Settings = "settings";
    public const string Games = "games";
    public const string ItTools = "ittools";
    public const string Projects = "projects";
    public const string Documents = "documents";
    public const string Travel = "travel";
    public const string Calendar = "calendar";
    public const string Tasks = "tasks";
    public const string Notes = "notes";
    public const string Finance = "finance";
    public const string Oracle = "oracle";
    public const string Automation = "automation";

    public static readonly IReadOnlyList<SectionDescriptor> All =
    [
        new(Home, "Home", "Nav_Home", false),
        new(Games, "Games", "Nav_Games", false, "Games", "Blue", "castle-dusk", "Trophy"),
        new(ItTools, "ITTools", "Nav_ITTools", false, "ITTools", "Blue", "mountains-storm", "Settings"),
        new(Projects, "Projects", "Nav_Projects", false, "Projects", "Cyan", "lake-sunset", "Tasks"),
        new(Documents, "Documents", "Nav_Documents", false, "Documents", "Amber", "golden-realm", "Notes"),
        new(Travel, "Travel", "Nav_Travel", false, "Travel", "Green", "desert-sunset", "Camera"),
        new(Oracle, "Oracle", "Nav_Oracle", false, "Oracle", "Cyan", "space-nebula", "Sparkle"),
        new(Calendar, "Calendar", "Nav_Calendar", true, "Calendar", "Blue", "mountains-snow", "Clock"),
        new(Tasks, "Tasks", "Nav_Tasks", true, "Tasks", "Green", "forest-night", "Check"),
        new(Notes, "Notes", "Nav_Notes", true, "Notes", "Amber", "forest-ember", "Journal"),
        new(Finance, "Finance", "Nav_Finance", true, "Finance", "Gold", "desert-sunset", "Wallet"),
        new(Automation, "Automation", "Nav_Automation", true, "Automation", "Cyan", "mountains-storm", "Bolt"),
        new(Settings, "Settings", "Nav_Settings", true),
    ];

    public static SectionDescriptor? Find(string? key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
}
