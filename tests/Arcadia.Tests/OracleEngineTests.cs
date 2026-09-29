using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Arcadia.Core.Finance;
using Arcadia.Core.Games;
using Arcadia.Core.ItTools.Network;
using Arcadia.Core.Oracle;
using Arcadia.Core.Oracle.Commands;
using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Travel;
using Arcadia.Data;
using Arcadia.Data.Finance;
using Arcadia.Data.Organizer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arcadia.Tests;

/// <summary>Oracle end to end on real stores (in-memory SQLite): matching, context, safety, previews, languages.</summary>
public sealed class OracleEngineTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 28, 10, 0, 0); // Monday
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ArcadiaDbContext> _factory = null!;
    private OracleServices _s = null!;
    private Env _env = null!;
    private OracleEngine _engine = null!;
    private readonly OracleContext _ctx = new();
    private int _homeServer, _cyberpunk, _italy;

    private sealed class Factory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Responses as "key|arg|arg" so tests check structure, not wording.</summary>
    public sealed class Env : IOracleEnvironment
    {
        public DateTime Now { get; set; } = OracleEngineTests.Now;
        public string UiLanguage => "en";
        public string T(string key, params object?[] args) => args.Length == 0 ? key : key + "|" + string.Join("|", args);
        public string Money(decimal amount, string currency) => amount.ToString("0.00", CultureInfo.InvariantCulture) + " " + currency;
        public string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public string Time(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
        public DecimalStyle DecimalStyle => DecimalStyle.Point;
        public int? DefaultAccountId { get; set; }
        public OracleConfirmation Confirmation { get; set; } = OracleConfirmation.Smart;
    }

    private sealed class FakePing : IPingService
    {
        public async IAsyncEnumerable<PingReplyInfo> PingAsync(NetworkTarget target, int count, int timeoutMs, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 1; i <= count; i++)
            {
                await Task.Yield();
                yield return new PingReplyInfo(i, "192.168.1.1", PingOutcome.Success, 3 + i, 64, 32);
            }
        }

        public Task<System.Net.IPAddress> ResolveAsync(NetworkTarget target, CancellationToken cancellationToken = default) => Task.FromResult(System.Net.IPAddress.Loopback);
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite(_connection).Options);
        await new DatabaseInitializer(_factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);
        var clock = new Clock();
        _s = new OracleServices
        {
            Games = new GameCatalog(_factory, clock),
            Projects = new ProjectStore(_factory, clock),
            Documents = new DocumentStore(_factory, clock),
            Travel = new TravelStore(_factory, clock, null),
            Tasks = new TaskStore(_factory, clock),
            Notes = new NoteStore(_factory, clock),
            Calendar = new CalendarStore(_factory, clock),
            Agenda = new CalendarQueryService(_factory, clock),
            Directory = new EntityDirectory(_factory),
            Search = new GlobalSearchService([new Arcadia.Data.Search.IndexSearchProvider(_factory), new Arcadia.Data.Search.GameSearchProvider(_factory)]),
            Finance = new FinanceStore(_factory, clock, NullLogger<FinanceStore>.Instance),
            Ping = new FakePing(),
        };
        _env = new Env();
        _engine = new OracleEngine(OracleRegistry.CreateDefault(), new OracleRuntime(_s, _env, new OracleEntityIndex(_s.LoadNamesAsync)));

        _cyberpunk = await _s.Games.SaveGameAsync(new Game { Title = "Cyberpunk 2077", Status = GameStatus.Playing, CompletionPercent = 64, PlaytimeHours = 80, LastPlayedAtUtc = Now.AddDays(-2) });
        await _s.Games.SaveGameAsync(new Game { Title = "Elden Ring", Status = GameStatus.Backlog, CompletionPercent = 10 });
        await _s.Games.SaveGameAsync(new Game { Title = "Hades", Status = GameStatus.Completed, CompletionPercent = 100, Tags = "Favorite" });
        await _s.Games.SaveGameAsync(new Game { Title = "Halo Infinite", Status = GameStatus.Backlog });
        await _s.Games.SaveGameAsync(new Game { Title = "Halo 3", Status = GameStatus.Abandoned });
        _homeServer = (await _s.Projects.CreateAsync(new ProjectDraft("Home Server", null, ProjectStatus.Active, ProjectPriority.High, null, new DateOnly(2026, 10, 15), "Blue", "Projects", "mountains-dusk", "", null))).Id;
        await _s.Projects.CreateAsync(new ProjectDraft("Garden Shed", null, ProjectStatus.OnHold, ProjectPriority.Normal, null, null, "Green", "Projects", "mountains-dusk", "", null));
        var hs = new EntityRef(EntityKind.Project, _homeServer);
        await _s.Tasks.CreateAsync(new TaskItemDraft("Label ports", Priority: ProjectPriority.High, DueDate: new DateOnly(2026, 9, 25), Context: hs));
        await _s.Tasks.CreateAsync(new TaskItemDraft("Order rack", DueDate: new DateOnly(2026, 10, 1), Context: hs));
        await _s.Tasks.CreateAsync(new TaskItemDraft("Buy milk", DueDate: new DateOnly(2026, 9, 28)));
        await _s.Notes.CreateAsync(new NoteDraft("Router settings", "Admin at 192.168.1.1, router firmware 2.1"));
        await _s.Calendar.CreateEventAsync(new EventDraft("Dentist", new DateTime(2026, 9, 29, 9, 0, 0), new DateTime(2026, 9, 29, 10, 0, 0)));
        _italy = (await _s.Travel.CreateTripAsync(new TripDraft("Italy", TripStatus.Upcoming, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 12)))).Id;
        await _s.Travel.CreatePlaceAsync(new PlaceDraft("Rome", PlaceType.City, PlaceStatus.WantToVisit, null, null, null, null, null, null, null, null, false, null, null));
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    public Task<OracleResult> RunForDump(string text) => Run(text);
    private Task<OracleResult> Run(string text, OracleLanguage? lang = null) => _engine.RunAsync(new OracleRequest(text, lang), _ctx);

    private async Task<FinanceAccount> Account(string name) => await _s.Finance.CreateAccountAsync(new AccountDraft(name, AccountType.Checking, "EUR"));

    // ─────────── queries ───────────

    [Fact]
    public async Task Game_queries_filter_count_and_rank()
    {
        var r = await Run("show unfinished games");
        Assert.Equal(OracleResultKind.List, r.Kind);
        Assert.Equal(["Cyberpunk 2077", "Elden Ring", "Halo Infinite"], r.Items.Select(i => i.Title).Order().ToArray());
        Assert.Equal("games.list", r.Trace!.Handler);
        Assert.Equal(OracleConfidence.High, r.Confidence);

        var playing = await Run("which games am I currently playing");
        Assert.Equal("Cyberpunk 2077", Assert.Single(playing.Items).Title);

        var count = await Run("how many games have I completed");
        Assert.Equal("Orc_CountGames|1", count.Headline);
        Assert.Equal("Hades", Assert.Single((await Run("favorite games")).Items).Title);
    }

    [Fact]
    public async Task Task_queries_scope_and_follow_ups()
    {
        var all = await Run("show open tasks");
        Assert.Equal(3, all.Items.Count);
        var only = await Run("only high priority");
        Assert.Equal("tasks.list", only.Trace!.Handler);
        Assert.Equal("Label ports", Assert.Single(only.Items).Title);

        var scoped = await Run("show tasks for Home Server");
        Assert.Equal(2, scoped.Items.Count);
        Assert.Equal(new EntityRef(EntityKind.Project, _homeServer), scoped.Focus);

        var overdue = await Run("show overdue tasks");
        Assert.Equal("Label ports", Assert.Single(overdue.Items).Title);
        Assert.Equal(2, (await Run("tasks due within 7 days")).Items.Count(i => i.Title is "Order rack" or "Buy milk"));
    }

    [Fact]
    public async Task Focus_supports_its_and_expires()
    {
        var open = await Run("open project Home Server");
        Assert.Equal(OracleResultKind.Navigated, open.Kind);
        Assert.Equal(new EntityRef(EntityKind.Project, _homeServer), open.AutoAction!.Target);
        var its = await Run("show its tasks");
        Assert.Equal(2, its.Items.Count);

        _env.Now = Now.AddMinutes(11);
        await Run("help");
        Assert.Null(_ctx.Focus); // expired after ten minutes
        _env.Now = Now;
    }

    [Fact]
    public async Task Names_resolve_conservatively()
    {
        var typo = await Run("open cyberpank");
        Assert.Equal(OracleResultKind.Navigated, typo.Kind);
        Assert.Equal(new EntityRef(EntityKind.Game, _cyberpunk), typo.AutoAction!.Target);
        Assert.Equal(OracleConfidence.Medium, typo.Confidence);

        var halo = await Run("open halo");
        Assert.Equal(OracleResultKind.Clarify, halo.Kind);
        Assert.Equal(2, halo.Items.Count);

        var tab = await Run("open Cyberpunk journal");
        Assert.Equal("Journal", tab.AutoAction!.Tab);
    }

    // ─────────── changes and safety ───────────

    [Fact]
    public async Task Exact_updates_run_directly_fuzzy_ones_preview()
    {
        var exact = await Run("mark game Elden Ring as completed");
        Assert.Equal(OracleResultKind.Done, exact.Kind);
        Assert.Equal(GameStatus.Completed, (await _s.Games.GetGamesAsync()).Single(g => g.Title == "Elden Ring").Status);

        var fuzzy = await Run("mark game Halo Infinte as playing");
        Assert.Equal(OracleResultKind.Preview, fuzzy.Kind);
        Assert.Equal(GameStatus.Backlog, (await _s.Games.GetGamesAsync()).Single(g => g.Title == "Halo Infinite").Status);
        await fuzzy.Preview!.Confirm.Invoke!(CancellationToken.None);
        Assert.Equal(GameStatus.Playing, (await _s.Games.GetGamesAsync()).Single(g => g.Title == "Halo Infinite").Status);

        _env.Confirmation = OracleConfirmation.Always;
        Assert.Equal(OracleResultKind.Preview, (await Run("complete task Buy milk")).Kind);
        _env.Confirmation = OracleConfirmation.Smart;
    }

    [Theory]
    [InlineData("delete all tasks")]
    [InlineData("kill process chrome")]
    [InlineData("remove project Home Server")]
    [InlineData("format drive C")]
    [InlineData("lösche alle Notizen")]
    [InlineData("احذف المهام")]
    public async Task Destructive_commands_are_refused(string text)
    {
        var r = await Run(text);
        Assert.Equal(OracleResultKind.Refused, r.Kind);
        Assert.Equal(3, (await _s.Tasks.QueryAsync(new TaskQuery(), Now)).Count);
        Assert.Equal(2, (await _s.Projects.ListAsync()).Count);
    }

    [Fact]
    public void Registry_has_no_destructive_command()
    {
        Assert.All(OracleRegistry.CreateDefault(), c => Assert.True(c.Safety is OracleSafety.ReadOnly or OracleSafety.Create or OracleSafety.Update));
        Assert.Equal(OracleRegistry.CreateDefault().Count, OracleRegistry.CreateDefault().Select(c => c.Id).Distinct().Count());
        Assert.All(OracleRegistry.CreateDefault(), c => Assert.NotEmpty(c.Examples));
    }

    [Fact]
    public async Task Creates_preview_first_and_apply_on_confirm()
    {
        var task = await Run("add task buy cables tomorrow");
        Assert.Equal(OracleResultKind.Preview, task.Kind);
        Assert.Contains(task.Preview!.Fields, f => f.Value == "buy cables");
        Assert.Contains(task.Preview.Fields, f => f.Value == "2026-09-29");
        Assert.Equal(3, (await _s.Tasks.QueryAsync(new TaskQuery(), Now)).Count); // nothing yet
        await task.Preview.Confirm.Invoke!(CancellationToken.None);
        var created = (await _s.Tasks.QueryAsync(new TaskQuery(Text: "buy cables"), Now)).Single();
        Assert.Equal(new DateOnly(2026, 9, 29), created.DueDate);

        var remind = await Run("remind me to call the bank Friday at 10");
        await remind.Preview!.Confirm.Invoke!(CancellationToken.None);
        var reminder = (await _s.Tasks.QueryAsync(new TaskQuery(Text: "call the bank"), Now)).Single();
        Assert.Equal(new DateTime(2026, 10, 2, 10, 0, 0), reminder.ReminderAt);

        var note = await Run("add note to Cyberpunk: Finish Panam missions");
        Assert.Equal(OracleResultKind.Preview, note.Kind);
        await note.Preview!.Confirm.Invoke!(CancellationToken.None);
        var game = await _s.Games.GetGameDetailAsync(_cyberpunk);
        Assert.Contains(game!.JournalEntries, j => j.Text == "Finish Panam missions");
    }

    [Fact]
    public async Task Ambiguous_dates_ask()
    {
        var r = await Run("create event dentist 03/04");
        Assert.Equal(OracleResultKind.Clarify, r.Kind);
        var pick = r.Items[0].Actions[0].Command!;
        Assert.Contains("2026-04-03", pick);
        var preview = await Run(pick);
        Assert.Equal(OracleResultKind.Preview, preview.Kind);
        Assert.Contains(preview.Preview!.Fields, f => f.Value.StartsWith("2026-04-03"));
    }

    // ─────────── modules ───────────

    [Fact]
    public async Task Agenda_documents_travel_and_navigation()
    {
        var tomorrow = await Run("what's on tomorrow");
        Assert.Equal("calendar.agenda", tomorrow.Trace!.Handler);
        Assert.Contains(tomorrow.Items, i => i.Title == "Dentist");

        var when = await Run("when is my Italy trip");
        Assert.Equal("Orc_TripWhen|Italy|2026-11-02 – 2026-11-12", when.Headline);

        var nav = await Run("open subscriptions");
        Assert.Equal(("finance", "recurring"), (nav.AutoAction!.Section, nav.AutoAction.Tab));
        Assert.Equal("calendar", (await Run("go to calendar")).AutoAction!.Section);
        Assert.Equal("ittools", (await Run("öffne Werkzeuge")).AutoAction!.Section);

        var visited = await Run("mark Rome as visited");
        Assert.Equal(OracleResultKind.Done, visited.Kind);
        Assert.Equal("Rome", Assert.Single((await Run("show visited places")).Items).Title);

        var add = await Run("add Rome to Italy trip");
        Assert.Equal(OracleResultKind.Preview, add.Kind);
        await add.Preview!.Confirm.Invoke!(CancellationToken.None);
        Assert.Single(await _s.Travel.ListPlacesAsync(new PlaceQuery(TripId: _italy)));

        var related = await Run("everything related to Home Server");
        Assert.Equal("related", related.Trace!.Handler);
        Assert.Equal(2, related.Items.Count(i => i.Kind == OracleEntity.Task));
    }

    [Fact]
    public async Task Finance_answers_and_expense_needs_an_account()
    {
        var checking = await Account("Checking");
        await Account("Visa");
        await _s.Finance.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, new DateOnly(2026, 9, 10), 40m, checking.Id, null, "Groceries run"));
        await _s.Finance.CreateTransactionAsync(new TransactionDraft(TransactionType.Expense, new DateOnly(2026, 9, 12), 60m, checking.Id, null, "Fuel"));

        var spend = await Run("how much did I spend this month");
        Assert.Equal("finance.spending", spend.Trace!.Handler);
        Assert.Contains(spend.Metrics, m => m.Value == "100.00 EUR" && m.IsMoney);

        var noAccount = await Run("I spent 35 euro on groceries");
        Assert.Equal(OracleResultKind.Preview, noAccount.Kind);
        Assert.False(noAccount.Preview!.Confirm.IsEnabled); // two accounts, none named, no default: never guess
        Assert.Equal(OracleActionKind.EditTransaction, noAccount.Preview.Edit!.Kind);

        var named = await Run("paid 12.50 EUR for coffee from Visa");
        Assert.True(named.Preview!.Confirm.IsEnabled);
        Assert.Contains(named.Preview.Fields, f => f.Value == "Visa");
        await named.Preview.Confirm.Invoke!(CancellationToken.None);
        var page = await _s.Finance.QueryTransactionsAsync(new TransactionQuery("coffee"), "en");
        Assert.Equal(12.50m, Assert.Single(page.Rows).Amount);

        _env.DefaultAccountId = checking.Id;
        Assert.True((await Run("I spent 35 euro on groceries")).Preview!.Confirm.IsEnabled);
        _env.DefaultAccountId = null;

        Assert.Equal("finance.largest", (await Run("largest expenses this month")).Trace!.Handler);
        Assert.Equal("finance.subscriptions", (await Run("show my subscriptions")).Trace!.Handler);
        Assert.Equal("finance.accounts", (await Run("account balances")).Trace!.Handler);
    }

    [Fact]
    public async Task It_tools_are_read_only_and_ask_for_a_target()
    {
        var ping = await Run("ping 192.168.1.1");
        Assert.Equal(OracleResultKind.Tool, ping.Kind);
        Assert.Equal(4, ping.Items.Count);
        Assert.Equal(OracleResultKind.Clarify, (await Run("ping")).Kind);
        Assert.Equal("ittools", (await Run("traceroute google.com")).AutoAction!.Section);
    }

    // ─────────── languages, unknown, help ───────────

    [Theory]
    [InlineData("zeige überfällige Aufgaben", "tasks.list", OracleLanguage.German)]
    [InlineData("zeige unfertige Spiele", "games.list", OracleLanguage.German)]
    [InlineData("wie viel habe ich diesen Monat ausgegeben", "finance.spending", OracleLanguage.German)]
    [InlineData("was steht morgen an", "calendar.agenda", OracleLanguage.German)]
    [InlineData("اعرض المهام المتأخرة", "tasks.list", OracleLanguage.Arabic)]
    [InlineData("اعرض الألعاب غير المكتملة", "games.list", OracleLanguage.Arabic)]
    [InlineData("كم صرفت هذا الشهر", "finance.spending", OracleLanguage.Arabic)]
    [InlineData("افتح التقويم", "nav.open", OracleLanguage.Arabic)]
    [InlineData("show overdue tasks", "tasks.list", OracleLanguage.English)]
    public async Task Same_engine_for_three_languages(string text, string handler, OracleLanguage language)
    {
        var r = await Run(text);
        Assert.Equal(handler, r.Trace!.Handler);
        Assert.Equal(language, r.Trace.Language);
    }

    [Fact]
    public async Task Unknown_input_suggests_and_help_comes_from_the_registry()
    {
        var r = await Run("blorf zzkq");
        Assert.Equal(OracleResultKind.Unknown, r.Kind);
        Assert.Equal("Orc_Unknown", r.Headline);
        Assert.NotEmpty(r.Suggestions);

        var help = await Run("help finance");
        Assert.Equal(OracleResultKind.Help, help.Kind);
        Assert.All(help.Items, i => Assert.Equal("Orc_Section_finance", i.Group));
        Assert.Equal(OracleResultKind.Help, (await Run("")).Kind);

        // every help example runs to something other than "unknown"
        foreach (var example in OracleRegistry.CreateDefault().Select(c => c.Examples[0]))
        {
            var e = await Run(example);
            Assert.True(e.Kind != OracleResultKind.Unknown, example);
            _ctx.Reset();
        }
    }

    [Fact]
    public async Task Parsing_is_fast()
    {
        await Run("show unfinished games"); // warm up
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 500; i++) OracleParser.Parse("show high priority tasks for Home Server due within 7 days", DateOnly.FromDateTime(Now), OracleLanguage.English);
        Assert.True(sw.Elapsed.TotalMilliseconds / 500 < 2, $"{sw.Elapsed.TotalMilliseconds / 500} ms per parse");
    }
}

