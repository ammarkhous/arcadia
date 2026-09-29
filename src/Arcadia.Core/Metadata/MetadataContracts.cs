namespace Arcadia.Core.Metadata;

/// <summary>A search hit from the game-information provider (IGDB).</summary>
public sealed record GameMatch(long Id, string Name, int? ReleaseYear, string Platforms, string? CoverUrl);

/// <summary>Facts about a game from the game-information provider (IGDB).</summary>
public sealed record GameDetails(
    long Id,
    string Name,
    string? Summary,
    DateOnly? ReleaseDate,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Platforms,
    string? CoverUrl,
    IReadOnlyList<string> ScreenshotUrls,
    int? SteamAppId,
    double? Rating);

/// <summary>Which kind of artwork (SteamGridDB naming).</summary>
public enum ArtKind
{
    /// <summary>Portrait box art (600×900 grid).</summary>
    BoxArt,
    /// <summary>Landscape capsule (920×430 / 460×215 grid) — Arcadia's card cover.</summary>
    Capsule,
    /// <summary>Wide banner (1920×620 hero).</summary>
    Hero,
    /// <summary>Transparent title logo.</summary>
    Logo,
}

public sealed record ArtCandidate(long Id, ArtKind Kind, string Url, string ThumbUrl, int Width, int Height, string? Style, string? Author);

/// <summary>Descriptions, dates, companies, genres (IGDB).</summary>
public interface IGameInfoProvider
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<GameMatch>> SearchAsync(string title, CancellationToken cancellationToken = default);
    Task<GameDetails?> GetDetailsAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>Custom box art, heroes and transparent logos (SteamGridDB).</summary>
public interface IGameArtProvider
{
    bool IsConfigured { get; }
    /// <summary>Finds the provider's game id, preferring an exact Steam app id match over a title search.</summary>
    Task<long?> FindGameAsync(string title, int? steamAppId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArtCandidate>> GetArtAsync(long gameId, ArtKind kind, CancellationToken cancellationToken = default);
}

/// <summary>Thrown for provider problems worth telling the user about (bad credentials, rate limits, offline).</summary>
public sealed class MetadataException(string message, MetadataFailure failure, Exception? inner = null) : Exception(message, inner)
{
    public MetadataFailure Failure { get; } = failure;
}

public enum MetadataFailure { NotConfigured, Unauthorized, RateLimited, Unavailable, InvalidResponse }
