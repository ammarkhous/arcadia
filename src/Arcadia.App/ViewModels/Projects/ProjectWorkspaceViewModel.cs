using Arcadia.Core.Organizer;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.Documents;
using Arcadia.Core.Formatting;
using Arcadia.Core.Localization;
using Arcadia.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Projects;

public enum WorkspaceTab { Overview, Tasks, Notes, Files, Links, Activity, Expenses }

/// <summary>Services the workspace and its sections share.</summary>
public sealed class WorkspaceContext(
    IProjectStore store,
    IDocumentStore documents,
    DocumentActions documentActions,
    DocumentAvailabilityService availability,
    IDialogService dialogs,
    INotificationService notifications,
    IClipboardService clipboard,
    IExplorerLauncher explorer,
    IShellLauncher shell,
    IMessenger messenger,
    ILocalizer loc,
    ILoggerFactory logs)
{
    public IProjectStore Store { get; } = store;
    public IDocumentStore Documents { get; } = documents;
    public DocumentActions DocumentActions { get; } = documentActions;
    public DocumentAvailabilityService Availability { get; } = availability;
    public IDialogService Dialogs { get; } = dialogs;
    public INotificationService Notifications { get; } = notifications;
    public IClipboardService Clipboard { get; } = clipboard;
    public IExplorerLauncher Explorer { get; } = explorer;
    public IShellLauncher Shell { get; } = shell;
    public IMessenger Messenger { get; } = messenger;
    public ILocalizer Loc { get; } = loc;
    public ILoggerFactory Logs { get; } = logs;
}

// ═══════════════════════════ rows ═══════════════════════════

public sealed partial class TaskRowViewModel(TaskItem task, bool isSubtask, ILocalizer loc, DateOnly today) : ObservableObject
{
    public TaskItem Task { get; } = task;
    public int Id => Task.Id;
    public bool IsSubtask { get; } = isSubtask;
    public string Title => Task.Title;
    public bool IsDone => Task.IsDone;
    public bool IsCancelled => Task.Status == TaskItemStatus.Cancelled;
    public string StatusText => loc[$"TaskStatus_{Task.Status}"];
    public string StatusTone => ProjectText.TaskTone(Task.Status);
    public string PriorityText => loc[$"Priority_{Task.Priority}"];
    public string PriorityTone => ProjectText.PriorityTone(Task.Priority);
    public bool ShowPriority => Task.Priority is ProjectPriority.High or ProjectPriority.Critical;
    public string? DueText => Task.DueDate is { } d ? ProjectText.Due(d, today, Task.IsDone, loc) : null;
    public string DueTone => ProjectText.DueTone(Task.DueDate, today, Task.IsDone);
    public string? Detail => string.IsNullOrWhiteSpace(Task.Description) ? null : Task.Description.Split('\n')[0];
    public int SubtaskCount => Task.Subtasks.Count;
    public string? SubtaskText => Task.Subtasks.Count == 0 ? null : $"{Task.Subtasks.Count(s => s.Status == TaskItemStatus.Completed)}/{Task.Subtasks.Count}";
    public IReadOnlyList<string> Tags => TagText.Split(Task.Tags);
}

public sealed partial class NoteItemViewModel(Note note, ILocalizer loc) : ObservableObject
{
    public int Id { get; } = note.Id;
    [ObservableProperty] private string _title = note.Title;
    [ObservableProperty] private string _preview = Snippet(note.Content);
    [ObservableProperty] private bool _isPinned = note.IsPinned;
    [ObservableProperty] private DateTime _updatedAtUtc = note.UpdatedAtUtc;
    public DateTime CreatedAtUtc { get; } = note.CreatedAtUtc;
    public string UpdatedText => RelativeTime.Format(UpdatedAtUtc, DateTime.UtcNow, loc);
    partial void OnUpdatedAtUtcChanged(DateTime value) => OnPropertyChanged(nameof(UpdatedText));

    public static string Snippet(string content)
    {
        var line = content.Split('\n').Select(l => l.Trim().TrimStart('#', '-', '*', '>', ' ')).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}

public sealed record FileRowViewModel(DocumentRow Row, bool IsPinned, string TypeName, string SizeText, string ModifiedText)
{
    public int Id => Row.Id;
    public string Name => Row.DisplayName;
    public string Path => Row.Path;
    public string Extension => Row.Extension;
    public bool IsFolder => Row.IsFolder;
    public bool IsMissing => Row.IsMissing;
}

public sealed record LinkRowViewModel(ProjectLink Link)
{
    public int Id => Link.Id;
    public string Title => Link.Title;
    public string Url => Link.Url;
    public string? Description => Link.Description;
    public bool IsPinned => Link.IsPinned;
    public string Host => Uri.TryCreate(Link.Url, UriKind.Absolute, out var u) ? u.Host : Link.Url;
    public IReadOnlyList<string> Tags => TagText.Split(Link.Tags);
}

public sealed record ActivityRowViewModel(string Icon, string Text, string Time, string Tone);

public enum SaveState { Idle, Saving, Saved, Failed }

// ═══════════════════════════ notes (autosave) ═══════════════════════════

/// <summary>
/// The notes section. Typing marks the note dirty; it is written 800 ms after the last keystroke, when another
/// note/tab/page is chosen, on Ctrl+S and when Arcadia closes. Text is never lost by switching views.
/// </summary>
public sealed partial class WorkspaceNotesViewModel : ObservableObject
{
    private readonly WorkspaceContext _ctx;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _autosave;
    private int _projectId;
    private List<Note> _notes = [];
    private bool _loading;
    private bool _dirty;

