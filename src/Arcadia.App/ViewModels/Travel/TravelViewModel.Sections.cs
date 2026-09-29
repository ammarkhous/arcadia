using System.Collections.ObjectModel;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.Text;
using Arcadia.Core.Travel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Travel;

public enum TripFilter { All, Upcoming, Current, Past, Ideas, Favorites }

public enum CountryFilter { Visited, Wanted, NotYet, All }

public sealed record ContinentBar(string Name, int Visited, int Total)
{
    public double Percent => Total == 0 ? 0 : Visited * 100.0 / Total;
    public string CountText => $"{Visited} / {Total}";
}

public sealed partial class TravelViewModel
{
    // ───────────────────────── desk ─────────────────────────

    [ObservableProperty] private TripCard? _featuredTrip;
    [ObservableProperty] private string _featuredLabel = string.Empty;
    [ObservableProperty] private IReadOnlyList<TripCard> _recentTrips = [];
    [ObservableProperty] private IReadOnlyList<PlaceItem> _favoritePlaces = [];
    [ObservableProperty] private IReadOnlyList<TripCard> _favoriteTrips = [];
    [ObservableProperty] private IReadOnlyList<TravelStatTile> _deskTiles = [];
    [ObservableProperty] private IReadOnlyList<BucketItem> _bucketPreview = [];
    [ObservableProperty] private IReadOnlyList<PhotoItem> _recentPhotos = [];
    [ObservableProperty] private MemoryChapterItem? _memoryHighlight;
    [ObservableProperty] private string _worldText = string.Empty;
    [ObservableProperty] private double _worldPercent;

    private void BuildDesk()
    {
        var current = AllTrips.Where(t => t.Summary.Status == TripStatus.Active || t.Phase == TripPhase.Current && t.Summary.Status is not (TripStatus.Cancelled or TripStatus.Idea))
            .OrderBy(t => t.Summary.StartDate).FirstOrDefault();
        var next = AllTrips.Where(t => t.Phase == TripPhase.Future && t.Summary.Status is not (TripStatus.Cancelled or TripStatus.Completed or TripStatus.Idea))
            .OrderBy(t => t.Summary.StartDate).FirstOrDefault();
        FeaturedTrip = current ?? next ?? AllTrips.Where(t => t.Summary.Status is TripStatus.Planning or TripStatus.Idea).OrderByDescending(t => t.Summary.UpdatedAtUtc).FirstOrDefault();
        FeaturedLabel = current != null ? Loc["Tr_OnTheRoad"] : next != null ? Loc["Tr_NextJourney"] : Loc["Tr_Dreaming"];
        RecentTrips = AllTrips.Where(t => t.Phase == TripPhase.Past || t.Summary.Status == TripStatus.Completed).Where(t => t.Id != FeaturedTrip?.Id)
            .OrderByDescending(t => t.Summary.EndDate ?? t.Summary.StartDate).Take(4).ToList();
        FavoritePlaces = AllPlaces.Where(p => p.IsFavorite).OrderByDescending(p => p.Summary.LastVisit).ThenBy(p => p.Name).Take(8).ToList();
        FavoriteTrips = AllTrips.Where(t => t.IsFavorite).Take(4).ToList();
        if (Stats is { } s)
        {
            var un = Countries.UnStateCount;
            WorldPercent = un == 0 ? 0 : s.UnCountriesVisited * 100.0 / un;
            WorldText = Loc.Format("Tr_WorldExplored", s.UnCountriesVisited, un, WorldPercent.ToString("0.#", Loc.Culture));
            DeskTiles =
            [
                new(Loc["Tr_StatCountries"], TravelText.Number(s.CountriesVisited, Loc), "Flag", "Cyan"),
                new(Loc["Tr_StatCities"], TravelText.Number(s.CitiesVisited, Loc), "City", "Blue"),
                new(Loc["Tr_StatPlaces"], TravelText.Number(s.PlacesSaved, Loc), "MapPin", "Amber"),
                new(Loc["Tr_StatTrips"], TravelText.Number(s.TripsCompleted, Loc), "Suitcase", "Gold"),
                new(Loc["Tr_StatDays"], TravelText.Number(s.DaysTraveling, Loc), "Calendar", "Green"),
                new(Loc["Tr_StatPhotos"], TravelText.Number(s.Photos, Loc), "Image", "Purple"),
            ];
        }
    }

