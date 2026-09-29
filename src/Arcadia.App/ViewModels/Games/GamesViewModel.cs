using System.Collections.ObjectModel;
using Arcadia.App.Messages;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Home;
using Arcadia.Core.Formatting;
using Arcadia.Core.Games;
using Arcadia.Core.Localization;
using Arcadia.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Games;

public sealed partial class FilterOption(string key, GameStatus? status) : ObservableObject
{
    public string Key { get; } = key;
    public GameStatus? Status { get; } = status;
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A game as shown in the library grid / list.</summary>
public sealed record GameCardViewModel(
    int Id,
    string Title,
    string Platform,
    string Meta,
    string StatusText,
    string StatusTone,
    int Progress,
    string ProgressText,
    string ProgressTone,
    string Playtime,
    string LastPlayed,
    string Theme,
    int Seed,
    string? ImagePath,
    bool IsFeatured,
    string? PosterPath);

/// <summary>The Games module: a filterable library plus the per-game detail page.</summary>
public sealed partial class GamesViewModel : ObservableObject, IActivatable, IRecipient<LibraryChangedMessage>
{
    private readonly IGameCatalog _catalog;
    private readonly GameActions _actions;
    private readonly StartupState _startup;
    private readonly ISettingsService _settings;
    private readonly ILocalizer _loc;
    private readonly TimeProvider _time;
    private readonly ILogger<GamesViewModel> _logger;
    private readonly Func<GameDetailViewModel> _detailFactory;

    private IReadOnlyList<Game> _games = [];
    private bool _isStale = true;
    private bool _suppressRefresh;

    public GamesViewModel(
        IGameCatalog catalog,
        GameActions actions,
        StartupState startup,
        ISettingsService settings,
        ILocalizer loc,
        TimeProvider time,
        IMessenger messenger,
        Func<GameDetailViewModel> detailFactory,
        ILogger<GamesViewModel> logger)
    {
        _catalog = catalog;
        _actions = actions;
        _startup = startup;
        _settings = settings;
        _loc = loc;
        _time = time;
        _detailFactory = detailFactory;
        _logger = logger;

        Filters =
        [
            new("all", null) { IsSelected = true },
            new("playing", GameStatus.Playing),
            new("backlog", GameStatus.Backlog),
            new("completed", GameStatus.Completed),
            new("abandoned", GameStatus.Abandoned),
        ];
        _sortKey = settings.Current.Games.Sort;
        _isListView = settings.Current.Games.ViewMode == "List";
        RefreshLabels();

        messenger.Register(this);
        loc.CultureChanged += (_, _) => { RefreshLabels(); ApplyFilter(); };
    }

    public ObservableCollection<FilterOption> Filters { get; }
    /// <summary>The filtered, sorted library. Replaced as a whole (one layout pass instead of one per game).</summary>
    [ObservableProperty] private IReadOnlyList<GameCardViewModel> _items = [];
    public ObservableCollection<string> PlatformOptions { get; } = [];
    public IReadOnlyList<Dialogs.Option<string>> SortOptions { get; private set; } = [];

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedPlatform = string.Empty;
    [ObservableProperty] private string _sortKey;
    [ObservableProperty] private bool _isListView;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private bool _isLibraryEmpty;
    [ObservableProperty] private bool _hasNoMatches;
    [ObservableProperty] private GameDetailViewModel? _detail;

