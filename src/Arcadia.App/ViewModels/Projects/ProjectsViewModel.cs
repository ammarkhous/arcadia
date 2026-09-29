using Arcadia.Core.Organizer;
using System.Collections.ObjectModel;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Localization;
using Arcadia.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Projects;

/// <summary>Presentation helpers shared by the Projects screens.</summary>
public static class ProjectText
{
    public static string StatusTone(ProjectStatus s) => s switch
    {
        ProjectStatus.Active => "Green",
        ProjectStatus.Planning => "Blue",
        ProjectStatus.OnHold => "Amber",
        ProjectStatus.Completed => "Cyan",
        _ => "Steel",
    };

    public static string PriorityTone(ProjectPriority p) => p switch
    {
        ProjectPriority.Critical => "Red",
        ProjectPriority.High => "Amber",
        ProjectPriority.Normal => "Blue",
        _ => "Steel",
    };

    public static string TaskTone(TaskItemStatus s) => s switch
    {
        TaskItemStatus.InProgress => "Cyan",
        TaskItemStatus.Blocked => "Red",
        TaskItemStatus.Completed => "Green",
        TaskItemStatus.Cancelled => "Steel",
        _ => "Blue",
    };

    /// <summary>"12 days left", "Due today", "3 days overdue" or "No target date".</summary>
    public static string Due(DateOnly? target, DateOnly today, bool done, ILocalizer loc)
    {
        if (target is not { } t) return loc["Proj_NoTarget"];
        if (done) return t.ToString("d", loc.Culture);
        var days = t.DayNumber - today.DayNumber;
        return days switch
        {
            0 => loc["Proj_DueToday"],
            1 => loc["Proj_DueTomorrow"],
            > 1 => loc.Format("Proj_DaysLeft", days),
            -1 => loc["Proj_OneDayOverdue"],
            _ => loc.Format("Proj_DaysOverdue", -days),
        };
    }

    public static string DueTone(DateOnly? target, DateOnly today, bool done) =>
        done || target is not { } t ? "Steel" : t < today ? "Red" : t.DayNumber - today.DayNumber <= 3 ? "Amber" : "Steel";
}

/// <summary>A project on the landing screen.</summary>
public sealed class ProjectCardViewModel(ProjectSummary s, ILocalizer loc, DateOnly today)
{
    public ProjectSummary Summary { get; } = s;
    public override string ToString() => Summary.Name;
    public int Id => Summary.Id;
    public string Name => Summary.Name;
    public string? Description => Summary.Description;
    public string Accent => Summary.Accent;
    public string Icon => Summary.Icon;
    public string Theme => Summary.ArtworkTheme;
    public int Seed => Summary.ArtworkSeed;
    public bool IsPinned => Summary.IsPinned;
    public bool IsArchived => Summary.Status == ProjectStatus.Archived;
    public int Progress => Summary.Progress;
    public string ProgressText => loc.Format("It_Percent", Summary.Progress);
    public string StatusText => loc[$"ProjStatus_{Summary.Status}"];
    public string StatusTone => ProjectText.StatusTone(Summary.Status);
    public string PriorityText => loc[$"Priority_{Summary.Priority}"];
    public string PriorityTone => ProjectText.PriorityTone(Summary.Priority);
    public bool IsDone => Summary.Status is ProjectStatus.Completed or ProjectStatus.Archived;
    public string DueText => ProjectText.Due(Summary.TargetDate, today, IsDone, loc);
    public string DueTone => ProjectText.DueTone(Summary.TargetDate, today, IsDone);
    public string TasksText => Summary.TotalTasks == 0 ? loc["Proj_NoTasks"] : loc.Format("Proj_TasksOpen", Summary.OpenTasks, Summary.TotalTasks);
    public IReadOnlyList<string> Tags => Core.Projects.TagText.Split(Summary.Tags);
    public string UpdatedText => Core.Formatting.RelativeTime.Format(Summary.UpdatedAtUtc, DateTime.UtcNow, loc);
    public string? OpenedText => Summary.LastOpenedAtUtc is { } o ? Core.Formatting.RelativeTime.Format(o, DateTime.UtcNow, loc) : null;
}

