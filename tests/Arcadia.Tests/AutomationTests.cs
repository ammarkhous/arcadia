using Arcadia.Core.Automation;
using Arcadia.Core.Organizer;
using Arcadia.Core.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arcadia.Tests;

/// <summary>In-memory store for engine tests.</summary>
internal sealed class MemoryAutomationStore : IAutomationStore
{
    public readonly List<AutomationDefinition> Items = [];
    public readonly List<AutomationRunEntity> Runs = [];

    public Task<IReadOnlyList<AutomationDefinition>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AutomationDefinition>>(Items.ToList());
    public Task<AutomationDefinition?> GetAsync(int id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
    public Task<AutomationDefinition> SaveAsync(AutomationDefinition d, CancellationToken ct = default)
    {
        var saved = d.Id == 0 ? d with { Id = Items.Count + 1 } : d;
        Items.RemoveAll(i => i.Id == saved.Id);
        Items.Add(saved);
        return Task.FromResult(saved);
    }
    public Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default) { var i = Items.FindIndex(x => x.Id == id); Items[i] = Items[i] with { Enabled = enabled }; return Task.CompletedTask; }
    public Task DeleteAsync(int id, CancellationToken ct = default) { Items.RemoveAll(i => i.Id == id); return Task.CompletedTask; }
    public Task<int> BeginRunAsync(AutomationRunEntity run, CancellationToken ct = default) { run.Id = Runs.Count + 1; Runs.Add(run); return Task.FromResult(run.Id); }
    public Task CompleteRunAsync(int runId, RunStatus status, int durationMs, string summary, string detailsJson, CancellationToken ct = default)
    {
        var r = Runs[runId - 1]; r.Status = status; r.Summary = summary; r.DetailsJson = detailsJson; return Task.CompletedTask;
    }
    public Task<bool> HasRunAsync(int automationId, string eventKey, CancellationToken ct = default) => Task.FromResult(Runs.Any(r => r.AutomationId == automationId && r.EventKey == eventKey && !r.IsSimulation));
    public Task<IReadOnlyList<AutomationRunRecord>> GetRunsAsync(int take, int? automationId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AutomationRunRecord>>([]);
    public Task<int> MarkInterruptedAsync(CancellationToken ct = default)
    {
        var n = 0;
        foreach (var r in Runs.Where(r => r.Status == RunStatus.Running)) { r.Status = RunStatus.Incomplete; n++; }
        return Task.FromResult(n);
    }
    public Task PruneRunsAsync(int keepDays, int keepMax, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Records what actions asked for; nothing real happens.</summary>
internal sealed class RecordingHost : IAutomationHost
{
    public DateTime Now { get; set; } = new(2026, 9, 28, 8, 30, 0);
    public List<string> Calls { get; } = [];
    public bool IsGamingMode { get; set; }
    public bool Confirm { get; set; }
    public HashSet<string> Running { get; } = [];
    public Func<Task>? OnNotify { get; set; }
    public bool? IsOnAcPower => true;
    public bool IsNetworkAvailable => true;
    public string T(string key, params object?[] args) => args.Length == 0 ? key : key + "|" + string.Join("|", args);
    public void SetGamingMode(bool on, string reason) { IsGamingMode = on; Calls.Add("gaming:" + on); }
    public bool IsProcessRunning(string executable) => Running.Contains(executable);
    public async Task NotifyAsync(string category, string title, string text, EntityRef? target = null) { Calls.Add("notify:" + title); if (OnNotify != null) await OnNotify(); }
    public Task OpenSectionAsync(string section, string? tab) { Calls.Add("open:" + section + "/" + tab); return Task.CompletedTask; }
    public Task OpenEntityAsync(EntityRef target) { Calls.Add("entity:" + target); return Task.CompletedTask; }
    public Task OpenQuickCaptureAsync(string kind, string? text) { Calls.Add("capture:" + kind + ":" + text); return Task.CompletedTask; }
    public Task<bool> ConfirmAsync(string message) { Calls.Add("confirm?"); return Task.FromResult(Confirm); }
    public Task OpenPathAsync(string path, bool revealFolder) { Calls.Add("path:" + path); return Task.CompletedTask; }
    public Task<string> AddDocumentAsync(string path, int? projectId, CancellationToken ct) { Calls.Add("doc:" + path); return Task.FromResult("ok"); }
    public Task<int> CreateTaskAsync(string title, DateOnly? due, EntityRef? context, CancellationToken ct) { Calls.Add("task:" + title + ":" + due + ":" + context); return Task.FromResult(1); }
    public Task<int> CreateNoteAsync(string title, string text, EntityRef? context, CancellationToken ct) { Calls.Add("note:" + title); return Task.FromResult(1); }
    public Task SetGameStatusPlayingAsync(int gameId, CancellationToken ct) { Calls.Add("playing:" + gameId); return Task.CompletedTask; }
    public Task ExpectGameAsync(int gameId, CancellationToken ct) { Calls.Add("expect:" + gameId); return Task.CompletedTask; }
    public Task StopGameSessionAsync(CancellationToken ct) { Calls.Add("stopgame"); return Task.CompletedTask; }
    public Task StartWorkSessionAsync(int projectId, bool automatic, CancellationToken ct) { Calls.Add("work:" + projectId); return Task.CompletedTask; }
    public Task StopWorkSessionAsync(CancellationToken ct) { Calls.Add("workstop"); return Task.CompletedTask; }
    public Task SetProjectStatusAsync(int projectId, string status, CancellationToken ct) { Calls.Add("status:" + projectId + ":" + status); return Task.CompletedTask; }
    public Task<string> PingAsync(string host, CancellationToken ct) => throw new InvalidOperationException("network down");
}

public sealed class AutomationEngineTests
{
    private readonly MemoryAutomationStore _store = new();
    private readonly RecordingHost _host = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 9, 28, 6, 30, 0, DateTimeKind.Utc));
    private readonly AutomationEngine _engine;

    internal sealed class FakeClock(DateTime utc) : TimeProvider
    {
        public DateTime Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(Utc);
    }

    public AutomationEngineTests() => _engine = new AutomationEngine(AutomationCatalog.CreateDefault(), _store, _host, _clock, NullLogger.Instance);

    private static AutomationStep S(string type, params (string K, string V)[] settings) => new(type, settings.ToDictionary(x => x.K, x => x.V));

    private async Task<AutomationDefinition> Add(string name, AutomationStep trigger, AutomationStep[] actions, AutomationStep[]? conditions = null,
        ConditionMode mode = ConditionMode.All, int cooldown = 0, bool enabled = true)
    {
        var d = await _store.SaveAsync(new AutomationDefinition(0, name, null, enabled, trigger, mode, conditions ?? [], actions, cooldown));
        await _engine.ReloadAsync();
        return d;
    }

    private static AutomationEvent Proc(string exe) => new("process.started", new Dictionary<string, string> { ["exe"] = exe });

    [Fact]
    public async Task Matching_trigger_runs_actions_in_order_and_records_success()
    {
        await Add("Gaming", S("process.started", ("exe", "Cyberpunk2077.exe")), [S("gaming.on"), S("notify", ("title", "Started {exe} at {time}"))]);
        var runs = await _engine.RaiseAsync(Proc("cyberpunk2077.exe"));
        Assert.Equal(RunStatus.Success, Assert.Single(runs).Status);
        Assert.Equal(["gaming:True", "notify:Started cyberpunk2077.exe at 08:30"], _host.Calls);
        Assert.Empty(await _engine.RaiseAsync(Proc("steam.exe")));         // other executables do not match
    }

    [Fact]
    public async Task Disabled_automation_and_paused_engine_do_nothing()
    {
        await Add("Off", S("app.started"), [S("gaming.on")], enabled: false);
        Assert.Empty(await _engine.RaiseAsync(new AutomationEvent("app.started", new Dictionary<string, string>())));
        await Add("On", S("app.started"), [S("gaming.on")]);
        _engine.IsPaused = true;
        Assert.Empty(await _engine.RaiseAsync(new AutomationEvent("app.started", new Dictionary<string, string>())));
        Assert.Empty(_engine.ActiveTriggerTypes());                      // sources unsubscribe while paused
        _engine.IsPaused = false;
        Assert.Single(await _engine.RaiseAsync(new AutomationEvent("app.started", new Dictionary<string, string>())));
    }

    [Fact]
    public async Task Conditions_all_and_any()
    {
        var morning = S("time.between", ("after", "06:00"), ("before", "11:00"));
        var weekend = S("weekday", ("days", "sat,sun"));
        await Add("All", S("app.started"), [S("open.today")], [morning, weekend], ConditionMode.All);
        await Add("Any", S("app.started"), [S("notify", ("title", "any"))], [morning, weekend], ConditionMode.Any);
        var runs = await _engine.RaiseAsync(new AutomationEvent("app.started", new Dictionary<string, string>()));   // Monday 08:30
        Assert.Equal(RunStatus.Skipped, runs.Single(r => r.AutomationName == "All").Status);
        Assert.Equal(RunStatus.Success, runs.Single(r => r.AutomationName == "Any").Status);
        Assert.Equal([true, false], runs.Single(r => r.AutomationName == "All").Details.Conditions.Select(c => c.Ok));
        Assert.True(AutomationText.InWindow(new TimeOnly(23, 30), new TimeOnly(22, 0), new TimeOnly(2, 0)));   // overnight window
    }

    [Fact]
    public async Task Cooldown_and_duplicate_events_are_suppressed()
    {
        await Add("Cool", S("process.started", ("exe", "a.exe")), [S("notify", ("title", "x"))], cooldown: 300);
        Assert.Single(await _engine.RaiseAsync(Proc("a.exe")));
        Assert.Empty(await _engine.RaiseAsync(Proc("a.exe")));            // duplicate within the window
        Assert.Equal(1, _engine.SuppressedDuplicates);
        _clock.Utc = _clock.Utc.AddMinutes(2);
        Assert.Empty(await _engine.RaiseAsync(Proc("a.exe")));            // cooldown
        _clock.Utc = _clock.Utc.AddMinutes(4);
        Assert.Single(await _engine.RaiseAsync(Proc("a.exe")));
    }

    [Fact]
    public async Task Once_per_occurrence_keys_never_fire_twice()
    {
        await Add("Bill", S("bill.due"), [S("notify", ("title", "{billName}"))]);
        var e = new AutomationEvent("bill.due", new Dictionary<string, string> { ["bill"] = "3", ["billName"] = "Rent", ["days"] = "1" }, OnceKey: "bill:3:2026-10-01");
        Assert.Single(await _engine.RaiseAsync(e));
        _clock.Utc = _clock.Utc.AddHours(5);
        Assert.Empty(await _engine.RaiseAsync(e));
    }

    [Fact]
    public async Task Loops_between_automations_are_blocked_and_logged()
    {
        // A notifies; the notification (simulating an action that raises an event) triggers B, which triggers A again.
        await Add("A", S("process.started", ("exe", "a.exe")), [S("notify", ("title", "A"))]);
        await Add("B", S("process.started", ("exe", "b.exe")), [S("notify", ("title", "B"))]);
        var depth = 0;
        _host.OnNotify = async () =>
        {
            if (++depth > 10) throw new InvalidOperationException("runaway");
            _clock.Utc = _clock.Utc.AddSeconds(10);                         // not a duplicate: a genuine new event
            var next = _host.Calls[^1] == "notify:A" ? "b.exe" : "a.exe";
            await _engine.RaiseAsync(Proc(next));
        };
        await _engine.RaiseAsync(Proc("a.exe"));
        Assert.Equal(1, _engine.BlockedLoops);
        Assert.Contains(_store.Runs, r => r.Status == RunStatus.Blocked && r.AutomationName == "A");
        Assert.Equal(["notify:A", "notify:B"], _host.Calls);
    }

    [Fact]
    public async Task A_failing_action_stops_its_automation_only()
    {
        await Add("Bad", S("app.started"), [S("ping", ("host", "router")), S("notify", ("title", "after"))]);
        await Add("Good", S("app.started"), [S("notify", ("title", "good"))]);
        var runs = await _engine.RaiseAsync(new AutomationEvent("app.started", new Dictionary<string, string>()));
        Assert.Equal(RunStatus.Failed, runs.Single(r => r.AutomationName == "Bad").Status);
        Assert.Equal("network down", runs.Single(r => r.AutomationName == "Bad").Details.Error);
        Assert.Equal(RunStatus.Success, runs.Single(r => r.AutomationName == "Good").Status);
        Assert.DoesNotContain("notify:after", _host.Calls);
    }

    [Fact]
    public async Task Confirmation_actions_ask_and_closing_allows_only_quick_actions()
    {
        await Add("Status", S("task.completed"), [S("project.status", ("project", "4"), ("status", "Completed"))]);
        var e = new AutomationEvent("task.completed", new Dictionary<string, string> { ["task"] = "1" });
        Assert.Equal(RunStatus.Blocked, Assert.Single(await _engine.RaiseAsync(e)).Status);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("status:"));
        _host.Confirm = true;
        _clock.Utc = _clock.Utc.AddMinutes(1);
        Assert.Equal(RunStatus.Success, Assert.Single(await _engine.RaiseAsync(e)).Status);
        Assert.Contains("status:4:Completed", _host.Calls);

        await Add("Exit", S("app.closing"), [S("open.today"), S("work.stop")]);
        await _engine.RaiseAsync(new AutomationEvent("app.closing", new Dictionary<string, string>()));
        Assert.Contains("workstop", _host.Calls);
        Assert.DoesNotContain("open:calendar/Today", _host.Calls);         // not allowed while closing
    }

