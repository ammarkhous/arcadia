using System.Collections.ObjectModel;
using System.Windows.Threading;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Projects;
using Arcadia.Core.Travel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Travel;

public sealed partial class JournalListItem(JournalSummary s, ITravelContext ctx) : ObservableObject
{
    public JournalSummary Summary { get; private set; } = s;
    public int Id => Summary.Id;
    [ObservableProperty] private string _title = s.Title;
    [ObservableProperty] private string _excerpt = s.Excerpt;
    public string DateText => Summary.EntryDate.ToString("d MMM yyyy", ctx.Loc.Culture);
    public string DayText => Summary.EntryDate.ToString("dd", ctx.Loc.Culture);
    public string MonthText => Summary.EntryDate.ToString("MMM", ctx.Loc.Culture).ToUpper(ctx.Loc.Culture);
    public string Where => string.Join(" · ", new[] { Summary.TripName, Summary.PlaceName }.Where(x => !string.IsNullOrEmpty(x)));
    public string MoodIcon => TravelText.MoodIcon(Summary.Mood);
    public bool HasMood => Summary.Mood != JournalMood.None;
    public bool IsHighlight => Summary.IsHighlight;
    public int PhotoCount => Summary.PhotoCount;
    public override string ToString() => Title;
}

/// <summary>
/// The travel journal (all entries, or those of one trip/place): a list and an editor that saves as you type
/// (800 ms after the last keystroke, when switching entries, on Ctrl+S and when Arcadia closes).
/// </summary>
public sealed partial class TravelJournalViewModel : ObservableObject
{
    private readonly ITravelContext _ctx;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _autosave;
    private readonly int? _tripId;
    private bool _loading;
    private bool _dirty;
    private List<JournalSummary> _all = [];

    public TravelJournalViewModel(ITravelContext ctx, int? tripId, ILogger logger)
    {
        _ctx = ctx;
        _tripId = tripId;
        _logger = logger;
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _autosave.Tick += async (_, _) => { _autosave.Stop(); await SaveAsync(); };
        MoodOptions = Enum.GetValues<JournalMood>().Select(m => new Option<JournalMood>(m, ctx.Loc["Mood_" + m])).ToList();
        Photos = new TravelPhotoGridViewModel(ctx, new PhotoQuery(JournalEntryId: -1), new PhotoLink(), logger);
        RebuildOptions();
    }

    public ObservableCollection<JournalListItem> Items { get; } = [];
    public IReadOnlyList<Option<JournalMood>> MoodOptions { get; }
    [ObservableProperty] private IReadOnlyList<Option<int?>> _tripOptions = [];
    [ObservableProperty] private IReadOnlyList<Option<int?>> _placeOptions = [];
    public TravelPhotoGridViewModel Photos { get; }
    public bool IsTripScoped => _tripId != null;

    [ObservableProperty] private JournalListItem? _selected;
    [ObservableProperty] private string _entryTitle = string.Empty;
    [ObservableProperty] private string _dateText = string.Empty;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private Option<int?>? _trip;
    [ObservableProperty] private Option<int?>? _place;
    [ObservableProperty] private Option<JournalMood>? _mood;
    [ObservableProperty] private string _tags = string.Empty;
    [ObservableProperty] private bool _isHighlight;
    [ObservableProperty] private bool _isPreview;
    [ObservableProperty] private bool _hasEntry;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private SaveState _state = SaveState.Idle;
    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private string? _dateError;
    [ObservableProperty] private bool _isEmpty;

    partial void OnEntryTitleChanged(string value) => MarkDirty();
    partial void OnDateTextChanged(string value) => MarkDirty();
    partial void OnTextChanged(string value) => MarkDirty();
    partial void OnTripChanged(Option<int?>? value) => MarkDirty();
    partial void OnPlaceChanged(Option<int?>? value) => MarkDirty();
    partial void OnMoodChanged(Option<JournalMood>? value) => MarkDirty();
    partial void OnTagsChanged(string value) => MarkDirty();
    partial void OnIsHighlightChanged(bool value) => MarkDirty();
    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnSelectedChanged(JournalListItem? value)
    {
        if (_loading) return;
        _ = SwitchAsync(value);
    }

    /// <summary>Trip and place choices follow the page's lists.</summary>
    public void RebuildOptions()
    {
        var keepTrip = Trip?.Value;
        var keepPlace = Place?.Value;
        _loading = true;
        TripOptions = [new Option<int?>(null, _ctx.Loc["Tr_NoTrip"]), .. _ctx.AllTrips.Select(t => new Option<int?>(t.Id, t.Name))];
        PlaceOptions = [new Option<int?>(null, _ctx.Loc["Tr_NoPlace"]), .. _ctx.AllPlaces.Select(p => new Option<int?>(p.Id, p.Name))];
        Trip = TripOptions.FirstOrDefault(o => o.Value == keepTrip) ?? TripOptions[0];
        Place = PlaceOptions.FirstOrDefault(o => o.Value == keepPlace) ?? PlaceOptions[0];
        _loading = false;
    }