    public WorkspaceNotesViewModel(WorkspaceContext ctx)
    {
        _ctx = ctx;
        _logger = ctx.Logs.CreateLogger<WorkspaceNotesViewModel>();
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _autosave.Tick += async (_, _) => { _autosave.Stop(); await SaveAsync(); };
    }

    public ObservableCollection<NoteItemViewModel> Items { get; } = [];

    [ObservableProperty] private NoteItemViewModel? _selected;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private string _tags = string.Empty;
    [ObservableProperty] private bool _isPreview;
    [ObservableProperty] private SaveState _state = SaveState.Idle;
    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private bool _hasNote;

    partial void OnTitleChanged(string value) => MarkDirty();
    partial void OnContentChanged(string value) => MarkDirty();
    partial void OnTagsChanged(string value) => MarkDirty();
    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnSelectedChanged(NoteItemViewModel? oldValue, NoteItemViewModel? newValue)
    {
        if (_loading) return;
        _ = SwitchAsync(newValue);
    }

    private async Task SwitchAsync(NoteItemViewModel? next)
    {
        await SaveAsync(); // flush the note we are leaving
        Show(next == null ? null : _notes.FirstOrDefault(n => n.Id == next.Id));
    }

    private void Show(Note? note)
    {
        _loading = true;
        Title = note?.Title ?? string.Empty;
        Content = note?.Content ?? string.Empty;
        Tags = note?.Tags ?? string.Empty;
        HasNote = note != null;
        _loading = false;
        _dirty = false;
        SetState(SaveState.Idle);
    }

    private void MarkDirty()
    {
        if (_loading || Selected == null) return;
        _dirty = true;
        SetState(SaveState.Idle, _ctx.Loc["Note_Unsaved"]);
        _autosave.Stop();
        _autosave.Start();
    }

    public void Load(int projectId, IReadOnlyList<Note> notes, int? select = null)
    {
        _projectId = projectId;
        _notes = notes.ToList();
        var keep = select ?? Selected?.Id;
        Rebuild();
        _loading = true;
        Selected = Items.FirstOrDefault(i => i.Id == keep) ?? (Selected == null ? Items.FirstOrDefault() : null);
        _loading = false;
        if (!_dirty) Show(Selected == null ? null : _notes.FirstOrDefault(n => n.Id == Selected.Id));
    }

    private void Rebuild()
    {
        var term = Filter.Trim();
        var list = _notes.Where(n => term.Length == 0 || n.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                                     || n.Content.Contains(term, StringComparison.CurrentCultureIgnoreCase) || n.Tags.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.UpdatedAtUtc).ToList();
        var selectedId = Selected?.Id;
        _loading = true;
        Items.Clear();
        foreach (var n in list) Items.Add(new NoteItemViewModel(n, _ctx.Loc));
        Selected = Items.FirstOrDefault(i => i.Id == selectedId);
        _loading = false;
    }

    public void Select(int noteId)
    {
        Filter = string.Empty;
        Selected = Items.FirstOrDefault(i => i.Id == noteId) ?? Selected;
    }

