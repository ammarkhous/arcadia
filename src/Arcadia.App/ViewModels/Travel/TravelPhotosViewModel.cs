using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.Localization;
using Arcadia.Core.Travel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Travel;

/// <summary>What photo grids, the viewer and the workspace need from the Travel page.</summary>
public interface ITravelContext
{
    ILocalizer Loc { get; }
    ITravelStore Store { get; }
    IDialogService Dialogs { get; }
    INotificationService Notifications { get; }
    TravelPhotoService Photos { get; }
    DocumentActions Documents { get; }
    FlagImages Flags { get; }
    ICountryCatalog Countries { get; }
    IReadOnlyList<PlaceItem> AllPlaces { get; }
    IReadOnlyList<TripCard> AllTrips { get; }
    DateOnly Today { get; }
    void OpenViewer(IReadOnlyList<PhotoItem> photos, PhotoItem current);
    void OpenPlace(int placeId);
    void OpenTrip(int tripId);
    /// <summary>Reloads shared lists (places, trips, stats, map) after a change.</summary>
    Task RefreshAsync();
    /// <summary>Runs an action and reports failures as a friendly notification instead of crashing.</summary>
    Task<bool> TryAsync(Func<Task> action, string failureKey);
}

public sealed class PhotoItem(PhotoRow row, ILocalizer loc)
{
    public PhotoRow Row { get; } = row;
    public int Id => Row.Id;
    public string Path => Row.Path;
    public string FileName => Row.FileName;
    public int Orientation => Row.Orientation;
    public bool IsMissing => Row.IsMissing;
    public bool IsHighlight => Row.IsHighlight;
    public bool HasGps => Row.Latitude != null;
    public string? Caption => Row.Caption;
    public string DateText => Row.TakenAt?.ToString("d MMM yyyy · HH:mm", loc.Culture) ?? Row.SortDate.ToString("d MMM yyyy", loc.Culture);
    public string Title => string.IsNullOrWhiteSpace(Row.Caption) ? System.IO.Path.GetFileNameWithoutExtension(Row.FileName) : Row.Caption!;
    public override string ToString() => Title;
}

/// <summary>A row of the virtualised photo grid (a fixed number of tiles that fits the available width).</summary>
public sealed record PhotoRowGroup(IReadOnlyList<PhotoItem> Items);

/// <summary>A grid of travel photos for a trip, a place, a journal entry or everything; virtualised by rows.</summary>
public sealed partial class TravelPhotoGridViewModel : ObservableObject
{
    public const double TileWidth = 176;
    private readonly ITravelContext _ctx;
    private readonly ILogger _logger;
    private double _width = 900;

    public TravelPhotoGridViewModel(ITravelContext ctx, PhotoQuery query, PhotoLink link, ILogger logger)
    {
        _ctx = ctx;
        Query = query;
        Link = link;
        _logger = logger;
    }

    public PhotoQuery Query { get; set; }
    public PhotoLink Link { get; set; }
    public IReadOnlyList<PhotoItem> Photos { get; private set; } = [];
    public ObservableCollection<PhotoRowGroup> Rows { get; } = [];
    [ObservableProperty] private int _count;
    [ObservableProperty] private string? _gpsSuggestion;
    [ObservableProperty] private bool _isBusy;

    public async Task LoadAsync()
    {
        try
        {
            var rows = await _ctx.Store.ListPhotosAsync(Query);
            Photos = rows.Select(r => new PhotoItem(r, _ctx.Loc)).ToList();
            Count = Photos.Count;
            var unplaced = Photos.Count(p => p.HasGps && p.Row.PlaceId == null);
            GpsSuggestion = unplaced > 0 && Query.PlaceId == null && _ctx.AllPlaces.Any(p => p.HasPosition) ? (unplaced == 1 ? _ctx.Loc["Tr_GpsSuggestionOne"] : _ctx.Loc.Format("Tr_GpsSuggestion", unplaced)) : null;
            Relayout();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not load photos");
            _ctx.Notifications.Show(_ctx.Loc["Tr_ErrLoad"], NotificationKind.Warning);
        }
    }

    /// <summary>Called by the view when its width changes.</summary>
    public void SetWidth(double width)
    {
        if (Math.Abs(width - _width) < 8 || width < 50) return;
        _width = width;
        Relayout();
    }

