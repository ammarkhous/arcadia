namespace Arcadia.Core.Travel;

/// <summary>
/// A country or territory from Arcadia's bundled Natural Earth data (public domain): names in English, Arabic and
/// German, continent, a label position and outlines at two levels of detail, as rings of (lon, lat) pairs.
/// </summary>
public sealed class CountryInfo
{
    public required string Code { get; init; }
    public required string A3 { get; init; }
    public required string NameEn { get; init; }
    public required string NameAr { get; init; }
    public required string NameDe { get; init; }
    public required string Continent { get; init; }
    public required string Subregion { get; init; }
    public long Population { get; init; }
    public double LabelLatitude { get; init; }
    public double LabelLongitude { get; init; }
    /// <summary>UN member or observer state (the 195 used for "countries visited").</summary>
    public bool IsUnState { get; init; }
    /// <summary>Natural Earth type: "Sovereign country", "Country", "Dependency", "Disputed", "Indeterminate"…</summary>
    public required string Type { get; init; }
    /// <summary>Polygons → rings → flat (lon, lat) pairs. Coarse is for world views, Detail for closer zoom.</summary>
    public required IReadOnlyList<float[][]> Coarse { get; init; }
    public required IReadOnlyList<float[][]> Detail { get; init; }
    public (double MinLon, double MinLat, double MaxLon, double MaxLat) Bounds { get; init; }

    public string Name(string? language) => language switch
    {
        "ar" when NameAr.Length > 0 => NameAr,
        "de" when NameDe.Length > 0 => NameDe,
        _ => NameEn,
    };

    public bool Contains(double lat, double lon)
    {
        var (minLon, minLat, maxLon, maxLat) = Bounds;
        if (lon < minLon || lon > maxLon || lat < minLat || lat > maxLat) return false;
        var inside = false;
        foreach (var polygon in Detail)
        {
            if (polygon.Length == 0 || !GeoMath.RingContains(polygon[0], lon, lat)) continue;
            inside = true;
            for (int h = 1; h < polygon.Length; h++)
                if (GeoMath.RingContains(polygon[h], lon, lat)) { inside = false; break; }
            if (inside) return true;
        }
        return false;
    }
}

public interface ICountryCatalog
{
    IReadOnlyList<CountryInfo> All { get; }
    /// <summary>Number of UN member and observer states (the denominator for "countries visited").</summary>
    int UnStateCount { get; }
    CountryInfo? Find(string? code);
    /// <summary>The country whose outline contains the point (null at sea).</summary>
    CountryInfo? At(double latitude, double longitude);
    /// <summary>
    /// Like <see cref="At"/>, but tolerant of coastal places that the generalised outlines put a little out at sea
    /// (Amalfi, Lisbon…): nearby points up to ~35 km away are tried.
    /// </summary>
    CountryInfo? Near(double latitude, double longitude);
    /// <summary>Countries whose English, Arabic or German name (or code) matches.</summary>
    IReadOnlyList<CountryInfo> Search(string text, int limit = 10);
    /// <summary>Small PNG flag, or null if none is bundled.</summary>
    byte[]? Flag(string code);
}

public enum GeoOrigin { Offline, Online, Map }

/// <summary>A location found by search or picked on the map (not yet saved).</summary>
public sealed record GeoPlace(
    string Name,
    string? Detail,
    double Latitude,
    double Longitude,
    string? CountryCode,
    string? Region,
    string? City,
    PlaceType SuggestedType,
    GeoOrigin Origin,
    string? SourceRef = null,
    long? Population = null);

public enum GeoSearchError { None, Disabled, Offline, RateLimited, ServiceUnavailable, Failed }

public sealed record GeoSearchResult(IReadOnlyList<GeoPlace> Places, GeoSearchError Error = GeoSearchError.None, bool FromCache = false);

/// <summary>
/// Location search. Offline search uses the bundled GeoNames cities and Natural Earth countries and never leaves the
/// computer. Online search (OpenStreetMap Nominatim) runs only when the user explicitly asks for it, is rate limited to
/// one request per second, identifies Arcadia, and caches answers locally; only the typed query (or the clicked
/// coordinates for a reverse lookup) and the display language are sent.
/// </summary>
public interface IGeocodingService
{
    IReadOnlyList<GeoPlace> SearchOffline(string text, string? language, int limit = 12);
    Task<GeoSearchResult> SearchOnlineAsync(string text, string? language, CancellationToken cancellationToken = default);
    /// <summary>Nearest bundled city (within ~60 km) and the containing country; never goes online.</summary>
    GeoPlace? DescribeOffline(double latitude, double longitude, string? language);
    Task<GeoSearchResult> ReverseOnlineAsync(double latitude, double longitude, string? language, CancellationToken cancellationToken = default);
    bool OnlineEnabled { get; }
}

public enum TileStatus { Ok, NotModified, Disabled, Offline, NotFound, RateLimited, Failed }

public sealed record TileResult(byte[]? Data, TileStatus Status, bool FromCache = false);

/// <summary>A raster tile layer (XYZ scheme). Attribution must be shown whenever its tiles are visible.</summary>
public sealed record TileSourceInfo(string Id, string Attribution, int MinZoom, int MaxZoom, bool Darken);

/// <summary>
/// Online detail tiles for the map. Tiles are cached on disk and honour the provider's cache headers; there is no
/// bulk or background downloading — only the tiles in view are requested.
/// </summary>
public interface IMapTileService
{
    /// <summary>Null when online tiles are switched off (the bundled vector map is still drawn).</summary>
    TileSourceInfo? Source { get; }
    event EventHandler? SourceChanged;
    Task<TileResult> GetTileAsync(int zoom, int x, int y, CancellationToken cancellationToken = default);
    /// <summary>A cached copy, even if expired (used while offline); null if never downloaded.</summary>
    byte[]? GetCachedTile(int zoom, int x, int y);
}

/// <summary>What Arcadia reads from a photo, locally. The original file is never modified.</summary>
public sealed record PhotoMetadata(DateTime? TakenAt, double? Latitude, double? Longitude, string? Camera, int Orientation, int? PixelWidth, int? PixelHeight)
{
    public static readonly PhotoMetadata Empty = new(null, null, null, null, 1, null, null);
}

public interface IPhotoMetadataReader
{
    /// <summary>Never throws; unreadable or unsupported files return <see cref="PhotoMetadata.Empty"/>.</summary>
    PhotoMetadata Read(string path);
}