    [RelayCommand]
    public async Task NewAsync()
    {
        await SaveAsync();
        try
        {
            var note = await _ctx.Store.AddNoteAsync(_projectId, _ctx.Loc["Note_Untitled"]);
            _notes.Add(note);
            Filter = string.Empty;
            Rebuild();
            Selected = Items.FirstOrDefault(i => i.Id == note.Id);
            IsPreview = false;
            _ctx.Messenger.Send(new ProjectsChangedMessage(_projectId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating a note failed");
            _ctx.Notifications.Show(_ctx.Loc["Proj_ErrSave"], NotificationKind.Warning);
        }
    }

    /// <summary>Writes the current note if it changed. Safe to call any time.</summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        _autosave.Stop();
        if (!_dirty || Selected is not { } item) return;
        var (id, title, content, tags) = (item.Id, Title, Content, Tags);
        _dirty = false;
        SetState(SaveState.Saving, _ctx.Loc["Note_Saving"]);
        try
        {
            var updated = await _ctx.Store.SaveNoteAsync(id, title, content, tags);
            if (_notes.FirstOrDefault(n => n.Id == id) is { } n)
            {
                n.Title = string.IsNullOrWhiteSpace(title) ? n.Title : title.Trim();
                n.Content = content;
                n.Tags = tags;
                n.UpdatedAtUtc = updated;
            }
            item.Title = string.IsNullOrWhiteSpace(title) ? item.Title : title.Trim();
            item.Preview = NoteItemViewModel.Snippet(content);
            item.UpdatedAtUtc = updated;
            if (!_dirty) SetState(SaveState.Saved, _ctx.Loc.Format("Note_SavedAt", updated.ToLocalTime().ToString("t", _ctx.Loc.Culture)));
        }
        catch (Exception ex)
        {
            _dirty = true; // keep the text and try again later
            _logger.LogError(ex, "Saving note {Id} failed", id);
            SetState(SaveState.Failed, _ctx.Loc["Note_SaveFailed"]);
        }
    }

    /// <summary>For shutdown: returns the pending write without continuations on the UI thread.</summary>
    public Task FlushForExit()
    {
        _autosave.Stop();
        if (!_dirty || Selected is not { } item) return Task.CompletedTask;
        _dirty = false;
        return _ctx.Store.SaveNoteAsync(item.Id, Title, Content, Tags);
    }

    private void SetState(SaveState state, string text = "")
    {
        State = state;
        StateText = text;
    }

    [RelayCommand]
    private async Task TogglePinAsync(NoteItemViewModel? item)
    {
        item ??= Selected;
        if (item == null) return;
        await _ctx.Store.SetNotePinnedAsync(item.Id, !item.IsPinned);
        item.IsPinned = !item.IsPinned;
        if (_notes.FirstOrDefault(n => n.Id == item.Id) is { } n) n.IsPinned = item.IsPinned;
        Rebuild();
        _ctx.Messenger.Send(new ProjectsChangedMessage(_projectId));
    }

    [RelayCommand]
    private async Task DeleteAsync(NoteItemViewModel? item)
    {
        item ??= Selected;
        if (item == null) return;
        if (!_ctx.Dialogs.Confirm(_ctx.Loc.Format("Note_ConfirmDelete", item.Title), _ctx.Loc["Proj_Delete"])) return;
        _autosave.Stop();
        _dirty = false;
        await _ctx.Store.DeleteNoteAsync(item.Id);
        _notes.RemoveAll(n => n.Id == item.Id);
        Rebuild();
        Selected = Items.FirstOrDefault();
        if (Selected == null) Show(null);
        _ctx.Messenger.Send(new ProjectsChangedMessage(_projectId));
    }

    [RelayCommand] private void TogglePreview() => IsPreview = !IsPreview;

    public IReadOnlyList<Note> Snapshot => _notes;
}

// ═══════════════════════════ workspace ═══════════════════════════

/// <summary>A project's "command center": overview, tasks, notes, files, links and activity.</summary>
public sealed partial class ProjectWorkspaceViewModel : ObservableObject, IShortcutTarget
{
    private readonly WorkspaceContext _ctx;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _descriptionSave;
    private readonly DispatcherTimer _progressSave;
    private Project? _project;
    private bool _loading;

    public ProjectWorkspaceViewModel(WorkspaceContext ctx)
    {
        _ctx = ctx;
        _logger = ctx.Logs.CreateLogger<ProjectWorkspaceViewModel>();
        Notes = new WorkspaceNotesViewModel(ctx);
        _descriptionSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _descriptionSave.Tick += async (_, _) => { _descriptionSave.Stop(); await SaveDescriptionAsync(); };
        _progressSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _progressSave.Tick += async (_, _) => { _progressSave.Stop(); await SaveProgressAsync(); };
        StatusOptions = Enum.GetValues<ProjectStatus>().Select(s => new Option<ProjectStatus>(s, Loc[$"ProjStatus_{s}"])).ToList();
        PriorityOptions = Enum.GetValues<ProjectPriority>().Select(p => new Option<ProjectPriority>(p, Loc[$"Priority_{p}"])).ToList();
        TaskFilters = ["open", "all", "done", "blocked"];
        TaskSorts = [new("due", Loc["Task_SortDue"]), new("priority", Loc["Task_SortPriority"]), new("created", Loc["Task_SortCreated"]), new("title", Loc["Task_SortTitle"])];
        _taskSort = TaskSorts[0];
    }

    private ILocalizer Loc => _ctx.Loc;

    public event EventHandler? CloseRequested;

    public WorkspaceNotesViewModel Notes { get; }
    public IReadOnlyList<Option<ProjectStatus>> StatusOptions { get; }
    public IReadOnlyList<Option<ProjectPriority>> PriorityOptions { get; }
    public IReadOnlyList<string> TaskFilters { get; }
    public IReadOnlyList<Option<string>> TaskSorts { get; }

    public int ProjectId => _project?.Id ?? 0;

    // header
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _accent = "Blue";
    [ObservableProperty] private string _icon = "Projects";
    [ObservableProperty] private string _theme = "mountains-dusk";
    [ObservableProperty] private int _seed;
    [ObservableProperty] private Option<ProjectStatus>? _status;
    [ObservableProperty] private Option<ProjectPriority>? _priority;
    [ObservableProperty] private string _statusTone = "Blue";
    [ObservableProperty] private string _priorityTone = "Blue";
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isArchived;
    [ObservableProperty] private string _datesText = string.Empty;
    [ObservableProperty] private string _dueText = string.Empty;
    [ObservableProperty] private string _dueTone = "Steel";
    [ObservableProperty] private IReadOnlyList<string> _tags = [];
    [ObservableProperty] private string? _folderPath;
    [ObservableProperty] private bool _folderAvailable;
    [ObservableProperty] private WorkspaceTab _selectedTab = WorkspaceTab.Overview;

