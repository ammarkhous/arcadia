using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arcadia.Core.Settings;
using Arcadia.Core.Travel;
using Microsoft.Extensions.Logging;

namespace Arcadia.Infrastructure.Geo;

/// <summary>
/// Offline search over the bundled countries and GeoNames cities, and — only when the user explicitly asks — online
/// search/reverse lookups on OpenStreetMap Nominatim (https://operations.osmfoundation.org/policies/nominatim/):
/// at most one request per second, an identifying User-Agent, no search-as-you-type, answers cached on disk for 30
/// days. Only the query text (or the clicked coordinates, rounded to ~1 m) and the display language are sent.
/// </summary>
public sealed class GeocodingService : IGeocodingService
{
    public const string HttpClientName = "nominatim";
    private const string Endpoint = "https://nominatim.openstreetmap.org/";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1100);

    private readonly ICountryCatalog _countries;
    private readonly CityIndex _cities;
    private readonly IHttpClientFactory _http;
    private readonly ISettingsService _settings;
    private readonly ILogger<GeocodingService> _logger;
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public GeocodingService(ICountryCatalog countries, CityIndex cities, IHttpClientFactory http, ISettingsService settings, string cacheDirectory,
        TimeProvider time, ILogger<GeocodingService> logger)
    {
        _countries = countries;
        _cities = cities;
        _http = http;
        _settings = settings;
        _time = time;
        _logger = logger;
        _cacheDir = Path.Combine(cacheDirectory, "Geocoding");
    }

    public bool OnlineEnabled => _settings.Current.Travel.OnlineSearch;

    public IReadOnlyList<GeoPlace> SearchOffline(string text, string? language, int limit = 12)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (GeoMath.TryParse(text, out var lat, out var lon))
            return DescribeOffline(lat, lon, language) is { } here ? [here] : [];

        var results = new List<GeoPlace>();
        foreach (var c in _countries.Search(text, 4))
            results.Add(new GeoPlace(c.Name(language), Continent(c.Continent), c.LabelLatitude, c.LabelLongitude, c.Code, null, null,
                PlaceType.Country, GeoOrigin.Offline, "country:" + c.Code, c.Population));
        foreach (var city in _cities.Search(text, limit))
        {
            var country = _countries.Find(city.CountryCode);
            var name = language == "ar" && city.Arabic.Length > 0 ? city.Arabic : city.Name;
            results.Add(new GeoPlace(name, country?.Name(language), city.Latitude, city.Longitude, city.CountryCode, null, city.Name,
                city.Population >= 100_000 || city.IsCapital ? PlaceType.City : PlaceType.Town, GeoOrigin.Offline,
                $"geonames:{city.CountryCode}:{city.Ascii}", city.Population));
        }
        return results.Take(limit).ToList();

        static string Continent(string c) => c;
    }

    public GeoPlace? DescribeOffline(double latitude, double longitude, string? language)
    {
        if (!GeoMath.IsValid(latitude, longitude)) return null;
        var country = _countries.At(latitude, longitude);
        var city = _cities.Nearest(latitude, longitude, 40);
        country ??= city != null ? _countries.Find(city.CountryCode) : _countries.Near(latitude, longitude); // coastal points
        if (city != null && country != null && !string.Equals(city.CountryCode, country.Code, StringComparison.OrdinalIgnoreCase)) city = null; // across a border
        var cityName = city == null ? null : language == "ar" && city.Arabic.Length > 0 ? city.Arabic : city.Name;
        var name = cityName ?? country?.Name(language) ?? GeoMath.Format(latitude, longitude, 3);
        var detail = cityName != null ? country?.Name(language) : country == null ? null : GeoMath.Format(latitude, longitude, 3);
        return new GeoPlace(name, detail, latitude, longitude, country?.Code, null, city?.Name, PlaceType.Other, GeoOrigin.Map, null, city?.Population);
    }

    public Task<GeoSearchResult> SearchOnlineAsync(string text, string? language, CancellationToken cancellationToken = default)
    {
        text = text.Trim();
        if (text.Length < 2) return Task.FromResult(new GeoSearchResult([]));
        var query = $"search?q={Uri.EscapeDataString(text)}&format=jsonv2&addressdetails=1&limit=8&accept-language={Lang(language)}";
        return RequestAsync(query, isReverse: false, cancellationToken);
    }

    public Task<GeoSearchResult> ReverseOnlineAsync(double latitude, double longitude, string? language, CancellationToken cancellationToken = default)
    {
        if (!GeoMath.IsValid(latitude, longitude)) return Task.FromResult(new GeoSearchResult([], GeoSearchError.Failed));
        var lat = Math.Round(latitude, 5).ToString(CultureInfo.InvariantCulture);
        var lon = Math.Round(longitude, 5).ToString(CultureInfo.InvariantCulture);
        return RequestAsync($"reverse?lat={lat}&lon={lon}&format=jsonv2&addressdetails=1&zoom=16&accept-language={Lang(language)}", isReverse: true, cancellationToken);
    }

    private static string Lang(string? language) => language is "ar" or "de" ? language + ",en" : "en";

    private async Task<GeoSearchResult> RequestAsync(string relative, bool isReverse, CancellationToken ct)
    {
        if (!OnlineEnabled) return new GeoSearchResult([], GeoSearchError.Disabled);
        var cacheFile = Path.Combine(_cacheDir, Hash(relative) + ".json");
        if (TryReadCache(cacheFile) is { } cached) return new GeoSearchResult(Parse(cached, isReverse), FromCache: true);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastRequest + MinInterval - _time.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastRequest = _time.GetUtcNow();
            using var client = _http.CreateClient(HttpClientName);
            using var response = await client.GetAsync(Endpoint + relative, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return new GeoSearchResult([], GeoSearchError.RateLimited);
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Nominatim answered {Status}", (int)response.StatusCode);
                return new GeoSearchResult([], GeoSearchError.ServiceUnavailable);
            }
            if (!response.IsSuccessStatusCode) return new GeoSearchResult([], GeoSearchError.Failed);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var places = Parse(json, isReverse);
            WriteCache(cacheFile, json);
            return new GeoSearchResult(places);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new GeoSearchResult([], GeoSearchError.ServiceUnavailable); // timeout
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException || ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError)
        {
            _logger.LogInformation("Online place search unavailable: {Message}", ex.Message);
            return new GeoSearchResult([], GeoSearchError.Offline);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            _logger.LogWarning(ex, "Online place search failed");
            return new GeoSearchResult([], GeoSearchError.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static IReadOnlyList<GeoPlace> Parse(string json, bool isReverse)
    {
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList()
            : doc.RootElement.TryGetProperty("error", out _) ? [] : [doc.RootElement];
        var list = new List<GeoPlace>();
        foreach (var e in items)
        {
            if (!double.TryParse(Str(e, "lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(Str(e, "lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) || !GeoMath.IsValid(lat, lon)) continue;
            var display = Str(e, "display_name");
            var name = Str(e, "name");
            if (string.IsNullOrWhiteSpace(name)) name = display?.Split(',')[0].Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            string? cc = null, region = null, city = null;
            if (e.TryGetProperty("address", out var a))
            {
                cc = Str(a, "country_code")?.ToUpperInvariant();
                region = Str(a, "state") ?? Str(a, "region") ?? Str(a, "province");
                city = Str(a, "city") ?? Str(a, "town") ?? Str(a, "village") ?? Str(a, "hamlet") ?? Str(a, "municipality");
            }
            var kind = Str(e, "addresstype") ?? Str(e, "type");
            var type = TypeOf(Str(e, "category"), Str(e, "type"), kind);
            var sourceRef = Str(e, "osm_type") is { } ot && e.TryGetProperty("osm_id", out var oid) ? $"osm:{ot}/{oid}" : null;
            list.Add(new GeoPlace(name!, display, lat, lon, cc, region, city, type, GeoOrigin.Online, sourceRef));
        }
        return list;

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null : null;
    }

    /// <summary>Maps OpenStreetMap categories to Arcadia place types.</summary>
    public static PlaceType TypeOf(string? category, string? type, string? addressType)
    {
        var t = type ?? string.Empty;
        return (addressType ?? t) switch
        {
            "country" => PlaceType.Country,
            "city" => PlaceType.City,
            "town" or "municipality" => PlaceType.Town,
            "village" or "hamlet" or "suburb" when category is "place" or "boundary" => PlaceType.Village,
            _ => t switch
            {
                "peak" or "volcano" or "mountain_range" or "ridge" or "saddle" or "glacier" => PlaceType.Mountain,
                "beach" or "bay" => PlaceType.Beach,
                "lake" or "reservoir" or "water" or "lagoon" => PlaceType.Lake,
                "national_park" or "protected_area" or "nature_reserve" => PlaceType.NationalPark,
                "viewpoint" => PlaceType.Viewpoint,
                "attraction" or "monument" or "memorial" or "castle" or "ruins" or "archaeological_site" or "cathedral" or "church" or "fort" or "tower" or "palace" => PlaceType.Landmark,
                "restaurant" or "cafe" or "bar" or "pub" or "fast_food" or "biergarten" or "food_court" or "ice_cream" => PlaceType.Restaurant,
                "hotel" or "hostel" or "guest_house" or "motel" or "alpine_hut" or "apartment" or "chalet" or "camp_site" => PlaceType.Hotel,
                "museum" or "gallery" => PlaceType.Museum,
                "city" => PlaceType.City,
                "town" => PlaceType.Town,
                "village" or "hamlet" => PlaceType.Village,
                _ => PlaceType.Other,
            },
        };
    }

    private string? TryReadCache(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || _time.GetUtcNow().UtcDateTime - info.LastWriteTimeUtc > CacheLifetime) return null;
            return File.ReadAllText(file, Encoding.UTF8);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void WriteCache(string file, string json)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(file, json, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not cache a geocoding answer");
        }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32];
}
