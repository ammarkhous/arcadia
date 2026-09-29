using CommunityToolkit.Mvvm.ComponentModel;

namespace Arcadia.App.ViewModels.Home;

/// <summary>Artwork reference: real image when available, procedural theme + seed otherwise.</summary>
public sealed partial class ArtworkThumbViewModel(string theme, int seed, string? imagePath, string caption) : ObservableObject
{
    public string Theme { get; } = theme;
    public int Seed { get; } = seed;
    public string? ImagePath { get; } = imagePath;
    public string Caption { get; } = caption;

    [ObservableProperty] private bool _isSelected;
}

public sealed record StatRowViewModel(string Label, string Value, string PercentText, string Icon, string Tone, System.Collections.IEnumerable Bars);

public sealed record GameRowViewModel(
    int Id,
    string Title,
    string Meta,
    int Progress,
    string ProgressText,
    string ProgressTone,
    string Theme,
    int Seed,
    string? CoverPath,
    string StatusText,
    string StatusTone);

public sealed record ScreenshotViewModel(
    int Id,
    string Title,
    string? Note,
    string? Location,
    string GameTitle,
    string DateText,
    string Theme,
    int Seed,
    string? ImagePath,
    string? ThumbnailPath);

public sealed record ModuleTileViewModel(
    string Key,
    string Icon,
    string SecondaryIcon,
    string Tone,
    int Seed,
    string Title,
    string Description,
    string ActionText);
