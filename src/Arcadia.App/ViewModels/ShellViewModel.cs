using System.Collections.ObjectModel;
using System.Windows;
using Arcadia.App.Services;
using Arcadia.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels;

/// <summary>Pages that want to know when they become visible (e.g. to load data lazily).</summary>
public interface IActivatable
{
    Task ActivateAsync();
}

/// <summary>Pages that react to the global shortcuts Ctrl+N (new) and Ctrl+S (save). Return false if not handled.</summary>
public interface IShortcutTarget
{
    bool OnNew();
    bool OnSave();
}

/// <summary>Pages that run live work (timers, monitoring) and must pause when the user leaves them.</summary>
public interface IDeactivatable
{
    void Deactivate();
}

public sealed partial class NavigationItemViewModel(SectionDescriptor section) : ObservableObject
{
    public SectionDescriptor Section { get; } = section;
    public string Key => Section.Key;
    public override string ToString() => Section.Key;
    public string Icon => Section.Icon;

    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _badge;
}

public sealed partial class ToastViewModel(string message, NotificationKind kind) : ObservableObject
{
    public string Message { get; } = message;
    public NotificationKind Kind { get; } = kind;
    public string Icon => Kind switch { NotificationKind.Success => "Check", NotificationKind.Warning => "Warning", _ => "Sparkle" };
}

public sealed partial class ShellViewModel : ObservableObject, INavigationService, INotificationService
{
    private readonly IServiceProvider _services;
    private readonly ILocalizer _loc;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly Dictionary<string, object> _pages = new(StringComparer.OrdinalIgnoreCase);

    public ShellViewModel(IServiceProvider services, HeaderViewModel header, ILocalizer loc, ILogger<ShellViewModel> logger)
    {
        _services = services;
        _loc = loc;
        _logger = logger;
        Header = header;

        foreach (var section in Sections.All)
            (section.IsSecondary ? SecondaryItems : PrimaryItems).Add(new NavigationItemViewModel(section));

        RefreshLabels();
        loc.CultureChanged += (_, _) => RefreshLabels();
    }

    public HeaderViewModel Header { get; }
    public ObservableCollection<NavigationItemViewModel> PrimaryItems { get; } = [];
    public ObservableCollection<NavigationItemViewModel> SecondaryItems { get; } = [];
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    private SessionTrackingService Tracking => _services.GetRequiredService<SessionTrackingService>();

    /// <summary>The game being recorded right now (drives the header "Now playing" plate).</summary>
    public NowPlayingState NowPlaying => Tracking.NowPlaying;

    [RelayCommand]
    private void OpenNowPlaying()
    {
        if (NowPlaying.IsPlaying) ShowGame(NowPlaying.GameId);
    }

    [RelayCommand]
    private Task StopTrackingAsync() => Tracking.StopCurrentAsync();

    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _currentKey = string.Empty;

    private IEnumerable<NavigationItemViewModel> AllItems => PrimaryItems.Concat(SecondaryItems);

    [RelayCommand]
    public void Navigate(string? key)
    {
        var section = Sections.Find(key) ?? Sections.All[0];
        foreach (var item in AllItems) item.IsSelected = item.Key == section.Key;
        if (section.Key == CurrentKey) return;

        if (!_pages.TryGetValue(section.Key, out var page))
        {
            page = section.Key switch
            {
                Sections.Home => _services.GetRequiredService<Home.HomeViewModel>(),
                Sections.Settings => _services.GetRequiredService<SettingsViewModel>(),
                Sections.Games => _services.GetRequiredService<Games.GamesViewModel>(),
                Sections.ItTools => _services.GetRequiredService<ItTools.ItToolsViewModel>(),
                Sections.Projects => _services.GetRequiredService<Projects.ProjectsViewModel>(),
                Sections.Documents => _services.GetRequiredService<Documents.DocumentsViewModel>(),
                Sections.Travel => _services.GetRequiredService<Travel.TravelViewModel>(),
                Sections.Calendar => _services.GetRequiredService<Organizer.CalendarViewModel>(),
                Sections.Tasks => _services.GetRequiredService<Organizer.TasksViewModel>(),
                Sections.Notes => _services.GetRequiredService<Organizer.NotesViewModel>(),
                Sections.Finance => _services.GetRequiredService<Finance.FinanceViewModel>(),
                Sections.Oracle => Oracle,
                Sections.Automation => _services.GetRequiredService<Automation.AutomationViewModel>(),
                _ => new ModulePlaceholderViewModel(section, _loc, this),
            };
            _pages[section.Key] = page;
        }

        if (CurrentPage is IDeactivatable leaving && !ReferenceEquals(leaving, page)) leaving.Deactivate();
        CurrentKey = section.Key;
        RememberWorkspace(section.Key, null, _loc[section.LabelKey], section.Icon);
        CurrentPage = page;
        _logger.LogDebug("Navigated to {Section}", section.Key);

        if (page is IActivatable activatable) _ = ActivateSafelyAsync(activatable);
    }

