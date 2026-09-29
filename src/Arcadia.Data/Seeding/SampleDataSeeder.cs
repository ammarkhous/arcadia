using Arcadia.Core.Games;

namespace Arcadia.Data.Seeding;

/// <summary>
/// Development sample library. Every row is flagged <c>IsSample</c> so it can be purged
/// independently of real user data. Artwork is procedural (theme + seed); no third-party images.
/// </summary>
public static class SampleDataSeeder
{
    public static void Seed(ArcadiaDbContext db, DateTime nowUtc)
    {
        foreach (var game in CreateGames(nowUtc))
        {
            game.Sessions.AddRange(CreateSessions(game));
            db.Games.Add(game);
        }
    }

    /// <summary>
    /// Adds sample play sessions to existing sample games (for libraries seeded before sessions existed).
    /// Returns the number of sessions added.
    /// </summary>
    public static int AddMissingSessions(ArcadiaDbContext db)
    {
        if (db.GameSessions.Any()) return 0;
        int added = 0;
        foreach (var game in db.Games.Where(g => g.IsSample && g.LastPlayedAtUtc != null).ToList())
        {
            var sessions = CreateSessions(game);
            foreach (var s in sessions) s.GameId = game.Id;
            db.GameSessions.AddRange(sessions);
            added += sessions.Count;
        }
        return added;
    }

    /// <summary>A plausible, deterministic history of sessions ending at the game's last-played time.</summary>
    public static List<GameSession> CreateSessions(Game game)
    {
        if (game.LastPlayedAtUtc is not { } last || game.PlaytimeHours <= 0) return [];
        var rng = new Random(game.ArtworkSeed * 97 + 13);
        int count = Math.Clamp((int)(game.PlaytimeHours / 6), 4, 16);
        var sessions = new List<GameSession>();
        var end = last;
        for (int i = 0; i < count; i++)
        {
            int minutes = 40 + rng.Next(0, 9) * 15;
            var start = end.AddMinutes(-minutes);
            sessions.Add(new GameSession { StartedAtUtc = start, DurationMinutes = minutes, IsSample = true });
            end = start.AddDays(-(1 + rng.Next(0, 9))).AddHours(-rng.Next(0, 5));
        }
        return sessions;
    }