    private async Task LoadDeskExtrasAsync()
    {
        try
        {
            var photos = await Store.ListPhotosAsync(new PhotoQuery(Take: 5000));
            RecentPhotos = photos.Where(p => !p.IsMissing).OrderByDescending(p => p.SortDate).Take(10).Select(p => new PhotoItem(p, Loc)).ToList();
            var bucket = await Store.ListBucketAsync();
            BucketPreview = bucket.Where(b => !b.IsCompleted).Take(5).Select(b => new BucketItem(b, Loc, Flags)).ToList();
            var memories = await Store.GetMemoriesAsync();
            MemoryHighlight = memories.Where(c => c.Items.Any(i => i.Kind == MemoryKind.Photo)).Select(c => new MemoryChapterItem(c, Loc, Flags)).FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Desk extras could not be loaded");
        }
    }

    [RelayCommand] private void OpenRecentPhoto(PhotoItem? p) { if (p != null) OpenViewer(RecentPhotos, p); }
    [RelayCommand] private void GoTo(string? section) { if (Enum.TryParse<TravelSection>(section, out var s)) ShowSection(s); }

    // ───────────────────────── trips list ─────────────────────────

    [ObservableProperty] private IReadOnlyList<Option<TripFilter>> _tripFilters = [];
    [ObservableProperty] private Option<TripFilter>? _tripFilterOption;
    [ObservableProperty] private IReadOnlyList<TripCard> _tripsView = [];
    [ObservableProperty] private string _tripSearch = string.Empty;

    partial void OnTripFilterOptionChanged(Option<TripFilter>? value) => BuildTrips();
    partial void OnTripSearchChanged(string value) => BuildTrips();

    [RelayCommand] private void SetTripFilter(Option<TripFilter>? f) { if (f != null) TripFilterOption = f; }

    public void ShowTripFilter(TripFilter filter)
    {
        ShowSection(TravelSection.Trips);
        TripFilterOption = TripFilters.First(f => f.Value == filter);
    }

    private void BuildTrips()
    {
        var f = TextFold.Fold(TripSearch);
        IEnumerable<TripCard> q = AllTrips;
        q = (TripFilterOption?.Value ?? TripFilter.All) switch
        {
            TripFilter.Upcoming => q.Where(t => t.Phase == TripPhase.Future && t.Summary.Status is not (TripStatus.Cancelled or TripStatus.Idea)).OrderBy(t => t.Summary.StartDate),
            TripFilter.Current => q.Where(t => t.Summary.Status == TripStatus.Active || t.Phase == TripPhase.Current && t.Summary.Status != TripStatus.Cancelled),
            TripFilter.Past => q.Where(t => t.Phase == TripPhase.Past || t.Summary.Status == TripStatus.Completed),
            TripFilter.Ideas => q.Where(t => t.Summary.Status is TripStatus.Idea or TripStatus.Planning && t.Phase == TripPhase.Undated || t.Summary.Status == TripStatus.Idea),
            TripFilter.Favorites => q.Where(t => t.IsFavorite),
            _ => q,
        };
        if (f.Length > 0) q = q.Where(t => TextFold.Fold(t.Name + " " + t.Destination + " " + string.Join(' ', t.Summary.Cities) + " " + t.Summary.Tags).Contains(f, StringComparison.Ordinal));
        TripsView = q.ToList();
    }

    // ───────────────────────── places list ─────────────────────────

    [ObservableProperty] private IReadOnlyList<Option<PlaceStatus?>> _placeStatusFilters = [];
    [ObservableProperty] private Option<PlaceStatus?>? _placeStatusFilter;
    [ObservableProperty] private string _placeSearch = string.Empty;
    [ObservableProperty] private IReadOnlyList<PlaceItem> _placesView = [];
    [ObservableProperty] private PlaceItem? _selectedPlace;
    [ObservableProperty] private bool _placeFavoritesOnly;

    partial void OnPlaceStatusFilterChanged(Option<PlaceStatus?>? value) => BuildPlaces();
    partial void OnPlaceSearchChanged(string value) => BuildPlaces();
    partial void OnPlaceFavoritesOnlyChanged(bool value) => BuildPlaces();

