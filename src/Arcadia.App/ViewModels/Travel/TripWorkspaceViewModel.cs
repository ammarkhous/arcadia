using System.Collections.ObjectModel;
using System.Globalization;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.App.Views.Travel;
using Arcadia.Core.Documents;
using Arcadia.Core.Travel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Travel;

public enum TripTab { Overview, Itinerary, Map, Places, Notes, Photos, Documents, Memories, Expenses }

public sealed partial class ItineraryItemViewModel(ItineraryItem item, TripDayViewModel day, ITravelContext ctx) : ObservableObject
{
    public ItineraryItem Item { get; } = item;
    public TripDayViewModel Day { get; } = day;
    public int Id => Item.Id;
    public string Title => Item.Title;
    public string TimeText => Item.Slot == TimeSlot.Exact && Item.Time is { } t ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : ctx.Loc["SlotShort_" + Item.Slot];
    public bool IsExact => Item.Slot == TimeSlot.Exact;
    public string Icon => TravelText.CategoryIcon(Item.Category);
    public string CategoryText => ctx.Loc["ItCat_" + Item.Category];
    public string? PlaceName => Item.Place?.Name;
    public string? Description => Item.Description;
    public string? Notes => Item.Notes;
    public string? Url => Item.Url;
    public bool HasDetails => !string.IsNullOrWhiteSpace(Item.Description) || !string.IsNullOrWhiteSpace(Item.Notes);
    [ObservableProperty] private bool _isDone = item.IsDone;
    public override string ToString() => Title;
}

public sealed partial class TripDayViewModel(TripDay day, DateOnly? tripStart, ITravelContext ctx) : ObservableObject
{
    public TripDay Day { get; } = day;
    public int Id => Day.Id;
    public int Index => Day.Index;
    public string NumberText => ctx.Loc.Format("Tr_DayN", Day.Index + 1).ToUpper(ctx.Loc.Culture);
    public string Tone => TravelText.DayTone(Day.Index);
    public DateOnly? Date => TravelRules.DayDate(tripStart, Day.Index);
    public string DateText => Date?.ToString("ddd d MMM", ctx.Loc.Culture) ?? string.Empty;
    public ObservableCollection<ItineraryItemViewModel> Items { get; } = [];
    [ObservableProperty] private string _title = day.Title ?? string.Empty;
    [ObservableProperty] private bool _isEditingTitle;
    public string DoneText => Items.Count == 0 ? string.Empty : $"{Items.Count(i => i.IsDone)}/{Items.Count}";
    public void Refresh() => OnPropertyChanged(nameof(DoneText));
    public override string ToString() => NumberText + (Title.Length > 0 ? " — " + Title : string.Empty);
}

public sealed record TripPlaceRow(PlaceItem Place, int Order)
{
    public string OrderText => (Order + 1).ToString(CultureInfo.InvariantCulture);
    public override string ToString() => Place.Name;
}

/// <summary>
/// A trip opened as a travel dossier: overview, day-by-day itinerary, trip map, places, notes (journal), photos,
/// documents (from the Phase 4 library) and memories.
/// </summary>
public sealed partial class TripWorkspaceViewModel : ObservableObject
{
    private readonly TravelViewModel _page;
    private readonly ILogger _logger;
    private bool _loaded;

    public TripWorkspaceViewModel(TravelViewModel page, int tripId, ILogger logger)
    {
        _page = page;
        _logger = logger;
        TripId = tripId;
        Journal = new TravelJournalViewModel(page, tripId, logger);
        Photos = new TravelPhotoGridViewModel(page, new PhotoQuery(TripId: tripId), new PhotoLink(TripId: tripId), logger);
        StatusOptions = Enum.GetValues<TripStatus>().Select(s => new Option<TripStatus>(s, page.Loc["TrStatus_" + s])).ToList();
    }

    public int TripId { get; }
    public TravelJournalViewModel Journal { get; }
    public TravelPhotoGridViewModel Photos { get; }
    public IReadOnlyList<Option<TripStatus>> StatusOptions { get; }
    public ICountryCatalog Countries => _page.Countries;
    public MapTileImages Tiles => _page.Tiles;
    public string LanguageCode => _page.LanguageCode;

