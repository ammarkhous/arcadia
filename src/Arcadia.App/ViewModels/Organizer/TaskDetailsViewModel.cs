using System.Collections.ObjectModel;
using System.ComponentModel;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.ViewModels.Organizer;

public sealed partial class SubtaskItem(int id, string title, bool done) : ObservableObject
{
    public int Id { get; } = id;
    public string Title { get; } = title;
    [ObservableProperty] private bool _isDone = done;
    public override string ToString() => Title;
}

/// <summary>
/// The selected task's properties. Edits are kept until Save (Ctrl+S) or until another task is selected, when they are
/// saved automatically if valid — nothing typed is silently dropped.
/// </summary>
public sealed partial class TaskDetailsViewModel : ObservableObject
{
    private readonly ITaskStore _store;
    private readonly OrganizerContext _ctx;
    private readonly TasksViewModel _page;
    private bool _loading;
    private TaskItem? _task;

    public TaskDetailsViewModel(ITaskStore store, OrganizerContext ctx, TasksViewModel page)
    {
        _store = store;
        _ctx = ctx;
        _page = page;
        var loc = ctx.Loc;
        StatusOptions = Enum.GetValues<TaskItemStatus>().Select(s => new Option<TaskItemStatus>(s, loc["TaskStatus_" + s])).ToList();
        PriorityOptions = Enum.GetValues<ProjectPriority>().Select(p => new Option<ProjectPriority>(p, loc["Priority_" + p])).ToList();
        _status = StatusOptions[0];
        _priority = PriorityOptions[1];
        PropertyChanged += OnAnyChange;
    }

    public IReadOnlyList<Option<TaskItemStatus>> StatusOptions { get; }
    public IReadOnlyList<Option<ProjectPriority>> PriorityOptions { get; }
    public ObservableCollection<SubtaskItem> Subtasks { get; } = [];

    [ObservableProperty] private bool _hasTask;
    [ObservableProperty] private int _taskId;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private Option<TaskItemStatus> _status;
    [ObservableProperty] private Option<ProjectPriority> _priority;
    [ObservableProperty] private string _startText = string.Empty;
    [ObservableProperty] private string _dueText = string.Empty;
    [ObservableProperty] private string _dueTimeText = string.Empty;
    [ObservableProperty] private string _reminderDateText = string.Empty;
    [ObservableProperty] private string _reminderTimeText = string.Empty;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _tags = string.Empty;
    [ObservableProperty] private ContextChip? _context;
    [ObservableProperty] private string? _parentTitle;
    [ObservableProperty] private string _newSubtask = string.Empty;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _stamp = string.Empty;

    public bool CanHaveSubtasks => _task?.ParentTaskId == null;

    private static readonly HashSet<string> Editable =
    [
        nameof(Title), nameof(Description), nameof(Notes), nameof(Status), nameof(Priority), nameof(StartText), nameof(DueText), nameof(DueTimeText),
        nameof(ReminderDateText), nameof(ReminderTimeText), nameof(IsPinned), nameof(Tags), nameof(Context),
    ];

    private void OnAnyChange(object? sender, PropertyChangedEventArgs e)
    {
        if (!_loading && HasTask && e.PropertyName != null && Editable.Contains(e.PropertyName)) IsDirty = true;
    }

    public async Task ShowAsync(TaskListItem? item)
    {
        if (IsDirty) await SaveCoreAsync(quiet: true);
        if (item == null) { Clear(); return; }
        await ShowByIdAsync(item.Id);
    }