public sealed record DeadlineRow(int ProjectId, int? TaskId, string Title, string ProjectName, string Accent, string DueText, string DueTone, string DateText, string Icon);

public sealed record StatTile(string Key, string Label, string Value, string Tone, string Icon)
{
    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>Content search hit (task, note, link) shown under the project list while searching.</summary>
public sealed record ProjectSearchHit(SearchResult Result, string KindText, string Icon, string ProjectName);

public sealed partial class ProjectFilter(string key, ProjectStatus? status) : ObservableObject
{
    public string Key { get; } = key;
    public ProjectStatus? Status { get; } = status;
    public override string ToString() => Label;
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The Projects module: a landing screen (pinned "worlds", active projects, deadlines, recent) and a
/// workspace per project. Everything is stored locally in SQLite.
/// </summary>
public sealed partial class ProjectsViewModel : ObservableObject, IActivatable, IDeactivatable, IShortcutTarget,
    IRecipient<ProjectsChangedMessage>, IRecipient<DocumentsChangedMessage>
{
    private readonly IProjectStore _store;
    private readonly IGlobalSearchService _searchService;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILocalizer _loc;
    private readonly ISettingsService _settings;
    private readonly StartupState _startup;
    private readonly Func<ProjectWorkspaceViewModel> _workspaceFactory;
    private readonly ILogger<ProjectsViewModel> _logger;
    private IReadOnlyList<ProjectSummary> _all = [];
    private bool _stale = true;
    private CancellationTokenSource? _searchCts;

    public ProjectsViewModel(IProjectStore store, IGlobalSearchService search, IDialogService dialogs, INotificationService notifications, ILocalizer loc,
        ISettingsService settings, StartupState startup, IMessenger messenger, Func<ProjectWorkspaceViewModel> workspaceFactory, ILogger<ProjectsViewModel> logger)
    {
        _store = store;
        _searchService = search;
        _dialogs = dialogs;
        _notifications = notifications;
        _loc = loc;
        _settings = settings;
        _startup = startup;
        _workspaceFactory = workspaceFactory;
        _logger = logger;
        Filters =
        [
            new("all", null) { IsSelected = true },
            new("planning", ProjectStatus.Planning),
            new("active", ProjectStatus.Active),
            new("onhold", ProjectStatus.OnHold),
            new("completed", ProjectStatus.Completed),
            new("archived", ProjectStatus.Archived),
        ];
        RefreshLabels();
        messenger.RegisterAll(this);
        loc.CultureChanged += (_, _) => { RefreshLabels(); Build(); };
    }

    public ObservableCollection<ProjectFilter> Filters { get; }
    public ObservableCollection<ProjectSearchHit> ContentHits { get; } = [];

    [ObservableProperty] private IReadOnlyList<StatTile> _stats = [];
    [ObservableProperty] private IReadOnlyList<ProjectCardViewModel> _pinned = [];
    [ObservableProperty] private IReadOnlyList<ProjectCardViewModel> _projects = [];
    [ObservableProperty] private string? _moreProjectsText;
    private const int PageSize = 40;
    private int _shownLimit = PageSize;

    [RelayCommand]
    private void ShowAllProjects()
    {
        _shownLimit = int.MaxValue;
        Build();
    }
    [ObservableProperty] private IReadOnlyList<ProjectCardViewModel> _recentlyOpened = [];
    [ObservableProperty] private IReadOnlyList<ProjectCardViewModel> _recentlyUpdated = [];
    [ObservableProperty] private IReadOnlyList<DeadlineRow> _deadlines = [];
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string _listTitle = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasNoMatches;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private ProjectWorkspaceViewModel? _workspace;

    partial void OnSearchChanged(string value)
    {
        Build();
        _ = SearchContentAsync(value);
    }

    public async Task ActivateAsync()
    {
        if (_stale) await LoadAsync();
        if (Workspace != null) await Workspace.ActivateAsync();
    }

    public void Deactivate() => Workspace?.Deactivate();

    public void Receive(ProjectsChangedMessage message)
    {
        _stale = true;
        _ = LoadAsync(silent: true);
    }

    public void Receive(DocumentsChangedMessage message)
    {
        if (Workspace != null && (message.ProjectId == null || message.ProjectId == Workspace.ProjectId)) _ = Workspace.ReloadFilesAsync();
    }

    [RelayCommand]
    private async Task LoadAsync(bool silent = false)
    {
        if (!silent) IsLoading = true;
        try
        {
            await _startup.DatabaseReady;
            var today = DateOnly.FromDateTime(DateTime.Today);
            _all = await _store.ListAsync();
            var stats = await _store.GetStatsAsync(today, 14);
            var deadlines = await _store.GetDeadlinesAsync(today, 14);
            _stale = false;
            Stats =
            [
                new("active", _loc["ProjStat_Active"], stats.Active.ToString(_loc.Culture), "Green", "Projects"),
                new("planning", _loc["ProjStat_Planning"], stats.Planning.ToString(_loc.Culture), "Blue", "Flag"),
                new("onhold", _loc["ProjStat_OnHold"], stats.OnHold.ToString(_loc.Culture), "Amber", "Hourglass"),
                new("completed", _loc["ProjStat_Completed"], stats.Completed.ToString(_loc.Culture), "Cyan", "Check"),
                new("archived", _loc["ProjStat_Archived"], stats.Archived.ToString(_loc.Culture), "Steel", "Library"),
                new("tasks", _loc["ProjStat_Tasks"], stats.TasksRemaining.ToString(_loc.Culture), "Purple", "Tasks"),
                new("deadlines", _loc["ProjStat_Deadlines"], stats.UpcomingDeadlines.ToString(_loc.Culture), stats.UpcomingDeadlines > 0 ? "Red" : "Steel", "Calendar"),
            ];
            Deadlines = deadlines.Take(8).Select(d => new DeadlineRow(d.ProjectId, d.TaskId, d.Title, d.ProjectName, d.Accent,
                ProjectText.Due(d.Due, today, false, _loc), ProjectText.DueTone(d.Due, today, false), d.Due.ToString("d", _loc.Culture), d.TaskId == null ? "Flag" : "Tasks")).ToList();
            Build();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading projects failed");
            _notifications.Show(_loc["Proj_ErrLoad"], NotificationKind.Warning);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Build()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var cards = _all.Select(s => new ProjectCardViewModel(s, _loc, today)).ToList();
        foreach (var f in Filters) f.Count = f.Status is { } st ? cards.Count(c => c.Summary.Status == st) : cards.Count(c => !c.IsArchived);

        Pinned = cards.Where(c => c.IsPinned && !c.IsArchived).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        RecentlyOpened = cards.Where(c => c.Summary.LastOpenedAtUtc != null && !c.IsArchived).OrderByDescending(c => c.Summary.LastOpenedAtUtc).Take(5).ToList();
        RecentlyUpdated = cards.Where(c => !c.IsArchived).OrderByDescending(c => c.Summary.UpdatedAtUtc).Take(5).ToList();

        var filter = Filters.First(f => f.IsSelected);
        IEnumerable<ProjectCardViewModel> list = filter.Status is { } status ? cards.Where(c => c.Summary.Status == status) : cards.Where(c => !c.IsArchived);
        var term = Search.Trim();
        if (term.Length > 0)
            list = cards.Where(c => c.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                                    || (c.Description?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)
                                    || c.Summary.Tags.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        // Active work first, then by deadline, then name.
        var ordered = list.OrderBy(c => c.Summary.Status switch { ProjectStatus.Active => 0, ProjectStatus.Planning => 1, ProjectStatus.OnHold => 2, ProjectStatus.Completed => 3, _ => 4 })
            .ThenBy(c => c.Summary.TargetDate ?? DateOnly.MaxValue).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // hundreds of projects: the first rows at once, the rest on request (the list is not virtualized)
        Projects = ordered.Count > _shownLimit ? ordered.Take(_shownLimit).ToList() : ordered;
        MoreProjectsText = ordered.Count > _shownLimit ? _loc.Format("Proj_ShowAll", ordered.Count) : null;
        ListTitle = term.Length > 0 ? _loc.Format("Proj_SearchResults", term) : filter.Status == null ? _loc["Proj_AllProjects"] : filter.Label;
        IsEmpty = _all.Count == 0;
        HasNoMatches = !IsEmpty && Projects.Count == 0 && ContentHits.Count == 0;
        Subtitle = _loc.Format("Proj_Subtitle", cards.Count(c => !c.IsArchived), cards.Sum(c => c.Summary.OpenTasks));
    }

    /// <summary>Tasks, notes and links matching the search (FTS), shown under the project matches.</summary>
    private async Task SearchContentAsync(string text)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, cts.Token);
            ContentHits.Clear();
            if (text.Trim().Length < 2) { Build(); return; }
            var kinds = new HashSet<SearchResultKind> { SearchResultKind.Task, SearchResultKind.Note, SearchResultKind.Link, SearchResultKind.Document };
            var results = await _searchService.SearchAsync(new SearchRequest(text, kinds, 20), cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var r in results)
            {
                var project = r.ProjectId is { } pid ? _all.FirstOrDefault(p => p.Id == pid)?.Name ?? string.Empty : string.Empty;
                ContentHits.Add(new ProjectSearchHit(r, _loc[$"SearchKind_{r.Kind}"], SearchIcons.For(r.Kind), project));
            }
            Build();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Project search failed");
        }
    }

