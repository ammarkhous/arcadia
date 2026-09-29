using System.Collections.ObjectModel;
using System.Globalization;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Organizer;

public sealed partial class TaskViewOption(TaskView view, string label, string icon) : ObservableObject
{
    public TaskView View { get; } = view;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _count;
    public override string ToString() => Label;
}

/// <summary>A heading inside the task list (By project, and the Upcoming view's days).</summary>
public sealed record TaskGroupHeader(string Title, string? Accent)
{
    public override string ToString() => Title;
}

/// <summary>One row of the mission list.</summary>
public sealed partial class TaskListItem(TaskRow row, ContextChip? context, string dueText, string dueTone, string? subtasks, bool isSubtask) : ObservableObject
{
    public TaskRow Row { get; private set; } = row;
    public int Id => Row.Id;
    public string Title => Row.Title;
    public ContextChip? Context { get; } = context;
    public string DueText { get; } = dueText;
    public string DueTone { get; } = dueTone;
    public string? SubtaskText { get; } = subtasks;
    public bool IsSubtask { get; } = isSubtask;
    public string? PriorityTone => OrgText.PriorityTone(Row.Priority);
    public bool HasReminder => Row.ReminderAt != null && !Row.IsDone;
    public bool IsPinned => Row.IsPinned;
    public bool HasDetails => Row.HasDetails;
    public bool IsCancelled => Row.Status == TaskItemStatus.Cancelled;
    [ObservableProperty] private bool _isDone = row.IsDone;
    public override string ToString() => Title;
}

/// <summary>
/// Tasks: Arcadia's global mission list. Project tasks (Phase 4) appear here with their project as context and stay
/// editable in the project workspace. Quick add understands dates ("Backup server Friday 18:00") without guessing.
/// </summary>
public sealed partial class TasksViewModel : ObservableObject, IActivatable, IShortcutTarget, IRecipient<OrganizerChangedMessage>
{
    private readonly ITaskStore _store;
    private readonly IProjectStore _projects;
    private CancellationTokenSource? _loadCts;
    private bool _loaded;

    public TasksViewModel(ITaskStore store, IProjectStore projects, OrganizerContext ctx)
    {
        _store = store;
        _projects = projects;
        Ctx = ctx;
        var loc = ctx.Loc;
        Views =
        [
            new(TaskView.Today, loc["TaskView_Today"], "Home"),
            new(TaskView.Upcoming, loc["TaskView_Upcoming"], "Calendar"),
            new(TaskView.Overdue, loc["TaskView_Overdue"], "Warning"),
            new(TaskView.Pinned, loc["TaskView_Pinned"], "Pin"),
            new(TaskView.All, loc["TaskView_All"], "Tasks"),
            new(TaskView.ByProject, loc["TaskView_ByProject"], "Projects"),
            new(TaskView.Completed, loc["TaskView_Completed"], "Check"),
        ];
        _view = Enum.TryParse<TaskView>(ctx.Prefs.TaskView, true, out var v) ? v : TaskView.Today;
        foreach (var o in Views) o.IsSelected = o.View == _view;
        SortOptions = Enum.GetValues<TaskSort>().Select(s => new Option<TaskSort>(s, loc["TaskSort_" + s])).ToList();
        _sort = SortOptions[0];
        PriorityFilters = [new Option<ProjectPriority?>(null, loc["Task_AnyPriority"]), .. Enum.GetValues<ProjectPriority>().Reverse().Select(p => new Option<ProjectPriority?>(p, loc["Priority_" + p]))];
        _priorityFilter = PriorityFilters[0];
        Details = new TaskDetailsViewModel(store, ctx, this);
        ctx.Messenger.Register(this);
    }

    public OrganizerContext Ctx { get; }
    public IReadOnlyList<TaskViewOption> Views { get; }
    public IReadOnlyList<Option<TaskSort>> SortOptions { get; }
    public IReadOnlyList<Option<ProjectPriority?>> PriorityFilters { get; }
    public ObservableCollection<object> Items { get; } = [];
    public TaskDetailsViewModel Details { get; }