    public async Task ShowByIdAsync(int id)
    {
        var task = await _store.GetAsync(id);
        if (task == null) { Clear(); return; }
        _task = task;
        _loading = true;
        try
        {
            var c = _ctx.Loc.Culture;
            TaskId = task.Id;
            Title = task.Title;
            Description = task.Description ?? string.Empty;
            Notes = task.Notes ?? string.Empty;
            Status = StatusOptions.First(o => o.Value == task.Status);
            Priority = PriorityOptions.First(o => o.Value == task.Priority);
            StartText = DateText.Format(task.StartDate, c);
            DueText = DateText.Format(task.DueDate, c);
            DueTimeText = OrgText.TimeInput(task.DueTime, c);
            ReminderDateText = task.ReminderAt is { } r ? DateText.Format(DateOnly.FromDateTime(r), c) : string.Empty;
            ReminderTimeText = task.ReminderAt is { } r2 ? OrgText.TimeInput(TimeOnly.FromDateTime(r2), c) : string.Empty;
            IsPinned = task.IsPinned;
            Tags = task.Tags;
            Context = task.Context is { } ctxRef ? (await ContextChip.ResolveAsync(_ctx, [ctxRef])).GetValueOrDefault(ctxRef) : null;
            ParentTitle = task.ParentTaskId is { } pid ? (await _store.GetAsync(pid))?.Title : null;
            Subtasks.Clear();
            foreach (var s in task.Subtasks) Subtasks.Add(new SubtaskItem(s.Id, s.Title, s.IsDone));
            Stamp = _ctx.Loc.Format("Task_Stamp", task.CreatedAtUtc.ToLocalTime().ToString("g", c), task.UpdatedAtUtc.ToLocalTime().ToString("g", c))
                    + (task.CompletedAtUtc is { } done ? "  ·  " + _ctx.Loc.Format("Task_CompletedAt", done.ToLocalTime().ToString("g", c)) : string.Empty);
            Error = null;
            HasTask = true;
            IsDirty = false;
            OnPropertyChanged(nameof(CanHaveSubtasks));
        }
        finally { _loading = false; }
    }

    private void Clear()
    {
        _task = null;
        _loading = true;
        HasTask = false;
        IsDirty = false;
        Subtasks.Clear();
        Context = null;
        _loading = false;
    }

    /// <summary>Ctrl+S and the Save button.</summary>
    public Task SaveAsync() => SaveCoreAsync(quiet: false);

    [RelayCommand] private Task Save() => SaveCoreAsync(quiet: false);

    /// <summary>Validates the fields; null (with <see cref="Error"/> set) when something is wrong.</summary>
    private TaskItemDraft? BuildDraft()
    {
        var c = _ctx.Loc.Culture;
        var loc = _ctx.Loc;
        Error = null;
        if (_task == null) return null;
        if (string.IsNullOrWhiteSpace(Title)) Error = loc["Task_ErrTitle"];
        else if (!DateText.TryParse(StartText, c, out var start) || !DateText.TryParse(DueText, c, out var due) || !DateText.TryParse(ReminderDateText, c, out var remDate))
            Error = loc["Proj_ErrDate"];
        else if (!OrgText.TryTime(DueTimeText, c, out var dueTime) || !OrgText.TryTime(ReminderTimeText, c, out var remTime))
            Error = loc["Org_ErrTime"];
        else if (dueTime != null && due == null) Error = loc["Task_ErrTimeWithoutDate"];
        else if (start is { } s && due is { } d && d < s) Error = loc["Task_ErrDueBeforeStart"];
        else
        {
            DateTime? reminder = remDate is { } day ? day.ToDateTime(remTime ?? new TimeOnly(9, 0)) : null;
            return new TaskItemDraft(Title.Trim(), Description, Status.Value, Priority.Value, start, due, dueTime, reminder, IsPinned, Notes, Tags, _task.ParentTaskId, Context?.Ref);
        }
        return null;
    }

    private async Task SaveCoreAsync(bool quiet)
    {
        if (!HasTask || _task == null || !IsDirty) return;
        if (BuildDraft() is not { } draft)
        {
            if (quiet) _ctx.Notifications.Show(_ctx.Loc.Format("Task_NotSaved", Title), NotificationKind.Warning);
            return;
        }
        IsSaving = true;
        try
        {
            if (await _ctx.TryAsync(() => _store.UpdateAsync(_task.Id, draft), "Task_ErrSave"))
            {
                IsDirty = false;
                _ctx.Changed(OrganizerChange.Tasks);
                if (!quiet) await _page.LoadAsync(keepSelection: true);
            }
        }
        finally { IsSaving = false; }
    }

