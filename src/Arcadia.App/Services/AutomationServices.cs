using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using Arcadia.App.Messages;
using Arcadia.App.ViewModels;
using Arcadia.Core.Automation;
using Arcadia.Core.Finance;
using Arcadia.Core.Games;
using Arcadia.Core.Localization;
using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Settings;
using Arcadia.Core.Tracking;
using Arcadia.Core.Travel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arcadia.App.Services;

/// <summary>
/// Runs Arcadia's automations (Phase 9): owns the <see cref="AutomationEngine"/>, implements the host its actions call
/// (always through existing services), and feeds it events from trigger sources. Zero-work idle: a source exists only while
/// an enabled automation needs it — no process scans without process triggers, no folder watchers without file triggers,
/// one timer for the next scheduled moment instead of polling. Also owns project work sessions.
/// </summary>
public sealed partial class AutomationService : ObservableObject, IAutomationService, IAutomationHost, IDisposable,
    IRecipient<OrganizerChangedMessage>, IRecipient<ProjectsChangedMessage>, IRecipient<TravelChangedMessage>
{
    private static readonly TimeSpan ProcessInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaxTimerSleep = TimeSpan.FromHours(6);
    private static readonly TimeSpan MissedScheduleGrace = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FileSettle = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan WorkHeartbeat = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WorkRestoreGap = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> NeverOpen = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr", ".hta",
        ".lnk", ".pif", ".reg", ".jar", ".cpl", ".appref-ms", ".application", ".gadget", ".msc", ".url",
    };

    private readonly IServiceProvider _sp;
    private readonly IAutomationStore _store;
    private readonly IWorkSessionStore _work;
    private readonly ISettingsService _settings;
    private readonly PerformanceService _performance;
    private readonly ArcadiaNotifier _notifier;
    private readonly SessionTrackingService _tracking;
    private readonly ILocalizer _loc;
    private readonly IMessenger _messenger;
    private readonly TimeProvider _time;
    private readonly ILogger<AutomationService> _logger;
    private readonly Lock _gate = new();

    // sources
    private ITimer? _processTimer;
    private HashSet<string> _watchedExes = [];
    private HashSet<string> _running = [];
    private readonly Dictionary<string, FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, (string Type, DateTime Due)> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _fileTimer;
    private ITimer? _clockTimer;
    private DateTime _lastScheduleCheckLocal;
    private DateTime _taskWatermarkUtc;
    private Dictionary<int, ProjectStatus>? _projectStatuses;
    private ITimer? _workTimer;
    private DateTime? _suspendedAtUtc;
    private bool _started;

    public AutomationService(IServiceProvider sp, IAutomationStore store, IWorkSessionStore work, ISettingsService settings, PerformanceService performance,
        ArcadiaNotifier notifier, SessionTrackingService tracking, ILocalizer loc, IMessenger messenger, TimeProvider time, ILogger<AutomationService> logger)
    {
        _sp = sp;
        _store = store;
        _work = work;
        _settings = settings;
        _performance = performance;
        _notifier = notifier;
        _tracking = tracking;
        _loc = loc;
        _messenger = messenger;
        _time = time;
        _logger = logger;
        Engine = new AutomationEngine(AutomationCatalog.CreateDefault(), store, this, time, logger);
        Engine.RunRecorded += r => Application.Current?.Dispatcher.InvokeAsync(() => { LastRun = r; RunRecorded?.Invoke(r); });
    }

    public AutomationEngine Engine { get; }
    public AutomationRegistry Registry => Engine.Registry;
    public IReadOnlyList<AutomationDefinition> Automations => Engine.Definitions;
    public bool IsPaused => Engine.IsPaused;
    public bool IsGamingMode => _performance.IsGamingMode;
    private ShellViewModel Shell => _sp.GetRequiredService<ShellViewModel>();

    [ObservableProperty] private AutomationRunRecord? _lastRun;
    [ObservableProperty] private WorkSession? _activeWork;
    [ObservableProperty] private string? _activeWorkProject;

    public event Action<AutomationRunRecord>? RunRecorded;
    /// <summary>Definitions, pause state or sources changed.</summary>
    public event EventHandler? Changed;

    /// <summary>What sources are running right now (diagnostics and the Triggers view).</summary>
    public string SourceSummary =>
        $"process:{(_processTimer != null ? _watchedExes.Count : 0)} files:{_watchers.Count} clock:{(_clockTimer != null ? "armed" : "off")}";

    // ── lifecycle ──

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        _messenger.RegisterAll(this);
        _tracking.GameStarted += OnGameStarted;
        _tracking.GameEnded += OnGameEnded;
        _performance.Changed += (_, _) => { _tracking.ReduceMonitoring = _performance.ReduceMonitoring; ConfigureProcessSource(); };
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        try
        {
            var interrupted = await _store.MarkInterruptedAsync();
            if (interrupted > 0) _logger.LogWarning("{Count} automation run(s) were interrupted last time; recorded as incomplete, not re-run", interrupted);
            var a = _settings.Current.Automation;
            await _store.PruneRunsAsync(a.LogRetentionDays, a.LogMaxEntries);
            Engine.IsEnabled = a.Enabled;
            Engine.IsPaused = a.Paused;
            await Engine.ReloadAsync();
            await RecoverWorkSessionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting automation failed");
        }
        _lastScheduleCheckLocal = Now;
        _taskWatermarkUtc = _time.GetUtcNow().UtcDateTime;
        ConfigureSources();
        _logger.LogInformation("Automation ready: {Count} automations ({Enabled} enabled), sources {Sources}", Automations.Count, Automations.Count(x => x.Enabled), SourceSummary);
    }

    /// <summary>Definitions for the UI even before the engine's sources start (the page may open first).</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_started || Engine.Definitions.Count > 0) return;
        await Engine.ReloadAsync();
    }

    /// <summary>ARCADIA STARTED (after the window is shown; never blocks startup).</summary>
    public Task RaiseStartedAsync() => RaiseAsync(new AutomationEvent("app.started", new Dictionary<string, string>()));

    /// <summary>ARCADIA CLOSING: only quick actions, and never longer than the timeout.</summary>
    public void RaiseClosing(TimeSpan timeout)
    {
        if (!Engine.ActiveTriggerTypes().Contains("app.closing")) return;
        using var cts = new CancellationTokenSource(timeout);
        try { Task.Run(() => Engine.RaiseAsync(new AutomationEvent("app.closing", new Dictionary<string, string>()), cts.Token)).Wait(timeout); }
        catch (Exception ex) { _logger.LogWarning(ex, "Closing automations did not finish"); }
    }

    public async Task ReloadAsync()
    {
        await Engine.ReloadAsync();
        ConfigureSources();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetPausedAsync(bool paused)
    {
        Engine.IsPaused = paused;
        _settings.Current.Automation.Paused = paused;
        await _settings.SaveAsync();
        ConfigureSources();
        OnPropertyChanged(nameof(IsPaused));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetEngineEnabled(bool enabled)
    {
        Engine.IsEnabled = enabled;
        ConfigureSources();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<AutomationRunRecord>> RecentRunsAsync(int take, CancellationToken ct = default) => _store.GetRunsAsync(take, null, ct);

    /// <summary>Validates and saves (new or edited); sources re-subscribe to what enabled automations now need.</summary>
    public async Task<AutomationDefinition> SaveAsync(AutomationDefinition definition)
    {
        var errors = Registry.Validate(definition);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(", ", errors));
        var saved = await _store.SaveAsync(definition);
        await ReloadAsync();
        return saved;
    }

    public async Task DeleteAsync(int id)
    {
        await _store.DeleteAsync(id);
        await ReloadAsync();
    }

    public async Task SetEnabledAsync(int id, bool enabled)
    {
        await _store.SetEnabledAsync(id, enabled);
        await ReloadAsync();
    }

    /// <summary>Clears the activity log (runs that guard once-per-occurrence triggers are kept).</summary>
    public Task ClearLogAsync() => _store.PruneRunsAsync(0, 0);

    public Task<AutomationDefinition> CreateAsync(AutomationDefinition definition, CancellationToken ct = default) => SaveAsync(definition);

    private async Task RaiseAsync(AutomationEvent e)
    {
        try { await Engine.RaiseAsync(e); }
        catch (Exception ex) { _logger.LogError(ex, "Automation event {Type} failed", e.Type); }
    }

    private void OnUi(Func<Task> work) => Application.Current?.Dispatcher.InvokeAsync(async () =>
    {
        try { await work(); }
        catch (Exception ex) { _logger.LogError(ex, "Automation source failed"); }
    });

    // ── sources ──

    private void ConfigureSources()
    {
        if (!_started) return;
        ConfigureProcessSource();
        ConfigureFileSource();
        RescheduleClock();
        var types = Engine.ActiveTriggerTypes();
        if (types.Contains("project.status") && _projectStatuses == null) _ = CacheProjectStatusesAsync();
        if (!types.Contains("project.status")) _projectStatuses = null;
    }

    /// <summary>Process triggers: a light name snapshot every few seconds, only for the executables enabled automations name.</summary>
    private void ConfigureProcessSource()
    {
        var exes = Engine.ActiveFor("process.started").Concat(Engine.ActiveFor("process.stopped"))
            .Select(d => AutomationText.ExeName(d.Trigger.Get("exe") ?? "")).Where(e => e.Length > 0).ToHashSet();
        lock (_gate)
        {
            _watchedExes = exes;
            if (exes.Count == 0)
            {
                _processTimer?.Dispose();
                _processTimer = null;
                return;
            }
            var interval = _performance.ReduceMonitoring ? ProcessInterval * 2 : ProcessInterval;
            if (_processTimer == null)
            {
                // processes already running when watching starts did not "start" now: they are the baseline
                _running = ProcessNames().Where(exes.Contains).ToHashSet();
                _processTimer = _time.CreateTimer(_ => ScanProcesses(), null, interval, interval);
            }
            else _processTimer.Change(interval, interval);
        }
    }

    private void ScanProcesses()
    {
        HashSet<string> now, started, stopped;
        lock (_gate)
        {
            now = ProcessNames().Where(_watchedExes.Contains).ToHashSet();
            started = now.Except(_running).ToHashSet();
            stopped = _running.Except(now).ToHashSet();
            _running = now;
        }
        foreach (var exe in started) OnUi(() => RaiseAsync(new AutomationEvent("process.started", new Dictionary<string, string> { ["exe"] = exe })));
        foreach (var exe in stopped) OnUi(() => RaiseAsync(new AutomationEvent("process.stopped", new Dictionary<string, string> { ["exe"] = exe })));
    }

    /// <summary>File triggers: one watcher per folder in use, events settled for a moment and reported once per file.</summary>
    private void ConfigureFileSource()
    {
        var wanted = Engine.ActiveFor("file.created").Concat(Engine.ActiveFor("file.changed"))
            .Select(d => (Folder: Path.TrimEndingDirectorySeparator(d.Trigger.Get("folder") ?? ""), Sub: d.Trigger.GetBool("subfolders")))
            .Where(x => x.Folder.Length > 0)
            .GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Any(x => x.Sub), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var gone in _watchers.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                _watchers[gone].Dispose();
                _watchers.Remove(gone);
            }
            foreach (var (folder, sub) in wanted)
            {
                if (_watchers.TryGetValue(folder, out var existing) && existing.IncludeSubdirectories == sub) continue;
                existing?.Dispose();
                _watchers.Remove(folder);
                if (!Directory.Exists(folder))
                {
                    _logger.LogWarning("Automation folder {Folder} is missing; it is watched again when automations reload", folder);
                    continue;
                }
                try
                {
                    var w = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = sub,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 32 * 1024,
                    };
                    w.Created += (_, e) => QueueFile("file.created", e.FullPath);
                    w.Renamed += (_, e) => QueueFile("file.created", e.FullPath);
                    w.Changed += (_, e) => QueueFile("file.changed", e.FullPath);
                    w.Error += (_, e) =>
                    {
                        _logger.LogWarning(e.GetException(), "Automation watcher for {Folder} failed; recreating", folder);
                        OnUi(() => { lock (_gate) { if (_watchers.Remove(folder, out var dead)) dead.Dispose(); } ConfigureFileSource(); return Task.CompletedTask; });
                    };
                    w.EnableRaisingEvents = true;
                    _watchers[folder] = w;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Cannot watch {Folder}", folder);
                }
            }
        }
    }

    /// <summary>Windows reports a new file several times (create, size, write…): wait until it settles, then report once.</summary>
    private void QueueFile(string type, string path)
    {
        if (Directory.Exists(path) || Path.GetFileName(path).StartsWith('~') || path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate)
        {
            // a "changed" right after "created" belongs to the creation
            var kind = _pendingFiles.TryGetValue(path, out var p) && p.Type == "file.created" ? "file.created" : type;
            _pendingFiles[path] = (kind, _time.GetUtcNow().UtcDateTime + FileSettle);
            _fileTimer ??= _time.CreateTimer(_ => FlushFiles(), null, FileSettle, FileSettle);
        }
    }

    private void FlushFiles()
    {
        List<(string Path, string Type)> due;
        lock (_gate)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            due = _pendingFiles.Where(kv => kv.Value.Due <= now).Select(kv => (kv.Key, kv.Value.Type)).ToList();
            foreach (var (path, _) in due) _pendingFiles.Remove(path);
            if (_pendingFiles.Count == 0) { _fileTimer?.Dispose(); _fileTimer = null; }
        }
        foreach (var (path, type) in due)
        {
            if (!File.Exists(path)) continue;
            var data = new Dictionary<string, string>
            {
                ["path"] = path, ["file"] = Path.GetFileName(path), ["ext"] = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                ["folder"] = Path.GetDirectoryName(path) ?? "",
            };
            OnUi(() => RaiseAsync(new AutomationEvent(type, data)));
        }
    }

    public int PendingFileEvents { get { lock (_gate) return _pendingFiles.Count; } }

    // ── the clock: schedules, daily checks, upcoming events — one timer for the next moment ──

    private static readonly string[] ClockTypes = ["schedule", "trip.started", "trip.ended", "bill.due", "budget.threshold", "goal.completed", "event.upcoming"];

    private void RescheduleClock()
    {
        _clockTimer?.Dispose();
        _clockTimer = null;
        var types = Engine.ActiveTriggerTypes();
        if (!ClockTypes.Any(types.Contains)) return;
        _ = PlanClockAsync(types);
    }

    private async Task PlanClockAsync(IReadOnlySet<string> types)
    {
        try
        {
            var now = Now;
            DateTime next = now.Date.AddDays(1).AddMinutes(1);          // daily checks right after midnight
            foreach (var d in Engine.ActiveFor("schedule"))
                if (ScheduleMath.Next(d.Trigger, now) is { } at && at < next) next = at;
            if (types.Contains("event.upcoming") && await NextEventMomentAsync(now) is { } ev && ev < next) next = ev;
            var delay = next - now;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            if (delay > MaxTimerSleep) delay = MaxTimerSleep;          // clock changes and new days are picked up anyway
            _clockTimer?.Dispose();
            _clockTimer = _time.CreateTimer(_ => OnUi(ClockTickAsync), null, delay, Timeout.InfiniteTimeSpan);
            NextClockCheck = now + delay;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Planning the automation clock failed");
        }
    }

    public DateTime? NextClockCheck { get; private set; }

    private async Task ClockTickAsync()
    {
        var now = Now;
        await CheckSchedulesAsync(_lastScheduleCheckLocal, now);
        _lastScheduleCheckLocal = now;
        var types = Engine.ActiveTriggerTypes();
        if (types.Contains("event.upcoming")) await CheckEventsAsync(now);
        if (_lastDailyCheck != DateOnly.FromDateTime(now)) await DailyChecksAsync();
        RescheduleClock();
    }

    /// <summary>Scheduled occurrences between the last check and now. A moment missed by more than 15 minutes (sleep,
    /// Arcadia closed, clock moved) is skipped and logged, never run late.</summary>
    public async Task CheckSchedulesAsync(DateTime fromLocal, DateTime toLocal)
    {
        foreach (var d in Engine.ActiveFor("schedule"))
        {
            var at = ScheduleMath.Next(d.Trigger, fromLocal);
            while (at is { } occurrence && occurrence <= toLocal)
            {
                if (toLocal - occurrence <= MissedScheduleGrace)
                    await RaiseAsync(new AutomationEvent("schedule", new Dictionary<string, string> { ["time"] = occurrence.ToString("HH:mm", CultureInfo.InvariantCulture) },
                        OnceKey: $"schedule:{occurrence:yyyyMMddHHmm}"));
                else _logger.LogInformation("Schedule of automation {Id} at {At} was missed (PC asleep or Arcadia closed); skipped", d.Id, occurrence);
                at = ScheduleMath.Next(d.Trigger, occurrence);
            }
        }
    }

    private DateOnly _lastDailyCheck;

    /// <summary>Date-based triggers (trips, bills, budgets, goals), once a day and when that data changes.</summary>
    public async Task DailyChecksAsync()
    {
        var types = Engine.ActiveTriggerTypes();
        var today = DateOnly.FromDateTime(Now);
        _lastDailyCheck = today;
        try
        {
            if (types.Contains("trip.started") || types.Contains("trip.ended"))
                foreach (var t in await _sp.GetRequiredService<ITravelStore>().ListTripsAsync())
                {
                    if (t.Status == TripStatus.Cancelled) continue;
                    var data = new Dictionary<string, string> { ["trip"] = t.Id.ToString(CultureInfo.InvariantCulture), ["tripName"] = t.Name };
                    if (t.StartDate == today) await RaiseAsync(new AutomationEvent("trip.started", data, $"trip:{t.Id}:start:{today:yyyyMMdd}"));
                    if (t.EndDate == today) await RaiseAsync(new AutomationEvent("trip.ended", data, $"trip:{t.Id}:end:{today:yyyyMMdd}"));
                }
            var finance = _sp.GetRequiredService<IFinanceStore>();
            var lang = _loc.Culture.TwoLetterISOLanguageName == "ar" ? "ar" : "en";
            if (types.Contains("bill.due"))
                foreach (var days in Engine.ActiveFor("bill.due").Select(d => d.Trigger.GetInt("days") ?? 1).Distinct())
                    foreach (var b in await finance.GetUpcomingBillsAsync(today.AddDays(days), today.AddDays(days), lang, 0))
                        await RaiseAsync(new AutomationEvent("bill.due", new Dictionary<string, string>
                        {
                            ["bill"] = b.Item.Id.ToString(CultureInfo.InvariantCulture), ["billName"] = b.Item.Name, ["due"] = b.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            ["days"] = days.ToString(CultureInfo.InvariantCulture),
                        }, $"bill:{b.Item.Id}:{b.DueDate:yyyyMMdd}"));
            if (types.Contains("budget.threshold"))
                foreach (var s in await finance.GetBudgetStatusAsync(new DateOnly(today.Year, today.Month, 1)))
                    await RaiseAsync(new AutomationEvent("budget.threshold", new Dictionary<string, string>
                    {
                        ["budget"] = s.Budget.Id.ToString(CultureInfo.InvariantCulture), ["budgetName"] = s.Budget.Name ?? s.Budget.Category?.Name ?? "",
                        ["percent"] = ((int)Math.Floor(s.Percent)).ToString(CultureInfo.InvariantCulture),
                    }, $"budget:{s.Budget.Id}:{s.PeriodStart:yyyyMMdd}"));
            if (types.Contains("goal.completed"))
                foreach (var g in await finance.GetSavingsGoalsAsync(today))
                    if (g.Target > 0 && g.Saved >= g.Target)
                        await RaiseAsync(new AutomationEvent("goal.completed", new Dictionary<string, string>
                        {
                            ["goal"] = g.Goal.Id.ToString(CultureInfo.InvariantCulture), ["goalName"] = g.Goal.Name,
                        }, $"goal:{g.Goal.Id}"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Daily automation checks failed");
        }
    }

    private async Task<DateTime?> NextEventMomentAsync(DateTime now)
    {
        var leads = Engine.ActiveFor("event.upcoming").Select(d => d.Trigger.GetInt("minutes") ?? 10).Distinct().ToList();
        var items = await _sp.GetRequiredService<ICalendarQueryService>().GetItemsAsync(now, now.AddDays(2), CalendarSources.Events);
        DateTime? next = null;
        foreach (var item in items.Where(i => !i.IsAllDay))
            foreach (var lead in leads)
            {
                var at = item.Start.AddMinutes(-lead);
                if (at > now && (next == null || at < next)) next = at;
            }
        return next;
    }

    private async Task CheckEventsAsync(DateTime now)
    {
        var leads = Engine.ActiveFor("event.upcoming").Select(d => d.Trigger.GetInt("minutes") ?? 10).Distinct().ToList();
        var items = await _sp.GetRequiredService<ICalendarQueryService>().GetItemsAsync(now.AddMinutes(-1), now.AddDays(1), CalendarSources.Events);
        foreach (var item in items.Where(i => !i.IsAllDay))
            foreach (var lead in leads)
            {
                var at = item.Start.AddMinutes(-lead);
                if (at <= now.AddSeconds(30) && at > now.AddMinutes(-2) && item.Start > now.AddMinutes(-1))
                    await RaiseAsync(new AutomationEvent("event.upcoming", new Dictionary<string, string>
                    {
                        ["event"] = item.Ref.Id.ToString(CultureInfo.InvariantCulture), ["eventTitle"] = item.Title,
                        ["start"] = item.Start.ToString("HH:mm", CultureInfo.InvariantCulture), ["lead"] = lead.ToString(CultureInfo.InvariantCulture),
                    }, $"event:{item.Ref.Id}:{item.Start:yyyyMMddHHmm}:{lead}"));
            }
    }

    private void OnTimeChanged(object? sender, EventArgs e) => OnUi(async () =>
    {
        // manual clock change / time zone / DST: re-plan from the new "now" (occurrences in between follow the missed rule)
        TimeZoneInfo.ClearCachedData();
        await CheckSchedulesAsync(_lastScheduleCheckLocal, Now);
        _lastScheduleCheckLocal = Now;
        RescheduleClock();
    });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _suspendedAtUtc = _time.GetUtcNow().UtcDateTime;
            if (ActiveWork != null) _ = _work.HeartbeatAsync(_suspendedAtUtc.Value);
        }
        else if (e.Mode == PowerModes.Resume)
            OnUi(async () =>
            {
                await CheckSchedulesAsync(_lastScheduleCheckLocal, Now);
                _lastScheduleCheckLocal = Now;
                RescheduleClock();
                // a work session does not count the time asleep: close it at suspend, continue from now
                if (ActiveWork is { } w && _suspendedAtUtc is { } slept)
                {
                    await _work.StopAsync(slept, null);
                    var again = await _work.StartAsync(w.ProjectId, _time.GetUtcNow().UtcDateTime, w.IsAutomatic);
                    ActiveWork = again;
                }
                _suspendedAtUtc = null;
            });
    }

    // ── data-change triggers ──

    public void Receive(OrganizerChangedMessage message)
    {
        var types = Engine.ActiveTriggerTypes();
        if ((message.What & OrganizerChange.Tasks) != 0 && types.Contains("task.completed")) OnUi(CheckCompletedTasksAsync);
        if ((message.What & OrganizerChange.Events) != 0 && types.Contains("event.upcoming")) RescheduleClock();
        if ((message.What & OrganizerChange.Finance) != 0 && (types.Contains("bill.due") || types.Contains("budget.threshold") || types.Contains("goal.completed"))) OnUi(DailyChecksAsync);
    }

    public void Receive(TravelChangedMessage message)
    {
        var types = Engine.ActiveTriggerTypes();
        if (types.Contains("trip.started") || types.Contains("trip.ended")) OnUi(DailyChecksAsync);
    }

    public void Receive(ProjectsChangedMessage message)
    {
        if (_projectStatuses != null) OnUi(CheckProjectStatusesAsync);
    }

    private async Task CheckCompletedTasksAsync()
    {
        var since = _taskWatermarkUtc;
        var rows = await _sp.GetRequiredService<ITaskStore>().QueryAsync(new TaskQuery(TaskView.Completed, Take: 30), Now);
        var fresh = rows.Where(t => t.CompletedAtUtc is { } c && c > since).OrderBy(t => t.CompletedAtUtc).ToList();
        if (fresh.Count == 0) return;
        _taskWatermarkUtc = fresh.Max(t => t.CompletedAtUtc!.Value);
        foreach (var t in fresh)
        {
            var data = new Dictionary<string, string> { ["task"] = t.Id.ToString(CultureInfo.InvariantCulture), ["taskTitle"] = t.Title };
            if (t.Context is { Kind: EntityKind.Project } p) data["project"] = p.Id.ToString(CultureInfo.InvariantCulture);
            if (t.Context is { Kind: EntityKind.Trip } tr) data["trip"] = tr.Id.ToString(CultureInfo.InvariantCulture);
            await RaiseAsync(new AutomationEvent("task.completed", data, $"task:{t.Id}:{t.CompletedAtUtc:yyyyMMddHHmmss}"));
        }
    }

    private async Task CacheProjectStatusesAsync() =>
        _projectStatuses = (await _sp.GetRequiredService<IProjectStore>().ListAsync()).ToDictionary(p => p.Id, p => p.Status);

    private async Task CheckProjectStatusesAsync()
    {
        var old = _projectStatuses;
        if (old == null) return;
        var now = await _sp.GetRequiredService<IProjectStore>().ListAsync();
        _projectStatuses = now.ToDictionary(p => p.Id, p => p.Status);
        foreach (var p in now)
            if (old.TryGetValue(p.Id, out var before) && before != p.Status)
                await RaiseAsync(new AutomationEvent("project.status", new Dictionary<string, string>
                {
                    ["project"] = p.Id.ToString(CultureInfo.InvariantCulture), ["projectName"] = p.Name, ["from"] = before.ToString(), ["to"] = p.Status.ToString(),
                }));
    }

    // ── games: sessions drive Gaming Mode, the game triggers and the session-note offer ──

    private void OnGameStarted(ActiveSession s)
    {
        _performance.GameStarted(s.GameId, _tracking.AutoGamingGames.Contains(s.GameId));
        _ = RaiseAsync(new AutomationEvent("game.started", new Dictionary<string, string>
        {
            ["game"] = s.GameId.ToString(CultureInfo.InvariantCulture), ["gameTitle"] = s.Title,
            ["start"] = s.StartedAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
        }));
    }

    private void OnGameEnded(ActiveSession s, int minutes)
    {
        _performance.GameEnded(s.GameId);
        _ = RaiseAsync(new AutomationEvent("game.stopped", new Dictionary<string, string>
        {
            ["game"] = s.GameId.ToString(CultureInfo.InvariantCulture), ["gameTitle"] = s.Title,
            ["start"] = s.StartedAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            ["end"] = s.LastSeenUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            ["minutes"] = minutes.ToString(CultureInfo.InvariantCulture),
        }));
        if (minutes > 0 && _settings.Current.Performance.OfferSessionNote)
            _notifier.Notify("gaming", _loc.Format("Auto_SessionNoteTitle", s.Title), _loc.Format("Auto_SessionNoteText", minutes), new EntityRef(EntityKind.Game, s.GameId));
    }

    // ── work sessions ──

    private async Task RecoverWorkSessionAsync()
    {
        if (await _work.GetOpenAsync() is not { } open) return;
        var now = _time.GetUtcNow().UtcDateTime;
        if (now - open.LastSeenAtUtc > WorkRestoreGap)
        {
            // Arcadia (or the PC) was gone: count up to the last heartbeat only
            var closed = await _work.StopAsync(open.LastSeenAtUtc, null);
            _logger.LogInformation("Recovered work session {Id}: closed at last heartbeat ({Minutes} min)", open.Id, closed?.Minutes);
            return;
        }
        await SetActiveWorkAsync(open);
    }

    private async Task SetActiveWorkAsync(WorkSession? session)
    {
        ActiveWork = session;
        ActiveWorkProject = session == null ? null : (await _sp.GetRequiredService<IProjectStore>().GetAsync(session.ProjectId))?.Name;
        _workTimer?.Dispose();
        _workTimer = session == null ? null : _time.CreateTimer(_ => _ = _work.HeartbeatAsync(_time.GetUtcNow().UtcDateTime), null, WorkHeartbeat, WorkHeartbeat);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task StartWorkSessionAsync(int projectId, bool automatic, CancellationToken ct)
    {
        var s = await _work.StartAsync(projectId, _time.GetUtcNow().UtcDateTime, automatic, ct);
        await SetActiveWorkAsync(s);
        _notifier.Notify("info", _loc.Format("Auto_WorkStarted", ActiveWorkProject ?? "?"), string.Empty, new EntityRef(EntityKind.Project, projectId));
    }

    public async Task StopWorkSessionAsync(CancellationToken ct) => await StopWorkAsync(null, ct);

    public async Task<WorkSession?> StopWorkAsync(string? note, CancellationToken ct = default)
    {
        var name = ActiveWorkProject;
        var s = await _work.StopAsync(_time.GetUtcNow().UtcDateTime, note, ct);
        await SetActiveWorkAsync(null);
        if (s != null) _notifier.Notify("info", _loc.Format("Auto_WorkStopped", name ?? "?", s.Minutes), string.Empty, new EntityRef(EntityKind.Project, s.ProjectId));
        return s;
    }

    // ── IAutomationHost (actions only use existing services; nothing here deletes, kills or runs commands) ──

    public DateTime Now => _time.GetLocalNow().DateTime;
    public string T(string key, params object?[] args) => args.Length == 0 ? _loc[key] : _loc.Format(key, args.Select(a => a ?? "").ToArray());
    public void SetGamingMode(bool on, string reason) => Application.Current.Dispatcher.Invoke(() => _performance.SetGamingMode(on, reason));
    void IAutomationService.SetGamingMode(bool on) => SetGamingMode(on, "manual");
    public bool IsProcessRunning(string executable) => ProcessNames().Contains(AutomationText.ExeName(executable));
    public bool? IsOnAcPower => SystemParameters.PowerLineStatus switch { PowerLineStatus.Online => true, PowerLineStatus.Offline => false, _ => null };
    public bool IsNetworkAvailable => NetworkInterface.GetIsNetworkAvailable();

    public Task NotifyAsync(string category, string title, string text, EntityRef? target = null)
    {
        Application.Current.Dispatcher.Invoke(() => _notifier.Notify(category, title, text, target));
        return Task.CompletedTask;
    }

    public async Task OpenSectionAsync(string section, string? tab) => await Application.Current.Dispatcher.Invoke(() => Shell.NavigateToAsync(section, tab));
    public async Task OpenEntityAsync(EntityRef target) => await Application.Current.Dispatcher.Invoke(() => Shell.OpenAsync(target, null));

    public Task OpenQuickCaptureAsync(string kind, string? text)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // bring Arcadia forward only when nobody is gaming (never interrupt a full-screen game)
            if (!_performance.IsGamingMode) Shell.RequestShowWindow();
            Shell.QuickCapture.Open(Enum.TryParse<CaptureKind>(kind, true, out var k) ? k : CaptureKind.Note, null, text);
        });
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string message) => Application.Current.Dispatcher.Invoke(() =>
    {
        var window = Application.Current.MainWindow;
        if (window is { IsVisible: true, WindowState: not WindowState.Minimized, IsActive: true } && !_performance.IsGamingMode)
            return Task.FromResult(_sp.GetRequiredService<IDialogService>().Confirm(message, _loc["Auto_ConfirmButton"]));
        _notifier.Notify("warning", _loc["Auto_NeedsConfirmTitle"], message);
        return Task.FromResult(false);
    });

    public Task OpenPathAsync(string path, bool revealFolder)
    {
        var full = Path.GetFullPath(path);
        if (revealFolder)
        {
            if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException(_loc["Auto_PathMissing"], full);
            Application.Current.Dispatcher.Invoke(() => _sp.GetRequiredService<DocumentActions>().Reveal(full));
            return Task.CompletedTask;
        }
        if (!File.Exists(full)) throw new FileNotFoundException(_loc["Auto_PathMissing"], full);
        // opening a program or script would be arbitrary execution: never from automation
        if (NeverOpen.Contains(Path.GetExtension(full))) throw new InvalidOperationException(_loc["Auto_NoPrograms"]);
        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }

    public async Task<string> AddDocumentAsync(string path, int? projectId, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(_loc["Auto_PathMissing"], path);
        var result = await Application.Current.Dispatcher.Invoke(() => _sp.GetRequiredService<DocumentActions>().AddPathsAsync([path], projectId));
        return result == null ? "—" : $"+{result.Added}";
    }

    public async Task<int> CreateTaskAsync(string title, DateOnly? due, EntityRef? context, CancellationToken ct)
    {
        var t = await _sp.GetRequiredService<ITaskStore>().CreateAsync(new TaskItemDraft(title.Trim(), DueDate: due, Context: Linkable(context)), ct);
        Application.Current.Dispatcher.Invoke(() => _messenger.Send(new OrganizerChangedMessage(OrganizerChange.Tasks)));
        return t.Id;
    }

    public async Task<int> CreateNoteAsync(string title, string text, EntityRef? context, CancellationToken ct)
    {
        var n = await _sp.GetRequiredService<INoteStore>().CreateAsync(new NoteDraft(title.Trim(), text, Context: Linkable(context)), ct);
        Application.Current.Dispatcher.Invoke(() => _messenger.Send(new OrganizerChangedMessage(OrganizerChange.Notes)));
        return n.Id;
    }

    private static EntityRef? Linkable(EntityRef? r) => r is { Kind: EntityKind.Project or EntityKind.Trip or EntityKind.Game or EntityKind.Place or EntityKind.Event } ? r : null;

    public async Task SetGameStatusPlayingAsync(int gameId, CancellationToken ct)
    {
        var catalog = _sp.GetRequiredService<IGameCatalog>();
        var game = await catalog.GetGameDetailAsync(gameId, ct) ?? throw new InvalidOperationException(_loc["Auto_NoRecord"]);
        if (game.Status == GameStatus.Playing) return;
        game.Status = GameStatus.Playing;
        await catalog.SaveGameAsync(game, ct);
        Application.Current.Dispatcher.Invoke(() => _messenger.Send(new LibraryChangedMessage(gameId)));
    }

    public async Task ExpectGameAsync(int gameId, CancellationToken ct)
    {
        var game = await _sp.GetRequiredService<IGameCatalog>().GetGameDetailAsync(gameId, ct) ?? throw new InvalidOperationException(_loc["Auto_NoRecord"]);
        await _tracking.NotifyLaunchedAsync(game);
    }

    public Task StopGameSessionAsync(CancellationToken ct) => _tracking.StopCurrentAsync();

    public async Task SetProjectStatusAsync(int projectId, string status, CancellationToken ct)
    {
        await _sp.GetRequiredService<IProjectStore>().SetStatusAsync(projectId, Enum.Parse<ProjectStatus>(status), ct);
        Application.Current.Dispatcher.Invoke(() => _messenger.Send(new ProjectsChangedMessage(projectId)));
    }

    public async Task<string> PingAsync(string host, CancellationToken ct)
    {
        if (!Arcadia.Core.ItTools.Network.NetworkTarget.TryParse(host, out var target, out var error)) throw new InvalidOperationException(error ?? host);
        var ping = _sp.GetRequiredService<Arcadia.Core.ItTools.Network.IPingService>();
        var ok = new List<long>();
        var sent = 0;
        await foreach (var r in ping.PingAsync(target, 3, 1500, ct))
        {
            sent++;
            if (r.Outcome == Arcadia.Core.ItTools.Network.PingOutcome.Success && r.RoundTripMs is { } ms) ok.Add(ms);
        }
        return ok.Count == 0 ? _loc.Format("Auto_PingFail", host) : _loc.Format("Auto_PingOk", host, (long)ok.Average(), 100 * (sent - ok.Count) / Math.Max(1, sent));
    }

    // ── process names (Toolhelp snapshot: names only, no handles to other processes) ──

    public static HashSet<string> ProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return names;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32First(snap, ref entry))
                do names.Add(entry.szExeFile.ToLowerInvariant());
                while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return names;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    public void Dispose()
    {
        SystemEvents.TimeChanged -= OnTimeChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _processTimer?.Dispose();
        _clockTimer?.Dispose();
        _fileTimer?.Dispose();
        _workTimer?.Dispose();
        lock (_gate)
        {
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
    }
}
