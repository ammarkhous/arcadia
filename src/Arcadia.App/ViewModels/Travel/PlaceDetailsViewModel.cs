using System.Windows.Media;
using System.Windows.Threading;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.Travel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Travel;

public sealed record VisitRow(int Id, string DateText, string? TripName, string? Note)
{
    public override string ToString() => DateText;
}

public sealed record NearbyPlace(int Id, string Name, string Icon, string DistanceText)
{
    public override string ToString() => Name;
}

/// <summary>
/// Everything about one saved place: position, status, visits, trips, photos, notes (saved as you type) and nearby
/// saved places. Actions: edit, add to trip, mark visited, favourite, show on map, add photo, add journal entry.
/// </summary>
public sealed partial class PlaceDetailsViewModel : ObservableObject
{
    private readonly TravelViewModel _page;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _autosave;
    private bool _loading;
    private bool _dirty;

    public PlaceDetailsViewModel(TravelViewModel page, ILogger logger)
    {
        _page = page;
        _logger = logger;
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _autosave.Tick += async (_, _) => { _autosave.Stop(); await SaveNotesAsync(); };
        Photos = new TravelPhotoGridViewModel(page, new PhotoQuery(PlaceId: -1), new PhotoLink(), logger);
    }

    public TravelPhotoGridViewModel Photos { get; }
    [ObservableProperty] private TravelPlace? _place;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _where = string.Empty;
    [ObservableProperty] private string _typeText = string.Empty;
    [ObservableProperty] private string _icon = "MapPin";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _statusTone = "Amber";
    [ObservableProperty] private string _coordinates = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _ratingText = string.Empty;
    [ObservableProperty] private string _tags = string.Empty;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isVisited;
    [ObservableProperty] private ImageSource? _flag;
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private string _visitsSummary = string.Empty;
    [ObservableProperty] private IReadOnlyList<VisitRow> _visits = [];
    [ObservableProperty] private IReadOnlyList<TripLink> _trips = [];
    [ObservableProperty] private IReadOnlyList<NearbyPlace> _nearby = [];
    [ObservableProperty] private string _notesState = string.Empty;

    public sealed record TripLink(int Id, string Name, string DatesText)
{
    public override string ToString() => Name;
}

    partial void OnNotesChanged(string value)
    {
        if (_loading || Place == null) return;
        _dirty = true;
        NotesState = _page.Loc["Note_Unsaved"];
        _autosave.Stop();
        _autosave.Start();
    }

    public async Task LoadAsync(int id)
    {
        await SaveNotesAsync();
        try
        {
            var p = await _page.Store.GetPlaceAsync(id);
            if (p == null) { Place = null; return; }
            var loc = _page.Loc;
            var summary = _page.AllPlaces.FirstOrDefault(x => x.Id == id);
            _loading = true;
            Place = p;
            Name = p.Name;
            Icon = TravelText.PlaceIcon(p.Type);
            TypeText = loc["PlaceType_" + p.Type];
            StatusText = loc["PlaceStatus_" + p.Status];
            StatusTone = TravelText.StatusTone(p.Status);
            Coordinates = p is { Latitude: { } la, Longitude: { } lo } ? GeoMath.Format(la, lo, 5) : loc["Tr_NoPosition"];
            Where = summary?.Where ?? string.Empty;
            Description = p.Description;
            Notes = p.Notes ?? string.Empty;
            RatingText = p.Rating is { } r ? new string('★', r) + new string('☆', 5 - r) : string.Empty;
            Tags = p.Tags;
            IsFavorite = p.IsFavorite;
            IsVisited = p.Status == PlaceStatus.Visited;
            Flag = _page.Flags.Get(p.CountryCode);
            CoverPath = summary?.CoverPath;
            var (count, first, last) = TravelRules.Summarize(p.Visits);
            VisitsSummary = count == 0 ? loc["Tr_NotVisited"] : first is { } f && last is { } l && f != l
                ? loc.Format("Tr_VisitSpan", count, TravelText.Date(f, loc), TravelText.Date(l, loc))
                : TravelText.Visits(count, loc) + (last is { } d ? " · " + TravelText.Date(d, loc) : string.Empty);
            var tripNames = _page.AllTrips.ToDictionary(t => t.Id, t => t.Name);
            Visits = p.Visits.Select(v => new VisitRow(v.Id, v.VisitedOn is { } vd ? TravelText.Date(vd, loc) : loc["Tr_DateUnknown"],
                v.TripId is { } t ? tripNames.GetValueOrDefault(t) : null, v.Note)).ToList();
            var trips = await _page.Store.GetPlaceTripsAsync(id);
            Trips = trips.Select(t => new TripLink(t.TripId, t.Name, _page.AllTrips.FirstOrDefault(x => x.Id == t.TripId)?.DatesText ?? string.Empty)).ToList();
            Nearby = p is { Latitude: { } lat, Longitude: { } lon }
                ? _page.AllPlaces.Where(x => x.Id != id && x.Summary.Latitude != null)
                    .Select(x => (x, Km: GeoMath.DistanceKm(lat, lon, x.Summary.Latitude!.Value, x.Summary.Longitude!.Value)))
                    .Where(x => x.Km <= 60).OrderBy(x => x.Km).Take(5)
                    .Select(x => new NearbyPlace(x.x.Id, x.x.Name, x.x.Icon, x.Km < 1 ? loc.Format("Tr_Meters", (int)(x.Km * 1000)) : loc.Format("Tr_Km", Math.Round(x.Km, x.Km < 10 ? 1 : 0))))
                    .ToList()
                : [];
            _loading = false;
            _dirty = false;
            NotesState = string.Empty;
            Photos.Query = new PhotoQuery(PlaceId: id);
            Photos.Link = new PhotoLink(PlaceId: id);
            await Photos.LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _loading = false;
            _logger.LogError(ex, "Loading place {Id} failed", id);
            _page.Notifications.Show(_page.Loc["Tr_ErrLoad"], NotificationKind.Warning);
        }
    }

