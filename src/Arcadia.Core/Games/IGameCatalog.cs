namespace Arcadia.Core.Games;

/// <summary>Reads and writes the game library (everything beyond the Home dashboard query).</summary>
public interface IGameCatalog
{
    Task<IReadOnlyList<Game>> GetGamesAsync(CancellationToken cancellationToken = default);

    /// <summary>A game with its sessions, journal and screenshots, or null.</summary>
    Task<Game?> GetGameDetailAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Inserts (Id == 0) or updates the scalar properties of a game. Returns its id.</summary>
    Task<int> SaveGameAsync(Game game, CancellationToken cancellationToken = default);

    /// <summary>Deletes a game and its dependent rows. Returns media paths the caller should remove.</summary>
    Task<IReadOnlyList<string>> DeleteGameAsync(int id, CancellationToken cancellationToken = default);

    Task SetFeaturedAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a session and updates the game's playtime, last-played time, status (Backlog → Playing)
    /// and optionally its progress and current objective.
    /// </summary>
    Task<GameSession> LogSessionAsync(SessionLog log, CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(int sessionId, CancellationToken cancellationToken = default);

    Task<JournalEntry> AddJournalEntryAsync(int gameId, string text, CancellationToken cancellationToken = default);
    Task SetJournalPinnedAsync(int entryId, bool pinned, CancellationToken cancellationToken = default);
    Task DeleteJournalEntryAsync(int entryId, CancellationToken cancellationToken = default);

    Task<Screenshot> AddScreenshotAsync(Screenshot screenshot, CancellationToken cancellationToken = default);
    /// <summary>True if a screenshot imported from this source file already exists.</summary>
    Task<bool> HasScreenshotFromSourceAsync(string sourcePath, CancellationToken cancellationToken = default);
    /// <summary>Remembers where a game runs from (used by play tracking).</summary>
    Task SetInstallDirectoryAsync(int gameId, string? installDirectory, CancellationToken cancellationToken = default);
    Task UpdateScreenshotAsync(Screenshot screenshot, CancellationToken cancellationToken = default);
    /// <summary>Returns media paths the caller should remove.</summary>
    Task<IReadOnlyList<string>> DeleteScreenshotAsync(int screenshotId, CancellationToken cancellationToken = default);
}

public sealed record SessionLog(
    int GameId,
    DateTime StartedAtUtc,
    int DurationMinutes,
    string? Note,
    int? CompletionPercent = null,
    string? CurrentObjective = null,
    bool IsAutomatic = false);