    /// <summary>Application exit: saves valid pending edits on a worker thread (the UI thread is waiting).</summary>
    public Task FlushForExit() =>
        IsDirty && _task is { } task && BuildDraft() is { } draft ? Task.Run(() => _store.UpdateAsync(task.Id, draft)) : Task.CompletedTask;

    [RelayCommand]
    private async Task RevertAsync()
    {
        if (_task != null) await ShowByIdAsync(_task.Id);
    }

    [RelayCommand]
    private async Task AddSubtaskAsync()
    {
        if (_task == null || string.IsNullOrWhiteSpace(NewSubtask)) return;
        var p = QuickDateParser.Parse(NewSubtask, _ctx.Now);
        var title = p.Title.Length > 0 ? p.Title : NewSubtask.Trim();
        if (await _ctx.TryAsync(() => _store.CreateAsync(new TaskItemDraft(title, DueDate: p.Date, DueTime: p.Date == null ? null : p.Time, ParentTaskId: _task.Id)), "Task_ErrSave"))
        {
            NewSubtask = string.Empty;
            _ctx.Changed(OrganizerChange.Tasks);
            var dirty = IsDirty;
            if (!dirty) await ShowByIdAsync(_task.Id);
            await _page.LoadAsync(keepSelection: true);
        }
    }

    [RelayCommand]
    private async Task ToggleSubtaskAsync(SubtaskItem? item)
    {
        if (item == null) return;
        if (await _ctx.TryAsync(() => _store.SetStatusAsync(item.Id, item.IsDone ? TaskItemStatus.Completed : TaskItemStatus.ToDo), "Task_ErrSave"))
        {
            _ctx.Changed(OrganizerChange.Tasks);
            await _page.LoadAsync(keepSelection: true);
        }
    }

    [RelayCommand]
    private async Task OpenSubtaskAsync(SubtaskItem? item)
    {
        if (item != null) await _page.ShowTaskAsync(item.Id);
    }

    [RelayCommand]
    private void PickContext()
    {
        var picker = new EntityPickerViewModel(_ctx, [EntityKind.Project, EntityKind.Trip, EntityKind.Game, EntityKind.Diagnostic, EntityKind.Document, EntityKind.Place]);
        if (_ctx.Dialogs.Show(picker) && picker.Selected is { } info)
            Context = new ContextChip(info, OrgText.KindLabel(info.Ref.Kind, _ctx.Loc), _ctx.Navigation);
    }

    [RelayCommand] private void ClearContext() => Context = null;

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_task == null) return;
        if (!_ctx.Dialogs.Confirm(_ctx.Loc.Format(_task.Subtasks.Count > 0 ? "Task_ConfirmDeleteWithSubtasks" : "Task_ConfirmDelete", _task.Title), _ctx.Loc["Confirm_Delete"])) return;
        if (await _ctx.TryAsync(() => _store.DeleteAsync(_task.Id), "Task_ErrSave"))
        {
            Clear();
            _ctx.Changed(OrganizerChange.Tasks);
            await _page.LoadAsync();
        }
    }

    /// <summary>Quick reminder presets for the details panel.</summary>
    [RelayCommand]
    private void RemindIn(string? preset)
    {
        var now = _ctx.Now;
        DateTime at = preset switch
        {
            "hour" => now.AddHours(1),
            "evening" => now.Date.AddHours(now.Hour < 18 ? 18 : 42),
            "tomorrow" => now.Date.AddDays(1).AddHours(9),
            _ => now.AddMinutes(30),
        };
        at = new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute / 5 * 5, 0);
        ReminderDateText = DateText.Format(DateOnly.FromDateTime(at), _ctx.Loc.Culture);
        ReminderTimeText = OrgText.TimeInput(TimeOnly.FromDateTime(at), _ctx.Loc.Culture);
    }

    [RelayCommand]
    private void ClearReminder()
    {
        ReminderDateText = string.Empty;
        ReminderTimeText = string.Empty;
    }
}
