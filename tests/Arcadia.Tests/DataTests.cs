using Arcadia.Core.Games;
using Arcadia.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

/// <summary>Runs the real migrations against an in-memory SQLite database.</summary>
public sealed class DataTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ArcadiaDbContext> _factory = null!;

    private sealed class Factory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly FixedTime Clock = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite(_connection).Options);
        await new DatabaseInitializer(_factory, Clock, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: true);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Dashboard_features_the_flagged_game_and_orders_recent_by_last_played()
    {
        var snapshot = await new GameLibraryService(_factory, Clock).GetHomeDashboardAsync();

        Assert.Equal("The Witcher 3: Wild Hunt", snapshot.Featured?.Title);
        Assert.Equal(5, snapshot.RecentGames.Count);
        Assert.Equal(["The Witcher 3: Wild Hunt", "Elden Ring", "Baldur's Gate 3", "Red Dead Redemption 2", "Cyberpunk 2077"],
            snapshot.RecentGames.Select(g => g.Title));
        Assert.True(snapshot.RecentGames.Zip(snapshot.RecentGames.Skip(1)).All(p => p.First.LastPlayedAtUtc >= p.Second.LastPlayedAtUtc));
    }

    [Fact]
    public async Task Upcoming_contains_backlog_and_just_started_games_but_not_long_running_ones()
    {
        var snapshot = await new GameLibraryService(_factory, Clock).GetHomeDashboardAsync();

        Assert.Equal(5, snapshot.Backlog.Count);
        Assert.Equal("Starfield", snapshot.Backlog[0].Title); // high priority first
        Assert.Contains(snapshot.Backlog, g => g.Title == "Ghost of Tsushima");
        Assert.DoesNotContain(snapshot.Backlog, g => g.Title == "Minecraft");
    }

    [Fact]
    public async Task Stats_journal_and_screenshots_are_populated()
    {
        var snapshot = await new GameLibraryService(_factory, Clock).GetHomeDashboardAsync();

        Assert.Equal(20, snapshot.Stats.Total);
        Assert.Equal(snapshot.Stats.Total, snapshot.Stats.Playing + snapshot.Stats.Completed + snapshot.Stats.Backlog + snapshot.Stats.Abandoned);
        Assert.Equal(4, snapshot.Screenshots.Count);
        Assert.True(snapshot.Journal[0].IsPinned);
        Assert.Equal(snapshot.Stats.Total, snapshot.Trends.Total[^1]);
    }

    [Fact]
    public async Task Seeding_is_skipped_when_the_library_already_has_games()
    {
        await new DatabaseInitializer(_factory, Clock, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: true);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(20, await db.Games.CountAsync());
    }

    [Fact]
    public async Task Removing_sample_data_keeps_user_data()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Games.Add(new Game { Title = "My Own Game", Status = GameStatus.Playing, AddedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await new DatabaseInitializer(_factory, Clock, NullLogger<DatabaseInitializer>.Instance).RemoveSampleDataAsync();

        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(["My Own Game"], await check.Games.Select(g => g.Title).ToListAsync());
        Assert.Empty(await check.Screenshots.ToListAsync());
        Assert.Empty(await check.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task Model_has_no_pending_changes_relative_to_migrations()
    {
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(db.Database.HasPendingModelChanges(), "Run `dotnet ef migrations add` after changing the model.");
    }
}