    // description (inline, autosaved)
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _descriptionState = string.Empty;

    // progress
    [ObservableProperty] private bool _taskBased;
    [ObservableProperty] private double _manualProgress;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _progressDetail = string.Empty;

    // overview
    [ObservableProperty] private string _taskSummary = string.Empty;
    [ObservableProperty] private int _openTasks;
    [ObservableProperty] private int _doneTasks;
    [ObservableProperty] private int _blockedTasks;
    [ObservableProperty] private int _overdueTasks;
    [ObservableProperty] private IReadOnlyList<TaskRowViewModel> _nextTasks = [];
    [ObservableProperty] private IReadOnlyList<NoteItemViewModel> _recentNotes = [];
    [ObservableProperty] private IReadOnlyList<FileRowViewModel> _recentFiles = [];
    [ObservableProperty] private IReadOnlyList<LinkRowViewModel> _importantLinks = [];
    [ObservableProperty] private IReadOnlyList<ActivityRowViewModel> _recentActivity = [];
    [ObservableProperty] private IReadOnlyList<object> _pinnedItems = [];

    // tasks
    [ObservableProperty] private IReadOnlyList<TaskRowViewModel> _taskRows = [];
    [ObservableProperty] private string _taskFilter = "open";
    [ObservableProperty] private Option<string> _taskSort;
    [ObservableProperty] private string _taskSearch = string.Empty;
    [ObservableProperty] private string _quickTask = string.Empty;
    [ObservableProperty] private TaskRowViewModel? _selectedTask;

    // files / links / activity
    [ObservableProperty] private IReadOnlyList<FileRowViewModel> _files = [];
    [ObservableProperty] private FileRowViewModel? _selectedFile;
    [ObservableProperty] private IReadOnlyList<LinkRowViewModel> _links = [];
    [ObservableProperty] private IReadOnlyList<ActivityRowViewModel> _activity = [];
    [ObservableProperty] private bool _isDragOver;

    partial void OnTaskFilterChanged(string value) => BuildTasks();
    partial void OnTaskSortChanged(Option<string> value) => BuildTasks();
    partial void OnTaskSearchChanged(string value) => BuildTasks();

    /// <summary>Recorded project expenses (Finance, Phase 7) — a read-out, created when the tab is first opened.</summary>
    [ObservableProperty] private Finance.ExpensePanelViewModel? _expenses;

    partial void OnSelectedTabChanged(WorkspaceTab oldValue, WorkspaceTab newValue)
    {
        if (oldValue == WorkspaceTab.Notes) _ = Notes.SaveAsync();
        if (newValue == WorkspaceTab.Files) _ = CheckFilesAsync();
        if (newValue == WorkspaceTab.Expenses)
        {
            Expenses ??= Finance.ExpensePanelViewModel.For(Arcadia.Core.Organizer.EntityKind.Project, ProjectId, _project?.Name ?? string.Empty);
            _ = Expenses?.LoadAsync();
        }
    }

    partial void OnDescriptionChanged(string value)
    {
        if (_loading) return;
        DescriptionState = Loc["Note_Unsaved"];
        _descriptionSave.Stop();
        _descriptionSave.Start();
    }

    partial void OnManualProgressChanged(double value)
    {
        if (_loading) return;
        UpdateProgressText();
        _progressSave.Stop();
        _progressSave.Start();
    }

    partial void OnTaskBasedChanged(bool value)
    {
        if (_loading) return;
        UpdateProgressText();
        _ = SaveProgressAsync();
    }

    partial void OnStatusChanged(Option<ProjectStatus>? value)
    {
        if (_loading || value == null || _project == null || value.Value == _project.Status) return;
        _ = ChangeStatusAsync(value.Value);
    }

    partial void OnPriorityChanged(Option<ProjectPriority>? value)
    {
        if (_loading || value == null || _project == null || value.Value == _project.Priority) return;
        _ = ChangePriorityAsync(value.Value);
    }

    // ── loading ──────────────────────────────────────────────

    public async Task<bool> LoadAsync(int id, bool keepTab = false)
    {
        var project = await _ctx.Store.GetAsync(id);
        if (project == null) return false;
        _project = project;
        _loading = true;
        Name = project.Name;
        Accent = project.Accent;
        Icon = project.Icon;
        Theme = project.ArtworkTheme;
        Seed = project.ArtworkSeed;
        Status = StatusOptions.First(o => o.Value == project.Status);
        Priority = PriorityOptions.First(o => o.Value == project.Priority);
        StatusTone = ProjectText.StatusTone(project.Status);
        PriorityTone = ProjectText.PriorityTone(project.Priority);
        IsPinned = project.IsPinned;
        IsArchived = project.IsArchived;
        Tags = project.TagList;
        if (_descriptionSave.IsEnabled == false) Description = project.Description ?? string.Empty;
        TaskBased = project.ProgressMode == ProgressMode.TaskBased;
        ManualProgress = project.ManualProgress;
        FolderPath = project.FolderPath;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var done = project.Status is ProjectStatus.Completed or ProjectStatus.Archived;
        DatesText = project.StartDate == null && project.TargetDate == null ? Loc["Proj_NoDates"]
            : $"{DateText.Format(project.StartDate, Loc.Culture)} → {DateText.Format(project.TargetDate, Loc.Culture)}";
        DueText = ProjectText.Due(project.TargetDate, today, done, Loc);
        DueTone = ProjectText.DueTone(project.TargetDate, today, done);
        _loading = false;

        UpdateProgressText();
        BuildTasks();
        Notes.Load(project.Id, project.Notes);
        Links = project.Links.OrderByDescending(l => l.IsPinned).ThenBy(l => l.Title, StringComparer.CurrentCultureIgnoreCase).Select(l => new LinkRowViewModel(l)).ToList();
        Activity = project.Activity.Select(ToActivity).ToList();
        await ReloadFilesAsync();
        _ = CheckFolderAsync();
        return true;
    }

