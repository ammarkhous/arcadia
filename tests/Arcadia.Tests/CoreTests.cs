using System.Globalization;
using Arcadia.Core.Formatting;
using Arcadia.Core.Games;
using Arcadia.Core.Localization;

namespace Arcadia.Tests;

public class GameLibraryStatsTests
{
    private static Game G(GameStatus status, double hours = 1, int ach = 0, int achTotal = 0, int addedDaysAgo = 10) => new()
    {
        Title = "Game",
        Status = status,
        PlaytimeHours = hours,
        AchievementsUnlocked = ach,
        AchievementsTotal = achTotal,
        AddedAtUtc = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc).AddDays(-addedDaysAgo),
    };

    [Fact]
    public void From_counts_each_status_and_sums_totals()
    {
        var stats = GameLibraryStats.From(
        [
            G(GameStatus.Playing, 10, 1, 10),
            G(GameStatus.Playing, 5, 2, 20),
            G(GameStatus.Completed, 20, 30, 30),
            G(GameStatus.Backlog),
            G(GameStatus.Abandoned, 2),
        ]);

        Assert.Equal(5, stats.Total);
        Assert.Equal(2, stats.Playing);
        Assert.Equal(1, stats.Completed);
        Assert.Equal(1, stats.Backlog);
        Assert.Equal(1, stats.Abandoned);
        Assert.Equal(38, stats.TotalPlaytimeHours);
        Assert.Equal(33, stats.AchievementsUnlocked);
        Assert.Equal(60, stats.AchievementsTotal);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(6, 20, 30)]
    [InlineData(1, 3, 33)]
    [InlineData(2, 3, 67)]
    public void PercentOf_rounds_half_away_from_zero(int count, int total, int expected)
    {
        var stats = GameLibraryStats.Empty with { Total = total };
        Assert.Equal(expected, stats.PercentOf(count));
    }

    [Fact]
    public void Trends_are_cumulative_and_end_with_current_totals()
    {
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var games = new List<Game>
        {
            G(GameStatus.Playing, addedDaysAgo: 5),
            G(GameStatus.Playing, addedDaysAgo: 70),
            G(GameStatus.Completed, addedDaysAgo: 400),
        };

        var trends = LibraryTrends.From(games, now, months: 8);

        Assert.Equal(8, trends.Total.Length);
        Assert.Equal(3, trends.Total[^1]);
        Assert.Equal(2, trends.Playing[^1]);
        Assert.Equal(1, trends.Completed[0]);
        for (int i = 1; i < trends.Total.Length; i++) Assert.True(trends.Total[i] >= trends.Total[i - 1]);
    }

    [Fact]
    public void TagList_splits_and_trims()
    {
        var game = new Game { Title = "x", Tags = " RPG, Open World ,,Single Player" };
        Assert.Equal(["RPG", "Open World", "Single Player"], game.TagList);
    }
}

public class RelativeTimeTests
{
    private sealed class FakeLocalizer : ILocalizer
    {
        private static readonly Dictionary<string, string> Strings = new()
        {
            ["Time_JustNow"] = "just now",
            ["Time_MinutesAgo"] = "{0} min ago",
            ["Time_HoursAgo"] = "{0} h ago",
            ["Time_Yesterday"] = "yesterday",
            ["Time_DaysAgo"] = "{0} days ago",
            ["Unit_Hours"] = "{0} h",
            ["Unit_Minutes"] = "{0} min",
        };

        public CultureInfo Culture => CultureInfo.InvariantCulture;
        public bool IsRightToLeft => false;
        public string this[string key] => Strings[key];
        public string Format(string key, params object[] args) => string.Format(Culture, this[key], args);
        public event EventHandler? CultureChanged { add { } remove { } }
    }

    private static readonly DateTime Now = new(2026, 9, 27, 18, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0.2, "just now")]
    [InlineData(25, "25 min ago")]
    [InlineData(150, "2 h ago")]
    public void Formats_recent_times(double minutesAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.Format(Now.AddMinutes(-minutesAgo), Now, new FakeLocalizer()));

    [Fact]
    public void Formats_yesterday_and_days()
    {
        var loc = new FakeLocalizer();
        Assert.Equal("yesterday", RelativeTime.Format(Now.AddDays(-1), Now, loc));
        Assert.Equal("4 days ago", RelativeTime.Format(Now.AddDays(-4), Now, loc));
    }

    [Fact]
    public void Future_timestamps_are_treated_as_now() =>
        Assert.Equal("just now", RelativeTime.Format(Now.AddMinutes(5), Now, new FakeLocalizer()));

    [Theory]
    [InlineData(96.4, "96 h")]
    [InlineData(2.25, "2.3 h")]
    [InlineData(0.25, "15 min")]
    [InlineData(0.004, "1 min")]
    [InlineData(0, "0 h")]
    public void Formats_hours(double hours, string expected) => Assert.Equal(expected, RelativeTime.Hours(hours, new FakeLocalizer()));
}
