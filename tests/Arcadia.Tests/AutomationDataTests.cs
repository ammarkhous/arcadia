using Arcadia.Core.Automation;
using Arcadia.Core.Oracle;
using Arcadia.Core.Oracle.Commands;
using Arcadia.Core.Projects;
using Arcadia.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arcadia.Tests;

public sealed class AutomationStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ArcadiaDbContext> _factory = null!;
    private AutomationStore _store = null!;
    private readonly AutomationEngineTests.FakeClock _clock = new(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc));

    private sealed class Factory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite(_connection).Options);
        await new DatabaseInitializer(_factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);
        _store = new AutomationStore(_factory, _clock);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private static AutomationDefinition Def(string name) => new(0, name, "d", true, new AutomationStep("process.started", new() { ["exe"] = "a.exe" }), ConditionMode.Any,
        [new AutomationStep("weekday", new() { ["days"] = "mon" })], [new AutomationStep("gaming.on"), new AutomationStep("notify", new() { ["title"] = "x" })], 300, "games");

    [Fact]
    public async Task Definitions_round_trip_and_toggle()
    {
        var saved = await _store.SaveAsync(Def("Gaming"));
        var back = Assert.Single(await _store.ListAsync());
        Assert.Equal(saved.Id, back.Id);
        Assert.Equal("a.exe", back.Trigger.Get("exe"));
        Assert.Equal(ConditionMode.Any, back.Mode);
        Assert.Equal(["gaming.on", "notify"], back.Actions.Select(a => a.Type));
        Assert.Equal(300, back.CooldownSeconds);
        await _store.SetEnabledAsync(saved.Id, false);
        Assert.False((await _store.GetAsync(saved.Id))!.Enabled);
        await _store.DeleteAsync(saved.Id);
        Assert.Empty(await _store.ListAsync());
    }

    [Fact]
    public async Task Runs_are_recorded_bounded_and_crash_safe()
    {
        var d = await _store.SaveAsync(Def("A"));
        var id = await _store.BeginRunAsync(new AutomationRunEntity { AutomationId = d.Id, AutomationName = "A", TriggerType = "process.started", EventKey = "bill:1", StartedAtUtc = _clock.Utc, Status = RunStatus.Running });
        await _store.CompleteRunAsync(id, RunStatus.Success, 12, "ok", "{}");
        Assert.True(await _store.HasRunAsync(d.Id, "bill:1"));
        var stats = (await _store.GetAsync(d.Id))!;
        Assert.Equal(1, stats.RunCount);
        Assert.Equal(RunStatus.Success, stats.LastStatus);

        await _store.BeginRunAsync(new AutomationRunEntity { AutomationId = d.Id, AutomationName = "A", TriggerType = "x", StartedAtUtc = _clock.Utc, Status = RunStatus.Running });
        Assert.Equal(1, await _store.MarkInterruptedAsync());          // a crash mid-run: recorded as incomplete, not re-run
        Assert.Contains(await _store.GetRunsAsync(10), r => r.Status == RunStatus.Incomplete);

        for (var i = 0; i < 30; i++)
            await _store.BeginRunAsync(new AutomationRunEntity { AutomationId = d.Id, AutomationName = "A", TriggerType = "x", StartedAtUtc = _clock.Utc, Status = RunStatus.Skipped });
        await _store.PruneRunsAsync(30, 10);
        var left = await _store.GetRunsAsync(100);
        Assert.Equal(10, left.Count(r => r.TriggerType == "x"));
        Assert.True(await _store.HasRunAsync(d.Id, "bill:1"));            // once-per-occurrence guards survive pruning
    }

    [Fact]
    public async Task Work_sessions_count_minutes_and_close_open_ones()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var project = await new ProjectStore(_factory, _clock).CreateAsync(new ProjectDraft("Home Server", null, ProjectStatus.Active, ProjectPriority.Normal, null, null, "Blue", "Projects", "mountains-dusk", "", null));
        var work = new WorkSessionStore(_factory);
        var t0 = new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);
        await work.StartAsync(project.Id, t0, automatic: false);
        await work.StartAsync(project.Id, t0.AddMinutes(30), automatic: true);   // starting again closes the first
        var stopped = await work.StopAsync(t0.AddMinutes(75), "cabling");
        Assert.Equal(45, stopped!.Minutes);
        Assert.Null(await work.GetOpenAsync());
        Assert.Equal(75, (await work.MinutesByProjectAsync())[project.Id]);
        Assert.Equal("Home Server", (await work.RecentAsync(5))[0].Project);
    }
}

