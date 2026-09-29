using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Arcadia.Core.Travel;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>
/// Decoded map tiles for the world map: an in-memory LRU of frozen bitmaps on top of the disk cache in
/// <see cref="IMapTileService"/>. OpenStreetMap tiles are darkened here, locally, to match Arcadia (inverted
/// lightness with the hues kept, then tinted towards graphite blue) — nothing about the image is sent anywhere.
/// </summary>
public sealed class MapTileImages
{
    private const int Capacity = 600;
    private readonly IMapTileService _tiles;
    private readonly ILogger<MapTileImages> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<(string, int, int, int), LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _lru = new();
    private readonly HashSet<(string, int, int, int)> _loading = [];
    private readonly HashSet<(string, int, int, int)> _failed = [];

    private sealed record Entry((string, int, int, int) Key, BitmapSource Image);

    public MapTileImages(IMapTileService tiles, ILogger<MapTileImages> logger)
    {
        _tiles = tiles;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _tiles.SourceChanged += (_, _) => _dispatcher.BeginInvoke(() =>
        {
            _map.Clear();
            _lru.Clear();
            _failed.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public TileSourceInfo? Source => _tiles.Source;

    /// <summary>Raised on the UI thread when a tile arrived (or the source changed).</summary>
    public event EventHandler? Changed;

    /// <summary>Last problem while loading tiles (offline, rate limited…); None when tiles arrive fine.</summary>
    public TileStatus LastStatus { get; private set; } = TileStatus.Ok;

    /// <summary>The decoded tile if it is in memory; otherwise null and (when <paramref name="load"/>) a background load starts.</summary>
    public BitmapSource? Get(int z, int x, int y, bool load)
    {
        if (_tiles.Source is not { } source) return null;
        var key = (source.Id, z, x, y);
        if (_map.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }
        if (load && !_failed.Contains(key) && _loading.Add(key)) _ = LoadAsync(key, source.Darken);
        return null;
    }

    private async Task LoadAsync((string Id, int Z, int X, int Y) key, bool darken)
    {
        try
        {
            var result = await _tiles.GetTileAsync(key.Z, key.X, key.Y).ConfigureAwait(false);
            if (result.Status != TileStatus.Ok || result.Data == null)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    _loading.Remove(key);
                    if (result.Status is TileStatus.NotFound or TileStatus.Failed) _failed.Add(key);
                    if (LastStatus != result.Status) { LastStatus = result.Status; Changed?.Invoke(this, EventArgs.Empty); }
                });
                return;
            }
            var image = Decode(result.Data, darken);
            await _dispatcher.InvokeAsync(() =>
            {
                _loading.Remove(key);
                if (image == null) { _failed.Add(key); return; }
                if (!result.FromCache) LastStatus = TileStatus.Ok; // a fresh download proves we are online again
                var node = _lru.AddFirst(new Entry(key, image));
                _map[key] = node;
                while (_lru.Count > Capacity)
                {
                    _map.Remove(_lru.Last!.Value.Key);
                    _lru.RemoveLast();
                }
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tile {Z}/{X}/{Y} could not be shown", key.Z, key.X, key.Y);
            _ = _dispatcher.InvokeAsync(() => { _loading.Remove(key); _failed.Add(key); });
        }
    }

    private static BitmapSource? Decode(byte[] data, bool darken)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            if (!darken) return bmp;
            var src = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
            var px = new byte[h * stride];
            src.CopyPixels(px, stride, 0);
            Darken(px);
            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// "Night chart" colouring: lightness is inverted while the hue is kept (so water stays blue and parks green),
    /// saturation is reduced and everything is pulled towards Arcadia's graphite blue.
    /// </summary>
    internal static void Darken(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2, s = 0, hue = 0;
            if (max > min)
            {
                var d = max - min;
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                hue = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                hue /= 6;
            }
            // invert lightness, compress into a dark range, keep a little colour
            l = 0.06 + Math.Pow(1 - l, 1.15) * 0.62;
            s *= 0.45;
            var (nr, ng, nb) = FromHsl(hue, s, l);
            // cool graphite tint
            nr = nr * 0.86 + 0.012;
            ng = ng * 0.92 + 0.026;
            nb = nb * 1.00 + 0.05;
            bgra[i] = (byte)Math.Clamp(nb * 255, 0, 255);
            bgra[i + 1] = (byte)Math.Clamp(ng * 255, 0, 255);
            bgra[i + 2] = (byte)Math.Clamp(nr * 255, 0, 255);
        }
    }

    private static (double, double, double) FromHsl(double h, double s, double l)
    {
        if (s <= 0) return (l, l, l);
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        return (Hue(p, q, h + 1.0 / 3), Hue(p, q, h), Hue(p, q, h - 1.0 / 3));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }
}

/// <summary>Frozen flag images from the bundled set, decoded once.</summary>
public sealed class FlagImages(ICountryCatalog countries)
{
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? Get(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        if (_cache.TryGetValue(code, out var cached)) return cached;
        ImageSource? image = null;
        if (countries.Flag(code) is { } png)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(png);
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException) { image = null; }
        }
        _cache[code] = image;
        return image;
    }
}
