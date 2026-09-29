using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Arcadia.Core.Security;
using Arcadia.Core.Settings;
using Arcadia.Core.Travel;
using Microsoft.Extensions.Logging;

namespace Arcadia.Infrastructure.Geo;

/// <summary>
/// Online detail tiles (OpenStreetMap standard tiles by default, https://operations.osmfoundation.org/policies/tiles/):
/// an identifying User-Agent, at most two parallel downloads, only the tiles currently in view, a disk cache that
/// honours Cache-Control/ETag (revalidated when expired), and cached tiles are reused while offline. No bulk or
/// background downloading. Only the tile coordinates are sent — never places, trips or other personal data.
/// </summary>
public sealed class MapTileService : IMapTileService
{
    public const string HttpClientName = "tiles";
    private const string OsmUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);
    private const long MaxCacheBytes = 300L * 1024 * 1024;

    private readonly IHttpClientFactory _http;
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger<MapTileService> _logger;
    private readonly string _root;
    private readonly SemaphoreSlim _parallel = new(2, 2);
    private DateTimeOffset _offlineUntil = DateTimeOffset.MinValue;
    private string? _template;
    private int _trimmed;

    public MapTileService(IHttpClientFactory http, ISettingsService settings, ISecretStore secrets, string cacheDirectory, TimeProvider time, ILogger<MapTileService> logger)
    {
        _http = http;
        _settings = settings;
        _secrets = secrets;
        _time = time;
        _logger = logger;
        _root = Path.Combine(cacheDirectory, "Tiles");
        Refresh();
        _settings.SettingsChanged += (_, _) => Refresh();
        _secrets.Changed += (_, _) => Refresh();
    }

    public TileSourceInfo? Source { get; private set; }
    public event EventHandler? SourceChanged;

    private void Refresh()
    {
        var t = _settings.Current.Travel;
        TileSourceInfo? source = null;
        string? template = null;
        if (t.OnlineMapTiles)
        {
            if (t.TileStyle == "custom" && IsValidTemplate(t.CustomTileUrl))
            {
                template = t.CustomTileUrl!;
                source = new TileSourceInfo("custom-" + StableHash(template), string.IsNullOrWhiteSpace(t.CustomTileAttribution) ? "© map data providers" : t.CustomTileAttribution!, 0, 19, false);
            }
            else
            {
                template = OsmUrl;
                source = new TileSourceInfo("osm", "© OpenStreetMap contributors", 0, 19, t.TileStyle != "osm");
            }
        }
        var changed = source != Source || template != _template;
        Source = source;
        _template = template;
        if (changed) SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>http(s) URL with {z}, {x} and {y} placeholders.</summary>
    public static bool IsValidTemplate(string? template) =>
        !string.IsNullOrWhiteSpace(template) &&
        Uri.TryCreate(template.Replace("{z}", "0").Replace("{x}", "0").Replace("{y}", "0").Replace("{key}", "k"), UriKind.Absolute, out var u) &&
        u.Scheme is "https" or "http" &&
        template.Contains("{z}", StringComparison.Ordinal) && template.Contains("{x}", StringComparison.Ordinal) && template.Contains("{y}", StringComparison.Ordinal);

    private string FilePath(TileSourceInfo s, int z, int x, int y) => Path.Combine(_root, s.Id, z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture) + ".png");

    public byte[]? GetCachedTile(int zoom, int x, int y)
    {
        if (Source is not { } s) return null;
        try { var p = FilePath(s, zoom, x, y); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<TileResult> GetTileAsync(int zoom, int x, int y, CancellationToken cancellationToken = default)
    {
        if (Source is not { } source || _template is not { } template) return new TileResult(null, TileStatus.Disabled);
        int n = 1 << zoom;
        if (zoom < source.MinZoom || zoom > source.MaxZoom || y < 0 || y >= n) return new TileResult(null, TileStatus.NotFound);
        x = ((x % n) + n) % n;
        if (Interlocked.Exchange(ref _trimmed, 1) == 0) _ = Task.Run(TrimCache, CancellationToken.None);

        var path = FilePath(source, zoom, x, y);
        var meta = path + ".meta";
        var cached = ReadCached(path, meta, out var expires, out var etag);
        if (cached != null && expires > _time.GetUtcNow()) return new TileResult(cached, TileStatus.Ok, FromCache: true);
        if (_time.GetUtcNow() < _offlineUntil) return new TileResult(cached, cached != null ? TileStatus.Ok : TileStatus.Offline, cached != null);

        await _parallel.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var url = template.Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture)).Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
                .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
            if (url.Contains("{key}", StringComparison.Ordinal)) url = url.Replace("{key}", Uri.EscapeDataString(_secrets.Get(SecretNames.MapTilesApiKey) ?? string.Empty));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cached != null && etag != null && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            using var client = _http.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var lifetime = response.Headers.CacheControl?.MaxAge is { } age && age > TimeSpan.Zero ? age : DefaultLifetime;
            if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
            {
                WriteMeta(meta, _time.GetUtcNow() + lifetime, etag);
                return new TileResult(cached, TileStatus.Ok, FromCache: true);
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return new TileResult(cached, cached != null ? TileStatus.Ok : TileStatus.RateLimited, cached != null);
            if (response.StatusCode == HttpStatusCode.NotFound) return new TileResult(null, TileStatus.NotFound);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Tile {Z}/{X}/{Y} answered {Status}", zoom, x, y, (int)response.StatusCode);
                return new TileResult(cached, cached != null ? TileStatus.Ok : TileStatus.Failed, cached != null);
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, CancellationToken.None).ConfigureAwait(false);
                WriteMeta(meta, _time.GetUtcNow() + lifetime, response.Headers.ETag?.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogDebug(ex, "Could not cache tile"); }
            return new TileResult(bytes, TileStatus.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (ex is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError } || ex.InnerException is SocketException)
            {
                _offlineUntil = _time.GetUtcNow().AddSeconds(30); // do not hammer a missing network
                _logger.LogInformation("Map tiles offline: {Message}", ex.Message);
                return new TileResult(cached, cached != null ? TileStatus.Ok : TileStatus.Offline, cached != null);
            }
            _logger.LogDebug(ex, "Tile download failed");
            return new TileResult(cached, cached != null ? TileStatus.Ok : TileStatus.Failed, cached != null);
        }
        finally
        {
            _parallel.Release();
        }
    }

    private static byte[]? ReadCached(string path, string meta, out DateTimeOffset expires, out string? etag)
    {
        expires = DateTimeOffset.MinValue;
        etag = null;
        try
        {
            if (!File.Exists(path)) return null;
            if (File.Exists(meta))
            {
                var lines = File.ReadAllLines(meta);
                if (lines.Length > 0 && long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)) expires = new DateTimeOffset(ticks, TimeSpan.Zero);
                if (lines.Length > 1 && lines[1].Length > 0) etag = lines[1];
            }
            return File.ReadAllBytes(path);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void WriteMeta(string meta, DateTimeOffset expires, string? etag)
    {
        try { File.WriteAllLines(meta, [expires.UtcTicks.ToString(CultureInfo.InvariantCulture), etag ?? string.Empty]); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Keeps the tile cache under 300 MB by removing the least recently written tiles.</summary>
    private void TrimCache()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            var files = new DirectoryInfo(_root).EnumerateFiles("*.png", SearchOption.AllDirectories).ToList();
            var total = files.Sum(f => f.Length);
            if (total <= MaxCacheBytes) return;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                total -= f.Length;
                f.Delete();
                var m = new FileInfo(f.FullName + ".meta");
                if (m.Exists) m.Delete();
                if (total <= MaxCacheBytes * 0.8) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Tile cache trim failed");
        }
    }

    private static string StableHash(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s) h = (h ^ ch) * 16777619;
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }
}
