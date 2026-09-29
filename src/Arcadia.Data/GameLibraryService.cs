using Arcadia.Core.Games;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

public sealed class GameLibraryService(IDbContextFactory<ArcadiaDbContext> contextFactory, TimeProvider time) : IGameLibraryService
{
    private const double JustStartedHours = 20;

    public async Task<HomeDashboardSnapshot> GetHomeDashboardAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // The library is small (hundreds of rows at most); a single read keeps stats consistent.
        var games = await db.Games.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        var featured = games.FirstOrDefault(g => g.IsFeatured)
            ?? games.Where(g => g.Status == GameStatus.Playing).OrderByDescending(g => g.LastPlayedAtUtc).FirstOrDefault();

        var recent = games.Where(g => g.LastPlayedAtUtc != null)
            .OrderByDescending(g => g.LastPlayedAtUtc)
            .Take(5)
            .ToList();

        // "Upcoming": the backlog plus games only just started that are not already in the recent list.
        var recentIds = recent.Select(g => g.Id).ToHashSet();
        var backlog = games.Where(g => g.Status == GameStatus.Backlog
                || (g.Status == GameStatus.Playing && g.PlaytimeHours < JustStartedHours && !recentIds.Contains(g.Id)))
            .OrderByDescending(g => g.Priority)
            .ThenBy(g => g.AddedAtUtc)
            .Take(5)
            .ToList();

        var screenshots = await db.Screenshots.AsNoTracking()
            .Include(s => s.Game)
            .OrderByDescending(s => s.CapturedAtUtc)
            .Take(4)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var journal = await db.JournalEntries.AsNoTracking()
            .Include(j => j.Game)
            .OrderByDescending(j => j.IsPinned)
            .ThenByDescending(j => j.CreatedAtUtc)
            .Take(4)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var now = time.GetUtcNow().UtcDateTime;
        var since = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-8);
        var sessions = await db.GameSessions.AsNoTracking()
            .Where(s => s.StartedAtUtc >= since)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new HomeDashboardSnapshot(featured, recent, backlog, screenshots, journal,
            GameLibraryStats.From(games), LibraryTrends.From(games, sessions, now));
    }
}