    [RelayCommand]
    private void SelectFilter(ProjectFilter? filter)
    {
        if (filter == null) return;
        foreach (var f in Filters) f.IsSelected = ReferenceEquals(f, filter);
        Build();
    }

    // ── actions ─────────────────────────────────────────────

    [RelayCommand]
    public async Task NewProjectAsync()
    {
        var editor = new ProjectEditorViewModel(_loc, _dialogs, null);
        if (!_dialogs.Show(editor) || editor.Draft is not { } draft) return;
        try
        {
            var project = await _store.CreateAsync(draft);
            _notifications.Show(_loc.Format("Proj_Created", project.Name), NotificationKind.Success);
            await LoadAsync(silent: true);
            await OpenProjectAsync(project.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating a project failed");
            _notifications.Show(_loc["Proj_ErrSave"], NotificationKind.Warning);
        }
    }

    [RelayCommand]
    public async Task OpenProjectAsync(int id)
    {
        if (Workspace != null && Workspace.ProjectId != id) await Workspace.FlushAsync();
        if (Workspace?.ProjectId == id) return;
        var workspace = _workspaceFactory();
        if (!await workspace.LoadAsync(id)) { _notifications.Show(_loc["Proj_ErrMissing"], NotificationKind.Warning); return; }
        workspace.CloseRequested += (_, _) => _ = CloseWorkspaceAsync();
        Workspace = workspace;
        _ = _store.MarkOpenedAsync(id);
    }

    [RelayCommand]
    private Task OpenCardAsync(ProjectCardViewModel? card) => card == null ? Task.CompletedTask : OpenProjectAsync(card.Id);

    [RelayCommand]
    private async Task OpenDeadlineAsync(DeadlineRow? row)
    {
        if (row == null) return;
        await OpenProjectAsync(row.ProjectId);
        if (row.TaskId != null && Workspace != null) Workspace.SelectedTab = WorkspaceTab.Tasks;
    }

    [RelayCommand]
    private async Task OpenHitAsync(ProjectSearchHit? hit)
    {
        if (hit == null) return;
        await OpenSearchResultAsync(hit.Result);
    }

    /// <summary>Opens the project that owns a search result, on the right tab.</summary>
    public async Task OpenSearchResultAsync(SearchResult r)
    {
        if (r.Kind == SearchResultKind.Document) return; // handled by Documents
        await OpenProjectAsync(r.ProjectId ?? r.Id);
        if (Workspace == null) return;
        switch (r.Kind)
        {
            case SearchResultKind.Task: Workspace.SelectedTab = WorkspaceTab.Tasks; break;
            case SearchResultKind.Note: Workspace.SelectedTab = WorkspaceTab.Notes; Workspace.Notes.Select(r.Id); break;
            case SearchResultKind.Link: Workspace.SelectedTab = WorkspaceTab.Links; break;
        }
    }

    public async Task CloseWorkspaceAsync()
    {
        if (Workspace == null) return;
        await Workspace.FlushAsync();
        Workspace.Deactivate();
        Workspace = null;
        await LoadAsync(silent: true);
    }

    [RelayCommand]
    private async Task TogglePinAsync(ProjectCardViewModel? card)
    {
        if (card == null) return;
        await _store.SetPinnedAsync(card.Id, !card.IsPinned);
        await LoadAsync(silent: true);
    }

    [RelayCommand]
    private async Task EditAsync(ProjectCardViewModel? card)
    {
        if (card == null) return;
        var project = await _store.GetAsync(card.Id);
        if (project == null) return;
        var editor = new ProjectEditorViewModel(_loc, _dialogs, project);
        if (!_dialogs.Show(editor) || editor.Draft is not { } draft) return;
        await _store.UpdateAsync(card.Id, draft);
        await LoadAsync(silent: true);
    }

    [RelayCommand]
    private async Task ArchiveAsync(ProjectCardViewModel? card)
    {
        if (card == null) return;
        if (card.IsArchived)
        {
            await _store.RestoreAsync(card.Id);
            _notifications.Show(_loc.Format("Proj_Restored", card.Name), NotificationKind.Success);
        }
        else
        {
            if (!_dialogs.Confirm(_loc.Format("Proj_ConfirmArchive", card.Name), _loc["Proj_Archive"])) return;
            await _store.ArchiveAsync(card.Id);
            _notifications.Show(_loc.Format("Proj_Archived", card.Name), NotificationKind.Success);
        }
        await LoadAsync(silent: true);
    }

    [RelayCommand]
    private async Task DeleteAsync(ProjectCardViewModel? card)
    {
        if (card == null) return;
        if (!_dialogs.Confirm(_loc.Format("Proj_ConfirmDelete", card.Name), _loc["Proj_DeletePermanently"])) return;
        await _store.DeleteAsync(card.Id);
        _notifications.Show(_loc.Format("Proj_Deleted", card.Name), NotificationKind.Success);
        await LoadAsync(silent: true);
    }

    // ── shortcuts ───────────────────────────────────────────

    public bool OnNew()
    {
        if (Workspace != null) return Workspace.OnNew();
        _ = NewProjectAsync();
        return true;
    }

    public bool OnSave() => Workspace?.OnSave() ?? false;

    /// <summary>Called when Arcadia closes: writes any unsaved note text synchronously-safe.</summary>
    public Task FlushAsync() => Workspace?.FlushAsync() ?? Task.CompletedTask;

    public IReadOnlyList<ProjectSummary> AllProjects => _all;

    public async Task<IReadOnlyList<ProjectSummary>> GetProjectsForPickerAsync()
    {
        if (_stale || _all.Count == 0) _all = await _store.ListAsync();
        return _all;
    }

    private void RefreshLabels()
    {
        foreach (var f in Filters) f.Label = f.Status is { } s ? _loc[$"ProjStatus_{s}"] : _loc["Proj_FilterAll"];
    }
}

public static class SearchIcons
{
    public static string For(SearchResultKind kind) => kind switch
    {
        SearchResultKind.Project => "Projects",
        SearchResultKind.Task => "Tasks",
        SearchResultKind.Note => "Notes",
        SearchResultKind.Link => "External",
        SearchResultKind.Document => "Documents",
        SearchResultKind.Game => "Games",
        SearchResultKind.Trip => "Suitcase",
        SearchResultKind.Place => "MapPin",
        SearchResultKind.Journal => "Journal",
        SearchResultKind.Country => "Flag",
        SearchResultKind.Event => "Calendar",
        SearchResultKind.Transaction => "Receipt",
        SearchResultKind.Account => "Wallet",
        SearchResultKind.Budget => "Chart",
        SearchResultKind.Recurring => "Repeat",
        SearchResultKind.Goal => "Target",
        _ => "Search",
    };
}
