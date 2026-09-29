using System.Collections.ObjectModel;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.App.Views.Travel;
using Arcadia.Core.Documents;
using Arcadia.Core.Localization;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Settings;
using Arcadia.Core.Travel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Travel;

public enum MapPanel { None, Place, Country, Found }

/// <summary>
/// Arcadia Travel — the explorer's desk: a world map with saved places, trips with day-by-day itineraries, a travel
/// journal, photos (referenced, never copied), memories, a bucket list, countries and a timeline. Everything is local;
/// the map may show online detail tiles and place search can ask OpenStreetMap when the user explicitly requests it.
/// </summary>
public sealed partial class TravelViewModel : ObservableObject, IActivatable, IDeactivatable, IShortcutTarget, ITravelContext, IRecipient<TravelChangedMessage>
{
    private readonly ISettingsService _settings;
    private readonly IGeocodingService _geo;
    private readonly IProjectStore _projects;
    private readonly IDocumentStore _documentStore;
    private readonly INavigationService _navigation;
    private readonly StartupState _startup;
    private readonly ILoggerFactory _logs;
    private readonly ILogger<TravelViewModel> _logger;
    private bool _stale = true;
    private bool _loaded;

    public TravelViewModel(ITravelStore store, ICountryCatalog countries, IGeocodingService geo, MapTileImages tiles, FlagImages flags, IDialogService dialogs,
        INotificationService notifications, ILocalizer loc, ISettingsService settings, IMessenger messenger, TravelPhotoService photos, DocumentActions documents,
        IDocumentStore documentStore, IProjectStore projects, INavigationService navigation, StartupState startup, ILoggerFactory logs)
    {
        Store = store;
        Countries = countries;
        _geo = geo;
        Tiles = tiles;
        Flags = flags;
        Dialogs = dialogs;
        Notifications = notifications;
        Loc = loc;
        _settings = settings;
        Photos = photos;
        Documents = documents;
        _documentStore = documentStore;
        _projects = projects;
        _navigation = navigation;
        _startup = startup;
        _logs = logs;
        _logger = logs.CreateLogger<TravelViewModel>();
        Current = this;
        Sections =
        [
            new(TravelSection.Desk, "Compass"), new(TravelSection.Map, "Map"), new(TravelSection.Trips, "Suitcase"), new(TravelSection.Places, "MapPin"),
            new(TravelSection.Journal, "Journal"), new(TravelSection.Memories, "Image"), new(TravelSection.Bucket, "Flag"),
            new(TravelSection.Countries, "Travel"), new(TravelSection.Timeline, "Timeline"),
        ];
        PlaceDetails = new PlaceDetailsViewModel(this, _logger);
        Journal = new TravelJournalViewModel(this, null, _logger);
        var t = settings.Current.Travel;
        _mapLatitude = t.MapLatitude ?? 30;
        _mapLongitude = t.MapLongitude ?? 12;
        _mapZoom = t.MapZoom ?? 1.7;
        _section = Enum.TryParse<TravelSection>(t.LastSection, true, out var s) ? s : TravelSection.Desk;
        RefreshLabels();
        messenger.RegisterAll(this);
        loc.CultureChanged += (_, _) => { RefreshLabels(); Build(); };
    }

    /// <summary>The page, once created (used to flush unsaved text when Arcadia closes).</summary>
    public static TravelViewModel? Current { get; private set; }

    // ── ITravelContext ──
    public ILocalizer Loc { get; }
    public ITravelStore Store { get; }
    public IDialogService Dialogs { get; }
    public INotificationService Notifications { get; }
    public TravelPhotoService Photos { get; }
    public DocumentActions Documents { get; }
    public FlagImages Flags { get; }
    public ICountryCatalog Countries { get; }
    public MapTileImages Tiles { get; }
    public IReadOnlyList<PlaceItem> AllPlaces { get; private set; } = [];
    public IReadOnlyList<TripCard> AllTrips { get; private set; } = [];
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    public string LanguageCode => Loc.Culture.TwoLetterISOLanguageName;

    public ObservableCollection<TravelSectionItem> Sections { get; }
    public PlaceDetailsViewModel PlaceDetails { get; }
    public TravelJournalViewModel Journal { get; }

