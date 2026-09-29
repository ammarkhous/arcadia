using System.Collections.ObjectModel;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.Organizer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Organizer;

public sealed partial class NoteShelf(string key, string label, string icon, NoteView view, EntityKind? module = null, bool general = false, string? tag = null) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    public NoteView View { get; } = view;
    public EntityKind? Module { get; } = module;
    public bool General { get; } = general;
    public string? Tag { get; } = tag;
    public bool IsSub => Module != null || General || Tag != null;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _count;
    public override string ToString() => Label;
}

public sealed class NoteListItem(NoteRow row, ContextChip? context, string when)
{
    public NoteRow Row { get; } = row;
    public int Id => Row.Id;
    public string Title => Row.Title;
    public string Excerpt => Row.Excerpt;
    public ContextChip? Context { get; } = context;
    public string When { get; } = when;
    public bool IsPinned => Row.IsPinned;
    public bool IsFavorite => Row.IsFavorite;
    public string Accent => Row.Accent ?? "Steel";
    public string Icon => Row.Icon ?? "Notes";
    public override string ToString() => Title;
}

public enum NoteSaveState { Saved, Pending, Saving, Failed }

/// <summary>An earlier version of the note, with its local time.</summary>
public sealed record RevisionRow(NoteRevision Revision, string When)
{
    public string Title => Revision.Title;
    public override string ToString() => $"{When} {Title}";
}

