using Arcadia.Core.Documents;
using Arcadia.Core.Finance;
using Arcadia.Core.Games;
using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Travel;
using Arcadia.Data;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Tests;

/// <summary>
/// Phase 10 large-data library for performance checks (development only, never shipped). Runs only when
/// ARCADIA_STRESS_DB points at a new file: dotnet test --filter StressDataGenerator with the variable set.
/// 1,000 games · 10,000 tasks · 5,000 notes · 5,000 transactions · 1,000 documents · 300 projects · 200 trips ·
/// 500 places · 5 years of events. Deterministic (fixed seed).
/// </summary>
public sealed class StressDataGenerator
{
    [Fact]
    public async Task Generate_large_library()
    {
        var path = Environment.GetEnvironmentVariable("ARCADIA_STRESS_DB");
        if (string.IsNullOrWhiteSpace(path)) return; // not a normal test run
        Assert.False(File.Exists(path), "refusing to touch an existing file");
        Assert.DoesNotContain(@"\AppData\Local\Arcadia\", path, StringComparison.OrdinalIgnoreCase);

        var options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using (var db = new ArcadiaDbContext(options)) await db.Database.MigrateAsync();

        var rnd = new Random(20260929);
        var now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var today = DateOnly.FromDateTime(now);
        string Pick(params string[] items) => items[rnd.Next(items.Length)];
        string Words(int n) => string.Join(' ', Enumerable.Range(0, n).Select(_ => Pick("alpha", "router", "dresden", "invoice", "backup", "journey", "review", "castle", "mountain", "harbour", "budget", "draft", "deadline", "server", "photo", "quest", "migration", "sprint", "garden", "archive")));
        var themes = new[] { "mountains-dusk", "forest-night", "desert-noon", "city-neon", "ocean-dawn" };

        await using (var db = new ArcadiaDbContext(options))
        {
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            var statuses = Enum.GetValues<GameStatus>();
            for (var i = 0; i < 1000; i++)
                db.Games.Add(new Game
                {
                    Title = $"{Pick("Shadow", "Crystal", "Iron", "Star", "Ember", "Frost")} {Pick("Legends", "Odyssey", "Tactics", "Frontier", "Chronicle")} {i + 1}",
                    Platform = Pick("PC", "PS5", "Switch", "Xbox"), Status = statuses[rnd.Next(statuses.Length)],
                    CompletionPercent = rnd.Next(101), PlaytimeHours = rnd.Next(0, 400), AchievementsTotal = 50, AchievementsUnlocked = rnd.Next(51),
                    AddedAtUtc = now.AddDays(-rnd.Next(2000)), LastPlayedAtUtc = rnd.Next(3) == 0 ? null : now.AddDays(-rnd.Next(700)),
                    ArtworkTheme = themes[rnd.Next(themes.Length)], ArtworkSeed = rnd.Next(1000), Tags = Pick("rpg", "shooter", "indie", "strategy"),
                    Description = Words(30),
                });
            await db.SaveChangesAsync();

            for (var i = 0; i < 300; i++)
                db.Projects.Add(new Project
                {
                    Name = $"Project {Words(2)} {i + 1}", Description = Words(20), Status = (ProjectStatus)rnd.Next(Enum.GetValues<ProjectStatus>().Length),
                    StartDate = today.AddDays(-rnd.Next(900)), TargetDate = today.AddDays(rnd.Next(-200, 400)),
                    ArtworkTheme = themes[rnd.Next(themes.Length)], ArtworkSeed = rnd.Next(1000), CreatedAtUtc = now.AddDays(-rnd.Next(900)), UpdatedAtUtc = now.AddDays(-rnd.Next(60)),
                });
            await db.SaveChangesAsync();
            var projectIds = await db.Projects.Select(p => p.Id).ToListAsync();

            var taskStatuses = Enum.GetValues<TaskItemStatus>();
            for (var i = 0; i < 10_000; i++)
            {
                var status = taskStatuses[rnd.Next(taskStatuses.Length)];
                db.Tasks.Add(new TaskItem
                {
                    Title = $"{Pick("Call", "Write", "Fix", "Buy", "Review", "Plan", "Pay")} {Words(3)}", Status = status,
                    ProjectId = rnd.Next(3) == 0 ? projectIds[rnd.Next(projectIds.Count)] : null,
                    DueDate = rnd.Next(4) == 0 ? null : today.AddDays(rnd.Next(-400, 200)), Priority = (ProjectPriority)rnd.Next(Enum.GetValues<ProjectPriority>().Length),
                    Tags = Pick("", "home", "work", "urgent"), CreatedAtUtc = now.AddDays(-rnd.Next(1500)), UpdatedAtUtc = now.AddDays(-rnd.Next(100)),
                    CompletedAtUtc = status == TaskItemStatus.Completed ? now.AddDays(-rnd.Next(1000)) : null,
                });
                if (i % 2000 == 1999) { await db.SaveChangesAsync(); db.ChangeTracker.Clear(); }
            }
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();

            for (var i = 0; i < 5000; i++)
            {
                db.Notes.Add(new Note
                {
                    Title = $"Note {Words(3)} {i + 1}", Content = "# " + Words(4) + "\n\n" + string.Join("\n\n", Enumerable.Range(0, rnd.Next(1, 8)).Select(_ => Words(rnd.Next(20, 80)))),
                    ProjectId = rnd.Next(4) == 0 ? projectIds[rnd.Next(projectIds.Count)] : null, IsPinned = i < 5, Tags = Pick("", "ideas", "journal", "it"),
                    CreatedAtUtc = now.AddDays(-rnd.Next(1800)), UpdatedAtUtc = now.AddDays(-rnd.Next(300)),
                });
                if (i % 1000 == 999) { await db.SaveChangesAsync(); db.ChangeTracker.Clear(); }
            }
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();

            for (var i = 0; i < 1000; i++)
            {
                var ext = Pick(".pdf", ".docx", ".xlsx", ".png", ".txt");
                db.Documents.Add(new Document
                {
                    DisplayName = $"{Words(2)} {i + 1}", Path = $@"D:\Stress\Docs\{i / 100}\doc-{i + 1}{ext}", Extension = ext, SizeBytes = rnd.Next(1000, 5_000_000),
                    AddedAtUtc = now.AddDays(-rnd.Next(1500)), IsMissing = true, Description = Words(8),
                });
            }
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();

            for (var i = 0; i < 500; i++)
                db.TravelPlaces.Add(new TravelPlace
                {
                    Name = $"{Pick("Old", "New", "Upper", "Grand")} {Pick("Harbour", "Castle", "Market", "Bridge", "Lake")} {i + 1}", CountryCode = Pick("DE", "FR", "IT", "ES", "JP", "SY", "US"),
                    Latitude = rnd.NextDouble() * 120 - 50, Longitude = rnd.NextDouble() * 340 - 170, Status = (PlaceStatus)rnd.Next(Enum.GetValues<PlaceStatus>().Length),
                    CreatedAtUtc = now.AddDays(-rnd.Next(1500)), UpdatedAtUtc = now.AddDays(-rnd.Next(100)),
                });
            for (var i = 0; i < 200; i++)
            {
                var start = today.AddDays(rnd.Next(-1800, 300));
                db.Trips.Add(new Trip
                {
                    Name = $"Trip {Words(2)} {i + 1}", StartDate = start, EndDate = start.AddDays(rnd.Next(1, 20)), Status = (TripStatus)rnd.Next(Enum.GetValues<TripStatus>().Length),
                    ArtworkTheme = themes[rnd.Next(themes.Length)], ArtworkSeed = rnd.Next(1000), CreatedAtUtc = now.AddDays(-rnd.Next(1800)), UpdatedAtUtc = now.AddDays(-rnd.Next(100)),
                });
            }
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();

            // five years of events: ~2,500 single events plus weekly/monthly series
            for (var i = 0; i < 2500; i++)
            {
                var start = now.Date.AddDays(rnd.Next(-1460, 365)).AddHours(rnd.Next(7, 20));
                db.CalendarEvents.Add(new CalendarEvent
                {
                    Title = $"{Pick("Meeting", "Dentist", "Call", "Dinner", "Workshop", "Match")} {Words(1)}", Start = start, End = start.AddMinutes(rnd.Next(1, 6) * 30),
                    IsAllDay = rnd.Next(10) == 0, CreatedAtUtc = now, UpdatedAtUtc = now,
                });
            }
            for (var i = 0; i < 40; i++)
            {
                var start = now.Date.AddDays(-rnd.Next(1460)).AddHours(18);
                db.CalendarEvents.Add(new CalendarEvent
                {
                    Title = $"Series {Words(1)} {i + 1}", Start = start, End = start.AddHours(1), Frequency = i % 2 == 0 ? RecurrenceFrequency.Weekly : RecurrenceFrequency.Monthly,
                    CreatedAtUtc = now, UpdatedAtUtc = now,
                });
            }
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();

            // finance: 4 accounts, 5,000 transactions over five years (some transfers), exact decimals
            var accounts = new[] { "Giro", "Savings", "Credit card", "Cash" }.Select((n, k) => new FinanceAccount
            {
                Name = n, Type = (AccountType)(k % Enum.GetValues<AccountType>().Length), OpeningBalance = 1000m * (k + 1), SortOrder = k, CreatedAtUtc = now, UpdatedAtUtc = now,
            }).ToList();
            db.FinanceAccounts.AddRange(accounts);
            await db.SaveChangesAsync();
            var categories = await db.FinanceCategories.Where(c => c.ParentId != null || c.Kind == CategoryKind.Expense).Select(c => new { c.Id, c.Kind }).ToListAsync();
            var expense = categories.Where(c => c.Kind == CategoryKind.Expense).Select(c => (int?)c.Id).DefaultIfEmpty(null).ToList();
            var income = categories.Where(c => c.Kind == CategoryKind.Income).Select(c => (int?)c.Id).DefaultIfEmpty(null).ToList();
            for (var i = 0; i < 5000; i++)
            {
                var type = i % 25 == 0 ? TransactionType.Transfer : i % 7 == 0 ? TransactionType.Income : TransactionType.Expense;
                var from = accounts[rnd.Next(accounts.Count)];
                var to = accounts.First(a => a.Id != from.Id);
                var amount = Math.Round((decimal)(rnd.NextDouble() * (type == TransactionType.Income ? 3000 : 180)) + 0.01m, 2);
                db.FinanceTransactions.Add(new FinanceTransaction
                {
                    Type = type, Date = today.AddDays(-rnd.Next(1825)), Amount = amount, AccountId = from.Id,
                    ToAccountId = type == TransactionType.Transfer ? to.Id : null, ToAmount = type == TransactionType.Transfer ? amount : null,
                    CategoryId = type == TransactionType.Expense ? expense[rnd.Next(expense.Count)] : type == TransactionType.Income ? income[rnd.Next(income.Count)] : null,
                    Description = $"{Pick("REWE", "Amazon", "DB Ticket", "Salary", "Rent", "Pharmacy", "Café")} {Words(1)}", Payee = Pick("REWE", "Amazon", "Deutsche Bahn", "Employer", "Landlord"),
                    CreatedAtUtc = now, UpdatedAtUtc = now,
                });
                if (i % 1000 == 999) { await db.SaveChangesAsync(); db.ChangeTracker.Clear(); }
            }
            await db.SaveChangesAsync();
        }
        Assert.True(new FileInfo(path).Length > 1_000_000);
    }

    /// <summary>ARCADIA_STRESS_REBUILD=path of a copy: times the search-index rebuild on it.</summary>
    [Fact]
    public async Task Time_search_index_rebuild()
    {
        var path = Environment.GetEnvironmentVariable("ARCADIA_STRESS_REBUILD");
        if (string.IsNullOrWhiteSpace(path)) return;
        Assert.DoesNotContain(@"\AppData\Local\Arcadia\", path, StringComparison.OrdinalIgnoreCase);
        var options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using var db = new ArcadiaDbContext(options);
        var log = new ListLogger();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Arcadia.Data.Search.SearchIndex.RebuildAsync(db, log, default);
        File.WriteAllText(path + ".rebuild.txt", $"{clock.ElapsedMilliseconds} ms{Environment.NewLine}" + string.Join(Environment.NewLine, log.Lines));
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    /// <summary>ARCADIA_STRESS_SEARCH=path of an indexed large library: times global search for typical inputs.</summary>
    [Fact]
    public async Task Time_search_on_large_library()
    {
        var path = Environment.GetEnvironmentVariable("ARCADIA_STRESS_SEARCH");
        if (string.IsNullOrWhiteSpace(path)) return;
        var options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={path};Mode=ReadOnly;Pooling=True").Options;
        var factory = new OptionsFactory(options);
        var search = new Arcadia.Core.Search.GlobalSearchService([new Arcadia.Data.Search.IndexSearchProvider(factory), new Arcadia.Data.Search.GameSearchProvider(factory)]);
        var lines = new List<string>();
        foreach (var text in new[] { "ro", "router", "dres", "invoice backup", "shadow", "rewe", "mountain photo", "zzzz", "\"quoted\" OR *" })
        {
            await search.SearchAsync(new Arcadia.Core.Search.SearchRequest(text, null, 12)); // warm
            var clock = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<Arcadia.Core.Search.SearchResult> r = [];
            for (var i = 0; i < 10; i++) r = await search.SearchAsync(new Arcadia.Core.Search.SearchRequest(text, null, 12));
            lines.Add($"{text,-18} {clock.Elapsed.TotalMilliseconds / 10,7:0.0} ms  {r.Count} results");
        }
        File.WriteAllLines(path + ".search.txt", lines);
    }

    private sealed class OptionsFactory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }
}
