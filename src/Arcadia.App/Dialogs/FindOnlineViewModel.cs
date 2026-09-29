using System.Collections.ObjectModel;
using Arcadia.App.Services;
using Arcadia.Core.Games;
using Arcadia.Core.Localization;
using Arcadia.Core.Metadata;
using Arcadia.Core.Tracking;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.Dialogs;

public sealed partial class MatchItem(GameMatch match) : ObservableObject
{
    public GameMatch Match { get; } = match;
    public string Name => Match.Name;
    public string Meta => string.Join(" · ", new[] { Match.ReleaseYear?.ToString(), Match.Platforms }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string? CoverUrl => Match.CoverUrl;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class ArtItem(ArtCandidate candidate) : ObservableObject
{
    public ArtCandidate Candidate { get; } = candidate;
    public string Thumb => Candidate.ThumbUrl;
    public string Caption => Candidate.Width > 0 ? $"{Candidate.Width}×{Candidate.Height}{(Candidate.Author is { } a ? " · " + a : string.Empty)}" : Candidate.Author ?? string.Empty;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>What the user chose in the dialog; applied to the game by <see cref="GameActions"/>.</summary>
public sealed record MetadataSelection(GameDetails? Details, bool ApplyDetails, long? SteamGridDbId, IReadOnlyDictionary<ArtKind, ArtCandidate> Art);

/// <summary>
/// "Find details &amp; art": search IGDB, pick the right game, preview its details and choose SteamGridDB artwork per kind.
/// </summary>
public sealed partial class FindOnlineViewModel : DialogViewModel
{
    private readonly MetadataService _metadata;
    private readonly ILocalizer _loc;
    private readonly int? _knownSteamAppId;
    private CancellationTokenSource? _selectionCts;
    private GameDetails? _details;
    private long? _sgdbId;

    public FindOnlineViewModel(Game game, MetadataService metadata, ILocalizer loc)
    {
        _metadata = metadata;
        _loc = loc;
        _knownSteamAppId = LaunchTargetInfo.SteamAppId(game.LaunchTarget);
        Title = loc["Meta_Title"];
        SearchText = game.Title;
        foreach (var kind in Enum.GetValues<ArtKind>()) ArtByKind[kind] = [];
        ArtUnavailable = !metadata.ArtAvailable;
    }

    public override string Icon => "Search";
    public override double DialogWidth => 1080;

    public ObservableCollection<MatchItem> Results { get; } = [];
    public Dictionary<ArtKind, ObservableCollection<ArtItem>> ArtByKind { get; } = [];
    public ObservableCollection<ArtItem> BoxArt => ArtByKind[ArtKind.BoxArt];
    public ObservableCollection<ArtItem> Capsules => ArtByKind[ArtKind.Capsule];
    public ObservableCollection<ArtItem> Heroes => ArtByKind[ArtKind.Hero];
    public ObservableCollection<ArtItem> Logos => ArtByKind[ArtKind.Logo];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private MatchItem? _selectedMatch;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isLoadingDetails;
    [ObservableProperty] private bool _isLoadingArt;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _artUnavailable;
    [ObservableProperty] private bool _applyDetails = true;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string? _facts;
    [ObservableProperty] private string? _genres;
    [ObservableProperty] private ArtKind _artTab = ArtKind.BoxArt;

    public bool HasSelection => SelectedMatch != null;

    public bool IsBoxArtTab { get => ArtTab == ArtKind.BoxArt; set { if (value) ArtTab = ArtKind.BoxArt; } }
    public bool IsCapsuleTab { get => ArtTab == ArtKind.Capsule; set { if (value) ArtTab = ArtKind.Capsule; } }
    public bool IsHeroTab { get => ArtTab == ArtKind.Hero; set { if (value) ArtTab = ArtKind.Hero; } }
    public bool IsLogoTab { get => ArtTab == ArtKind.Logo; set { if (value) ArtTab = ArtKind.Logo; } }

    partial void OnArtTabChanged(ArtKind value)
    {
        OnPropertyChanged(nameof(IsBoxArtTab));
        OnPropertyChanged(nameof(IsCapsuleTab));
        OnPropertyChanged(nameof(IsHeroTab));
        OnPropertyChanged(nameof(IsLogoTab));
        OnPropertyChanged(nameof(CurrentArt));
    }

    public ObservableCollection<ArtItem> CurrentArt => ArtByKind[ArtTab];

    public MetadataSelection? Result { get; private set; }

    /// <summary>Runs the initial search when the dialog opens.</summary>
    public override Task OnShownAsync() => SearchAsync();

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        IsSearching = true;
        Message = null;
        Results.Clear();
        try
        {
            foreach (var m in await _metadata.Info.SearchAsync(SearchText)) Results.Add(new MatchItem(m));
            if (Results.Count == 0) Message = _loc["Meta_NoResults"];
            else SelectedMatch = Results[0];
        }
        catch (Exception ex) when (ex is MetadataException or HttpRequestException)
        {
            Message = _metadata.Describe(ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    partial void OnSelectedMatchChanged(MatchItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        foreach (var r in Results) r.IsSelected = ReferenceEquals(r, value);
        if (value != null) _ = LoadSelectionAsync(value.Match);
    }

    private async Task LoadSelectionAsync(GameMatch match)
    {
        _selectionCts?.Cancel();
        var cts = _selectionCts = new CancellationTokenSource();
        _details = null;
        _sgdbId = null;
        Summary = Facts = Genres = null;
        foreach (var list in ArtByKind.Values) list.Clear();
        OnPropertyChanged(nameof(CurrentArt));

        IsLoadingDetails = true;
        try
        {
            var details = await _metadata.Info.GetDetailsAsync(match.Id, cts.Token);
            if (cts.IsCancellationRequested || details == null) return;
            _details = details;
            Summary = details.Summary;
            Facts = string.Join("  ·  ", new[]
            {
                details.ReleaseDate?.ToString("d MMM yyyy", _loc.Culture),
                details.Developers.Count > 0 ? string.Join(", ", details.Developers.Take(2)) : null,
                details.Publishers.Count > 0 && !details.Publishers.SequenceEqual(details.Developers) ? string.Join(", ", details.Publishers.Take(2)) : null,
                details.Rating is { } r ? $"★ {r:0}" : null,
            }.Where(s => !string.IsNullOrEmpty(s)));
            Genres = string.Join(", ", details.Genres);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is MetadataException or HttpRequestException)
        {
            Message = _metadata.Describe(ex);
            return;
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoadingDetails = false;
        }

        if (!_metadata.ArtAvailable) return;
        IsLoadingArt = true;
        try
        {
            _sgdbId = await _metadata.Art.FindGameAsync(match.Name, _details?.SteamAppId ?? _knownSteamAppId, cts.Token);
            if (_sgdbId is not { } sgdb || cts.IsCancellationRequested) return;
            var tasks = Enum.GetValues<ArtKind>().Select(async kind => (kind, items: await _metadata.Art.GetArtAsync(sgdb, kind, cts.Token))).ToList();
            foreach (var (kind, items) in await Task.WhenAll(tasks))
            {
                if (cts.IsCancellationRequested) return;
                var list = ArtByKind[kind];
                foreach (var item in items) list.Add(new ArtItem(item));
                // Pre-select the best candidate; for logos prefer an official one.
                var pick = kind == ArtKind.Logo ? list.FirstOrDefault(i => i.Candidate.Style == "official") ?? list.FirstOrDefault() : list.FirstOrDefault();
                if (pick != null) pick.IsSelected = true;
            }
            OnPropertyChanged(nameof(CurrentArt));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is MetadataException or HttpRequestException)
        {
            Message = _metadata.Describe(ex);
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoadingArt = false;
        }
    }

    [RelayCommand]
    private void SelectArt(ArtItem? item)
    {
        if (item == null) return;
        var list = ArtByKind[item.Candidate.Kind];
        bool wasSelected = item.IsSelected;
        foreach (var i in list) i.IsSelected = false;
        item.IsSelected = !wasSelected; // clicking the chosen one again means "keep current"
    }

    [RelayCommand]
    private void ShowArtTab(ArtKind kind) => ArtTab = kind;

    [RelayCommand]
    private void SelectMatch(MatchItem? item)
    {
        if (item != null) SelectedMatch = item;
    }

    [RelayCommand]
    private void Apply()
    {
        if (_details == null) return;
        var art = ArtByKind.Where(kv => kv.Value.Any(i => i.IsSelected))
            .ToDictionary(kv => kv.Key, kv => kv.Value.First(i => i.IsSelected).Candidate);
        Result = new MetadataSelection(_details, ApplyDetails, _sgdbId, art);
        _selectionCts?.Cancel();
        Close(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _selectionCts?.Cancel();
        Close(false);
    }
}