    [ObservableProperty] private TaskView _view;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private Option<TaskSort> _sort;
    [ObservableProperty] private Option<ProjectPriority?> _priorityFilter;
    [ObservableProperty] private object? _selected;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyText = string.Empty;
    [ObservableProperty] private string _quickText = string.Empty;
    [ObservableProperty] private string? _quickHint;
    [ObservableProperty] private bool _quickAmbiguous;
    [ObservableProperty] private string _heading = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    /// <summary>A context new quick-add tasks get (set when the list is filtered to one project/trip/game).</summary>
    [ObservableProperty] private ContextChip? _scope;

    public string ViewTitle => Views.First(v => v.View == View).Label;

    public async Task ActivateAsync()
    {
        await Ctx.Ready;
        if (!_loaded) { _loaded = true; await LoadAsync(); }
        else await LoadAsync(keepSelection: true);
    }

    public void Receive(OrganizerChangedMessage message)
    {
        if (_loaded && (message.What & OrganizerChange.Tasks) != 0 && !Details.IsSaving) _ = LoadAsync(keepSelection: true);
    }

    private bool _quietView;

    partial void OnViewChanged(TaskView value)
    {
        foreach (var o in Views) o.IsSelected = o.View == value;
        Ctx.Prefs.TaskView = value.ToString();
        Ctx.SavePrefs();
        OnPropertyChanged(nameof(ViewTitle));
        if (!_quietView) _ = LoadAsync();
    }

    partial void OnFilterChanged(string value) => _ = LoadAsync(keepSelection: true);
    partial void OnSortChanged(Option<TaskSort> value) => _ = LoadAsync(keepSelection: true);
    partial void OnPriorityFilterChanged(Option<ProjectPriority?> value) => _ = LoadAsync(keepSelection: true);

    partial void OnSelectedChanged(object? oldValue, object? newValue)
    {
        _ = Details.ShowAsync(newValue as TaskListItem);
    }