public sealed class OracleTraceDump
{
    [Fact]
    public async Task Dump()
    {
        var file = Environment.GetEnvironmentVariable("ORACLE_DUMP");
        if (file == null) return;
        var t = new OracleEngineTests();
        await t.InitializeAsync();
        var lines = new List<string>();
        var input = File.ReadAllLines(file + ".in").SelectMany(l => l == "#examples" ? OracleRegistry.CreateDefault().SelectMany(c => c.Examples.Concat(c.ExamplesDe).Concat(c.ExamplesAr)) : [l]);
        foreach (var q in input)
        {
            var r = await t.RunForDump(q);
            lines.Add($"{q}\n   => {r.Kind} '{r.Headline}' items={r.Items.Count} [{string.Join(", ", r.Items.Take(4).Select(i => i.Title))}] handler={r.Trace?.Handler} score={r.Trace?.Score} conf={r.Confidence}\n   concepts={r.Trace?.Concepts}\n   params={r.Trace?.Parameters} detail={r.Detail}");
        }
        // latency: every example twice (the second pass is warm: lexicon, index and EF queries compiled)
        var all = OracleRegistry.CreateDefault().SelectMany(c => c.Examples.Concat(c.ExamplesDe).Concat(c.ExamplesAr)).ToList();
        foreach (var q in all) await t.RunForDump(q);
        var warm = new List<(double Parse, double Total)>();
        foreach (var q in all) { var r = await t.RunForDump(q); warm.Add((r.Trace!.ParseMs, r.Trace.TotalMs)); }
        lines.Add($"LATENCY warm over {warm.Count} commands: parse avg {warm.Average(w => w.Parse):0.000} ms max {warm.Max(w => w.Parse):0.00} ms; total avg {warm.Average(w => w.Total):0.0} ms, p95 {warm.Select(w => w.Total).Order().ElementAt((int)(warm.Count * 0.95)):0.0} ms, max {warm.Max(w => w.Total):0.0} ms");
        File.WriteAllLines(file + ".out", lines);
    }
}