    partial void OnSelectedPlaceChanged(PlaceItem? value)
    {
        if (value != null) _ = PlaceDetails.LoadAsync(value.Id);
    }

    [RelayCommand] private void SetPlaceFilter(Option<PlaceStatus?>? f) { if (f != null) PlaceStatusFilter = f; }

    private void BuildPlaces()
    {
        var f = TextFold.Fold(PlaceSearch);
        var countryHits = PlaceSearch.Trim().Length >= 3 ? Countries.Search(PlaceSearch, 5).Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var keep = SelectedPlace?.Id;
        PlacesView = AllPlaces
            .Where(p => PlaceStatusFilter?.Value is not { } s || p.Summary.Status == s)
            .Where(p => !PlaceFavoritesOnly || p.IsFavorite)
            .Where(p => f.Length == 0 || TextFold.Fold(p.Name + " " + p.Where + " " + p.Tags + " " + p.TypeText).Contains(f, StringComparison.Ordinal)
                        || p.Summary.CountryCode != null && countryHits.Contains(p.Summary.CountryCode))
            .ToList();
        SelectedPlace = PlacesView.FirstOrDefault(p => p.Id == keep) ?? (Section == TravelSection.Places ? PlacesView.FirstOrDefault() : null);
    }

    // ───────────────────────── countries / world explored ─────────────────────────

    [ObservableProperty] private IReadOnlyList<Option<CountryFilter>> _countryFilters = [];
    [ObservableProperty] private Option<CountryFilter>? _countryFilterOption;
    [ObservableProperty] private string _countrySearch = string.Empty;
    [ObservableProperty] private IReadOnlyList<CountryRow> _countryRows = [];
    [ObservableProperty] private IReadOnlyList<ContinentBar> _continents = [];
    [ObservableProperty] private IReadOnlyList<TravelStatTile> _worldTiles = [];

    partial void OnCountryFilterOptionChanged(Option<CountryFilter>? value) => BuildCountries();
    partial void OnCountrySearchChanged(string value) => BuildCountries();

    [RelayCommand] private void SetCountryFilter(Option<CountryFilter>? f) { if (f != null) CountryFilterOption = f; }

    private static readonly string[] ContinentOrder = ["Europe", "Asia", "Africa", "North America", "South America", "Oceania", "Antarctica"];

    private void BuildCountries()
    {
        var progress = CountryProgress.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        // the UN states, plus any territory the user actually has something to do with
        var relevant = Countries.All.Where(c => c.IsUnState || progress.ContainsKey(c.Code)).Select(c => new CountryRow(c, progress.GetValueOrDefault(c.Code), Loc, Flags)).ToList();
        var f = TextFold.Fold(CountrySearch);
        CountryRows = relevant
            .Where(r => (CountryFilterOption?.Value ?? CountryFilter.All) switch
            {
                CountryFilter.Visited => r.Visited,
                CountryFilter.Wanted => r.WantToVisit,
                CountryFilter.NotYet => !r.Visited && !r.WantToVisit,
                _ => true,
            })
            .Where(r => f.Length == 0 || TextFold.Fold(r.Country.NameEn + " " + r.Country.NameDe + " " + r.Country.NameAr).Contains(f, StringComparison.Ordinal))
            .OrderByDescending(r => r.IsFavorite).ThenBy(r => r.Name, StringComparer.Create(Loc.Culture, true)).ToList();

        var un = Countries.All.Where(c => c.IsUnState).ToList();
        Continents = ContinentOrder.Select(name => new ContinentBar(Loc["Continent_" + name.Replace(" ", string.Empty)],
                un.Count(c => c.Continent == name && progress.TryGetValue(c.Code, out var p) && p.Visited), un.Count(c => c.Continent == name)))
            .Where(b => b.Total > 0).ToList();
        if (Stats is { } s)
            WorldTiles =
            [
                new(Loc["Tr_StatCountries"], TravelText.Number(s.CountriesVisited, Loc), "Flag", "Cyan", Loc.Format("Tr_OfUn", s.UnCountriesVisited, Countries.UnStateCount)),
                new(Loc["Tr_StatContinents"], $"{s.ContinentsVisited} / 7", "Travel", "Blue"),
                new(Loc["Tr_StatCities"], TravelText.Number(s.CitiesVisited, Loc), "City", "Amber"),
                new(Loc["Tr_StatWanted"], TravelText.Number(CountryProgress.Count(c => c.WantToVisit && !c.Visited), Loc), "Compass", "Gold"),
                new(Loc["Tr_StatDays"], TravelText.Number(s.DaysTraveling, Loc), "Calendar", "Green"),
                new(Loc["Tr_StatBucket"], $"{s.BucketCompleted} / {s.BucketTotal}", "Flag", "Purple"),
            ];
    }