    partial void OnQuickTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { QuickHint = null; QuickAmbiguous = false; return; }
        var p = QuickDateParser.Parse(value, Ctx.Now);
        QuickAmbiguous = p.Ambiguous;
        QuickHint = p.Ambiguous ? Ctx.Loc["Task_DateUnclear"]
            : p.Date is { } d ? "→ " + OrgText.Due(d, p.Time, Ctx.Now, Ctx.Loc) + (p.Title.Length > 0 ? "   “" + p.Title + "”" : string.Empty)
            : null;
    }

    [RelayCommand] private void SelectView(TaskViewOption? o) { if (o != null) View = o.View; }

    /// <summary>Shows tasks about one record (from a project, trip or game page).</summary>
    public async Task ShowContextAsync(EntityRef context)
    {
        var chips = await ContextChip.ResolveAsync(Ctx, [context]);
        Scope = chips.GetValueOrDefault(context);
        if (View != TaskView.All) View = TaskView.All; else await LoadAsync();
    }

    [RelayCommand]
    private async Task ClearScopeAsync()
    {
        Scope = null;
        await LoadAsync();
    }

    public async Task LoadAsync(bool keepSelection = false)
    {
        await Ctx.Ready;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var keepId = keepSelection ? (Selected as TaskListItem)?.Id : null;
        try
        {
            var now = Ctx.Now;
            var query = new TaskQuery(View, Filter, Context: Scope?.Ref, Priority: PriorityFilter.Value, Sort: Sort.Value);
            var rows = await _store.QueryAsync(query, now, cts.Token);
            // All / By project show subtasks under their parent
            var expanded = new List<(TaskRow Row, bool Sub)>();
            if (View is TaskView.All or TaskView.ByProject && string.IsNullOrWhiteSpace(Filter))
            {
                var parents = rows.Where(r => r.SubtaskCount > 0).Select(r => r.Id).ToList();
                var subs = parents.Count == 0 ? [] : await _store.GetSubtasksForAsync(parents, cts.Token);
                var byParent = subs.GroupBy(s => s.ParentTaskId!.Value).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var r in rows)
                {
                    expanded.Add((r, false));
                    if (byParent.TryGetValue(r.Id, out var list)) expanded.AddRange(list.Select(s => (s, true)));
                }
            }
            else expanded.AddRange(rows.Select(r => (r, r.ParentTaskId != null)));

            var chips = await ContextChip.ResolveAsync(Ctx, expanded.Select(x => x.Row.Context).OfType<EntityRef>());
            if (cts.IsCancellationRequested) return;
            var items = new List<object>();
            string? group = null;
            foreach (var (row, sub) in expanded)
            {
                var chip = row.Context is { } c ? chips.GetValueOrDefault(c) : null;
                if (View == TaskView.ByProject && !sub)
                {
                    var name = chip?.Name ?? Ctx.Loc["Task_NoProject"];
                    if (name != group) { items.Add(new TaskGroupHeader(name, chip?.Accent)); group = name; }
                }
                else if (View == TaskView.Upcoming && row.DueDate is { } due)
                {
                    var day = OrgText.Day(due, Ctx.Today, Ctx.Loc);
                    if (day != group) { items.Add(new TaskGroupHeader(day, null)); group = day; }
                }
                items.Add(new TaskListItem(row, View == TaskView.ByProject ? null : chip, OrgText.Due(row.DueDate, row.DueTime, now, Ctx.Loc),
                    OrgText.DueTone(row.Status, row.DueDate, row.DueTime, now), row.SubtaskCount > 0 ? $"{row.SubtasksDone}/{row.SubtaskCount}" : null, sub));
            }
            Items.Clear();
            foreach (var i in items) Items.Add(i);
            IsEmpty = Items.Count == 0;
            EmptyText = Ctx.Loc[string.IsNullOrWhiteSpace(Filter) ? "TaskEmpty_" + View : "Task_NoMatch"];
            Selected = keepId is { } id ? Items.OfType<TaskListItem>().FirstOrDefault(t => t.Id == id) : null;
            await RefreshCountsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Ctx.Logger.LogError(ex, "Loading tasks failed");
            Ctx.Notifications.Show(Ctx.Loc["Task_ErrLoad"], NotificationKind.Warning);
        }
    }

    public async Task RefreshCountsAsync()
    {
        var c = await _store.GetCountsAsync(Ctx.Now);
        string? N(int n) => n > 0 ? n.ToString(Ctx.Loc.Culture) : null;
        foreach (var o in Views)
            o.Count = o.View switch { TaskView.Today => N(c.Today + c.Overdue), TaskView.Overdue => N(c.Overdue), TaskView.Upcoming => N(c.Upcoming), TaskView.Pinned => N(c.Pinned), TaskView.All => N(c.Open), _ => null };
        Heading = ViewTitle;
        Summary = c.Overdue > 0 ? Ctx.Loc.Format("Task_SummaryOverdue", c.Today, c.Overdue) : Ctx.Loc.Format("Task_Summary", c.Today, c.CompletedToday);
    }

    // ───────────── commands ─────────────

    [RelayCommand]
    private async Task QuickAddAsync()
    {
        var text = QuickText.Trim();
        if (text.Length == 0) return;
        var p = QuickDateParser.Parse(text, Ctx.Now);
        var title = p.Title.Length > 0 ? p.Title : text;
        var due = p.Date ?? (View == TaskView.Today ? Ctx.Today : null);
        TaskItem? created = null;
        if (await Ctx.TryAsync(async () => created = await _store.CreateAsync(new TaskItemDraft(title, DueDate: due, DueTime: due == null ? null : p.Time,
                IsPinned: View == TaskView.Pinned, Context: Scope?.Ref)), "Task_ErrSave"))
        {
            QuickText = string.Empty;
            if (p.Ambiguous) Ctx.Notifications.Show(Ctx.Loc["Task_DateUnclearSaved"], NotificationKind.Info);
            Ctx.Changed(OrganizerChange.Tasks);
            await LoadAsync();
            Selected = Items.OfType<TaskListItem>().FirstOrDefault(t => t.Id == created!.Id);
        }
    }

    [RelayCommand]
    private async Task ToggleDoneAsync(TaskListItem? item)
    {
        if (item == null) return;
        var done = !item.Row.IsDone;
        if (await Ctx.TryAsync(() => _store.SetStatusAsync(item.Id, done ? TaskItemStatus.Completed : TaskItemStatus.ToDo), "Task_ErrSave"))
        {
            item.IsDone = done;
            Ctx.Changed(OrganizerChange.Tasks);
            await LoadAsync(keepSelection: true);
        }
        else item.IsDone = item.Row.IsDone;
    }

    [RelayCommand]
    private async Task TogglePinAsync(TaskListItem? item)
    {
        if (item == null) return;
        if (await Ctx.TryAsync(() => _store.SetPinnedAsync(item.Id, !item.IsPinned), "Task_ErrSave")) { Ctx.Changed(OrganizerChange.Tasks); await LoadAsync(keepSelection: true); }
    }

    [RelayCommand]
    private async Task DeleteAsync(TaskListItem? item)
    {
        item ??= Selected as TaskListItem;
        if (item == null) return;
        var message = item.SubtaskText != null ? Ctx.Loc.Format("Task_ConfirmDeleteWithSubtasks", item.Title) : Ctx.Loc.Format("Task_ConfirmDelete", item.Title);
        if (!Ctx.Dialogs.Confirm(message, Ctx.Loc["Confirm_Delete"])) return;
        if (await Ctx.TryAsync(() => _store.DeleteAsync(item.Id), "Task_ErrSave"))
        {
            Ctx.Changed(OrganizerChange.Tasks);
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task SetDueTodayAsync(TaskListItem? item) => await SetDueAsync(item, Ctx.Today);

    [RelayCommand]
    private async Task SetDueTomorrowAsync(TaskListItem? item) => await SetDueAsync(item, Ctx.Today.AddDays(1));

    [RelayCommand]
    private async Task ClearDueAsync(TaskListItem? item) => await SetDueAsync(item, null);

    private async Task SetDueAsync(TaskListItem? item, DateOnly? due)
    {
        if (item == null) return;
        if (await Ctx.TryAsync(() => _store.SetDueAsync(item.Id, due, due == null ? null : item.Row.DueTime), "Task_ErrSave"))
        {
            Ctx.Changed(OrganizerChange.Tasks);
            await LoadAsync(keepSelection: true);
        }
    }

    /// <summary>Opens the list on one task (search result, reminder, calendar).</summary>
    public async Task ShowTaskAsync(int id)
    {
        var task = await _store.GetAsync(id);
        if (task == null) { Ctx.Notifications.Show(Ctx.Loc["Org_ErrGone"], NotificationKind.Warning); return; }
        Scope = null;
        Filter = string.Empty;
        var view = task.IsDone ? TaskView.Completed : TaskView.All;
        if (View != view)
        {
            _quietView = true;
            try { View = view; } finally { _quietView = false; }
        }
        _loaded = true;
        await LoadAsync();
        Selected = Items.OfType<TaskListItem>().FirstOrDefault(t => t.Id == id);
        if (Selected == null) await Details.ShowByIdAsync(id); // e.g. a subtask outside the page
    }

    public bool OnNew()
    {
        NewTaskRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool OnSave()
    {
        _ = Details.SaveAsync();
        return true;
    }

    /// <summary>The view focuses the quick-add box.</summary>
    public event EventHandler? NewTaskRequested;
}