    public async void ShowGame(int gameId)
    {
        Navigate(Sections.Games);
        var games = _services.GetRequiredService<Games.GamesViewModel>();
        try
        {
            await games.ShowGameAsync(gameId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening game {GameId} failed", gameId);
        }
    }

    public async void ShowProject(int projectId)
    {
        Navigate(Sections.Projects);
        try { await _services.GetRequiredService<Projects.ProjectsViewModel>().OpenProjectAsync(projectId); }
        catch (Exception ex) { _logger.LogError(ex, "Opening project {Id} failed", projectId); }
    }

    public async void ShowDocument(int documentId)
    {
        Navigate(Sections.Documents);
        try { await _services.GetRequiredService<Documents.DocumentsViewModel>().ShowDocumentAsync(documentId); }
        catch (Exception ex) { _logger.LogError(ex, "Opening document {Id} failed", documentId); }
    }

    /// <summary>Opens any Arcadia record: the router behind arcadia:// links, context chips, reminders and calendar items.</summary>
    public async void Open(Arcadia.Core.Organizer.EntityRef target)
    {
        try
        {
            switch (target.Kind)
            {
                case Arcadia.Core.Organizer.EntityKind.Game: ShowGame(target.Id); break;
                case Arcadia.Core.Organizer.EntityKind.Project: ShowProject(target.Id); break;
                case Arcadia.Core.Organizer.EntityKind.Document: ShowDocument(target.Id); break;
                case Arcadia.Core.Organizer.EntityKind.Trip:
                {
                    var t = TravelPage();
                    await t.LoadAsync();
                    t.OpenTrip(target.Id);
                    break;
                }
                case Arcadia.Core.Organizer.EntityKind.Place:
                {
                    var t = TravelPage();
                    await t.LoadAsync();
                    t.OpenPlace(target.Id);
                    break;
                }
                case Arcadia.Core.Organizer.EntityKind.Journal:
                {
                    var t = TravelPage();
                    await t.LoadAsync();
                    await t.OpenSearchResultAsync(new Arcadia.Core.Search.SearchResult(Arcadia.Core.Search.SearchResultKind.Journal, target.Id, null, string.Empty, null, 0));
                    break;
                }
                case Arcadia.Core.Organizer.EntityKind.Diagnostic:
                    Navigate(Sections.ItTools);
                    await _services.GetRequiredService<ItTools.ItToolsViewModel>().ShowSessionAsync(target.Id);
                    break;
                case Arcadia.Core.Organizer.EntityKind.Task:
                    Navigate(Sections.Tasks);
                    await _services.GetRequiredService<Organizer.TasksViewModel>().ShowTaskAsync(target.Id);
                    break;
                case Arcadia.Core.Organizer.EntityKind.Note:
                    Navigate(Sections.Notes);
                    await _services.GetRequiredService<Organizer.NotesViewModel>().ShowNoteAsync(target.Id);
                    break;
                case Arcadia.Core.Organizer.EntityKind.Event:
                    Navigate(Sections.Calendar);
                    await _services.GetRequiredService<Organizer.CalendarViewModel>().ShowEventAsync(target.Id);
                    break;
                case Arcadia.Core.Organizer.EntityKind.Automation:
                {
                    Navigate(Sections.Automation);
                    var auto = _services.GetRequiredService<Automation.AutomationViewModel>();
                    await auto.ActivateAsync();
                    auto.Tab = Automation.AutomationTab.Automations;
                    auto.Selected = auto.Automations.FirstOrDefault(r => r.Definition.Id == target.Id);
                    break;
                }
                case Arcadia.Core.Organizer.EntityKind.Transaction or Arcadia.Core.Organizer.EntityKind.Account or Arcadia.Core.Organizer.EntityKind.Budget
                    or Arcadia.Core.Organizer.EntityKind.Recurring or Arcadia.Core.Organizer.EntityKind.Goal:
                {
                    Navigate(Sections.Finance);
                    var fin = _services.GetRequiredService<Finance.FinanceViewModel>();
                    await fin.ActivateAsync();
                    await fin.OpenAsync(target);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening {Target} failed", target);
        }
    }

    private Travel.TravelViewModel TravelPage()
    {
        Navigate(Sections.Travel);
        return _services.GetRequiredService<Travel.TravelViewModel>();
    }

    // ── recent workspaces & restore (Phase 9) ─────────────────

    private Arcadia.Core.Settings.ISettingsService Settings => _services.GetRequiredService<Arcadia.Core.Settings.ISettingsService>();

    /// <summary>Remembers where the user is: the last place (for "restore last workspace") and a short recent list.</summary>
    public void RememberWorkspace(string target, string? tab, string title, string icon)
    {
        if (target is Sections.Settings) return;
        var w = Settings.Current.Workspace;
        w.Last = tab == null ? target : target + "#" + tab;
        if (target is Sections.Home) return;
        w.Recent.RemoveAll(r => r.Target == target);
        w.Recent.Insert(0, new Arcadia.Core.Settings.RecentWorkspace { Target = target, Tab = tab, Title = title, Icon = icon, AtUtc = DateTime.UtcNow });
        if (w.Recent.Count > 12) w.Recent.RemoveRange(12, w.Recent.Count - 12);
    }

    private async Task RememberRecordAsync(Arcadia.Core.Organizer.EntityRef target, string? tab)
    {
        try
        {
            var names = await _services.GetRequiredService<Arcadia.Core.Organizer.IEntityDirectory>().DescribeAsync([target]);
            if (names.TryGetValue(target, out var info)) RememberWorkspace(target.ToString(), tab, info.Name, info.Icon);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not remember {Target}", target); }
    }

    /// <summary>Recent places for the palette (shown when it opens empty).</summary>
    public IReadOnlyList<PaletteItem> RecentItems() => Settings.Current.Workspace.Recent.Take(6).Select(r => new PaletteItem(r.Icon, r.Title, null, _loc["Palette_Recent"], null, async () =>
    {
        if (Arcadia.Core.Organizer.EntityRef.TryParse(r.Target, out var target)) await OpenAsync(target, r.Tab);
        else await NavigateToAsync(r.Target, r.Tab);
    })).ToList();

    // ── notifications & quick capture ─────────────────────────

    public NotificationCenter NotificationCenter => _services.GetRequiredService<NotificationCenter>();

    [ObservableProperty] private bool _isNotificationsOpen;

    private Organizer.QuickCaptureViewModel? _capture;

    public Organizer.QuickCaptureViewModel QuickCapture => _capture ??= _services.GetRequiredService<Organizer.QuickCaptureViewModel>();

    /// <summary>Ctrl+Shift+N: capture a task, note or event; attached to what the current page is about.</summary>
    [RelayCommand]
    private async Task OpenQuickCaptureAsync(string? kind)
    {
        if (QuickCapture.IsOpen) { QuickCapture.CloseCommand.Execute(null); return; }
        Organizer.ContextChip? chip = null;
        if (CurrentContext() is { } ctxRef)
        {
            var org = _services.GetRequiredService<Organizer.OrganizerContext>();
            chip = (await Organizer.ContextChip.ResolveAsync(org, [ctxRef])).GetValueOrDefault(ctxRef);
        }
        Arcadia.Core.Organizer.CaptureKind? k = Enum.TryParse<Arcadia.Core.Organizer.CaptureKind>(kind, true, out var parsed) ? parsed
            : CurrentPage switch { Organizer.NotesViewModel => Arcadia.Core.Organizer.CaptureKind.Note, Organizer.CalendarViewModel => Arcadia.Core.Organizer.CaptureKind.Event, _ => null };
        QuickCapture.Open(k, chip);
    }

    /// <summary>The window is visible and not minimized (animations and clocks run only then).</summary>
    [ObservableProperty] private bool _isWindowShown = true;

    /// <summary>Asks the window to come forward (tray, global shortcut, automation Quick Capture).</summary>
    public event EventHandler? ShowWindowRequested;

    public void RequestShowWindow() => ShowWindowRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The record the current page is about (Oracle's page context).</summary>
    public Arcadia.Core.Organizer.EntityRef? PageContext => CurrentContext();

    // ── Oracle ─────────────────────────────────────────────

    private Oracle.OracleConsoleViewModel? _oracle;

    public Oracle.OracleConsoleViewModel Oracle => _oracle ??= _services.GetRequiredService<Oracle.OracleConsoleViewModel>();

    /// <summary>The Oracle shortcut (Ctrl+Space by default): opens or closes the command overlay.</summary>
    [RelayCommand]
    private Task ToggleOracleAsync()
    {
        if (CurrentKey == Sections.Oracle) { Oracle.RequestFocus(); return Task.CompletedTask; }
        Palette.Close();
        if (QuickCapture.IsOpen) QuickCapture.CloseCommand.Execute(null);
        return Oracle.OpenOverlayAsync();
    }

    /// <summary>Opens a record and, when named, one of its tabs ("Cyberpunk journal", "Home Server tasks", "Italy documents").</summary>
    public async Task OpenAsync(Arcadia.Core.Organizer.EntityRef target, string? tab)
    {
        _ = RememberRecordAsync(target, tab);
        try
        {
            switch (target.Kind)
            {
                case Arcadia.Core.Organizer.EntityKind.Game:
                {
                    Navigate(Sections.Games);
                    var games = _services.GetRequiredService<Games.GamesViewModel>();
                    await games.ShowGameAsync(target.Id);
                    if (tab != null && games.Detail != null && Enum.TryParse<Games.GameDetailTab>(tab, true, out var gt)) games.Detail.SelectedTab = gt;
                    return;
                }
                case Arcadia.Core.Organizer.EntityKind.Project:
                {
                    Navigate(Sections.Projects);
                    var projects = _services.GetRequiredService<Projects.ProjectsViewModel>();
                    await projects.OpenProjectAsync(target.Id);
                    var name = tab == "Documents" ? "Files" : tab;
                    if (name != null && projects.Workspace != null && Enum.TryParse<Projects.WorkspaceTab>(name, true, out var wt)) projects.Workspace.SelectedTab = wt;
                    return;
                }
                case Arcadia.Core.Organizer.EntityKind.Trip:
                {
                    var t = TravelPage();
                    await t.LoadAsync();
                    t.OpenTrip(target.Id);
                    var name = tab == "Journal" ? "Memories" : tab;
                    if (name != null && t.Workspace != null && Enum.TryParse<Travel.TripTab>(name, true, out var tt)) t.Workspace.Tab = tt;
                    return;
                }
                default:
                    Open(target);
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening {Target} failed", target);
        }
    }

    /// <summary>Opens a section and, when named, a sub-view (finance section, travel section, IT tool).</summary>
    public async Task NavigateToAsync(string section, string? tab)
    {
        Navigate(section);
        if (tab == null) return;
        try
        {
            switch (section)
            {
                case Sections.Finance when Enum.TryParse<Finance.FinanceSection>(tab, true, out var fs):
                {
                    var fin = _services.GetRequiredService<Finance.FinanceViewModel>();
                    await fin.ActivateAsync();
                    await fin.ShowSectionAsync(fs);
                    break;
                }
                case Sections.Travel when Enum.TryParse<Travel.TravelSection>(tab, true, out var ts):
                {
                    var t = _services.GetRequiredService<Travel.TravelViewModel>();
                    await t.ActivateAsync();
                    t.ShowSection(ts);
                    break;
                }
                case Sections.ItTools:
                    await _services.GetRequiredService<ItTools.ItToolsViewModel>().OpenToolAsync(tab);
                    break;
                case Sections.Calendar when Enum.TryParse<Organizer.CalendarView>(tab, true, out var cv):
                {
                    var cal = _services.GetRequiredService<Organizer.CalendarViewModel>();
                    await cal.ShowDateAsync(DateOnly.FromDateTime(DateTime.Now), cv);
                    break;
                }
                case Sections.Automation when Enum.TryParse<Automation.AutomationTab>(tab, true, out var at):
                    _services.GetRequiredService<Automation.AutomationViewModel>().Tab = at;
                    break;
                case Sections.Tasks when Enum.TryParse<Arcadia.Core.Organizer.TaskView>(tab, true, out var tv):
                {
                    var tasks = _services.GetRequiredService<Organizer.TasksViewModel>();
                    await tasks.ActivateAsync();
                    tasks.View = tv;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening {Section}/{Tab} failed", section, tab);
        }
    }

    /// <summary>The record the current page is about (an open project, trip, game or diagnostic session), if any.</summary>
    private Arcadia.Core.Organizer.EntityRef? CurrentContext() => CurrentPage switch
    {
        Projects.ProjectsViewModel { Workspace: { } w } => new(Arcadia.Core.Organizer.EntityKind.Project, w.ProjectId),
        Travel.TravelViewModel { Workspace: { } t } => new(Arcadia.Core.Organizer.EntityKind.Trip, t.TripId),
        Games.GamesViewModel { Detail: { } d } => new(Arcadia.Core.Organizer.EntityKind.Game, d.GameId),
        _ => null,
    };

    // ── command palette & shortcuts ───────────────────────────

    private CommandPaletteViewModel? _palette;

    public CommandPaletteViewModel Palette => _palette ??= CreatePalette();

    private CommandPaletteViewModel CreatePalette()
    {
        var p = _services.GetRequiredService<CommandPaletteViewModel>();
        Task Go(string key) { Navigate(key); return Task.CompletedTask; }
        p.Register(new("open.home", "Home", "Cmd_OpenHome", ["home", "start", "الرئيسية"], null, () => Go(Sections.Home)));
        p.Register(new("open.games", "Games", "Cmd_OpenGames", ["games", "library", "الألعاب"], null, () => Go(Sections.Games)));
        p.Register(new("open.ittools", "ITTools", "Cmd_OpenItTools", ["it", "tools", "network", "ping", "أدوات"], null, () => Go(Sections.ItTools)));
        p.Register(new("open.projects", "Projects", "Cmd_OpenProjects", ["projects", "workspace", "المشاريع"], null, () => Go(Sections.Projects)));
        p.Register(new("open.documents", "Documents", "Cmd_OpenDocuments", ["documents", "files", "library", "المستندات"], null, () => Go(Sections.Documents)));
        p.Register(new("new.project", "Plus", "Cmd_NewProject", ["create", "project", "new", "مشروع"], "Ctrl+N", async () =>
        {
            Navigate(Sections.Projects);
            await _services.GetRequiredService<Projects.ProjectsViewModel>().NewProjectAsync();
        }));
        p.Register(new("add.document", "Import", "Cmd_AddDocument", ["add", "document", "file", "import", "مستند"], null, async () =>
        {
            Navigate(Sections.Documents);
            await _services.GetRequiredService<DocumentActions>().PickFilesAsync(null);
        }));
        p.Register(new("search.projects", "Search", "Cmd_SearchProjects", ["find", "search", "projects", "بحث"], null, () =>
        {
            Navigate(Sections.Projects);
            SearchFocusRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }));
        p.Register(new("search.documents", "Search", "Cmd_SearchDocuments", ["find", "search", "documents", "بحث"], null, () =>
        {
            Navigate(Sections.Documents);
            SearchFocusRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }));
        p.Register(new("open.session", "Diagnostic", "Cmd_DiagnosticSession", ["diagnostic", "session", "troubleshoot"], null, async () =>
        {
            Navigate(Sections.ItTools);
            await _services.GetRequiredService<ItTools.ItToolsViewModel>().OpenToolAsync("session");
        }));
        // Travel
        Travel.TravelViewModel TravelPage() { Navigate(Sections.Travel); return _services.GetRequiredService<Travel.TravelViewModel>(); }
        async Task TravelSection(Travel.TravelSection s) { var t = TravelPage(); await t.ActivateAsync(); t.ShowSection(s); }
        p.Register(new("open.travel", "Travel", "Cmd_OpenTravel", ["travel", "trips", "map", "reisen", "سفر"], null, () => TravelSection(Travel.TravelSection.Desk)));
        p.Register(new("new.trip", "Suitcase", "Cmd_NewTrip", ["trip", "journey", "new", "reise", "رحلة"], null, async () => { var t = TravelPage(); await t.ActivateAsync(); await t.NewTripAsync(null); }));
        p.Register(new("add.place", "MapPin", "Cmd_AddPlace", ["place", "location", "add", "ort", "مكان"], null, async () => { var t = TravelPage(); await t.ActivateAsync(); await t.AddPlaceAsync(); }));
        p.Register(new("open.worldmap", "Map", "Cmd_OpenWorldMap", ["map", "world", "karte", "خريطة"], null, () => TravelSection(Travel.TravelSection.Map)));
        p.Register(new("open.bucket", "Flag", "Cmd_OpenBucket", ["bucket", "dreams", "wishlist", "أحلام"], null, () => TravelSection(Travel.TravelSection.Bucket)));
        p.Register(new("open.journal", "Journal", "Cmd_OpenJournal", ["journal", "diary", "tagebuch", "يوميات"], null, () => TravelSection(Travel.TravelSection.Journal)));
        p.Register(new("open.upcoming", "Calendar", "Cmd_UpcomingTrips", ["upcoming", "next", "trips", "القادمة"], null, async () =>
        {
            var t = TravelPage();
            await t.ActivateAsync();
            t.ShowTripFilter(Travel.TripFilter.Upcoming);
        }));
        // Calendar, tasks, notes
        async Task Cal(Organizer.CalendarView? view)
        {
            Navigate(Sections.Calendar);
            var cal = _services.GetRequiredService<Organizer.CalendarViewModel>();
            await cal.ShowDateAsync(DateOnly.FromDateTime(DateTime.Now), view);
        }
        async Task TaskView(Arcadia.Core.Organizer.TaskView view)
        {
            Navigate(Sections.Tasks);
            var tasks = _services.GetRequiredService<Organizer.TasksViewModel>();
            await tasks.ActivateAsync();
            tasks.View = view;
        }
        p.Register(new("open.calendar", "Calendar", "Cmd_OpenCalendar", ["calendar", "month", "week", "kalender", "تقويم"], null, () => Cal(null)));
        p.Register(new("open.tasks", "Tasks", "Cmd_OpenTasks", ["tasks", "todo", "aufgaben", "مهام"], null, () => Go(Sections.Tasks)));
        p.Register(new("open.notes", "Notes", "Cmd_OpenNotes", ["notes", "notebook", "notizen", "ملاحظات"], null, () => Go(Sections.Notes)));
        p.Register(new("new.event", "Calendar", "Cmd_NewEvent", ["event", "appointment", "meeting", "termin", "موعد", "حدث"], null, async () =>
        {
            Navigate(Sections.Calendar);
            var cal = _services.GetRequiredService<Organizer.CalendarViewModel>();
            await cal.ActivateAsync();
            await cal.NewEventAtAsync(null);
        }));
        p.Register(new("new.task", "Tasks", "Cmd_NewTask", ["task", "todo", "aufgabe", "مهمة"], null, () => OpenQuickCaptureAsync("Task")));
        p.Register(new("new.note", "Notes", "Cmd_NewNote", ["note", "write", "notiz", "ملاحظة"], null, async () =>
        {
            Navigate(Sections.Notes);
            var notes = _services.GetRequiredService<Organizer.NotesViewModel>();
            await notes.ActivateAsync();
            await notes.NewNoteAsync();
        }));
        p.Register(new("capture", "Bolt", "Cmd_QuickCapture", ["capture", "quick", "inbox", "schnell", "التقاط"], "Ctrl+Shift+N", () => OpenQuickCaptureAsync(null)));
        p.Register(new("today", "Agenda", "Cmd_TodayAgenda", ["today", "agenda", "day", "heute", "اليوم"], null, () => Cal(Organizer.CalendarView.Today)));
        p.Register(new("tasks.upcoming", "Calendar", "Cmd_UpcomingTasks", ["upcoming", "next", "tasks", "bevorstehend", "القادمة"], null, () => TaskView(Arcadia.Core.Organizer.TaskView.Upcoming)));
        p.Register(new("tasks.overdue", "Warning", "Cmd_OverdueTasks", ["overdue", "late", "tasks", "überfällig", "متأخرة"], null, () => TaskView(Arcadia.Core.Organizer.TaskView.Overdue)));
        p.Register(new("notes.pinned", "Pin", "Cmd_PinnedNotes", ["pinned", "notes", "angeheftet", "مثبتة"], null, async () =>
        {
            Navigate(Sections.Notes);
            await _services.GetRequiredService<Organizer.NotesViewModel>().ShowShelfAsync("pinned");
        }));
        // Finance
        Finance.FinanceViewModel FinancePage() { Navigate(Sections.Finance); return _services.GetRequiredService<Finance.FinanceViewModel>(); }
        async Task FinanceSection(Finance.FinanceSection s) { var f = FinancePage(); await f.ActivateAsync(); await f.ShowSectionAsync(s); }
        Finance.FinanceActions FinanceActions() => _services.GetRequiredService<Finance.FinanceActions>();
        p.Register(new("open.finance", "Finance", "Cmd_OpenFinance", ["finance", "money", "budget", "finanzen", "geld", "مالية", "مال"], null, () => FinanceSection(Finance.FinanceSection.Overview)));
        p.Register(new("new.transaction", "Receipt", "Cmd_NewTransaction", ["transaction", "booking", "buchung", "معاملة"], null, () => FinanceActions().NewTransactionAsync(Arcadia.Core.Finance.TransactionType.Expense)));
        p.Register(new("add.expense", "Expense", "Cmd_AddExpense", ["expense", "spent", "paid", "ausgabe", "مصروف"], null, () => FinanceActions().NewTransactionAsync(Arcadia.Core.Finance.TransactionType.Expense)));
        p.Register(new("add.income", "Income", "Cmd_AddIncome", ["income", "salary", "einnahme", "gehalt", "دخل"], null, () => FinanceActions().NewTransactionAsync(Arcadia.Core.Finance.TransactionType.Income)));
        p.Register(new("open.accounts", "Wallet", "Cmd_OpenAccounts", ["accounts", "balance", "konten", "حسابات"], null, () => FinanceSection(Finance.FinanceSection.Accounts)));
        p.Register(new("open.budgets", "Chart", "Cmd_OpenBudgets", ["budgets", "limits", "budget", "ميزانية"], null, () => FinanceSection(Finance.FinanceSection.Budgets)));
        p.Register(new("open.subscriptions", "Repeat", "Cmd_OpenSubscriptions", ["subscriptions", "abos", "abonnements", "اشتراكات"], null, () => FinanceSection(Finance.FinanceSection.Recurring)));
        p.Register(new("bills.upcoming", "Receipt", "Cmd_UpcomingBills", ["bills", "due", "rechnungen", "fällig", "فواتير"], null, () => FinanceSection(Finance.FinanceSection.Recurring)));
        p.Register(new("finance.month", "Calendar", "Cmd_MonthlySummary", ["summary", "month", "monat", "ملخص", "شهر"], null, () => FinanceSection(Finance.FinanceSection.History)));
        p.Register(new("open.goals", "Target", "Cmd_SavingsGoals", ["goals", "savings", "sparziele", "أهداف", "ادخار"], null, () => FinanceSection(Finance.FinanceSection.Goals)));
        p.Register(new("finance.privacy", "EyeOff", "Cmd_TogglePrivacy", ["privacy", "hide", "amounts", "verbergen", "إخفاء"], "Ctrl+Shift+H", () => { TogglePrivacy(); return Task.CompletedTask; }));
        p.Register(new("open.settings", "Settings", "Cmd_OpenSettings", ["settings", "preferences", "options", "الإعدادات"], null, () => Go(Sections.Settings)));
        // Automation (Phase 9)
        async Task Auto(Automation.AutomationTab tab, bool create = false)
        {
            Navigate(Sections.Automation);
            var a = _services.GetRequiredService<Automation.AutomationViewModel>();
            await a.ActivateAsync();
            a.Tab = tab;
            if (create) a.NewAutomation();
        }
        Services.AutomationService AutoService() => _services.GetRequiredService<Services.AutomationService>();
        Services.PerformanceService Perf() => _services.GetRequiredService<Services.PerformanceService>();
        p.Register(new("open.automation", "Automation", "Cmd_OpenAutomation", ["automation", "rules", "workflows", "automatisierung", "أتمتة"], null, () => Auto(Automation.AutomationTab.Automations)));
        p.Register(new("new.automation", "Plus", "Cmd_NewAutomation", ["new", "automation", "rule", "when", "neue", "أتمتة"], null, () => Auto(Automation.AutomationTab.Automations, true)));
        p.Register(new("automation.pause", "Stop", "Cmd_PauseAutomations", ["pause", "automations", "stop", "pausieren", "إيقاف"], null, () => AutoService().SetPausedAsync(true)));
        p.Register(new("automation.resume", "Play", "Cmd_ResumeAutomations", ["resume", "automations", "continue", "fortsetzen", "استئناف"], null, () => AutoService().SetPausedAsync(false)));
        p.Register(new("automation.activity", "Timeline", "Cmd_AutomationActivity", ["activity", "log", "runs", "history", "protokoll", "سجل"], null, () => Auto(Automation.AutomationTab.Activity)));
        p.Register(new("sessions", "Clock", "Cmd_ActiveSessions", ["sessions", "work", "playing", "timer", "sitzungen", "جلسات"], null, () => Auto(Automation.AutomationTab.Sessions)));
        p.Register(new("gaming.toggle", "Games", "Cmd_GamingMode", ["gaming", "mode", "game", "spielmodus", "ألعاب"], Settings.Current.Shortcuts.GamingMode, () => { Perf().SetGamingMode(!Perf().IsGamingMode, "manual"); return Task.CompletedTask; }));
        p.Register(new("powersaver.toggle", "Bolt", "Cmd_PowerSaver", ["power", "saver", "battery", "energie", "طاقة"], null, () => { Perf().TogglePowerSaver(); return Task.CompletedTask; }));
        p.Recent = RecentItems;
        p.OpenResult = OpenSearchResultAsync;
        p.Describe = DescribeResultsAsync;
        return p;
    }

    /// <summary>Asks the current page view to focus its search box (Ctrl+F, "Search projects").</summary>
    public event EventHandler? SearchFocusRequested;

    public async Task OpenSearchResultAsync(Arcadia.Core.Search.SearchResult r)
    {
        switch (r.Kind)
        {
            case Arcadia.Core.Search.SearchResultKind.Game:
                ShowGame(r.Id);
                break;
            case Arcadia.Core.Search.SearchResultKind.Document:
                ShowDocument(r.Id);
                break;
            case Arcadia.Core.Search.SearchResultKind.Task:
                Open(new Arcadia.Core.Organizer.EntityRef(Arcadia.Core.Organizer.EntityKind.Task, r.Id));
                break;
            case Arcadia.Core.Search.SearchResultKind.Note:
                Open(new Arcadia.Core.Organizer.EntityRef(Arcadia.Core.Organizer.EntityKind.Note, r.Id));
                break;
            case Arcadia.Core.Search.SearchResultKind.Event:
                Open(new Arcadia.Core.Organizer.EntityRef(Arcadia.Core.Organizer.EntityKind.Event, r.Id));
                break;
            case Arcadia.Core.Search.SearchResultKind.Transaction:
                Open(new Arcadia.Core.Organizer.EntityRef(Arcadia.Core.Organizer.EntityKind.Transaction, r.Id));
                break;
            case Arcadia.Core.Search.SearchResultKind.Account:
                Open(new Arcadia.Core.Organizer.EntityRef(Arcadia.Core.Organizer.EntityKind.Account, r.Id));
                break;
            case Arcadia.Core.Search.SearchResultKind.Budget:
                Open(new Arcadia.Core.Organizer.EntityRef(Arcadia.Core.Organizer.EntityKind.Budget, r.Id));
                break;
            case Arcadia.Core.Search.SearchResultKind.Recurring:
                Open(new Arcadia.Core.Organizer.EntityRef(Arcadia.Core.Organizer.EntityKind.Recurring, r.Id));
                break;
            case Arcadia.Core.Search.SearchResultKind.Goal:
                Open(new Arcadia.Core.Organizer.EntityRef(Arcadia.Core.Organizer.EntityKind.Goal, r.Id));
                break;
            case Arcadia.Core.Search.SearchResultKind.Trip or Arcadia.Core.Search.SearchResultKind.Place
                or Arcadia.Core.Search.SearchResultKind.Journal or Arcadia.Core.Search.SearchResultKind.Country:
                Navigate(Sections.Travel);
                await _services.GetRequiredService<Travel.TravelViewModel>().OpenSearchResultAsync(r);
                break;
            default:
                Navigate(Sections.Projects);
                await _services.GetRequiredService<Projects.ProjectsViewModel>().OpenSearchResultAsync(r);
                break;
        }
    }

    [RelayCommand]
    private void OpenPalette(string? text) => Palette.Open(text);

    /// <summary>Ctrl+N: "new" in the current module.</summary>
    [RelayCommand]
    private void NewInContext()
    {
        switch (CurrentPage)
        {
            case IShortcutTarget target when target.OnNew():
                break;
            case Games.GamesViewModel games:
                games.AddGameCommand.Execute(null);
                break;
            default:
                Palette.Open();
                break;
        }
    }

    /// <summary>Ctrl+S: save where something is being edited.</summary>
    [RelayCommand]
    private void SaveInContext()
    {
        if (CurrentPage is IShortcutTarget target) target.OnSave();
    }

    private async Task ActivateSafelyAsync(IActivatable page)
    {
        try
        {
            await page.ActivateAsync();
        }
        catch (Exception ex)
        {
            // Pages report their own user-facing errors; this is a last line of defence.
            _logger.LogError(ex, "Activating {Page} failed", page.GetType().Name);
        }
    }

    public void Show(string message, NotificationKind kind = NotificationKind.Info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => Show(message, kind));
            return;
        }

        // Avoid stacking the same message when a button is clicked repeatedly.
        if (Toasts.Any(t => t.Message == message)) return;
        var toast = new ToastViewModel(message, kind);
        Toasts.Add(toast);
        while (Toasts.Count > 3) Toasts.RemoveAt(0);
        _ = DismissLaterAsync(toast);
    }

    private async Task DismissLaterAsync(ToastViewModel toast)
    {
        await Task.Delay(TimeSpan.FromSeconds(4.5));
        Toasts.Remove(toast);
    }

    [RelayCommand]
    private void DismissToast(ToastViewModel? toast)
    {
        if (toast != null) Toasts.Remove(toast);
    }

    [RelayCommand]
    private void Search(string? text) => Palette.Open(text);

    /// <summary>The header bell: reminders shown this session (or a note that there are none).</summary>
    [RelayCommand]
    private void ShowNotifications()
    {
        if (NotificationCenter.Items.Count == 0) { Show(_loc["Toast_NoNotifications"]); return; }
        IsNotificationsOpen = !IsNotificationsOpen;
        if (IsNotificationsOpen) NotificationCenter.MarkRead();
    }

    [RelayCommand]
    private void OpenNotice(ReminderNotice? notice)
    {
        if (notice == null) return;
        IsNotificationsOpen = false;
        NotificationCenter.Remove(notice);
        Open(notice.Target);
    }

    [RelayCommand] private void DismissNotice(ReminderNotice? notice) { if (notice != null) NotificationCenter.Remove(notice); if (NotificationCenter.Items.Count == 0) IsNotificationsOpen = false; }

    [RelayCommand]
    private void ClearNotices()
    {
        NotificationCenter.Clear();
        IsNotificationsOpen = false;
    }

    /// <summary>Palette rows for events and tasks say when (e.g. "Tomorrow · 09:00") instead of a text excerpt.</summary>
    private async Task<IReadOnlyDictionary<(Arcadia.Core.Search.SearchResultKind, int), string>> DescribeResultsAsync(IReadOnlyList<Arcadia.Core.Search.SearchResult> results)
    {
        var map = new Dictionary<(Arcadia.Core.Search.SearchResultKind, int), string>();
        var org = _services.GetRequiredService<Organizer.OrganizerContext>();
        var now = org.Now;
        foreach (var r in results)
        {
            if (r.Kind == Arcadia.Core.Search.SearchResultKind.Event)
            {
                var ev = await _services.GetRequiredService<Arcadia.Core.Organizer.ICalendarStore>().GetEventAsync(r.Id);
                if (ev == null) continue;
                var occ = Arcadia.Core.Organizer.Recurrence.Expand(ev, now.Date, now.Date.AddYears(1)).FirstOrDefault();
                var start = occ?.Start ?? ev.Start;
                var text = Organizer.OrgText.Day(DateOnly.FromDateTime(start), org.Today, _loc) + (ev.IsAllDay ? string.Empty : " · " + Organizer.OrgText.Time(start, _loc.Culture));
                map[(r.Kind, r.Id)] = ev.Location is { Length: > 0 } loc ? $"{text} · {loc}" : text;
            }
            else if (r.Kind == Arcadia.Core.Search.SearchResultKind.Task)
            {
                var t = await _services.GetRequiredService<Arcadia.Core.Organizer.ITaskStore>().GetAsync(r.Id);
                if (t == null) continue;
                var parts = new List<string>();
                if (t.IsDone) parts.Add(_loc["TaskStatus_" + t.Status]);
                else if (t.DueDate != null) parts.Add(Organizer.OrgText.Due(t.DueDate, t.DueTime, now, _loc));
                if (t.Project != null) parts.Add(t.Project.Name);
                if (parts.Count > 0) map[(r.Kind, r.Id)] = string.Join(" · ", parts);
            }
        }
        await DescribeFinanceAsync(results, map);
        return map;
    }

    /// <summary>"€129.99 · Tue 22 Sep · Checking" / "€39.99 per month · Subscription" — with amounts hidden in privacy mode.</summary>
    private async Task DescribeFinanceAsync(IReadOnlyList<Arcadia.Core.Search.SearchResult> results, Dictionary<(Arcadia.Core.Search.SearchResultKind, int), string> map)
    {
        var finance = results.Where(r => r.Kind is Arcadia.Core.Search.SearchResultKind.Transaction or Arcadia.Core.Search.SearchResultKind.Account
            or Arcadia.Core.Search.SearchResultKind.Recurring or Arcadia.Core.Search.SearchResultKind.Goal).ToList();
        if (finance.Count == 0) return;
        var ctx = _services.GetRequiredService<FinanceContext>();
        var hidden = FinancePrivacy.Instance.IsHidden;
        string Money(decimal amount, string currency) => FinancePrivacy.Apply(ctx.Format(amount, currency), hidden);
        var balances = (await ctx.Store.GetAccountBalancesAsync(true)).ToDictionary(b => b.Account.Id);
        foreach (var r in finance)
        {
            switch (r.Kind)
            {
                case Arcadia.Core.Search.SearchResultKind.Transaction when await ctx.Store.GetTransactionAsync(r.Id) is { } t:
                    var cur = t.Account?.Currency ?? "EUR";
                    map[(r.Kind, r.Id)] = string.Join(" · ", new[]
                    {
                        t.Type == Arcadia.Core.Finance.TransactionType.Expense ? Money(-t.Amount, cur) : Money(t.Amount, cur),
                        Organizer.OrgText.Day(t.Date, ctx.Today, _loc), t.Account?.Name,
                    }.Where(s => !string.IsNullOrEmpty(s)));
                    break;
                case Arcadia.Core.Search.SearchResultKind.Account when balances.TryGetValue(r.Id, out var b):
                    map[(r.Kind, r.Id)] = $"{Money(b.Balance, b.Account.Currency)} · {_loc["FinAcct_" + b.Account.Type]}";
                    break;
                case Arcadia.Core.Search.SearchResultKind.Recurring when await ctx.Store.GetRecurringItemAsync(r.Id) is { } item:
                    map[(r.Kind, r.Id)] = $"{Money(item.Amount, item.Currency)} {Finance.FinanceText.Frequency(item.Frequency, item.Interval, ctx).ToLower(_loc.Culture)} · {_loc["FinKind_" + item.Kind]}";
                    break;
                case Arcadia.Core.Search.SearchResultKind.Goal:
                    var goal = (await ctx.Store.GetSavingsGoalsAsync(ctx.Today, true)).FirstOrDefault(g => g.Goal.Id == r.Id);
                    if (goal != null) map[(r.Kind, r.Id)] = _loc.Format("Fin_BudgetOfFmt", Money(goal.Saved, goal.Goal.Currency), Money(goal.Target, goal.Goal.Currency));
                    break;
            }
        }
    }

    /// <summary>Ctrl+Shift+H: hide or show every amount (Finance privacy mode).</summary>
    [RelayCommand]
    private void TogglePrivacy()
    {
        var privacy = FinancePrivacy.Instance;
        privacy.IsHidden = !privacy.IsHidden;
        Show(_loc[privacy.IsHidden ? "Fin_PrivacyOn" : "Fin_PrivacyOff"]);
    }

    [RelayCommand]
    private void ShowProfile() => Show(_loc["Toast_ProfileUnavailable"]);

    private void RefreshLabels()
    {
        foreach (var item in AllItems) item.Label = _loc[item.Section.LabelKey];
    }
}