public sealed class AutomationMigrationTests
{
    [Fact]
    public async Task A_phase_7_library_gains_automation_and_keeps_everything()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arcadia-p9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "arcadia.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options;
            await using (var db = new ArcadiaDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("Finance");
                await OldSchema.InsertAsync(db, "Games", new() { ["Id"] = 7, ["Title"] = "Cyberpunk 2077", ["Platform"] = "PC", ["Tags"] = "", ["PlaytimeHours"] = 42.0, ["AddedAtUtc"] = DateTime.UtcNow });
                await OldSchema.InsertAsync(db, "FinanceAccounts", new() { ["Id"] = 1, ["Name"] = "Main", ["Currency"] = "EUR", ["Type"] = "Checking", ["CreatedAtUtc"] = DateTime.UtcNow, ["UpdatedAtUtc"] = DateTime.UtcNow });
            }
            var factory = new FileFactory(options);
            await new DatabaseInitializer(factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);
            await using var after = new ArcadiaDbContext(options);
            Assert.Contains(await after.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_Automation", StringComparison.Ordinal));
            var game = await after.Games.SingleAsync();
            Assert.Equal("Cyberpunk 2077", game.Title);
            Assert.Null(game.Executables);
            Assert.False(game.AutoGamingMode);
            Assert.Equal("Main", (await after.FinanceAccounts.SingleAsync()).Name);
            Assert.Empty(await after.Automations.ToListAsync());
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "Backups"), "arcadia-before-Automation-*.db"));
            // the upgraded library stores automations and game executables
            await new AutomationStore(factory, TimeProvider.System).SaveAsync(new AutomationDefinition(0, "x", null, true, new AutomationStep("app.started"), ConditionMode.All, [], [new AutomationStep("open.today")]));
            game.Executables = "Cyberpunk2077.exe";
            await after.SaveChangesAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FileFactory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }
}

/// <summary>Oracle's automation commands against a fake automation service.</summary>
public sealed class OracleAutomationTests
{
    private sealed class FakeAutomation : IAutomationService
    {
        public List<AutomationDefinition> Items { get; } = [];
        public IReadOnlyList<AutomationDefinition> Automations => Items;
        public bool IsPaused { get; private set; }
        public bool IsGamingMode { get; private set; }
        public AutomationRegistry Registry { get; } = AutomationCatalog.CreateDefault();
        public Task SetPausedAsync(bool paused) { IsPaused = paused; return Task.CompletedTask; }
        public void SetGamingMode(bool on) => IsGamingMode = on;
        public Task<IReadOnlyList<AutomationRunRecord>> RecentRunsAsync(int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AutomationRunRecord>>([new AutomationRunRecord(1, 1, "Morning", "app.started", DateTime.UtcNow, 5, RunStatus.Success, "ok", RunDetails.Empty, false)]);
        public Task<AutomationDefinition> CreateAsync(AutomationDefinition d, CancellationToken ct = default)
        {
            if (Registry.Validate(d).Count > 0) throw new InvalidOperationException("invalid");
            var saved = d with { Id = Items.Count + 1 };
            Items.Add(saved);
            return Task.FromResult(saved);
        }
    }

    private readonly FakeAutomation _auto = new();
    private readonly OracleEngine _engine;
    private readonly OracleContext _ctx = new();