    [ObservableProperty] private TravelSection _section;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    /// <summary>Any trip at all (the desk hero then invites the next trip rather than the first).</summary>
    [ObservableProperty] private bool _hasTrips;
    [ObservableProperty] private TravelStats? _stats;
    [ObservableProperty] private IReadOnlyList<CountryProgress> _countryProgress = [];
    [ObservableProperty] private TripWorkspaceViewModel? _workspace;
    [ObservableProperty] private PhotoViewerViewModel? _viewer;

    // ── map state ──
    public ObservableCollection<MapMarker> Markers { get; } = [];
    [ObservableProperty] private IReadOnlyDictionary<string, CountryShade> _countryShades = new Dictionary<string, CountryShade>();
    [ObservableProperty] private double _mapLatitude;
    [ObservableProperty] private double _mapLongitude;
    [ObservableProperty] private double _mapZoom;
    [ObservableProperty] private object? _selectedMarker;
    [ObservableProperty] private string? _selectedCountry;
    [ObservableProperty] private MapPoint? _contextPoint;
    [ObservableProperty] private MapPanel _panel;
    [ObservableProperty] private bool _showWanted = true;
    [ObservableProperty] private bool _showPlanned = true;
    [ObservableProperty] private bool _showVisited = true;
    [ObservableProperty] private bool _favoritesOnly;
    [ObservableProperty] private string _tileNotice = string.Empty;

    /// <summary>The view flies the map when this is raised (lat, lon, zoom or null to keep).</summary>
    public event Action<double, double, double?>? FlyRequested;
    /// <summary>The view frames these points.</summary>
    public event Action<IReadOnlyCollection<(double Lat, double Lon)>>? FitRequested;

    // A request made before the map view exists (deep link, first visit) is kept for it.
    private Action? _pendingMap;

    private void Fly(double lat, double lon, double? zoom)
    {
        if (FlyRequested is { } h) h(lat, lon, zoom);
        else _pendingMap = () => FlyRequested?.Invoke(lat, lon, zoom);
    }

    private void Fit(IReadOnlyCollection<(double Lat, double Lon)> points)
    {
        if (FitRequested is { } h) h(points);
        else _pendingMap = () => FitRequested?.Invoke(points);
    }

    /// <summary>Called by the map view once it listens.</summary>
    public void ReplayPendingMapRequest()
    {
        var a = _pendingMap;
        _pendingMap = null;
        a?.Invoke();
    }

    // ── search ──
    public ObservableCollection<GeoResultItem> SearchResults { get; } = [];
    [ObservableProperty] private string _mapSearch = string.Empty;
    [ObservableProperty] private string? _searchStatus;
    [ObservableProperty] private bool _isSearchingOnline;
    [ObservableProperty] private GeoResultItem? _found;
    public bool OnlineSearchEnabled => _geo.OnlineEnabled;
    public bool OnlineTilesEnabled => _settings.Current.Travel.OnlineMapTiles;

    // ── country panel ──
    [ObservableProperty] private CountryRow? _country;
    [ObservableProperty] private IReadOnlyList<PlaceItem> _countryPlaces = [];
    [ObservableProperty] private IReadOnlyList<TripCard> _countryTrips = [];

    partial void OnSectionChanged(TravelSection value)
    {
        foreach (var s in Sections) s.IsSelected = s.Section == value;
        _settings.Current.Travel.LastSection = value.ToString();
        _ = SaveSettingsAsync();
        if (value == TravelSection.Journal) _ = Journal.LoadAsync();
        if (value == TravelSection.Memories) _ = LoadMemoriesAsync();
        if (value == TravelSection.Bucket) _ = LoadBucketAsync();
        if (value == TravelSection.Places && SelectedPlace == null && PlacesView.FirstOrDefault() is { } first) SelectedPlace = first;
    }

    partial void OnShowWantedChanged(bool value) => BuildMarkers();
    partial void OnShowPlannedChanged(bool value) => BuildMarkers();
    partial void OnShowVisitedChanged(bool value) => BuildMarkers();
    partial void OnFavoritesOnlyChanged(bool value) => BuildMarkers();

    private void RefreshLabels()
    {
        foreach (var s in Sections) s.Label = Loc["TrSection_" + s.Section];
        foreach (var s in Sections) s.IsSelected = s.Section == Section;
        PlaceStatusFilters = [new(null, Loc["Tr_FilterAll"]), .. Enum.GetValues<PlaceStatus>().Select(s => new Option<PlaceStatus?>(s, Loc["PlaceStatus_" + s]))];
        PlaceStatusFilter ??= PlaceStatusFilters[0];
        TripFilters = Enum.GetValues<TripFilter>().Select(f => new Option<TripFilter>(f, Loc["TripFilter_" + f])).ToList();
        TripFilterOption = TripFilters.FirstOrDefault(f => f.Value == (TripFilterOption?.Value ?? TripFilter.All));
        CountryFilters = Enum.GetValues<CountryFilter>().Select(f => new Option<CountryFilter>(f, Loc["CountryFilter_" + f])).ToList();
        CountryFilterOption = CountryFilters.FirstOrDefault(f => f.Value == (CountryFilterOption?.Value ?? CountryFilter.Visited));
    }