    [Fact]
    public async Task Test_runs_a_labelled_simulation_with_sample_data()
    {
        var d = await Add("Files", S("file.created", ("folder", @"C:\Downloads"), ("extensions", "pdf")), [S("notify", ("title", "New {file}"))]);
        var run = await _engine.TestAsync(d);
        Assert.True(run.IsSimulation);
        Assert.Equal(RunStatus.Success, run.Status);
        Assert.Contains("notify:New sample.pdf", _host.Calls);
        Assert.True(_store.Runs.Single().IsSimulation);
    }

    [Fact]
    public async Task Interrupted_runs_become_incomplete_and_are_not_rerun()
    {
        _store.Runs.Add(new AutomationRunEntity { Id = 1, AutomationId = 1, Status = RunStatus.Running });
        Assert.Equal(1, await _store.MarkInterruptedAsync());
        Assert.Equal(RunStatus.Incomplete, _store.Runs[0].Status);
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public async Task Cancellation_stops_the_run()
    {
        await Add("C", S("app.started"), [S("notify", ("title", "x"))]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var run = Assert.Single(await _engine.RaiseAsync(new AutomationEvent("app.started", new Dictionary<string, string>()), cts.Token));
        Assert.Equal(RunStatus.Failed, run.Status);                       // recorded as cancelled, nothing executed
        Assert.Equal("Auto_Cancelled", run.Details.Error);
        Assert.Empty(_host.Calls);
    }

    // ── registry, validation and safety ──

    [Fact]
    public void Registry_validates_and_never_registers_restricted_actions()
    {
        var reg = AutomationCatalog.CreateDefault();
        Assert.All(reg.Actions, a => Assert.NotEqual(ActionSafety.Restricted, a.Safety));
        Assert.Throws<InvalidOperationException>(() => reg.Add(new ActionDescriptor("file.delete", "files", "Trash", ActionSafety.Restricted) { Execute = (_, _, _, _) => Task.FromResult<string?>(null) }));
        string[] forbidden = ["delete", "kill", "registry", "service", "script", "powershell", "cmd", "shell", "run", "exec", "format"];
        Assert.All(reg.Actions, a => Assert.DoesNotContain(forbidden, f => a.Id.Split('.').Contains(f)));

        var bad = new AutomationDefinition(0, "", null, true, new AutomationStep("process.started", new() { ["exe"] = "not an exe; rm -rf" }), ConditionMode.All, [], []);
        var errors = reg.Validate(bad);
        Assert.Contains("Auto_ErrName", errors);
        Assert.Contains(errors, e => e.StartsWith("Auto_ErrInvalid"));
        Assert.Contains("Auto_ErrNoActions", errors);
    }

    [Fact]
    public void Import_is_validated_and_always_disabled()
    {
        var reg = AutomationCatalog.CreateDefault();
        var good = new AutomationDefinition(1, "Morning", null, true, new AutomationStep("app.started"), ConditionMode.All,
            [new AutomationStep("time.between", new() { ["after"] = "06:00", ["before"] = "11:00" })], [new AutomationStep("open.today")]);
        var json = AutomationTransfer.Export([good], DateTime.UtcNow);
        var result = AutomationTransfer.Import(json, reg);
        var imported = Assert.Single(result.Accepted);
        Assert.False(imported.Enabled);
        Assert.Equal(0, imported.Id);

        var evil = json.Replace("\"open.today\"", "\"system.shell\"");
        var rejected = AutomationTransfer.Import(evil, reg);
        Assert.Empty(rejected.Accepted);
        Assert.Contains(rejected.Errors, e => e.Contains("Auto_ErrUnknownAction:system.shell"));
        Assert.Empty(AutomationTransfer.Import("{\"format\":\"arcadia-automation\",\"version\":99}", reg).Accepted);
        Assert.Equal("Auto_ImportMalformed", Assert.Single(AutomationTransfer.Import("{not json", reg).Errors));
        Assert.Equal("Auto_ImportNotArcadia", Assert.Single(AutomationTransfer.Import("{\"$type\":\"System.Diagnostics.Process\"}", reg).Errors));
    }

    // ── schedules (Phase 6 recurrence underneath) ──

    [Fact]
    public void Schedules_daily_weekly_monthly_once_and_dst()
    {
        var at = new DateTime(2026, 9, 28, 17, 0, 0);                    // Monday 17:00
        Assert.Equal(new DateTime(2026, 9, 28, 18, 0, 0), ScheduleMath.Next(S("schedule", ("repeat", "daily"), ("time", "18:00")), at));
        Assert.Equal(new DateTime(2026, 9, 29, 9, 0, 0), ScheduleMath.Next(S("schedule", ("repeat", "daily"), ("time", "09:00")), at));
        Assert.Equal(new DateTime(2026, 9, 30, 8, 0, 0), ScheduleMath.Next(S("schedule", ("repeat", "weekly"), ("days", "wed,fri"), ("time", "08:00")), at));
        Assert.Equal(new DateTime(2026, 10, 1, 7, 0, 0), ScheduleMath.Next(S("schedule", ("repeat", "monthly"), ("day", "1"), ("time", "07:00")), at));
        Assert.Equal(new DateTime(2026, 9, 30, 7, 0, 0), ScheduleMath.Next(S("schedule", ("repeat", "monthly"), ("day", "31"), ("time", "07:00")), at));   // short month → last day
        Assert.Null(ScheduleMath.Next(S("schedule", ("repeat", "once"), ("date", "2026-09-01"), ("time", "07:00")), at));                     // past one-off
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var spring = ScheduleMath.Next(S("schedule", ("repeat", "daily"), ("time", "02:30")), new DateTime(2026, 3, 28, 12, 0, 0), berlin);
        Assert.Equal(new DateTime(2026, 3, 29, 3, 0, 0), spring);        // 02:30 does not exist that night
    }
}

public sealed class TrackerPhase9Tests
{
    private sealed class Scanner : IProcessScanner
    {
        public List<RunningProcess> Processes { get; } = [];
        public IReadOnlyList<RunningProcess> Snapshot() => [.. Processes];
        public IReadOnlyList<RunningProcess> WindowedSnapshot() => [];
    }

    private static readonly DateTime T0 = new(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc);
    private readonly AutomationEngineTests.FakeClock _clock = new(T0);
    private readonly Scanner _scanner = new();
    private readonly PlaySessionTracker _tracker;

    public TrackerPhase9Tests()
    {
        _tracker = new PlaySessionTracker(_scanner, _clock) { DetectExternalLaunches = true };
        _tracker.UpdateLibrary([new TrackableGame(1, "Cyberpunk 2077", null, ["Cyberpunk2077.exe"]), new TrackableGame(2, "Emu game", null, [@"D:\Emu\emu.exe"])]);
    }

    private void Tick(int seconds) { _clock.Utc = _clock.Utc.AddSeconds(seconds); }

    [Fact]
    public void Associated_executable_starts_and_ends_a_session_by_name_or_path()
    {
        _scanner.Processes.Add(new RunningProcess(10, @"E:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", T0.AddMinutes(-5)));
        _scanner.Processes.Add(new RunningProcess(11, @"C:\Other\Cyberpunk2077Launcher.exe", T0));
        var started = _tracker.Tick().Started!;
        Assert.Equal(1, started.GameId);
        Assert.Equal(T0.AddMinutes(-5), started.StartedAtUtc);            // Arcadia opened after the game started
        _scanner.Processes.Clear();
        Tick(3); Assert.Null(_tracker.Tick().Ended);
        Tick(3); Assert.Null(_tracker.Tick().Ended);                      // grace: a quick relaunch continues
        _scanner.Processes.Add(new RunningProcess(12, @"E:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", _clock.Utc));
        Tick(3); Assert.Null(_tracker.Tick().Ended);
        Assert.Equal(T0.AddMinutes(-5), _tracker.Active!.StartedAtUtc);   // same session
        _scanner.Processes.Clear();
        var seen = _tracker.Active.LastSeenUtc;
        Tick(3); _tracker.Tick(); Tick(30);
        var ended = _tracker.Tick().Ended!;                                 // crash or exit: ends at last seen
        Assert.Equal(seen, ended.LastSeenUtc);

        _scanner.Processes.Add(new RunningProcess(20, @"C:\Other\emu.exe", _clock.Utc));   // same name, other folder: path association does not match
        Assert.Null(_tracker.Tick().Started);
        _scanner.Processes.Add(new RunningProcess(21, @"D:\Emu\emu.exe", _clock.Utc));
        Assert.Equal(2, _tracker.Tick().Started!.GameId);
    }

    [Fact]
    public void Sleep_is_not_counted_and_stale_restores_are_not_double_counted()
    {
        _scanner.Processes.Add(new RunningProcess(10, @"E:\G\Cyberpunk2077.exe", T0));
        _tracker.Tick();
        Tick(3600);
        _tracker.Tick();
        var beforeSleep = _tracker.Suspend()!;                               // PC sleeps after an hour
        Assert.Equal(T0.AddHours(1), beforeSleep.LastSeenUtc);
        Tick(8 * 3600);                                                      // eight hours asleep, game still "running"
        _tracker.Resumed();
        var again = _tracker.Tick().Started!;
        Assert.Equal(_clock.Utc, again.StartedAtUtc);                        // new segment from resume, not from process start

        var stale = new ActiveSession(1, "Cyberpunk 2077", T0, "", T0.AddMinutes(30), ["Cyberpunk2077.exe"]);
        var t = new PlaySessionTracker(_scanner, _clock);
        var closed = t.Restore(stale);                                       // Arcadia crashed hours ago
        Assert.Equal(T0.AddMinutes(30), closed!.LastSeenUtc);                // recorded up to the last heartbeat only
        Assert.Null(t.Active);
        var fresh = new ActiveSession(1, "Cyberpunk 2077", T0, "", _clock.Utc.AddMinutes(-2), ["Cyberpunk2077.exe"]);
        Assert.Null(t.Restore(fresh));                                       // a recent heartbeat resumes the session
        Assert.Equal(T0, t.Active!.StartedAtUtc);
    }
}
