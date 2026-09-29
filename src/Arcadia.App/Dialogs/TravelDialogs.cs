using System.Collections.ObjectModel;
using System.Globalization;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Travel;
using Arcadia.Core.Localization;
using Arcadia.Core.Projects;
using Arcadia.Core.Text;
using Arcadia.Core.Travel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.Dialogs;

/// <summary>Add or edit a place: search (bundled data or OpenStreetMap on request) fills the fields; everything can also be typed.</summary>
public sealed partial class PlaceEditorViewModel : DialogViewModel
{
    private readonly ILocalizer _loc;
    private readonly IGeocodingService _geo;
    private readonly FlagImages _flags;
    private string? _source;

    public PlaceEditorViewModel(ILocalizer loc, IGeocodingService geo, ICountryCatalog countries, FlagImages flags, TravelPlace? existing, GeoPlace? preset = null,
        PlaceStatus status = PlaceStatus.WantToVisit)
    {
        _loc = loc;
        _geo = geo;
        _flags = flags;
        IsNew = existing == null;
        Title = loc[IsNew ? "Tr_AddPlaceTitle" : "Tr_EditPlaceTitle"];
        var lang = loc.Culture.TwoLetterISOLanguageName;
        TypeOptions = Enum.GetValues<PlaceType>().Select(t => new Option<PlaceType>(t, loc["PlaceType_" + t])).ToList();
        StatusOptions = Enum.GetValues<PlaceStatus>().Select(s => new Option<PlaceStatus>(s, loc["PlaceStatus_" + s])).ToList();
        CountryOptions = [new Option<string?>(null, loc["Tr_NoCountry"]), .. countries.All.Select(c => new Option<string?>(c.Code, c.Name(lang))).OrderBy(o => o.Label, StringComparer.Create(loc.Culture, true))];
        RatingOptions = [new Option<int?>(null, loc["Tr_NoRating"]), .. Enumerable.Range(1, 5).Select(i => new Option<int?>(i, new string('★', i)))];

        _name = existing?.Name ?? string.Empty;
        _type = TypeOptions.First(o => o.Value == (existing?.Type ?? PlaceType.City));
        _status = StatusOptions.First(o => o.Value == (existing?.Status ?? status));
        _country = CountryOptions.FirstOrDefault(o => string.Equals(o.Value, existing?.CountryCode, StringComparison.OrdinalIgnoreCase)) ?? CountryOptions[0];
        _region = existing?.Region ?? string.Empty;
        _city = existing?.City ?? string.Empty;
        _coordinates = existing is { Latitude: { } lat, Longitude: { } lon } ? Coords(lat, lon) : string.Empty;
        _description = existing?.Description ?? string.Empty;
        _notes = existing?.Notes ?? string.Empty;
        _rating = RatingOptions.First(o => o.Value == existing?.Rating);
        _isFavorite = existing?.IsFavorite ?? false;
        _tags = existing?.Tags ?? string.Empty;
        _source = existing?.Source;
        if (preset != null) Apply(preset);
    }

    public override string Icon => "MapPin";
    public override double DialogWidth => 640;
    public bool IsNew { get; }
    public string ConfirmText => _loc[IsNew ? "Tr_SavePlace" : "Editor_Save"];
    public IReadOnlyList<Option<PlaceType>> TypeOptions { get; }
    public IReadOnlyList<Option<PlaceStatus>> StatusOptions { get; }
    public IReadOnlyList<Option<string?>> CountryOptions { get; }
    public IReadOnlyList<Option<int?>> RatingOptions { get; }
    public ObservableCollection<GeoResultItem> Results { get; } = [];
    public bool OnlineAvailable => _geo.OnlineEnabled;

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string? _searchStatus;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _name;
    [ObservableProperty] private Option<PlaceType> _type;
    [ObservableProperty] private Option<PlaceStatus> _status;
    [ObservableProperty] private Option<string?> _country;
    [ObservableProperty] private string _region;
    [ObservableProperty] private string _city;
    [ObservableProperty] private string _coordinates;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private Option<int?> _rating;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private string? _nameError;
    [ObservableProperty] private string? _coordinatesError;

