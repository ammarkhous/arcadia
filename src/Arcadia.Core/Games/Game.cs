namespace Arcadia.Core.Games;

/// <summary>
/// A game in the user's library. Persisted by Arcadia.Data.
/// Artwork is referenced by path so that user supplied or provider supplied images can
/// replace the procedural placeholder (<see cref="ArtworkTheme"/> + <see cref="ArtworkSeed"/>).
/// </summary>
public sealed class Game
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string Platform { get; set; } = "PC";
    /// <summary>Comma separated genre/descriptor tags, e.g. "RPG,Open World".</summary>
    public string Tags { get; set; } = string.Empty;
    public string? Description { get; set; }
    public GameStatus Status { get; set; }
    public BacklogPriority Priority { get; set; } = BacklogPriority.Normal;

    /// <summary>0–100.</summary>
    public int CompletionPercent { get; set; }
    public double PlaytimeHours { get; set; }
    public double? EstimatedHours { get; set; }
    public int AchievementsUnlocked { get; set; }
    public int AchievementsTotal { get; set; }
    public string? CurrentObjective { get; set; }

    public DateTime AddedAtUtc { get; set; }
    public DateTime? LastPlayedAtUtc { get; set; }

    public bool IsFeatured { get; set; }

    /// <summary>Media path (relative to the media root, or absolute) of a user supplied cover.</summary>
    public string? CoverImagePath { get; set; }
    public string? CoverThumbnailPath { get; set; }
    public string? HeroImagePath { get; set; }
    /// <summary>Portrait box art (600×900).</summary>
    public string? BoxArtPath { get; set; }
    /// <summary>Transparent title logo shown over artwork.</summary>
    public string? LogoImagePath { get; set; }

    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    /// <summary>IGDB game id once the game has been matched online.</summary>
    public long? IgdbId { get; set; }
    /// <summary>SteamGridDB game id once artwork has been looked up.</summary>
    public long? SteamGridDbId { get; set; }
    public string ArtworkTheme { get; set; } = "mountains-dusk";
    public int ArtworkSeed { get; set; }

    /// <summary>
    /// What "Continue Playing" opens: a store URI (steam://rungameid/292030) or a path to an .exe / shortcut.
    /// Validated by <see cref="LaunchTargets"/>.
    /// </summary>
    public string? LaunchTarget { get; set; }

    /// <summary>
    /// Folder the game runs from. Processes started from here are recorded as play sessions.
    /// Resolved from Steam/Epic manifests, learned on first launch, or set by the user.
    /// </summary>
    public string? InstallDirectory { get; set; }
    /// <summary>Associated executables ("Cyberpunk2077.exe; D:\Emu\emu.exe"): when set, only these processes count as the game (Phase 9).</summary>
    public string? Executables { get; set; }
    /// <summary>Turn Gaming Mode on while this game runs (Phase 9).</summary>
    public bool AutoGamingMode { get; set; }

    /// <summary>True for development sample content that can be removed without touching user data.</summary>
    public bool IsSample { get; set; }

    public List<JournalEntry> JournalEntries { get; set; } = [];
    public List<Screenshot> Screenshots { get; set; } = [];
    public List<GameSession> Sessions { get; set; } = [];

    public IReadOnlyList<string> TagList =>
        Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