    public Task ActivateAsync() => _project == null ? Task.CompletedTask : LoadAsync(_project.Id);

    public void Deactivate()
    {
        _ = Notes.SaveAsync();
        if (_descriptionSave.IsEnabled) { _descriptionSave.Stop(); _ = SaveDescriptionAsync(); }
    }

    public async Task FlushAsync()
    {
        await Notes.SaveAsync();
        if (_descriptionSave.IsEnabled) { _descriptionSave.Stop(); await SaveDescriptionAsync(); }
    }

    /// <summary>Shutdown path: pending writes as plain tasks (no UI continuations), see App.OnExit.</summary>
    public Task FlushForExit()
    {
        var tasks = new List<Task> { Notes.FlushForExit() };
        if (_descriptionSave.IsEnabled && _project != null)
        {
            _descriptionSave.Stop();
            tasks.Add(_ctx.Store.SetDescriptionAsync(_project.Id, Description));
        }
        return Task.WhenAll(tasks);
    }

    private async Task RefreshAsync()
    {
        if (_project == null) return;
        await LoadAsync(_project.Id);
        _ctx.Messenger.Send(new ProjectsChangedMessage(_project.Id));
    }

    public async Task ReloadFilesAsync()
    {
        if (_project == null) return;
        try
        {
            var rows = await _ctx.Documents.ListForProjectAsync(_project.Id);
            var pinned = (await Task.WhenAll(rows.Select(async r => (r.Id, (await _ctx.Documents.GetProjectsAsync(r.Id)).Any(p => p.Id == _project.Id && p.IsPinned))))).ToDictionary(x => x.Id, x => x.Item2);
            Files = rows.Select(r => new FileRowViewModel(r, pinned.GetValueOrDefault(r.Id), ShellFileInfo.TypeName(r.Extension, r.IsFolder),
                r.SizeBytes is { } s ? Core.ItTools.Files.ByteSize.Format(s, Loc.Culture) : "—",
                r.FileModifiedUtc?.ToLocalTime().ToString("g", Loc.Culture) ?? "—")).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading project files failed");
        }
        BuildOverview();
    }

    private async Task CheckFilesAsync()
    {
        if (Files.Count == 0) return;
        await _ctx.Availability.CheckAsync(Files.Select(f => f.Id).ToList());
        await ReloadFilesAsync();
    }

    private async Task CheckFolderAsync()
    {
        if (FolderPath is not { } folder) { FolderAvailable = false; return; }
        try
        {
            FolderAvailable = await Task.Run(() => System.IO.Directory.Exists(folder)).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            FolderAvailable = false;
        }
    }

    // ── overview ─────────────────────────────────────────────

    private void UpdateProgressText()
    {
        if (_project == null) return;
        var statuses = _project.Tasks.Select(t => t.Status).ToList();
        Progress = ProjectRules.Progress(TaskBased ? ProgressMode.TaskBased : ProgressMode.Manual, (int)Math.Round(ManualProgress), statuses);
        ProgressText = Loc.Format("It_Percent", Progress);
        var counted = statuses.Count(s => s != TaskItemStatus.Cancelled);
        ProgressDetail = TaskBased
            ? counted == 0 ? Loc["Proj_ProgressNoTasks"] : Loc.Format("Proj_ProgressTasks", statuses.Count(s => s == TaskItemStatus.Completed), counted)
            : Loc["Proj_ProgressManualHint"];
    }

    private void BuildOverview()
    {
        if (_project == null) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var tasks = _project.Tasks;
        OpenTasks = tasks.Count(t => t.IsOpen);
        DoneTasks = tasks.Count(t => t.Status == TaskItemStatus.Completed);
        BlockedTasks = tasks.Count(t => t.Status == TaskItemStatus.Blocked);
        OverdueTasks = tasks.Count(t => ProjectRules.IsOverdue(t.DueDate, today, t.IsDone));
        TaskSummary = tasks.Count == 0 ? Loc["Proj_NoTasks"] : Loc.Format("Proj_TasksOpen", OpenTasks, tasks.Count);
        NextTasks = tasks.Where(t => t.IsOpen).OrderBy(t => t.DueDate ?? DateOnly.MaxValue).ThenByDescending(t => t.Priority).Take(5)
            .Select(t => new TaskRowViewModel(t, t.ParentTaskId != null, Loc, today)).ToList();
        RecentNotes = Notes.Snapshot.OrderByDescending(n => n.UpdatedAtUtc).Take(4).Select(n => new NoteItemViewModel(n, Loc)).ToList();
        RecentFiles = Files.OrderByDescending(f => f.Row.FileModifiedUtc ?? DateTime.MinValue).Take(5).ToList();
        ImportantLinks = Links.Take(5).ToList();
        RecentActivity = Activity.Take(8).ToList();
        PinnedItems = [.. Notes.Snapshot.Where(n => n.IsPinned).Select(n => (object)new NoteItemViewModel(n, Loc)), .. Files.Where(f => f.IsPinned), .. Links.Where(l => l.IsPinned)];
    }