    private static string Coords(double lat, double lon) => $"{lat.ToString("0.#####", CultureInfo.InvariantCulture)}, {lon.ToString("0.#####", CultureInfo.InvariantCulture)}";

    partial void OnSearchChanged(string value)
    {
        Results.Clear();
        SearchStatus = null;
        foreach (var p in _geo.SearchOffline(value, _loc.Culture.TwoLetterISOLanguageName, 8)) Results.Add(new GeoResultItem(p, _loc, _flags));
    }

    /// <summary>Asks OpenStreetMap (only on this explicit request).</summary>
    [RelayCommand]
    private async Task SearchOnlineAsync()
    {
        if (string.IsNullOrWhiteSpace(Search) || IsSearching) return;
        IsSearching = true;
        SearchStatus = _loc["Tr_Searching"];
        try
        {
            var result = await _geo.SearchOnlineAsync(Search, _loc.Culture.TwoLetterISOLanguageName);
            Results.Clear();
            foreach (var p in result.Places) Results.Add(new GeoResultItem(p, _loc, _flags));
            SearchStatus = result.Error != GeoSearchError.None ? _loc["GeoError_" + result.Error] : result.Places.Count == 0 ? _loc["Tr_NoResults"] : null;
        }
        finally { IsSearching = false; }
    }

    [RelayCommand]
    private void Pick(GeoResultItem? item)
    {
        if (item == null) return;
        Apply(item.Place);
        Results.Clear();
        SearchStatus = _loc.Format("Tr_Picked", item.Name);
    }

    private void Apply(GeoPlace p)
    {
        Name = p.Name;
        Type = TypeOptions.First(o => o.Value == (p.SuggestedType == PlaceType.Other && p.Origin == GeoOrigin.Map ? Type.Value : p.SuggestedType));
        Coordinates = Coords(p.Latitude, p.Longitude);
        if (p.CountryCode != null) Country = CountryOptions.FirstOrDefault(o => string.Equals(o.Value, p.CountryCode, StringComparison.OrdinalIgnoreCase)) ?? Country;
        if (p.Region != null) Region = p.Region;
        if (p.City != null && !string.Equals(p.City, p.Name, StringComparison.CurrentCultureIgnoreCase)) City = p.City;
        _source = p.SourceRef ?? (p.Origin == GeoOrigin.Map ? "map" : _source);
    }