    public async Task SaveNotesAsync()
    {
        _autosave.Stop();
        if (!_dirty || Place is not { } p) return;
        _dirty = false;
        var text = Notes;
        NotesState = _page.Loc["Note_Saving"];
        try
        {
            await _page.Store.SetPlaceNotesAsync(p.Id, text);
            if (!_dirty) NotesState = _page.Loc["Note_Saved"];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _dirty = true;
            _logger.LogError(ex, "Saving place notes failed");
            NotesState = _page.Loc["Note_SaveFailed"];
        }
    }

    public Task FlushForExit()
    {
        _autosave.Stop();
        if (!_dirty || Place is not { } p) return Task.CompletedTask;
        _dirty = false;
        return _page.Store.SetPlaceNotesAsync(p.Id, Notes);
    }

    [RelayCommand] private Task EditAsync() => Place is { } p ? _page.EditPlaceAsync(p.Id) : Task.CompletedTask;
    [RelayCommand] private Task AddToTripAsync() => Place is { } p ? _page.AddPlaceToTripAsync(p.Id) : Task.CompletedTask;
    [RelayCommand] private Task MarkVisitedAsync() => Place is { } p ? _page.MarkVisitedAsync(p.Id) : Task.CompletedTask;
    [RelayCommand] private Task ToggleFavoriteAsync() => Place is { } p ? _page.SetFavoriteAsync(p.Id, !p.IsFavorite) : Task.CompletedTask;
    [RelayCommand] private Task SetWantedAsync() => Place is { } p ? _page.SetStatusAsync(p.Id, PlaceStatus.WantToVisit) : Task.CompletedTask;
    [RelayCommand] private Task SetPlannedAsync() => Place is { } p ? _page.SetStatusAsync(p.Id, PlaceStatus.Planned) : Task.CompletedTask;
    [RelayCommand] private void OpenOnMap() { if (Place is { } p) _page.ShowOnMap(p.Id); }
    [RelayCommand] private Task AddPhotoAsync() => Photos.AddCommand.ExecuteAsync(null);
    [RelayCommand] private Task AddNoteAsync() => Place is { } p ? _page.NewJournalForAsync(null, p.Id) : Task.CompletedTask;
    [RelayCommand] private Task DeleteAsync() => Place is { } p ? _page.DeletePlaceAsync(p.Id) : Task.CompletedTask;
    [RelayCommand] private Task NewTripHereAsync() => Place is { } p ? _page.NewTripAsync(p.Id) : Task.CompletedTask;
    [RelayCommand] private void OpenTrip(TripLink? t) { if (t != null) _page.OpenTrip(t.Id); }
    [RelayCommand] private void OpenNearby(NearbyPlace? n) { if (n != null) _page.OpenPlace(n.Id); }

    [RelayCommand]
    private async Task DeleteVisitAsync(VisitRow? v)
    {
        if (v == null || Place is not { } p) return;
        if (await _page.TryAsync(() => _page.Store.DeleteVisitAsync(v.Id), "Tr_ErrSave")) { await LoadAsync(p.Id); await _page.RefreshAsync(); }
    }
}