    private ActivityRowViewModel ToActivity(ProjectActivity a)
    {
        var (icon, tone) = a.Kind switch
        {
            ProjectActivityKind.ProjectCreated => ("Sparkle", "Cyan"),
            ProjectActivityKind.StatusChanged or ProjectActivityKind.ProjectUpdated => ("Flag", "Blue"),
            ProjectActivityKind.ProjectArchived => ("Library", "Steel"),
            ProjectActivityKind.ProjectRestored => ("Refresh", "Green"),
            ProjectActivityKind.TaskCreated => ("Plus", "Blue"),
            ProjectActivityKind.TaskCompleted => ("Check", "Green"),
            ProjectActivityKind.TaskReopened => ("Refresh", "Amber"),
            ProjectActivityKind.TaskDeleted or ProjectActivityKind.NoteDeleted or ProjectActivityKind.LinkRemoved or ProjectActivityKind.FileRemoved => ("Trash", "Steel"),
            ProjectActivityKind.NoteAdded => ("Notes", "Gold"),
            ProjectActivityKind.FileAttached => ("Documents", "Amber"),
            ProjectActivityKind.LinkAdded => ("External", "Cyan"),
            ProjectActivityKind.FolderSet => ("Documents", "Blue"),
            ProjectActivityKind.FileRelocated => ("Search", "Green"),
            _ => ("Clock", "Steel"),
        };
        var subject = a.Kind is ProjectActivityKind.StatusChanged or ProjectActivityKind.ProjectRestored && Enum.TryParse<ProjectStatus>(a.Subject, out var s)
            ? Loc[$"ProjStatus_{s}"] : a.Subject ?? string.Empty;
        return new ActivityRowViewModel(icon, Loc.Format($"Act_{a.Kind}", subject), RelativeTime.Format(a.CreatedAtUtc, DateTime.UtcNow, Loc), tone);
    }

    // ── header actions ───────────────────────────────────────

    [RelayCommand] private void Back() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand] private void ShowTab(WorkspaceTab tab) => SelectedTab = tab;