    [ObservableProperty] private TripTab _tab = TripTab.Overview;
    /// <summary>Money recorded for this trip (Finance, Phase 7), created when the tab is first opened.</summary>
    [ObservableProperty] private Finance.ExpensePanelViewModel? _expenses;
    [ObservableProperty] private Trip? _trip;
    [ObservableProperty] private TripCard? _card;
    [ObservableProperty] private Option<TripStatus>? _status;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _datesText = string.Empty;
    [ObservableProperty] private string _durationText = string.Empty;
    [ObservableProperty] private string _whenText = string.Empty;
    [ObservableProperty] private string _whenLabel = string.Empty;
    [ObservableProperty] private string _bigNumber = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _projectName;
    [ObservableProperty] private string _tagsText = string.Empty;
    [ObservableProperty] private string _ratingText = string.Empty;
    public ObservableCollection<TripDayViewModel> Days { get; } = [];
    [ObservableProperty] private IReadOnlyList<TripPlaceRow> _places = [];
    [ObservableProperty] private IReadOnlyList<PlaceItem> _keyPlaces = [];
    [ObservableProperty] private IReadOnlyList<JournalSummary> _recentNotes = [];
    [ObservableProperty] private IReadOnlyList<PhotoItem> _selectedPhotos = [];
    [ObservableProperty] private IReadOnlyList<TripDocumentRow> _documents = [];
    [ObservableProperty] private IReadOnlyList<TripDocumentRow> _importantDocuments = [];
    [ObservableProperty] private IReadOnlyList<MemoryChapterItem> _memories = [];
    [ObservableProperty] private IReadOnlyList<CountryChip> _countryChips = [];
    [ObservableProperty] private string _citiesText = string.Empty;
    public ObservableCollection<MapMarker> Markers { get; } = [];
    public ObservableCollection<MapRoute> Routes { get; } = [];
    [ObservableProperty] private object? _selectedMarker;
    [ObservableProperty] private string? _mapInfoTitle;
    [ObservableProperty] private string? _mapInfoText;
    [ObservableProperty] private string? _mapInfoNotes;
    [ObservableProperty] private bool _isEmptyItinerary;

    public sealed record CountryChip(string Code, string Name, System.Windows.Media.ImageSource? Flag)
{
    public override string ToString() => Name;
}

    /// <summary>The view frames the map when this fires.</summary>
    public event Action<IReadOnlyCollection<(double Lat, double Lon)>>? FitRequested;

    partial void OnTabChanged(TripTab value)
    {
        _ = Journal.SaveAsync();
        switch (value)
        {
            case TripTab.Notes: _ = Journal.LoadAsync(); break;
            case TripTab.Photos: _ = Photos.LoadAsync(); break;
            case TripTab.Memories: _ = LoadMemoriesAsync(); break;
            case TripTab.Map: RequestFit(); break;
            case TripTab.Expenses:
                Expenses ??= Finance.ExpensePanelViewModel.For(Arcadia.Core.Organizer.EntityKind.Trip, TripId, Name);
                if (Expenses != null) { Expenses.TripStart = Trip?.StartDate; Expenses.TripEnd = Trip?.EndDate; _ = Expenses.LoadAsync(); }
                break;
        }
    }

    private bool _settingStatus;
    partial void OnStatusChanged(Option<TripStatus>? value)
    {
        if (_settingStatus || value == null || Trip == null || value.Value == Trip.Status) return;
        _ = _page.SetTripStatusAsync(TripId, value.Value);
    }

    public async Task ActivateAsync()
    {
        if (!_loaded) await ReloadAsync();
        await _page.Store.MarkTripOpenedAsync(TripId);
    }

    public void Deactivate() => _ = Journal.SaveAsync();
    public Task FlushForExit() => Journal.FlushForExit();

    public bool OnNew()
    {
        switch (Tab)
        {
            case TripTab.Notes: _ = Journal.NewAsync(); break;
            case TripTab.Photos: _ = Photos.AddCommand.ExecuteAsync(null); break;
            case TripTab.Places: _ = AddPlaceAsync(); break;
            case TripTab.Documents: _ = AddFilesAsync(); break;
            default: _ = AddDayAsync(); break;
        }
        return true;
    }

    public bool OnSave() { _ = Journal.SaveAsync(); return true; }