    public static IReadOnlyList<Game> CreateGames(DateTime now)
    {
        Game G(string title, string platform, string tags, GameStatus status, int pct, double hours, string theme, int seed,
               int ach = 0, int achTotal = 0, string? objective = null, TimeSpan? lastPlayedAgo = null,
               BacklogPriority priority = BacklogPriority.Normal, double? estimate = null, string? description = null, int addedDaysAgo = 200) => new()
        {
            Title = title, Platform = platform, Tags = tags, Status = status, CompletionPercent = pct, PlaytimeHours = hours,
            ArtworkTheme = theme, ArtworkSeed = seed, AchievementsUnlocked = ach, AchievementsTotal = achTotal,
            CurrentObjective = objective, LastPlayedAtUtc = lastPlayedAgo is { } ago ? now - ago : null,
            Priority = priority, EstimatedHours = estimate, Description = description,
            AddedAtUtc = now.AddDays(-addedDaysAgo), IsSample = true,
        };

        var witcher = G("The Witcher 3: Wild Hunt", "PC", "RPG,Open World,Single Player", GameStatus.Playing, 82, 96.4, "castle-dusk", 3,
            48, 78, "The Isle of Mists", TimeSpan.FromHours(2),
            description: "A sprawling dark-fantasy journey across war-torn kingdoms. A monster hunter for hire follows the trail of his adopted daughter while an otherworldly army closes in.",
            addedDaysAgo: 140);
        witcher.IsFeatured = true;

        witcher.Screenshots.AddRange(
        [
            new() { Title = "Skellige — Beautiful view!", Location = "Kaer Trolde, Skellige", Note = "Explored the northern islands. Amazing atmosphere.", Tags = "landscape,skellige", CapturedAtUtc = now.AddDays(-3), ArtworkTheme = "castle-dusk", ArtworkSeed = 11, IsSample = true },
            new() { Title = "Road to Novigrad", Location = "Velen", Note = "Storm rolling in over the swamps.", Tags = "landscape", CapturedAtUtc = now.AddDays(-4), ArtworkTheme = "mountains-storm", ArtworkSeed = 12, IsSample = true },
            new() { Title = "Harbour at dusk", Location = "Novigrad", Note = "City lights across the water.", Tags = "city", CapturedAtUtc = now.AddDays(-6), ArtworkTheme = "lake-sunset", ArtworkSeed = 13, IsSample = true },
            new() { Title = "Campfire", Location = "White Orchard", Note = "Quiet night before the hunt.", Tags = "night", CapturedAtUtc = now.AddDays(-9), ArtworkTheme = "forest-ember", ArtworkSeed = 14, IsSample = true },
        ]);
        witcher.JournalEntries.AddRange(
        [
            new() { Text = "Reached Novigrad — finally.", CreatedAtUtc = now.AddHours(-3), IsSample = true },
            new() { Text = "Need to return to the hidden cave near Crow's Perch.", CreatedAtUtc = now.AddDays(-1), IsPinned = true, IsSample = true },
            new() { Text = "Finish the side quest before continuing the story.", CreatedAtUtc = now.AddDays(-2), IsSample = true },
        ]);

        var elden = G("Elden Ring", "PC", "Action RPG,Open World", GameStatus.Playing, 45, 61, "golden-realm", 21, 19, 42, "Reach the Royal Capital", TimeSpan.FromDays(1).Add(TimeSpan.FromHours(3)), addedDaysAgo: 90);
        elden.JournalEntries.Add(new() { Text = "Try the boss again with a stronger spirit summon.", CreatedAtUtc = now.AddDays(-1).AddHours(-4), IsSample = true });

        return
        [
            witcher,
            elden,
            G("Baldur's Gate 3", "PC", "RPG,Turn-based,Co-op", GameStatus.Completed, 100, 138, "castle-violet", 31, 54, 54, "Completed", TimeSpan.FromDays(2), addedDaysAgo: 300),
            G("Red Dead Redemption 2", "PS5", "Action,Open World,Western", GameStatus.Playing, 67, 74.5, "desert-sunset", 41, 31, 52, "Chapter 4 — Saint Denis", TimeSpan.FromDays(4), addedDaysAgo: 260),
            G("Cyberpunk 2077", "PC", "RPG,Open World,Sci-Fi", GameStatus.Playing, 38, 42, "neon-city", 51, 17, 57, "Phantom Liberty — Dogtown", TimeSpan.FromDays(6), addedDaysAgo: 120),
            G("Minecraft", "PC", "Sandbox,Survival,Co-op", GameStatus.Playing, 0, 212, "blocks-day", 61, 44, 122, "Build the river fortress", TimeSpan.FromDays(9), addedDaysAgo: 900),
            G("Ghost of Tsushima", "PS5", "Action,Open World", GameStatus.Playing, 21, 14, "mountains-crimson", 71, 9, 77, "Izuhara — Act I", TimeSpan.FromDays(12), addedDaysAgo: 30),

            G("Starfield", "PC", "RPG,Space,Exploration", GameStatus.Backlog, 0, 0, "space-nebula", 81, 0, 50, priority: BacklogPriority.High, estimate: 90, addedDaysAgo: 60),
            G("Hogwarts Legacy", "PC", "Action RPG,Open World", GameStatus.Backlog, 0, 0, "castle-night", 82, 0, 46, priority: BacklogPriority.High, estimate: 45, addedDaysAgo: 50),
            G("Alan Wake 2", "PS5", "Survival Horror,Story", GameStatus.Backlog, 0, 0, "forest-night", 83, 0, 64, priority: BacklogPriority.Normal, estimate: 22, addedDaysAgo: 40),
            G("God of War Ragnarök", "PS5", "Action,Adventure", GameStatus.Backlog, 0, 0, "mountains-snow", 84, 0, 36, priority: BacklogPriority.Normal, estimate: 35, addedDaysAgo: 35),
            G("Horizon Forbidden West", "PS5", "Action RPG,Open World", GameStatus.Backlog, 0, 0, "lake-sunset", 85, 0, 59, priority: BacklogPriority.Low, estimate: 60, addedDaysAgo: 20),
            G("Disco Elysium", "PC", "RPG,Story", GameStatus.Backlog, 0, 0, "lake-storm", 86, 0, 45, priority: BacklogPriority.Low, estimate: 30, addedDaysAgo: 15),

            G("Mass Effect Legendary Edition", "PC", "RPG,Sci-Fi", GameStatus.Completed, 100, 118, "space-planet", 91, 62, 72, lastPlayedAgo: TimeSpan.FromDays(80), addedDaysAgo: 500),
            G("Hades", "PC", "Roguelike,Action", GameStatus.Completed, 100, 64, "forest-ember", 92, 49, 49, lastPlayedAgo: TimeSpan.FromDays(120), addedDaysAgo: 700),
            G("Portal 2", "PC", "Puzzle,Co-op", GameStatus.Completed, 100, 19, "blocks-night", 93, 51, 51, lastPlayedAgo: TimeSpan.FromDays(400), addedDaysAgo: 1500),
            G("Half-Life: Alyx", "PC VR", "Action,VR", GameStatus.Completed, 100, 15, "neon-city", 94, 21, 29, lastPlayedAgo: TimeSpan.FromDays(600), addedDaysAgo: 1400),
            G("Stardew Valley", "PC", "Farming,Co-op", GameStatus.Completed, 100, 87, "blocks-day", 95, 33, 40, lastPlayedAgo: TimeSpan.FromDays(200), addedDaysAgo: 1000),

            G("Death Stranding", "PC", "Action,Exploration", GameStatus.Abandoned, 23, 11, "mountains-storm", 96, 6, 58, "Episode 3", TimeSpan.FromDays(300), addedDaysAgo: 800),
            G("Anthem", "PC", "Action,Online", GameStatus.Abandoned, 12, 6, "lake-storm", 97, 3, 50, lastPlayedAgo: TimeSpan.FromDays(900), addedDaysAgo: 1000),
        ];
    }
}
