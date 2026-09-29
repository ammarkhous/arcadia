using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.Core.Games;
using Arcadia.Core.Media;
using Arcadia.Core.Platform;
using Arcadia.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

public sealed class CatalogTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ArcadiaDbContext> _factory = null!;
    private GameCatalog _catalog = null!;

    private sealed class Factory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite(_connection).Options);
        await new DatabaseInitializer(_factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);
        _catalog = new GameCatalog(_factory, TimeProvider.System);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private Task<int> AddAsync(string title, GameStatus status = GameStatus.Backlog, double hours = 0) =>
        _catalog.SaveGameAsync(new Game { Title = title, Status = status, PlaytimeHours = hours, Tags = " RPG , Co-op " });

    [Fact]
    public async Task Save_inserts_then_updates_and_normalises_tags()
    {
        var id = await AddAsync("  Outer Wilds ");
        var game = await _catalog.GetGameDetailAsync(id);
        Assert.Equal("Outer Wilds", game!.Title);
        Assert.Equal("RPG,Co-op", game.Tags);
        Assert.True(game.AddedAtUtc > DateTime.UtcNow.AddMinutes(-1));

        game.Title = "Outer Wilds: Archaeologist Edition";
        game.CompletionPercent = 40;
        Assert.Equal(id, await _catalog.SaveGameAsync(game));
        Assert.Equal(40, (await _catalog.GetGameDetailAsync(id))!.CompletionPercent);
    }

    [Theory]
    [InlineData(101, 0, 0, null)]
    [InlineData(10, 5, 3, null)]
    [InlineData(10, 0, 0, "https://example.com/game.exe")]
    public async Task Save_rejects_invalid_values(int progress, int unlocked, int total, string? launch)
    {
        var game = new Game { Title = "Bad", CompletionPercent = progress, AchievementsUnlocked = unlocked, AchievementsTotal = total, LaunchTarget = launch };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _catalog.SaveGameAsync(game));
    }

    [Fact]
    public async Task Logging_a_session_updates_playtime_status_progress_and_last_played()
    {
        var id = await AddAsync("Hollow Knight", GameStatus.Backlog, hours: 2);
        var start = new DateTime(2026, 9, 20, 18, 0, 0, DateTimeKind.Utc);

        await _catalog.LogSessionAsync(new SessionLog(id, start, 90, "  Beat the Mantis Lords ", 35, "Find the City Crest"));

        var game = (await _catalog.GetGameDetailAsync(id))!;
        Assert.Equal(3.5, game.PlaytimeHours);
        Assert.Equal(GameStatus.Playing, game.Status);
        Assert.Equal(35, game.CompletionPercent);
        Assert.Equal("Find the City Crest", game.CurrentObjective);
        Assert.Equal(start.AddMinutes(90), game.LastPlayedAtUtc);
        var session = Assert.Single(game.Sessions);
        Assert.Equal("Beat the Mantis Lords", session.Note);
    }

    [Fact]
    public async Task Reaching_100_percent_completes_the_game_and_deleting_a_session_refunds_playtime()
    {
        var id = await AddAsync("Celeste", GameStatus.Playing, hours: 10);
        var session = await _catalog.LogSessionAsync(new SessionLog(id, DateTime.UtcNow.AddHours(-2), 60, null, 100));
        Assert.Equal(GameStatus.Completed, (await _catalog.GetGameDetailAsync(id))!.Status);

        await _catalog.DeleteSessionAsync(session.Id);
        var game = (await _catalog.GetGameDetailAsync(id))!;
        Assert.Equal(10, game.PlaytimeHours);
        Assert.Empty(game.Sessions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(24 * 60 + 1)]
    public async Task Sessions_must_have_a_sensible_duration(int minutes)
    {
        var id = await AddAsync("Tunic");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _catalog.LogSessionAsync(new SessionLog(id, DateTime.UtcNow, minutes, null)));
    }

    [Fact]
    public async Task Featured_flag_is_exclusive()
    {
        var a = await AddAsync("A");
        var b = await AddAsync("B");
        await _catalog.SetFeaturedAsync(a);
        await _catalog.SetFeaturedAsync(b);
        var games = await _catalog.GetGamesAsync();
        Assert.Equal(["B"], games.Where(g => g.IsFeatured).Select(g => g.Title));
    }

    [Fact]
    public async Task Deleting_a_game_removes_dependents_and_reports_its_media()
    {
        var id = await AddAsync("Inside");
        var game = (await _catalog.GetGameDetailAsync(id))!;
        game.CoverImagePath = "covers/c.jpg";
        game.CoverThumbnailPath = "covers/c.thumb.jpg";
        await _catalog.SaveGameAsync(game);
        await _catalog.AddJournalEntryAsync(id, "note");
        await _catalog.AddScreenshotAsync(new Screenshot { GameId = id, Title = "shot", ImagePath = "screenshots/s.png", ThumbnailPath = "screenshots/s.thumb.jpg", CapturedAtUtc = DateTime.UtcNow });
        await _catalog.LogSessionAsync(new SessionLog(id, DateTime.UtcNow, 30, null));

        var media = await _catalog.DeleteGameAsync(id);

        Assert.Equal(["covers/c.jpg", "covers/c.thumb.jpg", "screenshots/s.png", "screenshots/s.thumb.jpg"], media.Order());
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.JournalEntries.ToListAsync());
        Assert.Empty(await db.Screenshots.ToListAsync());
        Assert.Empty(await db.GameSessions.ToListAsync());
    }

    [Fact]
    public async Task Journal_entries_can_be_pinned_and_are_ordered_pinned_first()
    {
        var id = await AddAsync("Outer Wilds");
        var first = await _catalog.AddJournalEntryAsync(id, "first");
        await _catalog.AddJournalEntryAsync(id, "second");
        await _catalog.SetJournalPinnedAsync(first.Id, true);
        var game = (await _catalog.GetGameDetailAsync(id))!;
        Assert.Equal("first", game.JournalEntries[0].Text);
    }

    [Fact]
    public void Sample_sessions_are_deterministic_and_end_at_last_played()
    {
        var game = new Game { Title = "x", ArtworkSeed = 3, PlaytimeHours = 60, LastPlayedAtUtc = new DateTime(2026, 9, 1, 20, 0, 0, DateTimeKind.Utc) };
        var a = Arcadia.Data.Seeding.SampleDataSeeder.CreateSessions(game);
        var b = Arcadia.Data.Seeding.SampleDataSeeder.CreateSessions(game);
        Assert.Equal(a.Select(s => (s.StartedAtUtc, s.DurationMinutes)), b.Select(s => (s.StartedAtUtc, s.DurationMinutes)));
        Assert.Equal(game.LastPlayedAtUtc, a[0].StartedAtUtc.AddMinutes(a[0].DurationMinutes));
        Assert.All(a, s => Assert.True(s.IsSample));
    }
}

