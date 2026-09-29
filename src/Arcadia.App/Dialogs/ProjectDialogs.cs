using Arcadia.Core.Organizer;
using System.Globalization;
using Arcadia.App.Services;
using Arcadia.Core.Documents;
using Arcadia.Core.Localization;
using Arcadia.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.Dialogs;

/// <summary>Dates typed by hand: the UI culture's short date, ISO (2026-10-01), or "today"/"heute"/"اليوم".</summary>
public static class DateText
{
    public static bool TryParse(string? text, CultureInfo culture, out DateOnly? date)
    {
        date = null;
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0) return true; // optional
        if (t.Equals("today", StringComparison.OrdinalIgnoreCase) || t.Equals("heute", StringComparison.OrdinalIgnoreCase) || t == "اليوم")
        {
            date = DateOnly.FromDateTime(DateTime.Today);
            return true;
        }
        if (DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)
            || DateOnly.TryParse(t, culture, DateTimeStyles.None, out iso)
            || DateOnly.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out iso))
        {
            date = iso;
            return true;
        }
        return false;
    }

    public static string Format(DateOnly? date, CultureInfo culture) => date?.ToString("d", culture) ?? string.Empty;
}

public sealed record ChoiceItem(string Key, string Label, string? Tone = null, string? Icon = null)
{
    /// <summary>What screen readers announce for the list item.</summary>
    public override string ToString() => Label;
}

/// <summary>Create or edit a project (name, status, priority, dates, identity, tags, folder).</summary>
public sealed partial class ProjectEditorViewModel : DialogViewModel
{
    public static readonly string[] Accents = ["Blue", "Cyan", "Green", "Amber", "Gold", "Purple", "Red", "Steel"];
    public static readonly string[] Icons = ["Projects", "Documents", "ITTools", "Games", "Travel", "Calendar", "Tasks", "Notes", "Finance", "Trophy", "Camera", "Network", "Terminal", "Monitor", "Library", "Flag", "Star", "Sparkle"];
    public static readonly string[] Worlds = ["mountains-dusk", "lake-sunset", "forest-night", "castle-violet", "mountains-snow", "golden-realm", "space-nebula", "desert-sunset", "lake-storm", "neon-city", "forest-ember", "mountains-storm"];

    private readonly ILocalizer _loc;
    private readonly IDialogService _dialogs;

    public ProjectEditorViewModel(ILocalizer loc, IDialogService dialogs, Project? existing)
    {
        _loc = loc;
        _dialogs = dialogs;
        IsNew = existing == null;
        Title = loc[IsNew ? "Proj_NewTitle" : "Proj_EditTitle"];
        StatusOptions = Enum.GetValues<ProjectStatus>().Select(s => new Option<ProjectStatus>(s, loc[$"ProjStatus_{s}"])).ToList();
        PriorityOptions = Enum.GetValues<ProjectPriority>().Select(p => new Option<ProjectPriority>(p, loc[$"Priority_{p}"])).ToList();
        AccentOptions = Accents.Select(a => new ChoiceItem(a, loc[$"Accent_{a}"], a)).ToList();
        IconOptions = Icons.Select(i => new ChoiceItem(i, i, null, i)).ToList();
        WorldOptions = Worlds.Select((w, i) => new ChoiceItem(w, w)).ToList();

        var p = existing;
        _name = p?.Name ?? string.Empty;
        _description = p?.Description ?? string.Empty;
        _status = StatusOptions.First(o => o.Value == (p?.Status ?? ProjectStatus.Active));
        _priority = PriorityOptions.First(o => o.Value == (p?.Priority ?? ProjectPriority.Normal));
        _startText = DateText.Format(p?.StartDate ?? (IsNew ? DateOnly.FromDateTime(DateTime.Today) : null), loc.Culture);
        _targetText = DateText.Format(p?.TargetDate, loc.Culture);
        _accent = p?.Accent ?? Accents[Random.Shared.Next(Accents.Length)];
        _projectIcon = p?.Icon ?? "Projects";
        _world = p?.ArtworkTheme ?? Worlds[Random.Shared.Next(Worlds.Length)];
        _seed = p?.ArtworkSeed ?? Random.Shared.Next(1, 10_000);
        _tags = p?.Tags ?? string.Empty;
        _folder = p?.FolderPath ?? string.Empty;
    }