    public Dialogs.Option<string>? SelectedSort
    {
        get => SortOptions.FirstOrDefault(o => o.Value == SortKey);
        set { if (value != null) SortKey = value.Value; }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedPlatformChanged(string value) => ApplyFilter();

    partial void OnSortKeyChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedSort));
        _settings.Current.Games.Sort = value;
        _ = SaveSettingsAsync();
        ApplyFilter();
    }

    partial void OnIsListViewChanged(bool value)
    {
        _settings.Current.Games.ViewMode = value ? "List" : "Grid";
        _ = SaveSettingsAsync();
    }

    public async Task ActivateAsync()
    {
        if (_isStale) await LoadAsync();
    }

    /// <summary>Opens the detail page of a game (used by Home and deep links).</summary>
    public async Task ShowGameAsync(int id)
    {
        var detail = _detailFactory();
        Detail = detail;
        await detail.LoadAsync(id);
        if (detail.IsMissing) Detail = null;
    }

    public void Receive(LibraryChangedMessage message)
    {
        _isStale = true;
        _ = LoadAsync(silent: true);
        if (Detail is { } d && message.GameId == d.GameId)
        {
            if (message.Deleted) Detail = null;
            else _ = d.LoadAsync(d.GameId);
        }
    }

    [RelayCommand]
    private async Task LoadAsync(bool silent = false)
    {
        if (!silent) IsLoading = true;
        HasError = false;
        try
        {
            await _startup.DatabaseReady;
            _games = await Task.Run(() => _catalog.GetGamesAsync());
            _isStale = false;
            RebuildPlatforms();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the game library failed");
            HasError = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SelectFilter(FilterOption? filter)
    {
        if (filter == null) return;
        foreach (var f in Filters) f.IsSelected = ReferenceEquals(f, filter);
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressRefresh = true;
        SearchText = string.Empty;
        SelectedPlatform = PlatformOptions.FirstOrDefault() ?? string.Empty;
        _suppressRefresh = false;
        SelectFilter(Filters[0]);
    }

    [RelayCommand] private void ShowGrid() => IsListView = false;
    [RelayCommand] private void ShowList() => IsListView = true;

    [RelayCommand]
    private async Task OpenGameAsync(GameCardViewModel? card)
    {
        if (card != null) await ShowGameAsync(card.Id);
    }

    [RelayCommand]
    private async Task AddGameAsync()
    {
        var id = await _actions.EditAsync(null);
        if (id is not { } newId) return;
        await ShowGameAsync(newId);
        // A new game is the perfect moment to fetch its details and artwork.
        if (_actions.OnlineMetadataAvailable) await _actions.FindOnlineAsync(newId);
    }

    [RelayCommand] private Task FindOnlineAsync(GameCardViewModel? card) => card == null ? Task.CompletedTask : _actions.FindOnlineAsync(card.Id);
    [RelayCommand] private Task EditGameAsync(GameCardViewModel? card) => card == null ? Task.CompletedTask : _actions.EditAsync(card.Id);
    [RelayCommand] private Task LogSessionAsync(GameCardViewModel? card) => card == null ? Task.CompletedTask : _actions.LogSessionAsync(card.Id);
    [RelayCommand] private Task LaunchGameAsync(GameCardViewModel? card) => card == null ? Task.CompletedTask : _actions.LaunchAsync(card.Id);
    [RelayCommand] private Task SetFeaturedAsync(GameCardViewModel? card) => card == null ? Task.CompletedTask : _actions.SetFeaturedAsync(card.Id, card.Title);
    [RelayCommand] private Task DeleteGameAsync(GameCardViewModel? card) => card == null ? Task.CompletedTask : _actions.DeleteGameAsync(card.Id, card.Title);

    [RelayCommand] private void CloseDetail() => Detail = null;

    // ── filtering ───────────────────────────────────────────────────

    private void RebuildPlatforms()
    {
        var current = SelectedPlatform;
        var all = _loc["Games_AllPlatforms"];
        _suppressRefresh = true;
        PlatformOptions.Clear();
        PlatformOptions.Add(all);
        foreach (var p in _games.Select(g => g.Platform).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
            PlatformOptions.Add(p);
        SelectedPlatform = PlatformOptions.Contains(current) ? current : all;
        _suppressRefresh = false;
    }

    private void ApplyFilter()
    {
        if (_suppressRefresh) return;
        var now = _time.GetUtcNow().UtcDateTime;

        foreach (var f in Filters) f.Count = f.Status is { } s ? _games.Count(g => g.Status == s) : _games.Count;

        IEnumerable<Game> query = _games;
        var status = Filters.FirstOrDefault(f => f.IsSelected)?.Status;
        if (status != null) query = query.Where(g => g.Status == status);
        if (!string.IsNullOrEmpty(SelectedPlatform) && SelectedPlatform != _loc["Games_AllPlatforms"])
            query = query.Where(g => string.Equals(g.Platform, SelectedPlatform, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(g => g.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                                     || g.Tags.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                                     || g.Platform.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        query = SortKey switch
        {
            "Title" => query.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase),
            "Progress" => query.OrderByDescending(g => g.CompletionPercent).ThenBy(g => g.Title),
            "Playtime" => query.OrderByDescending(g => g.PlaytimeHours),
            "Added" => query.OrderByDescending(g => g.AddedAtUtc),
            _ => query.OrderByDescending(g => g.LastPlayedAtUtc ?? DateTime.MinValue).ThenByDescending(g => g.AddedAtUtc),
        };

        Items = query.Select(g => ToCard(g, now)).ToList();

        IsLibraryEmpty = _games.Count == 0;
        HasNoMatches = !IsLibraryEmpty && Items.Count == 0;
        Subtitle = _loc.Format("Games_Subtitle", _games.Count, RelativeTime.Hours(_games.Sum(g => g.PlaytimeHours), _loc));
    }

    private GameCardViewModel ToCard(Game g, DateTime now)
    {
        var lastPlayed = g.LastPlayedAtUtc is { } lp ? RelativeTime.Format(lp, now, _loc) : "—";
        var playtime = RelativeTime.Hours(g.PlaytimeHours, _loc);
        return new GameCardViewModel(
            g.Id, g.Title, g.Platform, $"{g.Platform} · {playtime}",
            _loc[$"Status_{g.Status}"], HomeViewModel.StatusTone(g.Status),
            g.CompletionPercent, $"{g.CompletionPercent}%", HomeViewModel.ProgressTone(g.CompletionPercent),
            playtime, lastPlayed, g.ArtworkTheme, g.ArtworkSeed,
            _actions.ResolveMedia(g.CoverThumbnailPath ?? g.CoverImagePath ?? g.HeroImagePath ?? g.BoxArtPath), g.IsFeatured,
            _actions.ResolveMedia(g.BoxArtPath ?? g.CoverImagePath ?? g.HeroImagePath));
    }

    private void RefreshLabels()
    {
        foreach (var f in Filters) f.Label = f.Status is { } s ? _loc[$"Status_{s}"] : _loc["Games_FilterAll"];
        SortOptions =
        [
            new("LastPlayed", _loc["Games_SortLastPlayed"]),
            new("Title", _loc["Games_SortTitle"]),
            new("Progress", _loc["Games_SortProgress"]),
            new("Playtime", _loc["Games_SortPlaytime"]),
            new("Added", _loc["Games_SortAdded"]),
        ];
        OnPropertyChanged(nameof(SortOptions));
        OnPropertyChanged(nameof(SelectedSort));
        if (_games.Count > 0) RebuildPlatforms();
    }

    private async Task SaveSettingsAsync()
    {
        try { await _settings.SaveAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Saving library view settings failed"); }
    }
}