public class LaunchTargetTests
{
    [Theory]
    [InlineData("steam://rungameid/292030", true)]
    [InlineData("com.epicgames.launcher://apps/abc?action=launch", true)]
    [InlineData(@"C:\Games\Witcher 3\bin\x64\witcher3.exe", true)]
    [InlineData(@"""C:\Games\Hades\Hades.lnk""", true)]
    [InlineData("https://example.com", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData(@"C:\Scripts\run.bat", false)]
    [InlineData(@"C:\Scripts\run.ps1", false)]
    [InlineData(@"relative\game.exe", false)]
    [InlineData("", false)]
    public void Only_store_protocols_and_local_programs_are_allowed(string target, bool allowed) =>
        Assert.Equal(allowed, LaunchTargets.IsAllowed(target));
}

public class EditorParsingTests
{
    [Theory]
    [InlineData("", "en", true, 0)]
    [InlineData("96.4", "en", true, 96.4)]
    [InlineData("96,4", "de", true, 96.4)]
    [InlineData("12", "ar", true, 12)]
    [InlineData("-3", "en", false, 0)]
    [InlineData("abc", "en", false, 0)]
    public void Numbers_accept_ui_and_invariant_formats(string text, string culture, bool ok, double expected)
    {
        Assert.Equal(ok, GameEditorViewModel.TryNumber(text, CultureInfo.GetCultureInfo(culture), out var value));
        if (ok) Assert.Equal(expected, value, 3);
    }

