using Arcadia.Core.Games;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

public sealed class GameCatalog(IDbContextFactory<ArcadiaDbContext> contextFactory, TimeProvider time) : IGameCatalog
{
    public async Task<IReadOnlyList<Game>> GetGamesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Games.AsNoTracking().OrderBy(g => g.Title).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Game?> GetGameDetailAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var game = await db.Games.AsNoTracking()
            .Include(g => g.Sessions)
            .Include(g => g.JournalEntries)
            .Include(g => g.Screenshots)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken).ConfigureAwait(false);
        if (game == null) return null;

        game.Sessions = [.. game.Sessions.OrderByDescending(s => s.StartedAtUtc)];
        game.JournalEntries = [.. game.JournalEntries.OrderByDescending(j => j.IsPinned).ThenByDescending(j => j.CreatedAtUtc)];
        game.Screenshots = [.. game.Screenshots.OrderByDescending(s => s.CapturedAtUtc)];
        return game;
    }

    public async Task<int> SaveGameAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(game.Title);
        Validate(game);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (game.Id == 0)
        {
            var entity = CopyScalars(game, new Game { Title = game.Title });
            entity.AddedAtUtc = time.GetUtcNow().UtcDateTime;
            db.Games.Add(entity);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return entity.Id;
        }

        var existing = await db.Games.FirstOrDefaultAsync(g => g.Id == game.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Game {game.Id} no longer exists.");
        CopyScalars(game, existing);
        existing.IsSample = false; // an edited sample becomes the user's own entry
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return existing.Id;
    }

    public async Task<IReadOnlyList<string>> DeleteGameAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var game = await db.Games.Include(g => g.Screenshots).FirstOrDefaultAsync(g => g.Id == id, cancellationToken).ConfigureAwait(false);
        if (game == null) return [];

        var media = new List<string?> { game.CoverImagePath, game.CoverThumbnailPath, game.HeroImagePath, game.BoxArtPath, game.LogoImagePath };
        media.AddRange(game.Screenshots.SelectMany(s => new[] { s.ImagePath, s.ThumbnailPath }));

        // Journal entries outlive the game (FK set null) unless we remove them explicitly.
        await db.JournalEntries.Where(j => j.GameId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Games.Remove(game);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return [.. media.Where(p => !string.IsNullOrEmpty(p))!];
    }

    public async Task SetFeaturedAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Games.Where(g => g.IsFeatured && g.Id != id).ExecuteUpdateAsync(s => s.SetProperty(g => g.IsFeatured, false), cancellationToken).ConfigureAwait(false);
        await db.Games.Where(g => g.Id == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.IsFeatured, true), cancellationToken).ConfigureAwait(false);
    }

    public async Task<GameSession> LogSessionAsync(SessionLog log, CancellationToken cancellationToken = default)
    {
        if (log.DurationMinutes is <= 0 or > 24 * 60) throw new ArgumentOutOfRangeException(nameof(log), "A session lasts between 1 minute and 24 hours.");
        if (log.CompletionPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(log), "Progress must be 0–100.");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == log.GameId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Game {log.GameId} no longer exists.");

        var session = new GameSession
        {
            GameId = game.Id,
            StartedAtUtc = log.StartedAtUtc,
            DurationMinutes = log.DurationMinutes,
            Note = string.IsNullOrWhiteSpace(log.Note) ? null : log.Note.Trim(),
            IsAutomatic = log.IsAutomatic,
        };
        db.GameSessions.Add(session);

        game.PlaytimeHours = Math.Round(game.PlaytimeHours + session.Hours, 2);
        var end = log.StartedAtUtc.AddMinutes(log.DurationMinutes);
        if (game.LastPlayedAtUtc == null || end > game.LastPlayedAtUtc) game.LastPlayedAtUtc = end;
        if (game.Status == GameStatus.Backlog) game.Status = GameStatus.Playing;
        if (log.CompletionPercent is { } pct)
        {
            game.CompletionPercent = pct;
            if (pct == 100) game.Status = GameStatus.Completed;
        }
        if (!string.IsNullOrWhiteSpace(log.CurrentObjective)) game.CurrentObjective = log.CurrentObjective.Trim();
        game.IsSample = false;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task DeleteSessionAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.GameSessions.Include(s => s.Game).FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken).ConfigureAwait(false);
        if (session == null) return;
        if (session.Game != null) session.Game.PlaytimeHours = Math.Max(0, Math.Round(session.Game.PlaytimeHours - session.Hours, 2));
        db.GameSessions.Remove(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JournalEntry> AddJournalEntryAsync(int gameId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = new JournalEntry { GameId = gameId, Text = text.Trim(), CreatedAtUtc = time.GetUtcNow().UtcDateTime };
        db.JournalEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async Task SetJournalPinnedAsync(int entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.JournalEntries.Where(j => j.Id == entryId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.IsPinned, pinned), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteJournalEntryAsync(int entryId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.JournalEntries.Where(j => j.Id == entryId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Screenshot> AddScreenshotAsync(Screenshot screenshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenshot.Title);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        screenshot.Game = null;
        db.Screenshots.Add(screenshot);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return screenshot;
    }

    public async Task<bool> HasScreenshotFromSourceAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Screenshots.AnyAsync(s => s.SourcePath == sourcePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetInstallDirectoryAsync(int gameId, string? installDirectory, CancellationToken cancellationToken = default)
    {
        var value = Clean(installDirectory)?.TrimEnd('\\');
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Games.Where(g => g.Id == gameId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.InstallDirectory, value), cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateScreenshotAsync(Screenshot screenshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenshot.Title);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Screenshots.FirstOrDefaultAsync(s => s.Id == screenshot.Id, cancellationToken).ConfigureAwait(false);
        if (existing == null) return;
        existing.Title = screenshot.Title.Trim();
        existing.Location = Clean(screenshot.Location);
        existing.Note = Clean(screenshot.Note);
        existing.Tags = screenshot.Tags?.Trim() ?? string.Empty;
        existing.IsSample = false;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> DeleteScreenshotAsync(int screenshotId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var shot = await db.Screenshots.FirstOrDefaultAsync(s => s.Id == screenshotId, cancellationToken).ConfigureAwait(false);
        if (shot == null) return [];
        db.Screenshots.Remove(shot);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new[] { shot.ImagePath, shot.ThumbnailPath }.Where(p => !string.IsNullOrEmpty(p)).ToList()!;
    }

    // ── helpers ─────────────────────────────────────────────────────

    private static void Validate(Game g)
    {
        if (g.CompletionPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(g), "Progress must be 0–100.");
        if (g.PlaytimeHours < 0) throw new ArgumentOutOfRangeException(nameof(g), "Playtime cannot be negative.");
        if (g.AchievementsUnlocked < 0 || g.AchievementsTotal < 0 || (g.AchievementsTotal > 0 && g.AchievementsUnlocked > g.AchievementsTotal))
            throw new ArgumentOutOfRangeException(nameof(g), "Unlocked achievements cannot exceed the total.");
        if (!string.IsNullOrWhiteSpace(g.LaunchTarget) && !LaunchTargets.IsAllowed(g.LaunchTarget))
            throw new ArgumentException("Unsupported launch target.", nameof(g));
    }

    private static Game CopyScalars(Game from, Game to)
    {
        to.Title = from.Title.Trim();
        to.Executables = string.IsNullOrWhiteSpace(from.Executables) ? null : string.Join("; ", Arcadia.Core.Tracking.TrackableGame.ParseExecutables(from.Executables));
        to.AutoGamingMode = from.AutoGamingMode;
        to.Platform = string.IsNullOrWhiteSpace(from.Platform) ? "PC" : from.Platform.Trim();
        to.Tags = string.Join(",", from.TagList);
        to.Description = Clean(from.Description);
        to.Status = from.Status;
        to.Priority = from.Priority;
        to.CompletionPercent = from.CompletionPercent;
        to.PlaytimeHours = from.PlaytimeHours;
        to.EstimatedHours = from.EstimatedHours;
        to.AchievementsUnlocked = from.AchievementsUnlocked;
        to.AchievementsTotal = from.AchievementsTotal;
        to.CurrentObjective = Clean(from.CurrentObjective);
        to.CoverImagePath = from.CoverImagePath;
        to.CoverThumbnailPath = from.CoverThumbnailPath;
        to.HeroImagePath = from.HeroImagePath;
        to.BoxArtPath = from.BoxArtPath;
        to.LogoImagePath = from.LogoImagePath;
        to.Developer = Clean(from.Developer);
        to.Publisher = Clean(from.Publisher);
        to.ReleaseDate = from.ReleaseDate;
        to.IgdbId = from.IgdbId;
        to.SteamGridDbId = from.SteamGridDbId;
        to.ArtworkTheme = from.ArtworkTheme;
        to.ArtworkSeed = from.ArtworkSeed;
        to.LaunchTarget = Clean(from.LaunchTarget)?.Trim('"');
        to.InstallDirectory = Clean(from.InstallDirectory)?.Trim('"').TrimEnd('\\');
        return to;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