    /// <summary>Opens a country on the world map.</summary>
    [RelayCommand]
    private void OpenCountry(CountryRow? r)
    {
        if (r == null) return;
        Workspace = null;
        Section = TravelSection.Map;
        ShowCountry(r.Code);
        if (Countries.Find(r.Code) is { } c) Fly(c.LabelLatitude, c.LabelLongitude, Math.Max(3.5, MapZoom));
    }

    [RelayCommand] private Task MarkCountryVisitedAsync() => SetCountryFlagAsync(markedVisited: !(Country?.Progress?.MarkedVisited ?? false));
    [RelayCommand] private Task ToggleCountryWantedAsync() => SetCountryFlagAsync(wanted: !(Country?.Progress?.WantToVisit ?? false));
    [RelayCommand] private Task ToggleCountryFavoriteAsync() => SetCountryFlagAsync(favorite: !(Country?.IsFavorite ?? false));

    private async Task SetCountryFlagAsync(bool? favorite = null, bool? wanted = null, bool? markedVisited = null)
    {
        if (Country is not { } c) return;
        if (await TryAsync(() => Store.SetCountryAsync(c.Code, favorite, wanted, markedVisited), "Tr_ErrSave")) await RefreshAsync();
    }

    [RelayCommand]
    private async Task AddCountryToBucketAsync()
    {
        if (Country is not { } c) return;
        if (await TryAsync(() => Store.CreateBucketAsync(new BucketDraft(Loc.Format("Tr_VisitCountry", c.Name), BucketKind.Country, CountryCode: c.Code)), "Tr_ErrSave"))
        {
            Notifications.Show(Loc.Format("Tr_BucketAdded", c.Name), NotificationKind.Success);
            await LoadDeskExtrasAsync();
        }
    }

    // ───────────────────────── timeline ─────────────────────────

    [ObservableProperty] private IReadOnlyList<TimelineYear> _timeline = [];

    private void BuildTimeline() =>
        Timeline = AllTrips.Where(t => t.Summary.Status != TripStatus.Cancelled && (t.Summary.StartDate != null || t.Summary.EndDate != null))
            .GroupBy(t => t.Year).OrderByDescending(g => g.Key)
            .Select(g => new TimelineYear(g.Key, g.OrderByDescending(t => t.Summary.StartDate).ToList(), Loc)).ToList();

    // ───────────────────────── memories ─────────────────────────

    [ObservableProperty] private IReadOnlyList<MemoryChapterItem> _memories = [];