    public async Task ReloadAsync()
    {
        try
        {
            var trip = await _page.Store.GetTripAsync(TripId);
            if (trip == null)
            {
                _page.Notifications.Show(_page.Loc["Tr_TripGone"], NotificationKind.Warning);
                _page.Workspace = null;
                return;
            }
            var summary = await _page.Store.GetTripSummaryAsync(TripId);
            var loc = _page.Loc;
            Trip = trip;
            Card = summary != null ? new TripCard(summary, loc, _page.Today, _page.Flags) : null;
            _settingStatus = true;
            Status = StatusOptions.First(o => o.Value == trip.Status);
            _settingStatus = false;
            Name = trip.Name;
            DatesText = TravelText.Range(trip.StartDate, trip.EndDate, loc);
            DurationText = TravelText.Duration(trip.StartDate, trip.EndDate, loc);
            Description = trip.Description;
            ProjectName = trip.Project?.Name;
            TagsText = trip.Tags;
            RatingText = trip.Rating is { } r ? new string('★', r) + new string('☆', 5 - r) : string.Empty;
            BuildWhen(trip);

            var placeItems = _page.AllPlaces.ToDictionary(p => p.Id);
            Places = trip.Places.Select((tp, i) => placeItems.TryGetValue(tp.PlaceId, out var p) ? new TripPlaceRow(p, i) : null).Where(r => r != null).Cast<TripPlaceRow>().ToList();
            KeyPlaces = Places.Select(p => p.Place).OrderByDescending(p => p.Id == trip.PrimaryPlaceId).ThenByDescending(p => p.IsFavorite).Take(6).ToList();
            CountryChips = (summary?.CountryCodes ?? []).Select(c => new CountryChip(c, _page.Countries.Find(c)?.Name(_page.LanguageCode) ?? c, _page.Flags.Get(c))).ToList();
            CitiesText = string.Join(" · ", summary?.Cities ?? []);

            Days.Clear();
            foreach (var d in trip.Days)
            {
                var day = new TripDayViewModel(d, trip.StartDate, _page);
                foreach (var item in d.Items) day.Items.Add(new ItineraryItemViewModel(item, day, _page));
                Days.Add(day);
            }
            IsEmptyItinerary = Days.Count == 0;
            BuildMap();

            var docs = await _page.Store.ListTripDocumentsAsync(TripId);
            Documents = docs;
            ImportantDocuments = docs.Where(d => d.IsImportant).Take(5).ToList();
            RecentNotes = (await _page.Store.ListJournalAsync(new JournalQuery(TripId: TripId))).Take(3).ToList();
            SelectedPhotos = (await _page.Store.ListPhotosAsync(new PhotoQuery(TripId: TripId))).Where(p => !p.IsMissing)
                .OrderByDescending(p => p.IsHighlight).ThenBy(p => p.SortDate).Take(6).Select(p => new PhotoItem(p, loc)).ToList();
            _loaded = true;
            if (Tab == TripTab.Map) RequestFit();
            switch (Tab)
            {
                case TripTab.Notes: await Journal.LoadAsync(); break;
                case TripTab.Photos: await Photos.LoadAsync(); break;
                case TripTab.Memories: await LoadMemoriesAsync(); break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Loading trip {Id} failed", TripId);
            _page.Notifications.Show(_page.Loc["Tr_ErrLoad"], NotificationKind.Warning);
        }
    }

    private void BuildWhen(Trip trip)
    {
        var loc = _page.Loc;
        var today = _page.Today;
        ShowProgress = false;
        Progress = 0;
        var phase = TravelRules.Phase(trip.StartDate, trip.EndDate, today);
        if (trip.Status == TripStatus.Cancelled)
        {
            BigNumber = "—";
            WhenLabel = loc["TrStatus_Cancelled"];
            WhenText = string.Empty;
            return;
        }
        // a finished trip shows its length, not a countdown, even if it was closed before its last date
        if (trip.Status == TripStatus.Completed && phase is TripPhase.Future or TripPhase.Current) phase = TripPhase.Past;
        switch (phase)
        {
            case TripPhase.Future:
                var days = TravelRules.DaysUntil(trip.StartDate, today) ?? 0;
                BigNumber = days.ToString("N0", loc.Culture);
                WhenLabel = days == 1 ? loc["Tr_DayToGo"] : loc["Tr_DaysToGo"];
                WhenText = loc.Format("Tr_Departs", TravelText.Date(trip.StartDate!.Value, loc));
                break;
            case TripPhase.Current when TravelRules.Progress(trip.StartDate, trip.EndDate, today) is { } p:
                BigNumber = p.Day.ToString(CultureInfo.CurrentCulture);
                WhenLabel = loc.Format("Tr_DayOfShort", p.Of);
                WhenText = loc["Tr_Underway"];
                Progress = p.Fraction * 100;
                ShowProgress = true;
                break;
            case TripPhase.Past:
                BigNumber = TravelRules.DurationDays(trip.StartDate, trip.EndDate)?.ToString(CultureInfo.CurrentCulture) ?? "—";
                WhenLabel = loc["Tr_DaysAway"];
                var back = (trip.EndDate ?? trip.StartDate)!.Value;
                WhenText = back <= today ? loc.Format("Tr_Returned", TravelText.Date(back, loc)) : loc["TrStatus_Completed"];
                break;
            default:
                BigNumber = "—";
                WhenLabel = loc["Tr_NoDates"];
                WhenText = loc["Tr_NoDatesHint"];
                break;
        }
    }

    // ───────────────────────── trip map ─────────────────────────

    private void BuildMap()
    {
        Markers.Clear();
        Routes.Clear();
        var onItinerary = new HashSet<int>();
        foreach (var day in Days)
        {
            var color = TravelText.DayColors[day.Index % TravelText.DayColors.Length];
            var points = new List<(double, double)>();
            var n = 0;
            foreach (var item in day.Items)
            {
                if (item.Item.Place is not { Latitude: { } lat, Longitude: { } lon } place) continue;
                n++;
                onItinerary.Add(place.Id);
                points.Add((lat, lon));
                Markers.Add(new MapMarker(("item", item.Id), lat, lon, MapMarkerKind.Itinerary, $"{day.NumberText} · {item.TimeText} · {place.Name}", false, n, color));
            }
            if (points.Count > 1) Routes.Add(new MapRoute(points, color));
        }
        foreach (var row in Places.Where(p => !onItinerary.Contains(p.Place.Id)))
            if (row.Place.ToMarker() is { } m) Markers.Add(m);
    }

    public void RequestFit()
    {
        var pts = Markers.Select(m => (m.Latitude, m.Longitude)).ToList();
        if (pts.Count > 0) FitRequested?.Invoke(pts);
    }

    [RelayCommand]
    private void MarkerClicked(MapMarker? marker)
    {
        var loc = _page.Loc;
        if (marker?.Key is ValueTuple<string, int> k && k.Item1 == "item" && Days.SelectMany(d => d.Items).FirstOrDefault(i => i.Id == k.Item2) is { } item)
        {
            MapInfoTitle = item.PlaceName ?? item.Title;
            MapInfoText = $"{item.Day.NumberText}{(item.Day.DateText.Length > 0 ? " · " + item.Day.DateText : string.Empty)} · {item.TimeText} · {item.Title}";
            MapInfoNotes = string.Join("\n", new[] { item.Description, item.Notes }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        else if (marker?.Key is ValueTuple<string, int> p && p.Item1 == "place" && Places.FirstOrDefault(x => x.Place.Id == p.Item2) is { } row)
        {
            MapInfoTitle = row.Place.Name;
            MapInfoText = $"{row.Place.TypeText} · {row.Place.StatusText} · {row.Place.Where}";
            MapInfoNotes = loc["Tr_NotOnItinerary"];
        }
    }

    // ───────────────────────── itinerary ─────────────────────────

    [RelayCommand]
    private async Task AddDayAsync()
    {
        if (await _page.TryAsync(() => _page.Store.AddDayAsync(TripId, null), "Tr_ErrSave")) { await _page.RefreshAsync(); await ReloadAsync(); Tab = TripTab.Itinerary; }
    }

    [RelayCommand]
    private async Task CreateDaysFromDatesAsync()
    {
        if (Trip is not { StartDate: not null, EndDate: not null })
        {
            _page.Notifications.Show(_page.Loc["Tr_NeedDates"], NotificationKind.Info);
            return;
        }
        var added = 0;
        if (await _page.TryAsync(async () => added = await _page.Store.EnsureDaysForDatesAsync(TripId), "Tr_ErrSave"))
        {
            _page.Notifications.Show(_page.Loc.Format("Tr_DaysAdded", added), NotificationKind.Success);
            await ReloadAsync();
        }
    }

    [RelayCommand] private void EditDayTitle(TripDayViewModel? day) { if (day != null) day.IsEditingTitle = true; }

    [RelayCommand]
    private async Task SaveDayTitleAsync(TripDayViewModel? day)
    {
        if (day == null) return;
        day.IsEditingTitle = false;
        if ((day.Day.Title ?? string.Empty) == day.Title.Trim()) return;
        if (await _page.TryAsync(() => _page.Store.UpdateDayAsync(day.Id, day.Title, day.Day.Notes), "Tr_ErrSave")) day.Day.Title = day.Title.Trim();
    }

    [RelayCommand]
    private async Task DeleteDayAsync(TripDayViewModel? day)
    {
        if (day == null) return;
        if (day.Items.Count > 0 && !_page.Dialogs.Confirm(_page.Loc.Format("Tr_ConfirmDeleteDay", day.NumberText, day.Items.Count), _page.Loc["Proj_Delete"])) return;
        if (await _page.TryAsync(() => _page.Store.DeleteDayAsync(day.Id), "Tr_ErrSave")) await ReloadAsync();
    }

    private IReadOnlyList<PlaceItem> ItineraryPlaces() =>
        Places.Select(p => p.Place).Concat(_page.AllPlaces.Where(p => Places.All(r => r.Place.Id != p.Id))).ToList();

    [RelayCommand]
    private async Task AddItemAsync(TripDayViewModel? day)
    {
        day ??= Days.LastOrDefault();
        if (day == null)
        {
            await AddDayAsync();
            day = Days.LastOrDefault();
            if (day == null) return;
        }
        var editor = new ItineraryItemEditorViewModel(_page.Loc, null, ItineraryPlaces(), day.ToString());
        if (!_page.Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        if (await _page.TryAsync(() => _page.Store.AddItemAsync(day.Id, draft), "Tr_ErrSave")) await AfterItineraryChangeAsync(draft.PlaceId != null);
    }

    [RelayCommand]
    private async Task EditItemAsync(ItineraryItemViewModel? item)
    {
        if (item == null) return;
        var editor = new ItineraryItemEditorViewModel(_page.Loc, item.Item, ItineraryPlaces(), item.Day.ToString());
        if (!_page.Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        if (await _page.TryAsync(() => _page.Store.UpdateItemAsync(item.Id, draft), "Tr_ErrSave")) await AfterItineraryChangeAsync(draft.PlaceId != item.Item.PlaceId);
    }

    private async Task AfterItineraryChangeAsync(bool placesMayHaveChanged)
    {
        if (placesMayHaveChanged) await _page.RefreshAsync();
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task DeleteItemAsync(ItineraryItemViewModel? item)
    {
        if (item == null) return;
        if (await _page.TryAsync(() => _page.Store.DeleteItemAsync(item.Id), "Tr_ErrSave"))
        {
            item.Day.Items.Remove(item);
            item.Day.Refresh();
            BuildMap();
        }
    }

    [RelayCommand]
    private async Task ToggleItemAsync(ItineraryItemViewModel? item)
    {
        if (item == null) return;
        var done = !item.Item.IsDone;
        if (await _page.TryAsync(() => _page.Store.SetItemDoneAsync(item.Id, done), "Tr_ErrSave"))
        {
            item.Item.IsDone = done;
            item.IsDone = done;
            item.Day.Refresh();
        }
        else item.IsDone = item.Item.IsDone;
    }

    [RelayCommand] private Task MoveItemUpAsync(ItineraryItemViewModel? item) => MoveWithinAsync(item, -1);
    [RelayCommand] private Task MoveItemDownAsync(ItineraryItemViewModel? item) => MoveWithinAsync(item, 1);

    private async Task MoveWithinAsync(ItineraryItemViewModel? item, int delta)
    {
        if (item == null) return;
        var index = item.Day.Items.IndexOf(item) + delta;
        if (index >= 0 && index < item.Day.Items.Count) await MoveItemAsync(item, item.Day, index);
        else if (delta < 0 && Days.IndexOf(item.Day) is > 0 and var d) await MoveItemAsync(item, Days[d - 1], Days[d - 1].Items.Count);
        else if (delta > 0 && Days.IndexOf(item.Day) is var d2 && d2 >= 0 && d2 < Days.Count - 1) await MoveItemAsync(item, Days[d2 + 1], 0);
    }

    /// <summary>Moves an item to a position in a day (used by drag and drop and the move buttons).</summary>
    public async Task MoveItemAsync(ItineraryItemViewModel item, TripDayViewModel target, int index)
    {
        if (await _page.TryAsync(() => _page.Store.MoveItemAsync(item.Id, target.Id, index), "Tr_ErrSave")) await ReloadAsync();
    }

    [RelayCommand]
    private async Task SortDayAsync(TripDayViewModel? day)
    {
        if (day != null && await _page.TryAsync(() => _page.Store.SortDayByTimeAsync(day.Id), "Tr_ErrSave")) await ReloadAsync();
    }

    [RelayCommand]
    private void OpenItemLink(ItineraryItemViewModel? item)
    {
        if (item?.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    [RelayCommand] private void OpenItemPlace(ItineraryItemViewModel? item) { if (item?.Item.PlaceId is { } id) _page.ShowOnMap(id); }

    // ───────────────────────── places ─────────────────────────

    [RelayCommand]
    private async Task AddPlaceAsync()
    {
        var candidates = _page.AllPlaces.Where(p => Places.All(r => r.Place.Id != p.Id)).ToList();
        if (candidates.Count == 0)
        {
            await _page.AddPlaceAsync();
            return;
        }
        var picker = TravelPickers.Place(_page, candidates);
        if (!_page.Dialogs.Show(picker) || picker.Selected is not { } choice) return;
        if (await _page.TryAsync(() => _page.Store.AddPlaceToTripAsync(TripId, choice.Id), "Tr_ErrSave")) { await _page.RefreshAsync(); await ReloadAsync(); }
    }

    [RelayCommand]
    private async Task NewPlaceAsync()
    {
        var before = _page.AllPlaces.Select(p => p.Id).ToHashSet();
        await _page.AddPlaceAsync();
        var created = _page.AllPlaces.FirstOrDefault(p => !before.Contains(p.Id));
        if (created != null && await _page.TryAsync(() => _page.Store.AddPlaceToTripAsync(TripId, created.Id), "Tr_ErrSave"))
        {
            _page.Workspace = this;
            await _page.RefreshAsync();
            await ReloadAsync();
            Tab = TripTab.Places;
        }
    }

    [RelayCommand]
    private async Task RemovePlaceAsync(TripPlaceRow? row)
    {
        if (row == null) return;
        if (await _page.TryAsync(() => _page.Store.RemovePlaceFromTripAsync(TripId, row.Place.Id), "Tr_ErrSave")) { await _page.RefreshAsync(); await ReloadAsync(); }
    }

    [RelayCommand] private Task MovePlaceUpAsync(TripPlaceRow? row) => MovePlaceAsync(row, -1);
    [RelayCommand] private Task MovePlaceDownAsync(TripPlaceRow? row) => MovePlaceAsync(row, 1);

    private async Task MovePlaceAsync(TripPlaceRow? row, int delta)
    {
        if (row == null) return;
        var ids = Places.Select(p => p.Place.Id).ToList();
        var i = ids.IndexOf(row.Place.Id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= ids.Count) return;
        (ids[i], ids[j]) = (ids[j], ids[i]);
        if (await _page.TryAsync(() => _page.Store.ReorderTripPlacesAsync(TripId, ids), "Tr_ErrSave")) await ReloadAsync();
    }

    [RelayCommand]
    private async Task VisitPlaceAsync(TripPlaceRow? row)
    {
        if (row == null) return;
        var start = Trip?.StartDate;
        var today = _page.Today;
        var date = start != null && TravelRules.Phase(start, Trip?.EndDate, today) == TripPhase.Current ? today : start;
        var dialog = new VisitEditorViewModel(_page.Loc, row.Place.Name, _page.AllTrips, TripId, date);
        if (!_page.Dialogs.Show(dialog)) return;
        if (await _page.TryAsync(() => _page.Store.AddVisitAsync(row.Place.Id, dialog.Date, dialog.Trip.Value, dialog.Note), "Tr_ErrSave"))
        {
            _page.Notifications.Show(_page.Loc.Format("Tr_MarkedVisited", row.Place.Name), NotificationKind.Success);
            await _page.RefreshAsync();
            await ReloadAsync();
        }
    }

    [RelayCommand] private void OpenPlace(TripPlaceRow? row) { if (row != null) _page.ShowOnMap(row.Place.Id); }
    [RelayCommand] private void OpenPlaceItem(PlaceItem? p) { if (p != null) _page.ShowOnMap(p.Id); }

    // ───────────────────────── documents ─────────────────────────

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var result = await _page.Documents.PickFilesAsync(null);
        if (result is not { DocumentIds.Count: > 0 }) return;
        await AssociateAsync(result.DocumentIds);
    }

    public async Task AddDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        var result = await _page.Documents.AddPathsAsync(paths, null);
        if (result is { DocumentIds.Count: > 0 }) await AssociateAsync(result.DocumentIds);
    }

    private async Task AssociateAsync(IReadOnlyList<int> ids)
    {
        if (await _page.TryAsync(async () => { foreach (var id in ids) await _page.Store.AssociateDocumentAsync(TripId, id); }, "Tr_ErrSave"))
        {
            _page.Notifications.Show(_page.Loc.Format("Tr_DocsAttached", ids.Count, Name), NotificationKind.Success);
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task AddFromLibraryAsync()
    {
        var page = await _page.DocumentStore.QueryAsync(new DocumentQuery(Take: 2000), DateTime.UtcNow);
        var linked = Documents.Select(d => d.Document.Id).ToHashSet();
        var picker = new ItemPickerViewModel(_page.Loc["Tr_PickDocumentTitle"], "Documents", _page.Loc["Tr_Attach"], _page.Loc["Tr_LibraryEmpty"],
            page.Rows.Where(r => !linked.Contains(r.Id)).Select(r => new PickItem(r.Id, r.DisplayName, r.Path, "Documents")).ToList());
        if (!_page.Dialogs.Show(picker) || picker.Selected is not { } choice) return;
        await AssociateAsync([choice.Id]);
    }

    [RelayCommand] private async Task OpenDocumentAsync(TripDocumentRow? d) { if (d != null) await _page.Documents.OpenAsync(d.Document.Id, d.Document.Path); }
    [RelayCommand] private void RevealDocument(TripDocumentRow? d) { if (d != null) _page.Documents.Reveal(d.Document.Path); }
    [RelayCommand] private void ShowInLibrary(TripDocumentRow? d) { if (d != null) _page.OpenDocument(d.Document.Id); }

    [RelayCommand]
    private async Task ToggleImportantAsync(TripDocumentRow? d)
    {
        if (d != null && await _page.TryAsync(() => _page.Store.SetDocumentImportantAsync(TripId, d.Document.Id, !d.IsImportant), "Tr_ErrSave")) await ReloadAsync();
    }

    [RelayCommand]
    private async Task RemoveDocumentAsync(TripDocumentRow? d)
    {
        if (d == null) return;
        if (await _page.TryAsync(() => _page.Store.DissociateDocumentAsync(TripId, d.Document.Id), "Tr_ErrSave"))
        {
            _page.Notifications.Show(_page.Loc.Format("Tr_DocRemoved", d.Document.DisplayName), NotificationKind.Info);
            await ReloadAsync();
        }
    }

    // ───────────────────────── memories, header actions ─────────────────────────

    private async Task LoadMemoriesAsync()
    {
        try { Memories = (await _page.Store.GetMemoriesAsync(TripId)).Select(c => new MemoryChapterItem(c, _page.Loc, _page.Flags)).ToList(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Loading trip memories failed"); }
    }

    [RelayCommand] private void Back() { _ = Journal.SaveAsync(); _page.Workspace = null; }
    [RelayCommand] private Task EditAsync() => Card != null ? _page.EditTripAsync(Card) : Task.CompletedTask;
    [RelayCommand] private Task DeleteAsync() => Card != null ? _page.DeleteTripAsync(Card) : Task.CompletedTask;
    [RelayCommand] private void OpenProject() { if (Trip?.ProjectId is { } id) _page.OpenProject(id); }
    [RelayCommand] private void ShowTab(TripTab tab) => Tab = tab;
    [RelayCommand] private void OpenPhoto(PhotoItem? p) { if (p != null) _page.OpenViewer(SelectedPhotos, p); }
    [RelayCommand] private async Task OpenNoteAsync(JournalSummary? j) { if (j != null) { Tab = TripTab.Notes; await Journal.SelectAsync(j.Id); } }
    [RelayCommand] private Task OpenMemoryAsync(MemoryCard? card) => _page.OpenMemoryCommand.ExecuteAsync(card);

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (Trip is { } t && await _page.TryAsync(() => _page.Store.SetTripFavoriteAsync(TripId, !t.IsFavorite), "Tr_ErrSave")) { await _page.RefreshAsync(); await ReloadAsync(); }
    }
}
