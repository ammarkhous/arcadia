namespace Arcadia.Core.Games;

/// <summary>Aggregated numbers for the library overview.</summary>
public sealed record GameLibraryStats(
    int Total,
    int Playing,
    int Completed,
    int Backlog,
    int Abandoned,
    double TotalPlaytimeHours,
    int AchievementsUnlocked,
    int AchievementsTotal)
{
    public static GameLibraryStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    public int PercentOf(int count) => Total == 0 ? 0 : (int)Math.Round(count * 100.0 / Total, MidpointRounding.AwayFromZero);

    public static GameLibraryStats From(IEnumerable<Game> games)
    {
        int total = 0, playing = 0, completed = 0, backlog = 0, abandoned = 0, unlocked = 0, achievements = 0;
        double hours = 0;
        foreach (var g in games)
        {
            total++;
            hours += g.PlaytimeHours;
            unlocked += g.AchievementsUnlocked;
            achievements += g.AchievementsTotal;
            switch (g.Status)
            {
                case GameStatus.Playing: playing++; break;
                case GameStatus.Completed: completed++; break;
                case GameStatus.Backlog: backlog++; break;
                case GameStatus.Abandoned: abandoned++; break;
            }
        }
        return new(total, playing, completed, backlog, abandoned, hours, unlocked, achievements);
    }
}

/// <summary>Everything the Home dashboard needs, fetched in one round trip.</summary>
public sealed record HomeDashboardSnapshot(
    Game? Featured,
    IReadOnlyList<Game> RecentGames,
    IReadOnlyList<Game> Backlog,
    IReadOnlyList<Screenshot> Screenshots,
    IReadOnlyList<JournalEntry> Journal,
    GameLibraryStats Stats,
    LibraryTrends Trends);

/// <summary>
/// Cumulative library size per status at the end of each of the last N months (oldest first).
/// Drives the level meters in the overview.
/// </summary>
/// <remarks><see cref="Playtime"/> is hours actually played per month, from logged sessions.</remarks>
public sealed record LibraryTrends(int[] Total, int[] Completed, int[] Playing, int[] Backlog, int[] Abandoned, double[] Playtime)
{
    public static LibraryTrends From(IReadOnlyCollection<Game> games, DateTime nowUtc, int months = 8) =>
        From(games, [], nowUtc, months);

    public static LibraryTrends From(IReadOnlyCollection<Game> games, IReadOnlyCollection<GameSession> sessions, DateTime nowUtc, int months = 8)
    {
        var firstMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1 - months);
        var playtime = new double[months];
        foreach (var s in sessions)
        {
            int index = (s.StartedAtUtc.Year - firstMonth.Year) * 12 + s.StartedAtUtc.Month - firstMonth.Month;
            if (index >= 0 && index < months) playtime[index] += s.Hours;
        }

        int[] Series(Func<Game, bool> filter)
        {
            var result = new int[months];
            for (int i = 0; i < months; i++)
            {
                var monthEnd = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(i - months + 2);
                result[i] = games.Count(g => filter(g) && g.AddedAtUtc < monthEnd);
            }
            return result;
        }

        return new LibraryTrends(
            Series(_ => true),
            Series(g => g.Status == GameStatus.Completed),
            Series(g => g.Status == GameStatus.Playing),
            Series(g => g.Status == GameStatus.Backlog),
            Series(g => g.Status == GameStatus.Abandoned),
            playtime);
    }
}