    private async Task LoadMemoriesAsync()
    {
        try { Memories = (await Store.GetMemoriesAsync()).Select(c => new MemoryChapterItem(c, Loc, Flags)).ToList(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Loading memories failed");
            Notifications.Show(Loc["Tr_ErrLoad"], NotificationKind.Warning);
        }
    }

    [RelayCommand]
    private async Task OpenMemoryAsync(MemoryCard? card)
    {
        if (card == null) return;
        switch (card.Kind)
        {
            case MemoryKind.Photo when await Store.GetPhotoAsync(card.Item.Id) is { } photo:
                var chapter = Memories.FirstOrDefault(c => c.Chapter.Items.Any(i => i.Kind == MemoryKind.Photo && i.Id == card.Item.Id)) ?? MemoryHighlight;
                var ids = chapter?.Chapter.Items.Where(i => i.Kind == MemoryKind.Photo).Select(i => i.Id).ToHashSet() ?? [photo.Id];
                var rows = (await Store.ListPhotosAsync(new PhotoQuery(Take: 5000))).Where(r => ids.Contains(r.Id)).Select(r => new PhotoItem(r, Loc)).ToList();
                OpenViewer(rows, rows.FirstOrDefault(r => r.Id == photo.Id) ?? new PhotoItem(photo, Loc));
                break;
            case MemoryKind.Journal:
                ShowSection(TravelSection.Journal);
                await Journal.SelectAsync(card.Item.Id);
                break;
            case MemoryKind.Visit when card.Item.PlaceId is { } pid:
                OpenPlace(pid);
                break;
        }
    }

    [RelayCommand] private void OpenChapterTrip(MemoryChapterItem? c) { if (c?.TripId is { } id) OpenTrip(id); }

    // ───────────────────────── bucket list ─────────────────────────

    public ObservableCollection<BucketItem> BucketItems { get; } = [];
    [ObservableProperty] private bool _bucketShowDone = true;
    [ObservableProperty] private string _bucketProgressText = string.Empty;
    [ObservableProperty] private double _bucketPercent;

    partial void OnBucketShowDoneChanged(bool value) => _ = LoadBucketAsync();

    private async Task LoadBucketAsync()
    {
        try
        {
            var rows = await Store.ListBucketAsync();
            BucketItems.Clear();
            foreach (var r in rows.Where(r => BucketShowDone || !r.IsCompleted)) BucketItems.Add(new BucketItem(r, Loc, Flags));
            var done = rows.Count(r => r.IsCompleted);
            BucketPercent = rows.Count == 0 ? 0 : done * 100.0 / rows.Count;
            BucketProgressText = Loc.Format("Tr_BucketProgress", done, rows.Count);
            BucketPreview = rows.Where(b => !b.IsCompleted).Take(5).Select(b => new BucketItem(b, Loc, Flags)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Loading the bucket list failed");
            Notifications.Show(Loc["Tr_ErrLoad"], NotificationKind.Warning);
        }
    }

    [RelayCommand]
    public async Task AddBucketAsync()
    {
        var editor = new BucketEditorViewModel(Loc, null, AllPlaces, AllTrips, Countries);
        if (!Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        if (await TryAsync(() => Store.CreateBucketAsync(draft), "Tr_ErrSave")) { await LoadBucketAsync(); Stats = await Store.GetStatsAsync(Today); BuildCountries(); }
    }

    [RelayCommand]
    private async Task EditBucketAsync(BucketItem? item)
    {
        if (item == null) return;
        var editor = new BucketEditorViewModel(Loc, item.Row, AllPlaces, AllTrips, Countries);
        if (!Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        if (await TryAsync(() => Store.UpdateBucketAsync(item.Id, draft), "Tr_ErrSave")) await LoadBucketAsync();
    }

    [RelayCommand]
    private async Task ToggleBucketAsync(BucketItem? item)
    {
        if (item == null) return;
        if (await TryAsync(() => Store.SetBucketCompletedAsync(item.Id, !item.IsCompleted, null), "Tr_ErrSave"))
        {
            if (!item.IsCompleted) Notifications.Show(Loc.Format("Tr_BucketDone", item.Title), NotificationKind.Success);
            await LoadBucketAsync();
            Stats = await Store.GetStatsAsync(Today);
            BuildCountries();
        }
    }

    [RelayCommand]
    private async Task DeleteBucketAsync(BucketItem? item)
    {
        if (item == null || !Dialogs.Confirm(Loc.Format("Tr_ConfirmDeleteBucket", item.Title), Loc["Proj_Delete"])) return;
        if (await TryAsync(() => Store.DeleteBucketAsync(item.Id), "Tr_ErrSave")) await LoadBucketAsync();
    }

    [RelayCommand] private Task MoveBucketUpAsync(BucketItem? item) => MoveBucketAsync(item, -1);
    [RelayCommand] private Task MoveBucketDownAsync(BucketItem? item) => MoveBucketAsync(item, 1);

    private async Task MoveBucketAsync(BucketItem? item, int delta)
    {
        if (item == null) return;
        var open = BucketItems.Where(b => b.IsCompleted == item.IsCompleted).ToList();
        var i = open.IndexOf(item);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= open.Count) return;
        (open[i], open[j]) = (open[j], open[i]);
        if (await TryAsync(() => Store.ReorderBucketAsync(open.Select(b => b.Id).ToList()), "Tr_ErrSave")) await LoadBucketAsync();
    }
}