    public OracleAutomationTests()
    {
        _auto.Items.Add(new AutomationDefinition(1, "Morning", null, true, new AutomationStep("app.started"), ConditionMode.All, [], [new AutomationStep("open.today")]) { LastStatus = RunStatus.Success });
        _auto.Items.Add(new AutomationDefinition(2, "Broken", null, true, new AutomationStep("app.started"), ConditionMode.All, [], [new AutomationStep("ping", new() { ["host"] = "x" })]) { LastStatus = RunStatus.Failed });
        var games = new Dictionary<int, string> { [7] = "Cyberpunk 2077", [8] = "Hades" };
        var index = new OracleEntityIndex((kind, text, ct) => Task.FromResult<IReadOnlyList<(Arcadia.Core.Organizer.EntityRef, string)>>(
            kind == Arcadia.Core.Organizer.EntityKind.Game ? games.Select(g => (new Arcadia.Core.Organizer.EntityRef(kind, g.Key), g.Value)).ToList() : []));
        var services = new OracleServices
        {
            Games = null!, Projects = null!, Documents = null!, Travel = null!, Tasks = null!, Notes = null!, Calendar = null!, Agenda = null!, Directory = null!, Search = null!, Finance = null!,
            Automation = _auto,
        };
        _engine = new OracleEngine(OracleRegistry.CreateDefault(), new OracleRuntime(services, new OracleEngineTests.Env(), index));
    }

    private Task<OracleResult> Run(string text) => _engine.RunAsync(new OracleRequest(text), _ctx);

    [Fact]
    public async Task Lists_pauses_resumes_and_reports()
    {
        Assert.Equal(2, (await Run("show automations")).Items.Count);
        Assert.Equal("Broken", Assert.Single((await Run("show failed automations")).Items).Title);
        Assert.Equal("Orc_AutoLastRun", (await Run("what automation ran last")).Headline.Split('|')[0]);
        Assert.Equal(OracleResultKind.Done, (await Run("pause automations")).Kind);
        Assert.True(_auto.IsPaused);
        await Run("resume automations");
        Assert.False(_auto.IsPaused);
        Assert.Equal("automation", (await Run("open automations")).AutoAction?.Section);
    }

    [Fact]
    public async Task Gaming_mode_status_and_switches_but_disable_stays_safe()
    {
        Assert.Equal("Orc_GamingIsOff", (await Run("is gaming mode on?")).Headline);
        await Run("enable gaming mode");
        Assert.True(_auto.IsGamingMode);
        Assert.Equal("Orc_GamingIsOn", (await Run("is gaming mode on")).Headline);
        await Run("disable gaming mode");
        Assert.False(_auto.IsGamingMode);
        Assert.Equal(OracleResultKind.Refused, (await Run("disable windows defender")).Kind);   // "disable" only switches modes/automations
        Assert.Equal(OracleResultKind.Refused, (await Run("delete all automations")).Kind);
        await Run("Spielmodus einschalten");
        Assert.True(_auto.IsGamingMode);
    }

    [Fact]
    public async Task Creating_from_text_is_a_preview_that_needs_confirm()
    {
        var r = await Run("when Cyberpunk starts enable gaming mode");
        Assert.Equal(OracleResultKind.Preview, r.Kind);
        Assert.Equal(2, _auto.Items.Count);                                // nothing created yet
        Assert.Equal(OracleActionKind.EditAutomation, r.Preview!.Edit!.Kind);
        await r.Preview.Confirm.Invoke!(CancellationToken.None);
        var created = _auto.Items[^1];
        Assert.Equal("game.started", created.Trigger.Type);
        Assert.Equal("7", created.Trigger.Get("game"));
        Assert.Equal("gaming.on", Assert.Single(created.Actions).Type);

        var exe = await Run("when steam.exe closes show notification Steam closed");
        await exe.Preview!.Confirm.Invoke!(CancellationToken.None);
        Assert.Equal("process.stopped", _auto.Items[^1].Trigger.Type);
        Assert.Equal("Steam closed", _auto.Items[^1].Actions[0].Get("title"));

        Assert.Equal(OracleResultKind.Refused, (await Run("when Cyberpunk starts delete my saves")).Kind);   // destructive: refused outright
        Assert.Equal(OracleResultKind.Clarify, (await Run("when Cyberpunk starts water the plants")).Kind);   // unknown action: not guessed
        Assert.Equal(OracleResultKind.Clarify, (await Run("when Nonexistent Game starts enable gaming mode")).Kind);
        var ar = await Run("عندما يبدأ Hades فعل وضع الألعاب");
        Assert.Equal(OracleResultKind.Preview, ar.Kind);
    }
}