    // ───────────────────────── lifecycle ─────────────────────────

    public async Task ActivateAsync()
    {
        if (_stale || !_loaded) await LoadAsync();
        if (Workspace != null) await Workspace.ActivateAsync();
    }

    public void Deactivate()
    {
        _ = PlaceDetails.SaveNotesAsync();
        _ = Journal.SaveAsync();
        Workspace?.Deactivate();
        var t = _settings.Current.Travel;
        t.MapLatitude = Math.Round(MapLatitude, 4);
        t.MapLongitude = Math.Round(MapLongitude, 4);
        t.MapZoom = Math.Round(MapZoom, 2);
        _ = SaveSettingsAsync();
    }

    public Task FlushForExit() => Task.WhenAll(PlaceDetails.FlushForExit(), Journal.FlushForExit(), Workspace?.FlushForExit() ?? Task.CompletedTask);

    private async Task SaveSettingsAsync()
    {
        try { await _settings.SaveAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not save Travel settings"); }
    }

    public void Receive(TravelChangedMessage message) => _stale = true;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await _startup.DatabaseReady;
            await RefreshAsync();
            _loaded = true;
            if (Section == TravelSection.Journal) await Journal.LoadAsync();
            if (Section == TravelSection.Memories) await LoadMemoriesAsync();
            if (Section == TravelSection.Bucket) await LoadBucketAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Loading Travel failed");
            Notifications.Show(Loc["Tr_ErrLoad"], NotificationKind.Warning);
        }
        finally { IsLoading = false; }
    }

    /// <summary>Reloads places, trips, countries and statistics, then rebuilds every view of them.</summary>
    public async Task RefreshAsync()
    {
        _stale = false;
        var places = await Store.ListPlacesAsync(new PlaceQuery());
        var trips = await Store.ListTripsAsync();
        CountryProgress = await Store.GetCountryProgressAsync();
        Stats = await Store.GetStatsAsync(Today);
        AllPlaces = places.Select(p => new PlaceItem(p, Loc, Countries, Flags)).ToList();
        AllTrips = trips.Select(t => new TripCard(t, Loc, Today, Flags)).ToList();
        OnPropertyChanged(nameof(AllPlaces));
        OnPropertyChanged(nameof(AllTrips));
        Journal.RebuildOptions();
        Build();
        await LoadDeskExtrasAsync();
        if (PlaceDetails.Place is { } p) await PlaceDetails.LoadAsync(p.Id);
        if (Country is { } c) ShowCountry(c.Code);
    }

    /// <summary>Rebuilds lists and map layers from the loaded data (no database access).</summary>
    private void Build()
    {
        IsEmpty = AllPlaces.Count == 0 && AllTrips.Count == 0;
        HasTrips = AllTrips.Count > 0;
        BuildMarkers();
        CountryShades = CountryProgress.Where(c => c.Visited || c.WantToVisit)
            .ToDictionary(c => c.Code, c => c.Visited ? CountryShade.Visited : CountryShade.Wanted, StringComparer.OrdinalIgnoreCase);
        BuildDesk();
        BuildTrips();
        BuildPlaces();
        BuildCountries();
        BuildTimeline();
        Sections.First(s => s.Section == TravelSection.Trips).Count = AllTrips.Count > 0 ? TravelText.Number(AllTrips.Count, Loc) : null;
        Sections.First(s => s.Section == TravelSection.Places).Count = AllPlaces.Count > 0 ? TravelText.Number(AllPlaces.Count, Loc) : null;
        Sections.First(s => s.Section == TravelSection.Countries).Count = Stats is { CountriesVisited: > 0 } st ? TravelText.Number(st.CountriesVisited, Loc) : null;
    }

    private void BuildMarkers()
    {
        Markers.Clear();
        foreach (var p in AllPlaces)
        {
            if (FavoritesOnly && !p.IsFavorite) continue;
            if (p.Summary.Status == PlaceStatus.WantToVisit && !ShowWanted || p.Summary.Status == PlaceStatus.Planned && !ShowPlanned || p.Summary.Status == PlaceStatus.Visited && !ShowVisited) continue;
            if (p.ToMarker() is { } m) Markers.Add(m);
        }
        if (Found is { } f) Markers.Add(new MapMarker(("found", 0), f.Place.Latitude, f.Place.Longitude, MapMarkerKind.Search, f.Name));
    }

    // ───────────────────────── ITravelContext helpers ─────────────────────────

    public async Task<bool> TryAsync(Func<Task> action, string failureKey)
    {
        try
        {
            await action();
            return true;
        }
        catch (ArgumentException ex)
        {
            Notifications.Show(ex.Message, NotificationKind.Warning);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Travel action failed ({Key})", failureKey);
            Notifications.Show(Loc[failureKey], NotificationKind.Warning);
            return false;
        }
    }

    public void OpenViewer(IReadOnlyList<PhotoItem> photos, PhotoItem current) =>
        Viewer = new PhotoViewerViewModel(this, photos, current, () => Viewer = null);

    [RelayCommand]
    private void SelectSection(TravelSectionItem? item)
    {
        if (item == null) return;
        Workspace = null;
        Section = item.Section;
    }

    public void ShowSection(TravelSection section)
    {
        Workspace = null;
        Section = section;
    }

    // ───────────────────────── map interaction ─────────────────────────

    [RelayCommand]
    private void MarkerClicked(MapMarker? marker)
    {
        if (marker?.Key is ValueTuple<string, int> key && key.Item1 == "place")
        {
            if (Section == TravelSection.Map) _ = ShowPlaceOnPanelAsync(key.Item2);
            else ShowOnMap(key.Item2); // from the desk: open the map on it
        }
        else if (marker?.Key is ValueTuple<string, int> f && f.Item1 == "found") Panel = MapPanel.Found;
    }

    [RelayCommand]
    private void CountryClicked(string? code)
    {
        if (code == null || Panel == MapPanel.Place && SelectedMarker != null) return;
        ShowCountry(code);
    }

    private async Task ShowPlaceOnPanelAsync(int id)
    {
        SelectedMarker = ("place", id);
        Panel = MapPanel.Place;
        await PlaceDetails.LoadAsync(id);
    }

    public void ShowCountry(string code)
    {
        if (Countries.Find(code) is not { } info) return;
        var progress = CountryProgress.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        Country = new CountryRow(info, progress, Loc, Flags);
        CountryPlaces = AllPlaces.Where(p => string.Equals(p.Summary.CountryCode, code, StringComparison.OrdinalIgnoreCase)).ToList();
        CountryTrips = AllTrips.Where(t => t.Summary.CountryCodes.Contains(code, StringComparer.OrdinalIgnoreCase)).ToList();
        SelectedCountry = code;
        if (Section == TravelSection.Map || Section == TravelSection.Countries)
        {
            SelectedMarker = null;
            Panel = MapPanel.Country;
        }
    }

    [RelayCommand] private void ClosePanel() { Panel = MapPanel.None; SelectedMarker = null; SelectedCountry = null; Found = null; BuildMarkers(); }

    [RelayCommand]
    private void FitAll()
    {
        var pts = Markers.Select(m => (m.Latitude, m.Longitude)).ToList();
        if (pts.Count == 0) Fly(30, 12, 1.7);
        else Fit(pts);
    }

    [RelayCommand]
    private async Task ToggleTilesAsync()
    {
        _settings.Current.Travel.OnlineMapTiles = !_settings.Current.Travel.OnlineMapTiles;
        _settings.NotifyChanged();
        OnPropertyChanged(nameof(OnlineTilesEnabled));
        await SaveSettingsAsync();
    }

    /// <summary>Called by the view when the tile layer reports its state.</summary>
    public void UpdateTileNotice(TileStatus status, bool showing) =>
        TileNotice = !showing || status is TileStatus.Ok or TileStatus.NotModified or TileStatus.NotFound ? string.Empty : Loc["TileStatus_" + status];

    public void ShowOnMap(int placeId)
    {
        if (AllPlaces.FirstOrDefault(p => p.Id == placeId) is not { } p) return;
        Workspace = null;
        Section = TravelSection.Map;
        _ = ShowPlaceOnPanelAsync(placeId);
        if (p.Summary.Latitude is { } lat && p.Summary.Longitude is { } lon) Fly(lat, lon, Math.Max(MapZoom, p.Summary.Type == PlaceType.Country ? 4 : 9));
        else Notifications.Show(Loc["Tr_NoPositionHint"], NotificationKind.Info);
    }

    public void OpenPlace(int placeId)
    {
        Workspace = null;
        if (Section is not (TravelSection.Map or TravelSection.Places)) Section = TravelSection.Places;
        if (Section == TravelSection.Places) SelectedPlace = PlacesView.FirstOrDefault(p => p.Id == placeId) ?? AllPlaces.FirstOrDefault(p => p.Id == placeId);
        else _ = ShowPlaceOnPanelAsync(placeId);
    }

    // ───────────────────────── search (map) ─────────────────────────

    partial void OnMapSearchChanged(string value)
    {
        SearchStatus = null;
        SearchResults.Clear();
        if (string.IsNullOrWhiteSpace(value)) return;
        // offline results as you type: bundled cities/countries, never the network
        foreach (var saved in AllPlaces.Where(p => Core.Text.TextFold.Fold(p.Name).Contains(Core.Text.TextFold.Fold(value), StringComparison.Ordinal)).Take(3))
            if (saved.Summary.Latitude is { } la && saved.Summary.Longitude is { } lo)
                SearchResults.Add(new GeoResultItem(new GeoPlace(saved.Name, Loc["Tr_SavedPlace"], la, lo, saved.Summary.CountryCode, saved.Summary.Region, saved.Summary.City,
                    saved.Summary.Type, GeoOrigin.Offline, "saved:" + saved.Id), Loc, Flags));
        foreach (var g in _geo.SearchOffline(value, LanguageCode, 8)) SearchResults.Add(new GeoResultItem(g, Loc, Flags));
        if (SearchResults.Count == 0) SearchStatus = OnlineSearchEnabled ? Loc["Tr_TryOnline"] : Loc["Tr_NoResults"];
    }

    /// <summary>Searches OpenStreetMap Nominatim — only on this explicit request (Enter or the button).</summary>
    [RelayCommand]
    private async Task SearchOnlineAsync()
    {
        if (string.IsNullOrWhiteSpace(MapSearch) || IsSearchingOnline) return;
        IsSearchingOnline = true;
        SearchStatus = Loc["Tr_Searching"];
        try
        {
            var result = await _geo.SearchOnlineAsync(MapSearch, LanguageCode);
            SearchResults.Clear();
            foreach (var p in result.Places) SearchResults.Add(new GeoResultItem(p, Loc, Flags));
            SearchStatus = result.Error != GeoSearchError.None ? Loc["GeoError_" + result.Error] : result.Places.Count == 0 ? Loc["Tr_NoResults"] : null;
        }
        finally { IsSearchingOnline = false; }
    }

    [RelayCommand]
    private void PickResult(GeoResultItem? item)
    {
        if (item == null) return;
        SearchResults.Clear();
        SearchStatus = null;
        if (item.Place.SourceRef is { } src && src.StartsWith("saved:", StringComparison.Ordinal) && int.TryParse(src[6..], out var savedId))
        {
            ShowOnMap(savedId);
            return;
        }
        Found = item;
        SelectedMarker = ("found", 0);
        BuildMarkers();
        Panel = MapPanel.Found;
        Section = TravelSection.Map;
        Fly(item.Place.Latitude, item.Place.Longitude, item.Place.SuggestedType == PlaceType.Country ? 4 : 10);
    }

    [RelayCommand] private Task SaveFoundWantedAsync() => SaveFoundAsync(PlaceStatus.WantToVisit);
    [RelayCommand] private Task SaveFoundVisitedAsync() => SaveFoundAsync(PlaceStatus.Visited);

    private async Task SaveFoundAsync(PlaceStatus status)
    {
        if (Found is not { } f) return;
        var editor = new PlaceEditorViewModel(Loc, _geo, Countries, Flags, null, f.Place, status);
        if (!Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        await CreatePlaceAsync(draft, status == PlaceStatus.Visited);
        Found = null;
    }

    /// <summary>Right-click "Add place here": the bundled data names the spot; online lookup only if the user asks in the editor.</summary>
    [RelayCommand]
    private async Task AddPlaceHereAsync()
    {
        if (ContextPoint is not { } p) return;
        var here = _geo.DescribeOffline(p.Latitude, p.Longitude, LanguageCode) ?? new GeoPlace(GeoMath.Format(p.Latitude, p.Longitude, 3), null, p.Latitude, p.Longitude, p.CountryCode, null, null, PlaceType.Other, GeoOrigin.Map);
        here = here with { Latitude = p.Latitude, Longitude = p.Longitude };
        var editor = new PlaceEditorViewModel(Loc, _geo, Countries, Flags, null, here) { Search = string.Empty };
        if (!Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        await CreatePlaceAsync(draft, draft.Status == PlaceStatus.Visited);
    }

    [RelayCommand]
    private void CenterHere()
    {
        if (ContextPoint is { } p) Fly(p.Latitude, p.Longitude, null);
    }

    [RelayCommand]
    private void CopyCoordinates()
    {
        if (ContextPoint is not { } p) return;
        try { System.Windows.Clipboard.SetText(GeoMath.Format(p.Latitude, p.Longitude, 5)); Notifications.Show(Loc["Tr_CoordinatesCopied"], NotificationKind.Info); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    [RelayCommand]
    private void CountryHere()
    {
        if (ContextPoint?.CountryCode is { } code) ShowCountry(code);
    }

    // ───────────────────────── places ─────────────────────────

    [RelayCommand]
    public async Task AddPlaceAsync()
    {
        var editor = new PlaceEditorViewModel(Loc, _geo, Countries, Flags, null);
        if (!Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        await CreatePlaceAsync(draft, draft.Status == PlaceStatus.Visited);
    }

    private async Task CreatePlaceAsync(PlaceDraft draft, bool addVisit)
    {
        try
        {
            if (draft.Source is { } src && src.StartsWith("osm:", StringComparison.Ordinal) && await Store.FindPlaceBySourceAsync(src) is { } dup)
            {
                Notifications.Show(Loc.Format("Tr_AlreadySaved", dup.Name), NotificationKind.Info);
                ShowOnMap(dup.Id);
                return;
            }
            var place = await Store.CreatePlaceAsync(draft);
            if (addVisit) await Store.AddVisitAsync(place.Id, null, null, null);
            Notifications.Show(Loc.Format("Tr_PlaceSaved", place.Name), NotificationKind.Success);
            await RefreshAsync();
            if (Section is TravelSection.Map or TravelSection.Desk) ShowOnMap(place.Id);
            else OpenPlace(place.Id);
        }
        catch (ArgumentException ex) { Notifications.Show(ex.Message, NotificationKind.Warning); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Saving a place failed");
            Notifications.Show(Loc["Tr_ErrSave"], NotificationKind.Warning);
        }
    }

    public async Task EditPlaceAsync(int id)
    {
        var place = await Store.GetPlaceAsync(id);
        if (place == null) return;
        var editor = new PlaceEditorViewModel(Loc, _geo, Countries, Flags, place);
        if (!Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        if (await TryAsync(() => Store.UpdatePlaceAsync(id, draft), "Tr_ErrSave")) await RefreshAsync();
    }

    public async Task AddPlaceToTripAsync(int placeId)
    {
        if (AllTrips.Count == 0)
        {
            await NewTripAsync(placeId);
            return;
        }
        var picker = TravelPickers.Trip(this);
        if (!Dialogs.Show(picker) || picker.Selected is not { } trip) return;
        if (await TryAsync(() => Store.AddPlaceToTripAsync(trip.Id, placeId), "Tr_ErrSave"))
        {
            Notifications.Show(Loc.Format("Tr_AddedToTrip", AllPlaces.FirstOrDefault(p => p.Id == placeId)?.Name ?? string.Empty, trip.Title), NotificationKind.Success);
            await RefreshAsync();
            if (Workspace?.TripId == trip.Id) await Workspace.ReloadAsync();
        }
    }

    public async Task MarkVisitedAsync(int placeId)
    {
        var place = AllPlaces.FirstOrDefault(p => p.Id == placeId);
        // the current trip (by dates) is the natural default
        var current = AllTrips.FirstOrDefault(t => t.Phase == TripPhase.Current && t.Summary.Status != TripStatus.Cancelled);
        var dialog = new VisitEditorViewModel(Loc, place?.Name ?? string.Empty, AllTrips, current?.Id, null);
        if (!Dialogs.Show(dialog)) return;
        if (await TryAsync(() => Store.AddVisitAsync(placeId, dialog.Date, dialog.Trip.Value, dialog.Note), "Tr_ErrSave"))
        {
            Notifications.Show(Loc.Format("Tr_MarkedVisited", place?.Name ?? string.Empty), NotificationKind.Success);
            await RefreshAsync();
        }
    }

    public async Task SetFavoriteAsync(int placeId, bool favorite)
    {
        if (await TryAsync(() => Store.SetPlaceFavoriteAsync(placeId, favorite), "Tr_ErrSave")) await RefreshAsync();
    }

    public async Task SetStatusAsync(int placeId, PlaceStatus status)
    {
        if (await TryAsync(() => Store.SetPlaceStatusAsync(placeId, status), "Tr_ErrSave")) await RefreshAsync();
    }

    public async Task DeletePlaceAsync(int placeId)
    {
        var place = AllPlaces.FirstOrDefault(p => p.Id == placeId);
        if (!Dialogs.Confirm(Loc.Format("Tr_ConfirmDeletePlace", place?.Name ?? string.Empty), Loc["Proj_Delete"])) return;
        if (!await TryAsync(() => Store.DeletePlaceAsync(placeId), "Tr_ErrSave")) return;
        if (PlaceDetails.Place?.Id == placeId) { PlaceDetails.Place = null; Panel = MapPanel.None; }
        SelectedPlace = null;
        await RefreshAsync();
    }

    // place context menu (map lists, places list)
    [RelayCommand] private void OpenPlaceItem(PlaceItem? p) { if (p != null) OpenPlace(p.Id); }
    [RelayCommand] private void ShowPlaceItemOnMap(PlaceItem? p) { if (p != null) ShowOnMap(p.Id); }
    [RelayCommand] private Task AddPlaceItemToTripAsync(PlaceItem? p) => p != null ? AddPlaceToTripAsync(p.Id) : Task.CompletedTask;
    [RelayCommand] private Task MarkPlaceItemVisitedAsync(PlaceItem? p) => p != null ? MarkVisitedAsync(p.Id) : Task.CompletedTask;
    [RelayCommand] private Task TogglePlaceItemFavoriteAsync(PlaceItem? p) => p != null ? SetFavoriteAsync(p.Id, !p.IsFavorite) : Task.CompletedTask;
    [RelayCommand] private Task EditPlaceItemAsync(PlaceItem? p) => p != null ? EditPlaceAsync(p.Id) : Task.CompletedTask;
    [RelayCommand] private Task DeletePlaceItemAsync(PlaceItem? p) => p != null ? DeletePlaceAsync(p.Id) : Task.CompletedTask;

    // ───────────────────────── trips ─────────────────────────

    [RelayCommand]
    private Task NewTrip() => NewTripAsync(null);

    public async Task NewTripAsync(int? placeId)
    {
        var projects = await LoadProjectsAsync();
        var editor = new TripEditorViewModel(Loc, null, AllPlaces, projects, placeId);
        if (!Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        try
        {
            var trip = await Store.CreateTripAsync(draft, editor.PlaceIds);
            if (TravelRules.DurationDays(trip.StartDate, trip.EndDate) is { } days && days <= 31) await Store.EnsureDaysForDatesAsync(trip.Id);
            Notifications.Show(Loc.Format("Tr_TripCreated", trip.Name), NotificationKind.Success);
            await RefreshAsync();
            OpenTrip(trip.Id);
        }
        catch (ArgumentException ex) { Notifications.Show(ex.Message, NotificationKind.Warning); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Creating a trip failed");
            Notifications.Show(Loc["Tr_ErrSave"], NotificationKind.Warning);
        }
    }

    internal async Task<IReadOnlyList<(int Id, string Name)>> LoadProjectsAsync()
    {
        try { return (await _projects.ListAsync()).Where(p => p.Status != ProjectStatus.Archived).Select(p => (p.Id, p.Name)).ToList(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Projects unavailable"); return []; }
    }

    public void OpenTrip(int tripId)
    {
        var ws = new TripWorkspaceViewModel(this, tripId, _logs.CreateLogger<TripWorkspaceViewModel>());
        Section = TravelSection.Trips; // "back" returns to the trips
        Workspace = ws;
        _ = ws.ActivateAsync();
    }

    [RelayCommand] private void OpenTripCard(TripCard? t) { if (t != null) OpenTrip(t.Id); }

    [RelayCommand]
    public async Task EditTripAsync(TripCard? card)
    {
        if (card == null) return;
        var trip = await Store.GetTripAsync(card.Id);
        if (trip == null) return;
        var editor = new TripEditorViewModel(Loc, trip, AllPlaces, await LoadProjectsAsync());
        if (!Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        if (await TryAsync(() => Store.UpdateTripAsync(trip.Id, draft), "Tr_ErrSave"))
        {
            await RefreshAsync();
            if (Workspace?.TripId == trip.Id) await Workspace.ReloadAsync();
        }
    }

    [RelayCommand] private Task CompleteTripAsync(TripCard? t) => t != null ? SetTripStatusAsync(t.Id, TripStatus.Completed) : Task.CompletedTask;
    [RelayCommand] private Task ActivateTripAsync(TripCard? t) => t != null ? SetTripStatusAsync(t.Id, TripStatus.Active) : Task.CompletedTask;
    [RelayCommand] private Task CancelTripAsync(TripCard? t) => t != null ? SetTripStatusAsync(t.Id, TripStatus.Cancelled) : Task.CompletedTask;

    public async Task SetTripStatusAsync(int tripId, TripStatus status)
    {
        if (!await TryAsync(() => Store.SetTripStatusAsync(tripId, status), "Tr_ErrSave")) return;
        Notifications.Show(Loc.Format("Tr_TripStatusSet", AllTrips.FirstOrDefault(t => t.Id == tripId)?.Name ?? string.Empty, Loc["TrStatus_" + status]), NotificationKind.Success);
        await RefreshAsync();
        if (Workspace?.TripId == tripId) await Workspace.ReloadAsync();
    }

    [RelayCommand]
    private async Task ToggleTripFavoriteAsync(TripCard? t)
    {
        if (t != null && await TryAsync(() => Store.SetTripFavoriteAsync(t.Id, !t.IsFavorite), "Tr_ErrSave")) await RefreshAsync();
    }

    [RelayCommand]
    public async Task DeleteTripAsync(TripCard? t)
    {
        if (t == null || !Dialogs.Confirm(Loc.Format("Tr_ConfirmDeleteTrip", t.Name), Loc["Proj_Delete"])) return;
        if (!await TryAsync(() => Store.DeleteTripAsync(t.Id), "Tr_ErrSave")) return;
        if (Workspace?.TripId == t.Id) Workspace = null;
        await RefreshAsync();
    }

    // ───────────────────────── journal ─────────────────────────

    public async Task NewJournalForAsync(int? tripId, int? placeId)
    {
        try
        {
            var entry = await Store.CreateJournalAsync(new JournalDraft(Loc["Tr_UntitledEntry"], Today, string.Empty, tripId, placeId));
            Workspace = null;
            Section = TravelSection.Journal;
            await Journal.LoadAsync(entry.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Creating a journal entry failed");
            Notifications.Show(Loc["Tr_ErrSave"], NotificationKind.Warning);
        }
    }

    // ───────────────────────── shortcuts & search results ─────────────────────────

    public bool OnNew()
    {
        if (Workspace != null) return Workspace.OnNew();
        switch (Section)
        {
            case TravelSection.Journal: _ = Journal.NewAsync(); break;
            case TravelSection.Places or TravelSection.Map: _ = AddPlaceAsync(); break;
            case TravelSection.Bucket: _ = AddBucketAsync(); break;
            default: _ = NewTripAsync(null); break;
        }
        return true;
    }

    public bool OnSave()
    {
        _ = PlaceDetails.SaveNotesAsync();
        _ = Journal.SaveAsync();
        Workspace?.OnSave();
        return true;
    }

    /// <summary>Opens a global search result (trip, place, journal entry or country).</summary>
    public async Task OpenSearchResultAsync(SearchResult r)
    {
        if (!_loaded) await LoadAsync();
        switch (r.Kind)
        {
            case SearchResultKind.Trip: OpenTrip(r.Id); break;
            case SearchResultKind.Place: ShowOnMap(r.Id); break;
            case SearchResultKind.Journal:
                Workspace = null;
                Section = TravelSection.Journal;
                await Journal.SelectAsync(r.Id);
                break;
            case SearchResultKind.Country when r.Key is { } code:
                Workspace = null;
                Section = TravelSection.Countries;
                ShowCountry(code);
                if (Countries.Find(code) is { } c) Fly(c.LabelLatitude, c.LabelLongitude, 4);
                break;
        }
    }

    public void OpenProject(int projectId) => _navigation.ShowProject(projectId);
    public void OpenDocument(int documentId) => _navigation.ShowDocument(documentId);
    internal IDocumentStore DocumentStore => _documentStore;
    internal IProjectStore ProjectStore => _projects;
    internal ILoggerFactory Logs => _logs;
}