    public override string Icon => "Projects";
    public override double DialogWidth => 660;
    public bool IsNew { get; }
    public string ConfirmText => _loc[IsNew ? "Proj_Create" : "Editor_Save"];
    public string DateHint => _loc.Format("Proj_DateHint", new DateOnly(2026, 10, 31).ToString("d", _loc.Culture));
    public IReadOnlyList<Option<ProjectStatus>> StatusOptions { get; }
    public IReadOnlyList<Option<ProjectPriority>> PriorityOptions { get; }
    public IReadOnlyList<ChoiceItem> AccentOptions { get; }
    public IReadOnlyList<ChoiceItem> IconOptions { get; }
    public IReadOnlyList<ChoiceItem> WorldOptions { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private Option<ProjectStatus> _status;
    [ObservableProperty] private Option<ProjectPriority> _priority;
    [ObservableProperty] private string _startText;
    [ObservableProperty] private string _targetText;
    [ObservableProperty] private string _accent;
    [ObservableProperty] private string _projectIcon;
    [ObservableProperty] private string _world;
    [ObservableProperty] private int _seed;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private string _folder;
    [ObservableProperty] private string? _nameError;
    [ObservableProperty] private string? _dateError;
    [ObservableProperty] private string? _folderError;

    [RelayCommand] private void SetAccent(string? key) { if (key != null) Accent = key; }
    [RelayCommand] private void SetIcon(string? key) { if (key != null) ProjectIcon = key; }
    [RelayCommand] private void SetWorld(string? key) { if (key != null) World = key; }

    [RelayCommand]
    private void BrowseFolder()
    {
        var picked = _dialogs.PickFolder(string.IsNullOrWhiteSpace(Folder) ? null : Folder);
        if (picked != null) Folder = picked;
    }

    [RelayCommand] private void ClearFolder() => Folder = string.Empty;

    public ProjectDraft? Draft { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        NameError = string.IsNullOrWhiteSpace(Name) ? _loc["Proj_ErrName"] : Name.Trim().Length > 200 ? _loc["Proj_ErrNameLength"] : null;
        DateError = null;
        bool startOk = DateText.TryParse(StartText, _loc.Culture, out var start);
        bool targetOk = DateText.TryParse(TargetText, _loc.Culture, out var target);
        if (!startOk || !targetOk) DateError = _loc["Proj_ErrDate"];
        else if (start is { } s && target is { } t && t < s) DateError = _loc["Proj_ErrDateOrder"];
        FolderError = null;
        string? folder = null;
        if (!string.IsNullOrWhiteSpace(Folder))
        {
            folder = PathRules.Normalize(Folder);
            if (folder == null) FolderError = _loc["Proj_ErrFolder"];
        }
        if (NameError != null || DateError != null || FolderError != null) return;
        Draft = new ProjectDraft(Name.Trim(), Description, Status.Value, Priority.Value, start, target, Accent, ProjectIcon, World, Tags, folder);
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>Add or edit a task (also used for subtasks).</summary>
public sealed partial class TaskEditorViewModel : DialogViewModel
{
    private readonly ILocalizer _loc;

    public TaskEditorViewModel(ILocalizer loc, TaskItem? existing, IReadOnlyList<TaskItem> topLevelTasks, int? parentId)
    {
        _loc = loc;
        IsNew = existing == null;
        Title = loc[IsNew ? (parentId != null ? "Task_NewSubtaskTitle" : "Task_NewTitle") : "Task_EditTitle"];
        StatusOptions = Enum.GetValues<TaskItemStatus>().Select(s => new Option<TaskItemStatus>(s, loc[$"TaskStatus_{s}"])).ToList();
        PriorityOptions = Enum.GetValues<ProjectPriority>().Select(p => new Option<ProjectPriority>(p, loc[$"Priority_{p}"])).ToList();
        // A task that has subtasks stays top-level (one level only).
        bool canNest = existing == null || existing.Subtasks.Count == 0;
        ParentOptions = [new Option<int?>(null, loc["Task_NoParent"]), .. (canNest ? topLevelTasks.Where(t => t.Id != existing?.Id).Select(t => new Option<int?>(t.Id, t.Title)) : [])];
        var t = existing;
        _taskTitle = t?.Title ?? string.Empty;
        _description = t?.Description ?? string.Empty;
        _status = StatusOptions.First(o => o.Value == (t?.Status ?? TaskItemStatus.ToDo));
        _priority = PriorityOptions.First(o => o.Value == (t?.Priority ?? ProjectPriority.Normal));
        _dueText = DateText.Format(t?.DueDate, loc.Culture);
        _notes = t?.Notes ?? string.Empty;
        _tags = t?.Tags ?? string.Empty;
        _parent = ParentOptions.FirstOrDefault(o => o.Value == (t?.ParentTaskId ?? parentId)) ?? ParentOptions[0];
    }

    public override string Icon => "Tasks";
    public override double DialogWidth => 560;
    public bool IsNew { get; }
    public string ConfirmText => _loc[IsNew ? "Task_Add" : "Editor_Save"];
    public IReadOnlyList<Option<TaskItemStatus>> StatusOptions { get; }
    public IReadOnlyList<Option<ProjectPriority>> PriorityOptions { get; }
    public IReadOnlyList<Option<int?>> ParentOptions { get; }

    [ObservableProperty] private string _taskTitle;
    [ObservableProperty] private string _description;
    [ObservableProperty] private Option<TaskItemStatus> _status;
    [ObservableProperty] private Option<ProjectPriority> _priority;
    [ObservableProperty] private string _dueText;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private Option<int?> _parent;
    [ObservableProperty] private string? _titleError;
    [ObservableProperty] private string? _dueError;

    public TaskDraft? Draft { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        TitleError = string.IsNullOrWhiteSpace(TaskTitle) ? _loc["Task_ErrTitle"] : null;
        DueError = DateText.TryParse(DueText, _loc.Culture, out var due) ? null : _loc["Proj_ErrDate"];
        if (TitleError != null || DueError != null) return;
        Draft = new TaskDraft(TaskTitle.Trim(), Description, Status.Value, Priority.Value, due, Notes, Tags, Parent.Value);
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>Add or edit a saved link. Only well-formed http(s) addresses are accepted; nothing is fetched.</summary>
public sealed partial class LinkEditorViewModel : DialogViewModel
{
    private readonly ILocalizer _loc;

    public LinkEditorViewModel(ILocalizer loc, ProjectLink? existing)
    {
        _loc = loc;
        IsNew = existing == null;
        Title = loc[IsNew ? "Link_NewTitle" : "Link_EditTitle"];
        _linkTitle = existing?.Title ?? string.Empty;
        _url = existing?.Url ?? string.Empty;
        _description = existing?.Description ?? string.Empty;
        _tags = existing?.Tags ?? string.Empty;
    }

    public override string Icon => "External";
    public override double DialogWidth => 540;
    public bool IsNew { get; }
    public string ConfirmText => _loc[IsNew ? "Link_Add" : "Editor_Save"];

    [ObservableProperty] private string _linkTitle;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private string? _urlError;

    public LinkDraft? Draft { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        if (!LinkRules.TryNormalize(Url, out var normal))
        {
            UrlError = _loc["Link_ErrUrl"];
            return;
        }
        Draft = new LinkDraft(LinkTitle.Trim(), normal, Description, Tags);
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}