/// <summary>
/// Notes: Arcadia's notebook. The editor writes after 800 ms of calm (never per keystroke), shows Saving… / Saved /
/// Save failed, and flushes when you switch note, leave the page or close Arcadia. Project notes are the same rows the
/// project workspace shows.
/// </summary>
public sealed partial class NotesViewModel : ObservableObject, IActivatable, IDeactivatable, IShortcutTarget, IRecipient<OrganizerChangedMessage>
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(800);
    private readonly INoteStore _store;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _saveCts;
    private bool _loaded;
    private bool _applying;
    private Note? _note;
    private int _selfChange;

    public NotesViewModel(INoteStore store, OrganizerContext ctx)
    {
        _store = store;
        Ctx = ctx;
        var loc = ctx.Loc;
        Shelves.Add(new NoteShelf("all", loc["NoteView_All"], "Notes", NoteView.All));
        Shelves.Add(new NoteShelf("recent", loc["NoteView_Recent"], "Clock", NoteView.Recent));
        Shelves.Add(new NoteShelf("pinned", loc["NoteView_Pinned"], "Pin", NoteView.Pinned));
        Shelves.Add(new NoteShelf("favorites", loc["NoteView_Favorites"], "Star", NoteView.Favorites));
        ModuleShelves =
        [
            new("general", loc["NoteModule_General"], "Notes", NoteView.ByModule, general: true),
            new("projects", loc["NoteModule_Project"], "Projects", NoteView.ByModule, EntityKind.Project),
            new("games", loc["NoteModule_Game"], "Games", NoteView.ByModule, EntityKind.Game),
            new("travel", loc["NoteModule_Trip"], "Suitcase", NoteView.ByModule, EntityKind.Trip),
            new("places", loc["NoteModule_Place"], "MapPin", NoteView.ByModule, EntityKind.Place),
            new("it", loc["NoteModule_Diagnostic"], "Diagnostic", NoteView.ByModule, EntityKind.Diagnostic),
            new("documents", loc["NoteModule_Document"], "Documents", NoteView.ByModule, EntityKind.Document),
        ];
        _shelf = Shelves.FirstOrDefault(s => string.Equals(s.View.ToString(), ctx.Prefs.NoteView, StringComparison.OrdinalIgnoreCase)) ?? Shelves[0];
        _shelf.IsSelected = true;
        _mode = ctx.Prefs.NoteMode is "Preview" or "Split" ? ctx.Prefs.NoteMode : "Edit";
        SortOptions = Enum.GetValues<NoteSort>().Select(s => new Option<NoteSort>(s, loc["NoteSort_" + s])).ToList();
        _sort = SortOptions[0];
        ctx.Messenger.Register(this);
        Current = this;
    }

    /// <summary>For the exit flush (App.OnExit).</summary>
    public static NotesViewModel? Current { get; private set; }

    public OrganizerContext Ctx { get; }
    public ObservableCollection<NoteShelf> Shelves { get; } = [];
    public IReadOnlyList<NoteShelf> ModuleShelves { get; }
    public ObservableCollection<NoteShelf> TagShelves { get; } = [];
    public ObservableCollection<NoteListItem> Items { get; } = [];
    public ObservableCollection<RevisionRow> Revisions { get; } = [];
    public IReadOnlyList<Option<NoteSort>> SortOptions { get; }

    [ObservableProperty] private NoteShelf _shelf;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private Option<NoteSort> _sort;
    [ObservableProperty] private NoteListItem? _selected;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyText = string.Empty;
    [ObservableProperty] private string _listTitle = string.Empty;

    // editor
    [ObservableProperty] private bool _hasNote;
    [ObservableProperty] private string _noteTitle = string.Empty;
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private string _tags = string.Empty;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string? _accent;
    [ObservableProperty] private ContextChip? _context;
    [ObservableProperty] private NoteSaveState _saveState;
    [ObservableProperty] private string _saveText = string.Empty;
    [ObservableProperty] private string _stats = string.Empty;
    [ObservableProperty] private string _mode;
    [ObservableProperty] private bool _showHistory;

    public bool ShowEditor => Mode != "Preview";
    public bool ShowPreview => Mode != "Edit";
    public IReadOnlyList<string> AccentChoices => CategoryEditorViewModel.Accents;

    partial void OnModeChanged(string value)
    {
        Ctx.Prefs.NoteMode = value;
        Ctx.SavePrefs();
        OnPropertyChanged(nameof(ShowEditor));
        OnPropertyChanged(nameof(ShowPreview));
    }

    [RelayCommand] private void SetMode(string? mode) { if (mode is "Edit" or "Preview" or "Split") Mode = mode; }

    public async Task ActivateAsync()
    {
        await Ctx.Ready;
        if (!_loaded) { _loaded = true; await LoadAsync(); }
        else await LoadAsync(keepSelection: true);
    }

    public void Deactivate() => _ = FlushAsync();

    public void Receive(OrganizerChangedMessage message)
    {
        if (_loaded && (message.What & OrganizerChange.Notes) != 0 && Interlocked.Exchange(ref _selfChange, 0) == 0) _ = LoadAsync(keepSelection: true);
    }

    partial void OnFilterChanged(string value) => _ = LoadAsync(keepSelection: true);
    partial void OnSortChanged(Option<NoteSort> value) => _ = LoadAsync(keepSelection: true);

    partial void OnSelectedChanged(NoteListItem? oldValue, NoteListItem? newValue)
    {
        if (newValue?.Id == _note?.Id && newValue != null) return;
        _ = OpenAsync(newValue?.Id);
    }

    [RelayCommand]
    private async Task SelectShelfAsync(NoteShelf? shelf)
    {
        if (shelf == null) return;
        foreach (var s in Shelves.Concat(ModuleShelves).Concat(TagShelves)) s.IsSelected = s == shelf;
        Shelf = shelf;
        if (!shelf.IsSub) { Ctx.Prefs.NoteView = shelf.View.ToString(); Ctx.SavePrefs(); }
        await LoadAsync();
    }

    public async Task LoadAsync(bool keepSelection = false)
    {
        await Ctx.Ready;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            var q = new NoteQuery(Shelf.View, Filter, Shelf.Module, Shelf.General, Shelf.Tag, Sort.Value);
            var rows = await _store.QueryAsync(q, cts.Token);
            var chips = await ContextChip.ResolveAsync(Ctx, rows.Select(r => r.Context).OfType<EntityRef>());
            if (cts.IsCancellationRequested) return;
            var keepId = keepSelection ? _note?.Id : null;
            Items.Clear();
            foreach (var r in rows) Items.Add(new NoteListItem(r, r.Context is { } c ? chips.GetValueOrDefault(c) : null, When(r.UpdatedAtUtc)));
            IsEmpty = Items.Count == 0;
            EmptyText = Ctx.Loc[string.IsNullOrWhiteSpace(Filter) ? "NoteEmpty_" + Shelf.View : "Note_NoMatch"];
            ListTitle = Shelf.Label;
            if (keepId is { } id)
            {
                _selectedSilently = true;
                Selected = Items.FirstOrDefault(i => i.Id == id);
                _selectedSilently = false;
            }
            await LoadShelfCountsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Ctx.Logger.LogError(ex, "Loading notes failed");
            Ctx.Notifications.Show(Ctx.Loc["Note_ErrLoad"], NotificationKind.Warning);
        }
    }

    private bool _selectedSilently;

    private async Task LoadShelfCountsAsync()
    {
        var tags = await _store.GetTagsAsync();
        var selectedTag = Shelf.Tag;
        TagShelves.Clear();
        foreach (var t in tags.Take(24))
            TagShelves.Add(new NoteShelf("tag:" + t.Tag, t.Tag, "Hash", NoteView.ByTag, tag: t.Tag) { Count = t.Count.ToString(Ctx.Loc.Culture), IsSelected = t.Tag == selectedTag });
        if (selectedTag != null && TagShelves.FirstOrDefault(s => s.Tag == selectedTag) is { } again) Shelf = again;
    }

    private string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var d = DateOnly.FromDateTime(local);
        return d == Ctx.Today ? OrgText.Time(local, Ctx.Loc.Culture) : OrgText.Day(d, Ctx.Today, Ctx.Loc);
    }

    // ───────────── editor ─────────────

    private async Task OpenAsync(int? id)
    {
        if (_selectedSilently) return;
        await FlushAsync();
        if (id is not { } noteId) { Close(); return; }
        var note = await _store.GetAsync(noteId);
        if (note == null) { Close(); return; }
        _note = note;
        _applying = true;
        try
        {
            NoteTitle = note.Title;
            Content = note.Content;
            Tags = note.Tags;
            IsPinned = note.IsPinned;
            IsFavorite = note.IsFavorite;
            Accent = note.Accent;
            Context = note.Context is { } r ? (await ContextChip.ResolveAsync(Ctx, [r])).GetValueOrDefault(r) : null;
            HasNote = true;
            ShowHistory = false;
            Revisions.Clear();
            SetState(NoteSaveState.Saved, note.UpdatedAtUtc);
            UpdateStats();
        }
        finally { _applying = false; }
    }

    private void Close()
    {
        _note = null;
        HasNote = false;
        Context = null;
    }

    partial void OnNoteTitleChanged(string value) => Touch();
    partial void OnContentChanged(string value) { Touch(); UpdateStats(); }
    partial void OnTagsChanged(string value) => Touch();

    private void UpdateStats()
    {
        var words = Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Stats = Ctx.Loc.Format("Note_Stats", words, Content.Length);
    }

    /// <summary>Called on every change: (re)starts the debounce timer; the write happens once typing pauses.</summary>
    private void Touch()
    {
        if (_applying || _note == null) return;
        SetState(NoteSaveState.Pending, null);
        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        _ = SaveLaterAsync(cts.Token);
    }

    private async Task SaveLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, ct);
            await SaveNowAsync();
        }
        catch (OperationCanceledException) { }
    }

    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>Writes pending text now (switching notes, leaving the page, Ctrl+S, exit).</summary>
    public async Task FlushAsync()
    {
        _saveCts?.Cancel();
        if (SaveState is NoteSaveState.Pending or NoteSaveState.Failed) await SaveNowAsync();
    }

    /// <summary>
    /// Application exit: writes pending text on a worker thread (the UI thread is blocked waiting for it, so nothing
    /// here may continue on the dispatcher).
    /// </summary>
    public Task FlushForExit()
    {
        _saveCts?.Cancel();
        if (_note is not { } note || SaveState == NoteSaveState.Saved) return Task.CompletedTask;
        var (title, content, tags) = (NoteTitle, Content, Tags);
        return Task.Run(() => _store.SaveAsync(note.Id, title, content, tags));
    }

    private async Task SaveNowAsync()
    {
        if (_note is not { } note) return;
        await _saveGate.WaitAsync();
        try
        {
            if (SaveState == NoteSaveState.Saved) return;
            SetState(NoteSaveState.Saving, null);
            var (title, content, tags) = (NoteTitle, Content, Tags);
            var at = await _store.SaveAsync(note.Id, title, content, tags);
            note.Title = title;
            note.Content = content;
            note.UpdatedAtUtc = at;
            // typing may have continued during the write; only then is it still pending
            SetState(title == NoteTitle && content == Content && tags == Tags ? NoteSaveState.Saved : NoteSaveState.Pending, at);
            if (SaveState == NoteSaveState.Pending) { _saveCts?.Cancel(); var cts = _saveCts = new CancellationTokenSource(); _ = SaveLaterAsync(cts.Token); }
            UpdateListRow(note.Id, title, content, at);
            Interlocked.Exchange(ref _selfChange, 1);
            Ctx.Changed(OrganizerChange.Notes);
        }
        catch (Exception ex)
        {
            Ctx.Logger.LogError(ex, "Saving note {Id} failed", note.Id);
            SetState(NoteSaveState.Failed, null);
        }
        finally { _saveGate.Release(); }
    }

    private void UpdateListRow(int id, string title, string content, DateTime at)
    {
        var index = Items.ToList().FindIndex(i => i.Id == id);
        if (index < 0) return;
        var old = Items[index];
        var row = old.Row with { Title = string.IsNullOrWhiteSpace(title) ? old.Row.Title : title.Trim(), Excerpt = Excerpt(content), UpdatedAtUtc = at };
        _selectedSilently = true;
        Items[index] = new NoteListItem(row, old.Context, When(at));
        Selected = Items[index];
        _selectedSilently = false;
    }

    private static string Excerpt(string content)
    {
        var flat = string.Join(' ', content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.TrimStart('#', '>', '-', '*', ' ')));
        return flat.Length > 240 ? flat[..240] + "…" : flat;
    }

    private void SetState(NoteSaveState state, DateTime? savedUtc)
    {
        SaveState = state;
        SaveText = state switch
        {
            NoteSaveState.Pending => Ctx.Loc["Note_Unsaved"],
            NoteSaveState.Saving => Ctx.Loc["Note_Saving"],
            NoteSaveState.Failed => Ctx.Loc["Note_SaveFailed"],
            _ => savedUtc is { } t ? Ctx.Loc.Format("Note_SavedAt", OrgText.Time(t.ToLocalTime(), Ctx.Loc.Culture)) : Ctx.Loc["Note_Saved"],
        };
    }

    [RelayCommand] private Task SaveAsync() => FlushAsync();

    [RelayCommand]
    private async Task RetrySaveAsync()
    {
        SetState(NoteSaveState.Pending, null);
        await SaveNowAsync();
    }

    // ───────────── note actions ─────────────

    [RelayCommand]
    public async Task NewNoteAsync()
    {
        await FlushAsync();
        Note? created = null;
        if (!await Ctx.TryAsync(async () => created = await _store.CreateAsync(new NoteDraft(Ctx.Loc["Note_NewTitle"], string.Empty, Shelf.Tag ?? string.Empty, null,
                IsPinned: Shelf.View == NoteView.Pinned, IsFavorite: Shelf.View == NoteView.Favorites)), "Note_ErrSave")) return;
        Interlocked.Exchange(ref _selfChange, 1);
        Ctx.Changed(OrganizerChange.Notes);
        if (Shelf.View is not (NoteView.All or NoteView.Recent or NoteView.Pinned or NoteView.Favorites) && Shelf.Tag == null) await SelectShelfAsync(Shelves[0]);
        else await LoadAsync();
        Selected = Items.FirstOrDefault(i => i.Id == created!.Id);
        if (Selected == null) await OpenAsync(created!.Id);
        TitleFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The view focuses the title box of a new note.</summary>
    public event EventHandler? TitleFocusRequested;

    /// <summary>Opens a note (search, links, Home).</summary>
    public async Task ShowNoteAsync(int id)
    {
        _loaded = true;
        if (!Items.Any(i => i.Id == id))
        {
            Filter = string.Empty;
            foreach (var s in Shelves.Concat(ModuleShelves).Concat(TagShelves)) s.IsSelected = s == Shelves[0];
            Shelf = Shelves[0];
            await LoadAsync();
        }
        Selected = Items.FirstOrDefault(i => i.Id == id);
        if (Selected == null) await OpenAsync(id);
    }

    public async Task ShowShelfAsync(string key)
    {
        _loaded = true;
        var shelf = Shelves.Concat(ModuleShelves).FirstOrDefault(s => s.Key == key) ?? Shelves[0];
        await SelectShelfAsync(shelf);
    }

    [RelayCommand]
    private async Task TogglePinAsync()
    {
        if (_note == null) return;
        var value = !IsPinned;
        if (await Ctx.TryAsync(() => _store.SetPinnedAsync(_note.Id, value), "Note_ErrSave")) { IsPinned = value; await AfterChangeAsync(); }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (_note == null) return;
        var value = !IsFavorite;
        if (await Ctx.TryAsync(() => _store.SetFavoriteAsync(_note.Id, value), "Note_ErrSave")) { IsFavorite = value; await AfterChangeAsync(); }
    }

    [RelayCommand]
    private async Task SetAccentAsync(string? accent)
    {
        if (_note == null) return;
        var value = accent == Accent ? null : accent;
        if (await Ctx.TryAsync(() => _store.SetAppearanceAsync(_note.Id, value, _note.Icon), "Note_ErrSave")) { Accent = value; await AfterChangeAsync(); }
    }

    [RelayCommand]
    private async Task PickContextAsync()
    {
        if (_note == null) return;
        var picker = new EntityPickerViewModel(Ctx, [EntityKind.Project, EntityKind.Game, EntityKind.Trip, EntityKind.Place, EntityKind.Diagnostic, EntityKind.Document, EntityKind.Task, EntityKind.Event]);
        if (!Ctx.Dialogs.Show(picker) || picker.Selected is not { } info) return;
        await FlushAsync();
        if (await Ctx.TryAsync(() => _store.SetContextAsync(_note.Id, info.Ref), "Note_ErrSave"))
        {
            Context = new ContextChip(info, OrgText.KindLabel(info.Ref.Kind, Ctx.Loc), Ctx.Navigation);
            await AfterChangeAsync();
        }
    }

    [RelayCommand]
    private async Task ClearContextAsync()
    {
        if (_note == null) return;
        if (await Ctx.TryAsync(() => _store.SetContextAsync(_note.Id, null), "Note_ErrSave")) { Context = null; await AfterChangeAsync(); }
    }

    /// <summary>Inserts an arcadia:// link to something in Arcadia at the caret (the view passes the caret index).</summary>
    public string? PickInternalLink()
    {
        var picker = new EntityPickerViewModel(Ctx, [EntityKind.Note, EntityKind.Project, EntityKind.Task, EntityKind.Game, EntityKind.Trip, EntityKind.Place, EntityKind.Document, EntityKind.Event, EntityKind.Diagnostic]);
        return Ctx.Dialogs.Show(picker) && picker.Selected is { } info ? $"[{info.Name.Replace("]", ")")}]({info.Ref})" : null;
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_note == null) return;
        if (!Ctx.Dialogs.Confirm(Ctx.Loc.Format("Note_ConfirmDelete", NoteTitle), Ctx.Loc["Confirm_Delete"])) return;
        _saveCts?.Cancel();
        var id = _note.Id;
        if (await Ctx.TryAsync(() => _store.DeleteAsync(id), "Note_ErrSave"))
        {
            SaveState = NoteSaveState.Saved;
            Close();
            Ctx.Changed(OrganizerChange.Notes);
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleHistoryAsync()
    {
        ShowHistory = !ShowHistory;
        if (!ShowHistory || _note == null) return;
        await FlushAsync();
        Revisions.Clear();
        foreach (var r in await _store.GetRevisionsAsync(_note.Id)) Revisions.Add(new RevisionRow(r, r.SavedAtUtc.ToLocalTime().ToString("g", Ctx.Loc.Culture)));
    }

    [RelayCommand]
    private void RestoreRevision(RevisionRow? row)
    {
        if (row?.Revision is not { } revision || _note == null) return;
        if (!Ctx.Dialogs.Confirm(Ctx.Loc.Format("Note_ConfirmRestore", revision.SavedAtUtc.ToLocalTime().ToString("g", Ctx.Loc.Culture)), Ctx.Loc["Note_Restore"])) return;
        NoteTitle = revision.Title;
        Content = revision.Content; // saved by the normal autosave, which keeps the current text as a revision first
        ShowHistory = false;
    }

    private async Task AfterChangeAsync()
    {
        Interlocked.Exchange(ref _selfChange, 1);
        Ctx.Changed(OrganizerChange.Notes);
        await LoadAsync(keepSelection: true);
    }

    public bool OnNew()
    {
        _ = NewNoteAsync();
        return true;
    }

    public bool OnSave()
    {
        _ = FlushAsync();
        return true;
    }
}