    private void Relayout()
    {
        var columns = Math.Max(1, (int)(_width / TileWidth));
        Rows.Clear();
        for (int i = 0; i < Photos.Count; i += columns) Rows.Add(new PhotoRowGroup(Photos.Skip(i).Take(columns).ToList()));
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var paths = _ctx.Dialogs.PickFiles(_ctx.Loc["Tr_PhotoFilter"] + "|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp;*.tif;*.tiff;*.heic", multiple: true,
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        if (paths.Count > 0) await AddPathsAsync(paths);
    }

    public async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        IsBusy = true;
        try
        {
            var result = await _ctx.Photos.AddAsync(paths, Link);
            if (result.Added == 0) { _ctx.Notifications.Show(_ctx.Loc["Tr_NoPhotosAdded"], NotificationKind.Warning); return; }
            _ctx.Notifications.Show(_ctx.Loc.Format("Tr_PhotosAdded", result.Added) + (result.WithGps > 0 ? " " + _ctx.Loc.Format("Tr_PhotosWithGps", result.WithGps) : string.Empty),
                NotificationKind.Success);
            await LoadAsync();
            await _ctx.RefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Adding photos failed");
            _ctx.Notifications.Show(_ctx.Loc["Tr_ErrPhotos"], NotificationKind.Warning);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Links photos with GPS positions to the nearest saved place (within 2 km), locally.</summary>
    [RelayCommand]
    private async Task MatchGpsAsync()
    {
        var linked = await _ctx.Photos.MatchToPlacesAsync(Photos.Select(p => p.Row).ToList(), _ctx.AllPlaces.Select(p => p.Summary).ToList());
        _ctx.Notifications.Show(linked > 0 ? _ctx.Loc.Format("Tr_GpsLinked", linked) : _ctx.Loc["Tr_GpsNoneNear"], linked > 0 ? NotificationKind.Success : NotificationKind.Info);
        await LoadAsync();
    }

    [RelayCommand] private void Open(PhotoItem? p) { if (p != null) _ctx.OpenViewer(Photos, p); }
    [RelayCommand] private void Reveal(PhotoItem? p) { if (p != null) _ctx.Documents.Reveal(p.Path); }

    [RelayCommand]
    private async Task SetTripCoverAsync(PhotoItem? p)
    {
        if (p?.Row.TripId is not { } trip) return;
        if (await _ctx.TryAsync(() => _ctx.Store.SetTripCoverAsync(trip, p.Id), "Tr_ErrSave"))
        {
            _ctx.Notifications.Show(_ctx.Loc["Tr_CoverSet"], NotificationKind.Success);
            await _ctx.RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task SetPlaceCoverAsync(PhotoItem? p)
    {
        if (p?.Row.PlaceId is not { } place) return;
        if (await _ctx.TryAsync(() => _ctx.Store.SetPlaceCoverAsync(place, p.Id), "Tr_ErrSave"))
        {
            _ctx.Notifications.Show(_ctx.Loc["Tr_CoverSet"], NotificationKind.Success);
            await _ctx.RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleHighlightAsync(PhotoItem? p)
    {
        if (p == null) return;
        if (await _ctx.TryAsync(() => _ctx.Store.UpdatePhotoAsync(p.Id, p.Caption, !p.IsHighlight), "Tr_ErrSave")) await LoadAsync();
    }

    [RelayCommand]
    private async Task AssociatePlaceAsync(PhotoItem? p)
    {
        if (p == null) return;
        var picker = TravelPickers.Place(_ctx);
        if (!_ctx.Dialogs.Show(picker) || picker.Selected is not { } choice) return;
        if (await _ctx.TryAsync(() => _ctx.Store.SetPhotosPlaceAsync([p.Id], choice.Id), "Tr_ErrSave")) await LoadAsync();
    }

    [RelayCommand]
    private async Task AssociateTripAsync(PhotoItem? p)
    {
        if (p == null) return;
        var picker = TravelPickers.Trip(_ctx);
        if (!_ctx.Dialogs.Show(picker) || picker.Selected is not { } choice) return;
        if (await _ctx.TryAsync(() => _ctx.Store.SetPhotosTripAsync([p.Id], choice.Id), "Tr_ErrSave")) { await LoadAsync(); await _ctx.RefreshAsync(); }
    }

    [RelayCommand]
    private async Task RemoveAsync(PhotoItem? p)
    {
        if (p == null || !_ctx.Dialogs.Confirm(_ctx.Loc.Format("Tr_ConfirmRemovePhoto", p.FileName), _ctx.Loc["Doc_RemoveFromArcadia"])) return;
        if (await _ctx.TryAsync(() => _ctx.Store.RemovePhotosAsync([p.Id]), "Tr_ErrSave")) { await LoadAsync(); await _ctx.RefreshAsync(); }
    }
}

/// <summary>Pickers shared by the Travel screens.</summary>
public static class TravelPickers
{
    public static ItemPickerViewModel Place(ITravelContext ctx, IEnumerable<PlaceItem>? only = null) =>
        new(ctx.Loc["Tr_PickPlaceTitle"], "MapPin", ctx.Loc["Tr_Choose"], ctx.Loc["Tr_NoPlacesYet"],
            (only ?? ctx.AllPlaces).Select(p => new PickItem(p.Id, p.Name, p.Where, p.Icon)).ToList());

    public static ItemPickerViewModel Trip(ITravelContext ctx) =>
        new(ctx.Loc["Tr_PickTripTitle"], "Suitcase", ctx.Loc["Tr_Choose"], ctx.Loc["Tr_NoTripsYet"],
            ctx.AllTrips.Where(t => t.Summary.Status != TripStatus.Cancelled).Select(t => new PickItem(t.Id, t.Name, t.DatesText, "Suitcase")).ToList());
}

/// <summary>Full-window photo viewer: previous/next, fit or actual size, metadata and actions.</summary>
public sealed partial class PhotoViewerViewModel : ObservableObject
{
    private readonly ITravelContext _ctx;
    private readonly Action _close;
    private CancellationTokenSource? _cts;

    public PhotoViewerViewModel(ITravelContext ctx, IReadOnlyList<PhotoItem> photos, PhotoItem current, Action close)
    {
        _ctx = ctx;
        _close = close;
        Photos = photos;
        _index = Math.Max(0, photos.ToList().FindIndex(p => p.Id == current.Id));
        _ = ShowAsync();
    }

    public IReadOnlyList<PhotoItem> Photos { get; }
    [ObservableProperty] private int _index;
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBroken;
    [ObservableProperty] private bool _actualSize;
    [ObservableProperty] private double _scale = 1;
    [ObservableProperty] private string _caption = string.Empty;
    [ObservableProperty] private IReadOnlyList<InfoLine> _details = [];

    public PhotoItem? Current => Index >= 0 && Index < Photos.Count ? Photos[Index] : null;
    public string PositionText => $"{Index + 1} / {Photos.Count}";
    public bool CanSetTripCover => Current?.Row.TripId != null;
    public bool CanSetPlaceCover => Current?.Row.PlaceId != null;

    partial void OnIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(CanSetTripCover));
        OnPropertyChanged(nameof(CanSetPlaceCover));
        _ = ShowAsync();
    }

    partial void OnActualSizeChanged(bool value) { Scale = 1; _ = ShowAsync(); }

    private async Task ShowAsync()
    {
        if (Current is not { } p) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Caption = p.Caption ?? string.Empty;
        Details = BuildDetails(p);
        IsLoading = true;
        IsBroken = false;
        try
        {
            var full = ActualSize;
            var image = await Task.Run(() =>
            {
                var bmp = new BitmapImage();
                using (var stream = new FileStream(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    // fit mode never decodes more than a large screen needs
                    if (!full) { if (p.Orientation is 5 or 6 or 7 or 8) bmp.DecodePixelHeight = 2400; else bmp.DecodePixelWidth = 2400; }
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return p.Orientation > 1 ? ThumbnailService.Orient(bmp, p.Orientation) : (BitmapSource)bmp;
            }, cts.Token);
            if (!cts.IsCancellationRequested) Image = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            if (!cts.IsCancellationRequested) { Image = null; IsBroken = true; }
        }
        finally { if (!cts.IsCancellationRequested) IsLoading = false; }
    }

    private IReadOnlyList<InfoLine> BuildDetails(PhotoItem p)
    {
        var loc = _ctx.Loc;
        var r = p.Row;
        var list = new List<InfoLine> { new(loc["Tr_PhotoTaken"], r.TakenAt?.ToString("f", loc.Culture) ?? loc["Tr_Unknown"]) };
        if (r.Camera != null) list.Add(new InfoLine(loc["Tr_PhotoCamera"], r.Camera));
        if (r.PixelWidth is { } w && r.PixelHeight is { } h) list.Add(new InfoLine(loc["Tr_PhotoSize"], $"{w} × {h}"));
        list.Add(new InfoLine(loc["Tr_PhotoLocation"], r.Latitude is { } la && r.Longitude is { } lo ? GeoMath.Format(la, lo, 5) : loc["Tr_NoGps"]));
        list.Add(new InfoLine(loc["Tr_PhotoPlace"], r.PlaceName ?? "—"));
        list.Add(new InfoLine(loc["Tr_PhotoTrip"], r.TripName ?? "—"));
        list.Add(new InfoLine(loc["Doc_Location"], r.Path));
        return list;
    }

    [RelayCommand] private void Next() { if (Index < Photos.Count - 1) Index++; }
    [RelayCommand] private void Previous() { if (Index > 0) Index--; }
    [RelayCommand] private void Close() { _cts?.Cancel(); _close(); }
    [RelayCommand] private void Fit() { ActualSize = false; Scale = 1; }
    [RelayCommand] private void Actual() => ActualSize = true;
    [RelayCommand] private void ZoomIn() => Scale = Math.Min(8, Scale * 1.25);
    [RelayCommand] private void ZoomOut() => Scale = Math.Max(0.1, Scale / 1.25);
    [RelayCommand] private void Reveal() { if (Current is { } p) _ctx.Documents.Reveal(p.Path); }
    [RelayCommand] private async Task OpenExternalAsync() { if (Current is { } p) await _ctx.Documents.OpenAsync(p.Row.DocumentId, p.Path); }

    [RelayCommand]
    private async Task SaveCaptionAsync()
    {
        if (Current is not { } p || Caption == (p.Caption ?? string.Empty)) return;
        await _ctx.TryAsync(() => _ctx.Store.UpdatePhotoAsync(p.Id, Caption, p.IsHighlight), "Tr_ErrSave");
    }

    [RelayCommand]
    private async Task SetTripCoverAsync()
    {
        if (Current?.Row.TripId is { } t && await _ctx.TryAsync(() => _ctx.Store.SetTripCoverAsync(t, Current.Id), "Tr_ErrSave"))
        {
            _ctx.Notifications.Show(_ctx.Loc["Tr_CoverSet"], NotificationKind.Success);
            await _ctx.RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task SetPlaceCoverAsync()
    {
        if (Current?.Row.PlaceId is { } pl && await _ctx.TryAsync(() => _ctx.Store.SetPlaceCoverAsync(pl, Current.Id), "Tr_ErrSave"))
        {
            _ctx.Notifications.Show(_ctx.Loc["Tr_CoverSet"], NotificationKind.Success);
            await _ctx.RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task AssociatePlaceAsync()
    {
        if (Current is not { } p) return;
        var picker = TravelPickers.Place(_ctx);
        if (!_ctx.Dialogs.Show(picker) || picker.Selected is not { } choice) return;
        if (await _ctx.TryAsync(() => _ctx.Store.SetPhotosPlaceAsync([p.Id], choice.Id), "Tr_ErrSave"))
            _ctx.Notifications.Show(_ctx.Loc.Format("Tr_PhotoLinked", choice.Title), NotificationKind.Success);
    }

    [RelayCommand]
    private async Task AssociateTripAsync()
    {
        if (Current is not { } p) return;
        var picker = TravelPickers.Trip(_ctx);
        if (!_ctx.Dialogs.Show(picker) || picker.Selected is not { } choice) return;
        if (await _ctx.TryAsync(() => _ctx.Store.SetPhotosTripAsync([p.Id], choice.Id), "Tr_ErrSave"))
            _ctx.Notifications.Show(_ctx.Loc.Format("Tr_PhotoLinked", choice.Title), NotificationKind.Success);
    }
}
