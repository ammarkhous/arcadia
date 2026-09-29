using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.ViewModels.Home;

/// <summary>The large "Featured Game" hero: artwork carousel, facts and the primary action.</summary>
public sealed partial class FeaturedGameViewModel : ObservableObject
{
    public FeaturedGameViewModel(int id, string title, IReadOnlyList<ArtworkThumbViewModel> thumbnails)
    {
        Id = id;
        Title = title;
        TitleMark = title.Split(':')[0].ToUpperInvariant();
        Thumbnails = new ObservableCollection<ArtworkThumbViewModel>(thumbnails);
        SelectThumbnail(Thumbnails.FirstOrDefault());
    }

    public int Id { get; }
    public string Title { get; }
    /// <summary>Short uppercase title set over the artwork, e.g. "THE WITCHER 3".</summary>
    public string TitleMark { get; }

    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Meta { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int Progress { get; init; }
    public string ProgressText { get; init; } = string.Empty;
    public string ProgressTone { get; init; } = "Green";
    public string Playtime { get; init; } = string.Empty;
    public string Achievements { get; init; } = string.Empty;
    public string? CurrentQuest { get; init; }
    public string? LatestNote { get; init; }
    public string PrimaryActionText { get; init; } = string.Empty;
    /// <summary>Transparent title logo (replaces the typographic title mark when present).</summary>
    public string? LogoPath { get; init; }

    public ObservableCollection<ArtworkThumbViewModel> Thumbnails { get; }

    [ObservableProperty] private ArtworkThumbViewModel? _hero;

    [RelayCommand]
    private void SelectThumbnail(ArtworkThumbViewModel? thumb)
    {
        if (thumb == null) return;
        foreach (var t in Thumbnails) t.IsSelected = ReferenceEquals(t, thumb);
        Hero = thumb;
    }
}