    public PlaceDraft? Draft { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        NameError = string.IsNullOrWhiteSpace(Name) ? _loc["Tr_ErrName"] : null;
        CoordinatesError = null;
        double? lat = null, lon = null;
        if (!string.IsNullOrWhiteSpace(Coordinates))
        {
            if (GeoMath.TryParse(Coordinates, out var a, out var o)) { lat = a; lon = o; }
            else CoordinatesError = _loc["Tr_ErrCoordinates"];
        }
        if (NameError != null || CoordinatesError != null) return;
        Draft = new PlaceDraft(Name.Trim(), Type.Value, Status.Value, lat, lon, Country.Value, Region, City, Description, Notes, Rating.Value, IsFavorite, Tags,
            _source ?? "manual");
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>Create or edit a trip. Only the name is required; everything else can come later.</summary>
public sealed partial class TripEditorViewModel : DialogViewModel
{
    public static readonly string[] Accents = ["Cyan", "Blue", "Green", "Amber", "Gold", "Purple", "Red", "Steel"];
    public static readonly string[] Worlds = ["mountains-dusk", "lake-sunset", "desert-sunset", "mountains-snow", "forest-night", "golden-realm", "lake-storm", "castle-violet", "neon-city", "forest-ember", "space-nebula", "mountains-storm"];

    private readonly ILocalizer _loc;

    public TripEditorViewModel(ILocalizer loc, Trip? existing, IReadOnlyList<PlaceItem> places, IReadOnlyList<(int Id, string Name)> projects, int? presetPlaceId = null)
    {
        _loc = loc;
        IsNew = existing == null;
        Title = loc[IsNew ? "Tr_NewTripTitle" : "Tr_EditTripTitle"];
        StatusOptions = Enum.GetValues<TripStatus>().Select(s => new Option<TripStatus>(s, loc["TrStatus_" + s])).ToList();
        DestinationOptions = [new Option<int?>(null, loc["Tr_NoDestination"]), .. places.Select(p => new Option<int?>(p.Id, p.Name))];
        ProjectOptions = [new Option<int?>(null, loc["Tr_NoProject"]), .. projects.Select(p => new Option<int?>(p.Id, p.Name))];
        RatingOptions = [new Option<int?>(null, loc["Tr_NoRating"]), .. Enumerable.Range(1, 5).Select(i => new Option<int?>(i, new string('★', i)))];
        AccentOptions = Accents.Select(a => new ChoiceItem(a, loc[$"Accent_{a}"], a)).ToList();
        WorldOptions = Worlds.Select(w => new ChoiceItem(w, w)).ToList();
        InitialPlaces = IsNew ? places.Select(p => new PlaceChoice(p) { IsChecked = p.Id == presetPlaceId }).ToList() : [];

        var t = existing;
        _name = t?.Name ?? string.Empty;
        _status = StatusOptions.First(o => o.Value == (t?.Status ?? TripStatus.Planning));
        _startText = DateText.Format(t?.StartDate, loc.Culture);
        _endText = DateText.Format(t?.EndDate, loc.Culture);
        _description = t?.Description ?? string.Empty;
        _tags = t?.Tags ?? string.Empty;
        _destination = DestinationOptions.FirstOrDefault(o => o.Value == (t?.PrimaryPlaceId ?? presetPlaceId)) ?? DestinationOptions[0];
        _project = ProjectOptions.FirstOrDefault(o => o.Value == t?.ProjectId) ?? ProjectOptions[0];
        _rating = RatingOptions.FirstOrDefault(o => o.Value == t?.Rating) ?? RatingOptions[0];
        _accent = t?.Accent ?? Accents[Random.Shared.Next(Accents.Length)];
        _world = t?.ArtworkTheme ?? Worlds[Random.Shared.Next(Worlds.Length)];
    }

    public override string Icon => "Suitcase";
    public override double DialogWidth => 660;
    public bool IsNew { get; }
    public string ConfirmText => _loc[IsNew ? "Tr_CreateTrip" : "Editor_Save"];
    public string DateHint => _loc.Format("Proj_DateHint", new DateOnly(2027, 4, 1).ToString("d", _loc.Culture));
    public IReadOnlyList<Option<TripStatus>> StatusOptions { get; }
    public IReadOnlyList<Option<int?>> DestinationOptions { get; }
    public IReadOnlyList<Option<int?>> ProjectOptions { get; }
    public IReadOnlyList<Option<int?>> RatingOptions { get; }
    public IReadOnlyList<ChoiceItem> AccentOptions { get; }
    public IReadOnlyList<ChoiceItem> WorldOptions { get; }
    public IReadOnlyList<PlaceChoice> InitialPlaces { get; }
    public bool HasInitialPlaces => InitialPlaces.Count > 0;

    [ObservableProperty] private string _name;
    [ObservableProperty] private Option<TripStatus> _status;
    [ObservableProperty] private string _startText;
    [ObservableProperty] private string _endText;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private Option<int?> _destination;
    [ObservableProperty] private Option<int?> _project;
    [ObservableProperty] private Option<int?> _rating;
    [ObservableProperty] private string _accent;
    [ObservableProperty] private string _world;
    [ObservableProperty] private string? _nameError;
    [ObservableProperty] private string? _dateError;

    [RelayCommand] private void SetAccent(string? key) { if (key != null) Accent = key; }
    [RelayCommand] private void SetWorld(string? key) { if (key != null) World = key; }

    public TripDraft? Draft { get; private set; }
    public IReadOnlyList<int> PlaceIds { get; private set; } = [];

    [RelayCommand]
    private void Confirm()
    {
        NameError = string.IsNullOrWhiteSpace(Name) ? _loc["Tr_ErrTripName"] : null;
        DateError = null;
        var startOk = DateText.TryParse(StartText, _loc.Culture, out var start);
        var endOk = DateText.TryParse(EndText, _loc.Culture, out var end);
        if (!startOk || !endOk) DateError = _loc["Proj_ErrDate"];
        else if (!TravelRules.DatesAreValid(start, end)) DateError = _loc["Tr_ErrDateOrder"];
        if (NameError != null || DateError != null) return;
        Draft = new TripDraft(Name.Trim(), Status.Value, start, end, Description, Tags, Accent, World, Destination.Value, Project.Value, Rating.Value);
        PlaceIds = InitialPlaces.Where(p => p.IsChecked).Select(p => p.Place.Id).ToList();
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}

public sealed partial class PlaceChoice(PlaceItem place) : ObservableObject
{
    public PlaceItem Place { get; } = place;
    [ObservableProperty] private bool _isChecked;
    public override string ToString() => Place.Name;
}

/// <summary>Add or edit one itinerary item: an exact time or just a part of the day.</summary>
public sealed partial class ItineraryItemEditorViewModel : DialogViewModel
{
    private readonly ILocalizer _loc;

    public ItineraryItemEditorViewModel(ILocalizer loc, ItineraryItem? existing, IReadOnlyList<PlaceItem> places, string dayTitle)
    {
        _loc = loc;
        IsNew = existing == null;
        Title = loc[IsNew ? "Tr_AddItemTitle" : "Tr_EditItemTitle"];
        DayTitle = dayTitle;
        SlotOptions = new[] { TimeSlot.AnyTime, TimeSlot.Morning, TimeSlot.Afternoon, TimeSlot.Evening, TimeSlot.Exact }.Select(s => new Option<TimeSlot>(s, loc["Slot_" + s])).ToList();
        CategoryOptions = Enum.GetValues<ItineraryCategory>().Select(c => new Option<ItineraryCategory>(c, loc["ItCat_" + c])).ToList();
        PlaceOptions = [new Option<int?>(null, loc["Tr_NoPlace"]), .. places.Select(p => new Option<int?>(p.Id, p.Name))];
        _itemTitle = existing?.Title ?? string.Empty;
        _slot = SlotOptions.First(o => o.Value == (existing?.Slot ?? TimeSlot.AnyTime));
        _timeText = existing?.Time?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
        _category = CategoryOptions.First(o => o.Value == (existing?.Category ?? ItineraryCategory.Activity));
        _place = PlaceOptions.FirstOrDefault(o => o.Value == existing?.PlaceId) ?? PlaceOptions[0];
        _description = existing?.Description ?? string.Empty;
        _notes = existing?.Notes ?? string.Empty;
        _url = existing?.Url ?? string.Empty;
    }

    public override string Icon => "Timeline";
    public override double DialogWidth => 560;
    public bool IsNew { get; }
    public string DayTitle { get; }
    public string ConfirmText => _loc[IsNew ? "Tr_AddItem" : "Editor_Save"];
    public IReadOnlyList<Option<TimeSlot>> SlotOptions { get; }
    public IReadOnlyList<Option<ItineraryCategory>> CategoryOptions { get; }
    public IReadOnlyList<Option<int?>> PlaceOptions { get; }

    [ObservableProperty] private string _itemTitle;
    [ObservableProperty] private Option<TimeSlot> _slot;
    [ObservableProperty] private string _timeText;
    [ObservableProperty] private Option<ItineraryCategory> _category;
    [ObservableProperty] private Option<int?> _place;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string? _titleError;
    [ObservableProperty] private string? _timeError;
    [ObservableProperty] private string? _urlError;

    public bool IsExact => Slot.Value == TimeSlot.Exact;
    partial void OnSlotChanged(Option<TimeSlot> value) => OnPropertyChanged(nameof(IsExact));

    /// <summary>Typing a time switches to "exact time".</summary>
    partial void OnTimeTextChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Slot.Value != TimeSlot.Exact) Slot = SlotOptions.First(o => o.Value == TimeSlot.Exact);
    }

    public ItineraryItemDraft? Draft { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        TitleError = string.IsNullOrWhiteSpace(ItemTitle) ? _loc["Tr_ErrItemTitle"] : null;
        TimeError = null;
        UrlError = null;
        TimeOnly? time = null;
        if (Slot.Value == TimeSlot.Exact)
        {
            if (TimeOnly.TryParseExact(TimeText.Trim().Replace('.', ':'), ["H:mm", "HH:mm", "H", "HHmm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) time = t;
            else TimeError = _loc["Tr_ErrTime"];
        }
        string? url = null;
        if (!string.IsNullOrWhiteSpace(Url))
        {
            if (LinkRules.TryNormalize(Url, out var normal)) url = normal;
            else UrlError = _loc["Link_ErrUrl"];
        }
        if (TitleError != null || TimeError != null || UrlError != null) return;
        Draft = new ItineraryItemDraft(ItemTitle.Trim(), Slot.Value, time, Category.Value, Place.Value, Description, Notes, url);
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>"Mark visited": when, on which trip, and a note.</summary>
public sealed partial class VisitEditorViewModel : DialogViewModel
{
    private readonly ILocalizer _loc;

    public VisitEditorViewModel(ILocalizer loc, string placeName, IReadOnlyList<TripCard> trips, int? presetTripId, DateOnly? presetDate)
    {
        _loc = loc;
        Title = loc.Format("Tr_VisitTitle", placeName);
        TripOptions = [new Option<int?>(null, loc["Tr_NoTrip"]), .. trips.Select(t => new Option<int?>(t.Id, t.Name))];
        _trip = TripOptions.FirstOrDefault(o => o.Value == presetTripId) ?? TripOptions[0];
        _dateText = global::Arcadia.App.Dialogs.DateText.Format(presetDate ?? DateOnly.FromDateTime(DateTime.Today), loc.Culture);
    }

    public override string Icon => "Check";
    public override double DialogWidth => 460;
    public IReadOnlyList<Option<int?>> TripOptions { get; }
    [ObservableProperty] private string _dateText;
    [ObservableProperty] private Option<int?> _trip;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string? _dateError;

    public DateOnly? Date { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        if (!global::Arcadia.App.Dialogs.DateText.TryParse(DateText, _loc.Culture, out var d)) { DateError = _loc["Proj_ErrDate"]; return; }
        Date = d;
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>Add or edit a bucket list entry (a dream: an experience, a place or a country).</summary>
public sealed partial class BucketEditorViewModel : DialogViewModel
{
    private readonly ILocalizer _loc;

    public BucketEditorViewModel(ILocalizer loc, BucketRow? existing, IReadOnlyList<PlaceItem> places, IReadOnlyList<TripCard> trips, ICountryCatalog countries)
    {
        _loc = loc;
        IsNew = existing == null;
        Title = loc[IsNew ? "Tr_AddBucketTitle" : "Tr_EditBucketTitle"];
        var lang = loc.Culture.TwoLetterISOLanguageName;
        KindOptions = Enum.GetValues<BucketKind>().Select(k => new Option<BucketKind>(k, loc["BucketKind_" + k])).ToList();
        PriorityOptions = Enum.GetValues<BucketPriority>().Select(p => new Option<BucketPriority>(p, loc["BucketPriority_" + p])).ToList();
        CountryOptions = [new Option<string?>(null, loc["Tr_NoCountry"]), .. countries.All.Select(c => new Option<string?>(c.Code, c.Name(lang))).OrderBy(o => o.Label, StringComparer.Create(loc.Culture, true))];
        PlaceOptions = [new Option<int?>(null, loc["Tr_NoPlace"]), .. places.Select(p => new Option<int?>(p.Id, p.Name))];
        TripOptions = [new Option<int?>(null, loc["Tr_NoTrip"]), .. trips.Select(t => new Option<int?>(t.Id, t.Name))];
        _itemTitle = existing?.Title ?? string.Empty;
        _kind = KindOptions.First(o => o.Value == (existing?.Kind ?? BucketKind.Experience));
        _priority = PriorityOptions.First(o => o.Value == (existing?.Priority ?? BucketPriority.Normal));
        _targetYear = existing?.TargetYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _country = CountryOptions.FirstOrDefault(o => string.Equals(o.Value, existing?.CountryCode, StringComparison.OrdinalIgnoreCase)) ?? CountryOptions[0];
        _place = PlaceOptions.FirstOrDefault(o => o.Value == existing?.PlaceId) ?? PlaceOptions[0];
        _trip = TripOptions.FirstOrDefault(o => o.Value == existing?.TripId) ?? TripOptions[0];
        _notes = existing?.Notes ?? string.Empty;
        _tags = existing?.Tags ?? string.Empty;
    }

    public override string Icon => "Flag";
    public override double DialogWidth => 560;
    public bool IsNew { get; }
    public string ConfirmText => _loc[IsNew ? "Tr_AddBucket" : "Editor_Save"];
    public IReadOnlyList<Option<BucketKind>> KindOptions { get; }
    public IReadOnlyList<Option<BucketPriority>> PriorityOptions { get; }
    public IReadOnlyList<Option<string?>> CountryOptions { get; }
    public IReadOnlyList<Option<int?>> PlaceOptions { get; }
    public IReadOnlyList<Option<int?>> TripOptions { get; }

    [ObservableProperty] private string _itemTitle;
    [ObservableProperty] private Option<BucketKind> _kind;
    [ObservableProperty] private Option<BucketPriority> _priority;
    [ObservableProperty] private string _targetYear;
    [ObservableProperty] private Option<string?> _country;
    [ObservableProperty] private Option<int?> _place;
    [ObservableProperty] private Option<int?> _trip;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private string? _titleError;
    [ObservableProperty] private string? _yearError;

    public BucketDraft? Draft { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        TitleError = string.IsNullOrWhiteSpace(ItemTitle) ? _loc["Tr_ErrBucketTitle"] : null;
        YearError = null;
        int? year = null;
        if (!string.IsNullOrWhiteSpace(TargetYear))
        {
            if (int.TryParse(TargetYear.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && y is >= 1900 and <= 2200) year = y;
            else YearError = _loc["Tr_ErrYear"];
        }
        if (TitleError != null || YearError != null) return;
        Draft = new BucketDraft(ItemTitle.Trim(), Kind.Value, Priority.Value, year, Country.Value, Place.Value, Trip.Value, Notes, Tags);
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>An item offered by <see cref="ItemPickerViewModel"/>.</summary>
public sealed record PickItem(int Id, string Title, string? Detail, string Icon)
{
    public override string ToString() => Title;
}

/// <summary>Choose one thing from a searchable list (a trip, a place, a library document…).</summary>
public sealed partial class ItemPickerViewModel : DialogViewModel
{
    private readonly IReadOnlyList<PickItem> _all;
    private readonly string _icon;

    public ItemPickerViewModel(string title, string icon, string confirmText, string emptyText, IReadOnlyList<PickItem> items)
    {
        Title = title;
        _icon = icon;
        ConfirmText = confirmText;
        EmptyText = emptyText;
        _all = items;
        Items = new ObservableCollection<PickItem>(items);
    }

    public override string Icon => _icon;
    public override double DialogWidth => 520;
    public string ConfirmText { get; }
    public string EmptyText { get; }
    public ObservableCollection<PickItem> Items { get; }
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private PickItem? _selected;

    partial void OnFilterChanged(string value)
    {
        var f = TextFold.Fold(value);
        Items.Clear();
        foreach (var i in _all.Where(i => f.Length == 0 || TextFold.Fold(i.Title).Contains(f, StringComparison.Ordinal) || TextFold.Fold(i.Detail).Contains(f, StringComparison.Ordinal)))
            Items.Add(i);
    }

    [RelayCommand]
    private void Confirm()
    {
        if (Selected != null) Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}