    public async Task LoadAsync(int? select = null)
    {
        try
        {
            _all = (await _ctx.Store.ListJournalAsync(new JournalQuery(TripId: _tripId))).ToList();
            IsEmpty = _all.Count == 0;
            var keep = select ?? Selected?.Id;
            Rebuild();
            _loading = true;
            Selected = Items.FirstOrDefault(i => i.Id == keep) ?? Items.FirstOrDefault();
            _loading = false;
            if (!_dirty) await ShowAsync(Selected?.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Loading the journal failed");
            _ctx.Notifications.Show(_ctx.Loc["Tr_ErrLoad"], NotificationKind.Warning);
        }
    }

    private void Rebuild()
    {
        var f = Core.Text.TextFold.Fold(Filter);
        var selectedId = Selected?.Id;
        _loading = true;
        Items.Clear();
        foreach (var j in _all.Where(j => f.Length == 0 || Core.Text.TextFold.Fold(j.Title + " " + j.Excerpt + " " + j.Tags + " " + j.TripName + " " + j.PlaceName).Contains(f, StringComparison.Ordinal)))
            Items.Add(new JournalListItem(j, _ctx));
        Selected = Items.FirstOrDefault(i => i.Id == selectedId);
        _loading = false;
    }

    private async Task SwitchAsync(JournalListItem? next)
    {
        await SaveAsync();
        await ShowAsync(next?.Id);
    }

    private async Task ShowAsync(int? id)
    {
        var entry = id is { } i ? await _ctx.Store.GetJournalAsync(i) : null;
        _loading = true;
        EntryTitle = entry?.Title ?? string.Empty;
        DateText = entry == null ? string.Empty : DateText_(entry.EntryDate);
        Text = entry?.Text ?? string.Empty;
        Trip = TripOptions.FirstOrDefault(o => o.Value == entry?.TripId) ?? TripOptions[0];
        Place = PlaceOptions.FirstOrDefault(o => o.Value == entry?.PlaceId) ?? PlaceOptions[0];
        Mood = MoodOptions.First(o => o.Value == (entry?.Mood ?? JournalMood.None));
        Tags = entry?.Tags ?? string.Empty;
        IsHighlight = entry?.IsHighlight ?? false;
        HasEntry = entry != null;
        DateError = null;
        _loading = false;
        _dirty = false;
        SetState(SaveState.Idle);
        if (entry != null)
        {
            Photos.Query = new PhotoQuery(JournalEntryId: entry.Id);
            Photos.Link = new PhotoLink(TripId: entry.TripId, PlaceId: entry.PlaceId, JournalEntryId: entry.Id);
            await Photos.LoadAsync();
        }
    }

    private string DateText_(DateOnly d) => Dialogs.DateText.Format(d, _ctx.Loc.Culture);

    private void MarkDirty()
    {
        if (_loading || Selected == null) return;
        _dirty = true;
        SetState(SaveState.Idle, _ctx.Loc["Note_Unsaved"]);
        _autosave.Stop();
        _autosave.Start();
    }

    private JournalDraft? CurrentDraft()
    {
        if (!Dialogs.DateText.TryParse(DateText, _ctx.Loc.Culture, out var date) || date == null)
        {
            DateError = _ctx.Loc["Proj_ErrDate"];
            return null;
        }
        DateError = null;
        return new JournalDraft(EntryTitle, date.Value, Text, Trip?.Value, Place?.Value, Mood?.Value ?? JournalMood.None, Tags, IsHighlight);
    }

    [RelayCommand]
    public async Task NewAsync()
    {
        await SaveAsync();
        try
        {
            var entry = await _ctx.Store.CreateJournalAsync(new JournalDraft(_ctx.Loc["Tr_UntitledEntry"], _ctx.Today, string.Empty, _tripId));
            Filter = string.Empty;
            await LoadAsync(entry.Id);
            IsPreview = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Creating a journal entry failed");
            _ctx.Notifications.Show(_ctx.Loc["Tr_ErrSave"], NotificationKind.Warning);
        }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        _autosave.Stop();
        if (!_dirty || Selected is not { } item) return;
        if (CurrentDraft() is not { } draft) { SetState(SaveState.Failed, _ctx.Loc["Proj_ErrDate"]); return; }
        _dirty = false;
        SetState(SaveState.Saving, _ctx.Loc["Note_Saving"]);
        try
        {
            await _ctx.Store.UpdateJournalAsync(item.Id, draft);
            item.Title = string.IsNullOrWhiteSpace(draft.Title) ? item.Title : draft.Title.Trim();
            item.Excerpt = draft.Text.Length > 160 ? draft.Text[..160] + "…" : draft.Text;
            if (!_dirty) SetState(SaveState.Saved, _ctx.Loc.Format("Note_SavedAt", DateTime.Now.ToString("t", _ctx.Loc.Culture)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _dirty = true; // keep the text and retry later
            _logger.LogError(ex, "Saving journal entry {Id} failed", item.Id);
            SetState(SaveState.Failed, _ctx.Loc["Note_SaveFailed"]);
        }
    }

    /// <summary>For shutdown: the pending write, without UI continuations.</summary>
    public Task FlushForExit()
    {
        _autosave.Stop();
        if (!_dirty || Selected is not { } item || CurrentDraft() is not { } draft) return Task.CompletedTask;
        _dirty = false;
        return _ctx.Store.UpdateJournalAsync(item.Id, draft);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } item) return;
        if (!_ctx.Dialogs.Confirm(_ctx.Loc.Format("Tr_ConfirmDeleteEntry", item.Title), _ctx.Loc["Proj_Delete"])) return;
        _dirty = false;
        if (await _ctx.TryAsync(() => _ctx.Store.DeleteJournalAsync(item.Id), "Tr_ErrSave"))
        {
            Selected = null;
            await LoadAsync();
        }
    }

    [RelayCommand] private void TogglePreview() => IsPreview = !IsPreview;

    public async Task SelectAsync(int id)
    {
        Filter = string.Empty;
        if (Items.All(i => i.Id != id)) await LoadAsync(id);
        Selected = Items.FirstOrDefault(i => i.Id == id) ?? Selected;
    }

    private void SetState(SaveState state, string text = "")
    {
        State = state;
        StateText = text;
    }
}