    [RelayCommand]
    private async Task EditAsync()
    {
        if (_project == null) return;
        var editor = new ProjectEditorViewModel(Loc, _ctx.Dialogs, _project);
        if (!_ctx.Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        await Guard(() => _ctx.Store.UpdateAsync(_project.Id, draft with { Description = Description }));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task TogglePinAsync()
    {
        if (_project == null) return;
        await _ctx.Store.SetPinnedAsync(_project.Id, !IsPinned);
        IsPinned = !IsPinned;
        _ctx.Messenger.Send(new ProjectsChangedMessage(_project.Id));
    }

    [RelayCommand]
    private async Task ArchiveAsync()
    {
        if (_project == null) return;
        if (IsArchived) await _ctx.Store.RestoreAsync(_project.Id);
        else
        {
            if (!_ctx.Dialogs.Confirm(Loc.Format("Proj_ConfirmArchive", Name), Loc["Proj_Archive"])) return;
            await _ctx.Store.ArchiveAsync(_project.Id);
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteProjectAsync()
    {
        if (_project == null) return;
        if (!_ctx.Dialogs.Confirm(Loc.Format("Proj_ConfirmDelete", Name), Loc["Proj_DeletePermanently"])) return;
        var id = _project.Id;
        await _ctx.Store.DeleteAsync(id);
        _ctx.Notifications.Show(Loc.Format("Proj_Deleted", Name), NotificationKind.Success);
        _project = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (FolderPath == null) return;
        if (!_ctx.Explorer.OpenFolder(FolderPath))
        {
            FolderAvailable = false;
            _ctx.Notifications.Show(Loc.Format("Proj_FolderUnavailable", FolderPath), NotificationKind.Warning);
        }
    }

    private async Task ChangeStatusAsync(ProjectStatus status)
    {
        if (_project == null) return;
        await Guard(() => _ctx.Store.SetStatusAsync(_project.Id, status));
        await RefreshAsync();
    }

    private async Task ChangePriorityAsync(ProjectPriority priority)
    {
        if (_project == null) return;
        var p = _project;
        var draft = new ProjectDraft(p.Name, Description, p.Status, priority, p.StartDate, p.TargetDate, p.Accent, p.Icon, p.ArtworkTheme, p.Tags, p.FolderPath);
        await Guard(() => _ctx.Store.UpdateAsync(p.Id, draft));
        await RefreshAsync();
    }

    private async Task SaveDescriptionAsync()
    {
        if (_project == null) return;
        DescriptionState = Loc["Note_Saving"];
        try
        {
            await _ctx.Store.SetDescriptionAsync(_project.Id, Description);
            _project.Description = Description;
            DescriptionState = Loc["Note_Saved"];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the project description failed");
            DescriptionState = Loc["Note_SaveFailed"];
        }
    }

    private async Task SaveProgressAsync()
    {
        if (_project == null) return;
        var mode = TaskBased ? ProgressMode.TaskBased : ProgressMode.Manual;
        await Guard(() => _ctx.Store.SetProgressAsync(_project.Id, mode, (int)Math.Round(ManualProgress)));
        _project.ProgressMode = mode;
        _project.ManualProgress = (int)Math.Round(ManualProgress);
        _ctx.Messenger.Send(new ProjectsChangedMessage(_project.Id));
    }

    // ── tasks ────────────────────────────────────────────────

    private void BuildTasks()
    {
        if (_project == null) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        bool Include(TaskItem t) => TaskFilter switch
        {
            "open" => t.IsOpen,
            "done" => t.IsDone,
            "blocked" => t.Status == TaskItemStatus.Blocked,
            _ => true,
        } && (TaskSearch.Trim().Length == 0 || t.Title.Contains(TaskSearch.Trim(), StringComparison.CurrentCultureIgnoreCase)
              || (t.Description?.Contains(TaskSearch.Trim(), StringComparison.CurrentCultureIgnoreCase) ?? false)
              || t.Tags.Contains(TaskSearch.Trim(), StringComparison.CurrentCultureIgnoreCase));

        IEnumerable<TaskItem> Sort(IEnumerable<TaskItem> tasks) => TaskSort.Value switch
        {
            "priority" => tasks.OrderByDescending(t => t.Priority).ThenBy(t => t.DueDate ?? DateOnly.MaxValue),
            "created" => tasks.OrderByDescending(t => t.CreatedAtUtc),
            "title" => tasks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => tasks.OrderBy(t => t.IsDone).ThenBy(t => t.DueDate ?? DateOnly.MaxValue).ThenBy(t => t.SortOrder),
        };

        var rows = new List<TaskRowViewModel>();
        foreach (var parent in Sort(_project.Tasks.Where(t => t.ParentTaskId == null)))
        {
            var subs = Sort(parent.Subtasks).Where(Include).ToList();
            if (!Include(parent) && subs.Count == 0) continue;
            rows.Add(new TaskRowViewModel(parent, false, Loc, today));
            rows.AddRange(subs.Select(s => new TaskRowViewModel(s, true, Loc, today)));
        }
        TaskRows = rows;
        BuildOverview();
    }

    [RelayCommand] private void SetTaskFilter(string? filter) => TaskFilter = filter ?? "open";

    /// <summary>Enter in the quick-add box: one keystroke to capture a task.</summary>
    [RelayCommand]
    private async Task QuickAddAsync()
    {
        var title = QuickTask.Trim();
        if (title.Length == 0 || _project == null) return;
        QuickTask = string.Empty;
        await Guard(() => _ctx.Store.AddTaskAsync(_project.Id, new TaskDraft(title, null, TaskItemStatus.ToDo, ProjectPriority.Normal, null, null, string.Empty, null)));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task NewTaskAsync(TaskRowViewModel? parent)
    {
        if (_project == null) return;
        var top = _project.Tasks.Where(t => t.ParentTaskId == null).ToList();
        var editor = new TaskEditorViewModel(Loc, null, top, parent?.IsSubtask == false ? parent.Id : null);
        if (!_ctx.Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        await Guard(() => _ctx.Store.AddTaskAsync(_project.Id, draft));
        SelectedTab = WorkspaceTab.Tasks;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task EditTaskAsync(TaskRowViewModel? row)
    {
        row ??= SelectedTask;
        if (row == null || _project == null) return;
        var top = _project.Tasks.Where(t => t.ParentTaskId == null).ToList();
        var editor = new TaskEditorViewModel(Loc, row.Task, top, null);
        if (!_ctx.Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        await Guard(() => _ctx.Store.UpdateTaskAsync(row.Id, draft));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ToggleTaskAsync(TaskRowViewModel? row)
    {
        if (row == null) return;
        await Guard(() => _ctx.Store.SetTaskStatusAsync(row.Id, ProjectRules.Toggle(row.Task.Status)));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SetTaskStatusAsync(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } args || args[0] is not TaskRowViewModel row || !Enum.TryParse<TaskItemStatus>(args[1]?.ToString(), out var status)) return;
        await Guard(() => _ctx.Store.SetTaskStatusAsync(row.Id, status));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(TaskRowViewModel? row)
    {
        row ??= SelectedTask;
        if (row == null) return;
        var message = row.SubtaskCount > 0 ? Loc.Format("Task_ConfirmDeleteWithSubtasks", row.Title, row.SubtaskCount) : Loc.Format("Task_ConfirmDelete", row.Title);
        if (!_ctx.Dialogs.Confirm(message, Loc["Proj_Delete"])) return;
        await Guard(() => _ctx.Store.DeleteTaskAsync(row.Id));
        await RefreshAsync();
    }

    // ── files ────────────────────────────────────────────────

    /// <summary>Drag and drop / picker: adds references (never copies or moves the files).</summary>
    public async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (_project == null) return;
        await _ctx.DocumentActions.AddPathsAsync(paths, _project.Id);
        await RefreshAsync();
        SelectedTab = WorkspaceTab.Files;
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (_project == null) return;
        await _ctx.DocumentActions.PickFilesAsync(_project.Id);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (_project == null) return;
        await _ctx.DocumentActions.AddFolderAsync(_project.Id);
        await RefreshAsync();
    }

    [RelayCommand] private Task OpenFileAsync(FileRowViewModel? f) => (f ?? SelectedFile) is { } target ? _ctx.DocumentActions.OpenAsync(target.Id, target.Path) : Task.CompletedTask;
    [RelayCommand] private void OpenFileWith(FileRowViewModel? f) { if ((f ?? SelectedFile) is { } target) _ctx.DocumentActions.OpenWith(target.Path); }
    [RelayCommand] private void RevealFile(FileRowViewModel? f) { if ((f ?? SelectedFile) is { } target) _ctx.DocumentActions.Reveal(target.Path); }
    [RelayCommand] private void CopyFilePath(FileRowViewModel? f) { if ((f ?? SelectedFile) is { } target) _ctx.DocumentActions.CopyPath(target.Path); }

    [RelayCommand]
    private async Task LocateFileAsync(FileRowViewModel? f)
    {
        f ??= SelectedFile;
        if (f == null) return;
        if (await _ctx.DocumentActions.LocateAsync(f.Id, f.Path)) await ReloadFilesAsync();
    }

    [RelayCommand]
    private async Task ToggleFilePinAsync(FileRowViewModel? f)
    {
        if (f == null || _project == null) return;
        await _ctx.Documents.SetProjectPinnedAsync(f.Id, _project.Id, !f.IsPinned);
        await ReloadFilesAsync();
    }

    [RelayCommand]
    private async Task RemoveFileAsync(FileRowViewModel? f)
    {
        f ??= SelectedFile;
        if (f == null || _project == null) return;
        await _ctx.DocumentActions.RemoveFromProjectAsync(f.Id, _project.Id, f.Name);
        await RefreshAsync();
    }

    // ── links ────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddLinkAsync()
    {
        if (_project == null) return;
        var editor = new LinkEditorViewModel(Loc, null);
        if (!_ctx.Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        await Guard(() => _ctx.Store.AddLinkAsync(_project.Id, draft));
        SelectedTab = WorkspaceTab.Links;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task EditLinkAsync(LinkRowViewModel? link)
    {
        if (link == null) return;
        var editor = new LinkEditorViewModel(Loc, link.Link);
        if (!_ctx.Dialogs.Show(editor) || editor.Draft is not { } draft) return;
        await Guard(() => _ctx.Store.UpdateLinkAsync(link.Id, draft));
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenLink(LinkRowViewModel? link)
    {
        if (link == null) return;
        if (LinkRules.TryNormalize(link.Url, out var url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)) _ctx.Shell.OpenUri(uri);
        else _ctx.Notifications.Show(Loc["Link_ErrUrl"], NotificationKind.Warning);
    }

    [RelayCommand]
    private void CopyLink(LinkRowViewModel? link)
    {
        if (link != null && _ctx.Clipboard.SetText(link.Url)) _ctx.Notifications.Show(Loc["Link_Copied"], NotificationKind.Success);
    }

    [RelayCommand]
    private async Task ToggleLinkPinAsync(LinkRowViewModel? link)
    {
        if (link == null) return;
        await _ctx.Store.SetLinkPinnedAsync(link.Id, !link.IsPinned);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteLinkAsync(LinkRowViewModel? link)
    {
        if (link == null) return;
        if (!_ctx.Dialogs.Confirm(Loc.Format("Link_ConfirmDelete", link.Title), Loc["Proj_Delete"])) return;
        await Guard(() => _ctx.Store.DeleteLinkAsync(link.Id));
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenPinned(object? item)
    {
        switch (item)
        {
            case NoteItemViewModel n: SelectedTab = WorkspaceTab.Notes; Notes.Select(n.Id); break;
            case FileRowViewModel f: _ = OpenFileAsync(f); break;
            case LinkRowViewModel l: OpenLink(l); break;
            case TaskRowViewModel t: SelectedTab = WorkspaceTab.Tasks; SelectedTask = TaskRows.FirstOrDefault(r => r.Id == t.Id); break;
        }
    }

    // ── shortcuts ────────────────────────────────────────────

    public bool OnNew()
    {
        switch (SelectedTab)
        {
            case WorkspaceTab.Notes: _ = Notes.NewAsync(); break;
            case WorkspaceTab.Links: _ = AddLinkAsync(); break;
            case WorkspaceTab.Files: _ = AddFilesAsync(); break;
            default: _ = NewTaskAsync(null); break;
        }
        return true;
    }

    public bool OnSave()
    {
        _ = FlushAsync();
        return true;
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ArgumentException ex)
        {
            _ctx.Notifications.Show(ex.Message, NotificationKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Project change failed");
            _ctx.Notifications.Show(Loc["Proj_ErrSave"], NotificationKind.Warning);
        }
    }
}