    [Fact]
    public void Session_playtime_trend_counts_hours_per_month()
    {
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var sessions = new[]
        {
            new GameSession { StartedAtUtc = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc), DurationMinutes = 90 },
            new GameSession { StartedAtUtc = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc), DurationMinutes = 30 },
            new GameSession { StartedAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), DurationMinutes = 60 },
            new GameSession { StartedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), DurationMinutes = 600 },
        };
        var trends = LibraryTrends.From([], sessions, now);
        Assert.Equal(2.0, trends.Playtime[^1]);
        Assert.Equal(1.0, trends.Playtime[^2]);
        Assert.Equal(3.0, trends.Playtime.Sum());
    }
}

public sealed class MediaStoreTests : IDisposable
{
    private sealed class Paths(string root) : IAppPaths
    {
        public string SettingsDirectory => root;
        public string DataDirectory => root;
        public string LogsDirectory => root;
        public string CacheDirectory => root;
        public string DatabasePath => Path.Combine(root, "db");
        public string SettingsFilePath => Path.Combine(root, "s.json");
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcadia-media-" + Guid.NewGuid().ToString("N"));
    private readonly MediaStore _store;

    public MediaStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new MediaStore(new Paths(_root), NullLogger<MediaStore>.Instance);
    }

    private string CreatePng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 90; pixels[i + 2] = 40; pixels[i + 3] = 255; }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var path = Path.Combine(_root, $"source-{Guid.NewGuid():N}.png");
        using var fs = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        encoder.Save(fs);
        return path;
    }

    [Fact]
    public async Task Import_copies_the_image_and_creates_a_thumbnail()
    {
        var source = CreatePng(1280, 720);
        var media = await _store.ImportAsync(source, MediaKind.Screenshot);

        Assert.False(Path.IsPathRooted(media.Path));
        Assert.True(File.Exists(_store.Resolve(media.Path)));
        Assert.NotNull(media.ThumbnailPath);
        var thumb = BitmapFrame.Create(new Uri(_store.Resolve(media.ThumbnailPath)!));
        Assert.Equal(480, thumb.PixelWidth);
        Assert.Equal(1280, media.PixelWidth);
    }

    [Fact]
    public async Task Import_rejects_files_that_are_not_images()
    {
        var fake = Path.Combine(_root, "fake.png");
        await File.WriteAllTextAsync(fake, "not an image");
        await Assert.ThrowsAnyAsync<Exception>(() => _store.ImportAsync(fake, MediaKind.Cover));
        await Assert.ThrowsAsync<NotSupportedException>(() => _store.ImportAsync(Path.Combine(_root, "x.exe"), MediaKind.Cover));
    }

    [Fact]
    public async Task Delete_never_touches_files_outside_the_media_folder()
    {
        var outside = CreatePng(10, 10);
        var media = await _store.ImportAsync(CreatePng(64, 64), MediaKind.Cover);

        _store.Delete([outside, media.Path, "../s.json", null]);

        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(_store.Resolve(media.Path)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public class ForeignDatabaseTests
{
    [Fact]
    public async Task A_database_created_by_another_program_is_refused_and_left_untouched()
    {
        var path = Path.Combine(Path.GetTempPath(), $"foreign-{Guid.NewGuid():N}.db");
        try
        {
            await using (var cn = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await cn.OpenAsync();
                var cmd = cn.CreateCommand();
                cmd.CommandText = "CREATE TABLE Games (Id INTEGER PRIMARY KEY, Title TEXT, Platform INTEGER); INSERT INTO Games (Title, Platform) VALUES ('Theirs', 0);";
                await cmd.ExecuteNonQueryAsync();
            }
            var before = await File.ReadAllBytesAsync(path);

            var options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            var init = new DatabaseInitializer(new Factory(options), TimeProvider.System, NullLogger<DatabaseInitializer>.Instance);

            await Assert.ThrowsAsync<IncompatibleDatabaseException>(() => init.InitializeAsync(seedSampleData: true));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_new_or_arcadia_database_is_accepted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ours-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            var init = new DatabaseInitializer(new Factory(options), TimeProvider.System, NullLogger<DatabaseInitializer>.Instance);
            await init.InitializeAsync(seedSampleData: true);   // creates
            await init.InitializeAsync(seedSampleData: true);   // re-opens its own file
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class Factory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }
}
