using System.Globalization;
using System.IO.Compression;
using System.Text;
using Arcadia.Core.Text;
using Arcadia.Core.Travel;

namespace Arcadia.Infrastructure.Geo;

/// <summary>
/// Countries from the bundled Natural Earth data (public domain), loaded once on first use (~40 ms).
/// Flags come from the bundled public-domain flag set.
/// </summary>
public sealed class CountryCatalog : ICountryCatalog
{
    private readonly Lazy<Loaded> _data;
    private readonly Lazy<Dictionary<string, byte[]>> _flags;

    public CountryCatalog()
    {
        _data = new Lazy<Loaded>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
        _flags = new Lazy<Dictionary<string, byte[]>>(LoadFlags, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<CountryInfo> All => _data.Value.Countries;
    public int UnStateCount => _data.Value.UnCount;

    public CountryInfo? Find(string? code) =>
        code != null && _data.Value.ByCode.TryGetValue(code, out var c) ? c : null;

    public CountryInfo? At(double latitude, double longitude)
    {
        if (!GeoMath.IsValid(latitude, longitude)) return null;
        foreach (var c in _data.Value.Countries)
            if (c.Contains(latitude, longitude)) return c;
        return null;
    }

    public CountryInfo? Near(double latitude, double longitude)
    {
        if (At(latitude, longitude) is { } exact) return exact;
        if (!GeoMath.IsValid(latitude, longitude)) return null;
        foreach (var r in new[] { 0.04, 0.09, 0.18, 0.32 })
            for (int i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                if (At(latitude + r * Math.Sin(a), longitude + r * Math.Cos(a) / Math.Max(0.2, Math.Cos(latitude * Math.PI / 180))) is { } c) return c;
            }
        return null;
    }

    public IReadOnlyList<CountryInfo> Search(string text, int limit = 10)
    {
        var q = TextFold.Fold(text);
        if (q.Length == 0) return [];
        return _data.Value.Folded
            .Select(f => (f.Country, Score: Score(q, f)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Country.NameEn, StringComparer.Ordinal)
            .Take(limit).Select(x => x.Country).ToList();

        static int Score(string q, (CountryInfo Country, string[] Names) f)
        {
            if (string.Equals(f.Country.Code, q, StringComparison.OrdinalIgnoreCase) || string.Equals(f.Country.A3, q, StringComparison.OrdinalIgnoreCase)) return 90;
            var best = 0;
            foreach (var n in f.Names)
            {
                if (n == q) return 100;
                if (n.StartsWith(q, StringComparison.Ordinal)) best = Math.Max(best, 70);
                else if (n.Contains(" " + q, StringComparison.Ordinal)) best = Math.Max(best, 50);
                else if (q.Length >= 3 && n.Contains(q, StringComparison.Ordinal)) best = Math.Max(best, 30);
            }
            return best;
        }
    }

    public byte[]? Flag(string code) => _flags.Value.TryGetValue(code, out var png) ? png : null;

    private sealed record Loaded(IReadOnlyList<CountryInfo> Countries, Dictionary<string, CountryInfo> ByCode, int UnCount, List<(CountryInfo Country, string[] Names)> Folded);

    internal static Stream Open(string name) =>
        typeof(CountryCatalog).Assembly.GetManifestResourceStream("Arcadia.Geo." + name)
        ?? throw new InvalidOperationException($"Bundled geo resource {name} is missing.");

    private static Loaded Load()
    {
        using var gz = new GZipStream(Open("countries.dat.gz"), CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        if (r.ReadString() != "ARCGEO1") throw new InvalidDataException("Unknown country data format.");
        var count = r.ReadInt32();
        var list = new List<CountryInfo>(count);
        for (int i = 0; i < count; i++)
        {
            string code = r.ReadString(), a3 = r.ReadString(), en = r.ReadString(), ar = r.ReadString(), de = r.ReadString();
            string continent = r.ReadString(), subregion = r.ReadString();
            long population = r.ReadInt64();
            double labelLon = r.ReadSingle(), labelLat = r.ReadSingle();
            bool un = r.ReadBoolean();
            string type = r.ReadString();
            var coarse = ReadPolygons(r);
            var detail = ReadPolygons(r);
            list.Add(new CountryInfo
            {
                Code = code, A3 = a3, NameEn = en, NameAr = ar.Length > 0 ? ar : en, NameDe = de.Length > 0 ? de : en,
                Continent = continent, Subregion = subregion, Population = population,
                LabelLatitude = labelLat, LabelLongitude = labelLon, IsUnState = un, Type = type,
                Coarse = coarse, Detail = detail, Bounds = BoundsOf(detail, labelLat, labelLon),
            });
        }
        var byCode = list.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var folded = list.Select(c => (c, new[] { TextFold.Fold(c.NameEn), TextFold.Fold(c.NameAr), TextFold.Fold(c.NameDe) }.Distinct().ToArray())).ToList();
        return new Loaded(list, byCode, list.Count(c => c.IsUnState), folded);
    }

    private static List<float[][]> ReadPolygons(BinaryReader r)
    {
        var count = r.ReadInt32();
        var polygons = new List<float[][]>(count);
        for (int p = 0; p < count; p++)
        {
            var rings = new float[r.ReadInt32()][];
            for (int k = 0; k < rings.Length; k++)
            {
                var ring = new float[r.ReadInt32() * 2];
                for (int v = 0; v < ring.Length; v++) ring[v] = r.ReadSingle();
                rings[k] = ring;
            }
            polygons.Add(rings);
        }
        return polygons;
    }

    private static (double, double, double, double) BoundsOf(List<float[][]> polygons, double lat, double lon)
    {
        double minLon = double.MaxValue, minLat = double.MaxValue, maxLon = double.MinValue, maxLat = double.MinValue;
        foreach (var p in polygons)
        {
            if (p.Length == 0) continue;
            var ring = p[0];
            for (int i = 0; i < ring.Length; i += 2)
            {
                minLon = Math.Min(minLon, ring[i]); maxLon = Math.Max(maxLon, ring[i]);
                minLat = Math.Min(minLat, ring[i + 1]); maxLat = Math.Max(maxLat, ring[i + 1]);
            }
        }
        return minLon == double.MaxValue ? (lon - 0.5, lat - 0.5, lon + 0.5, lat + 0.5) : (minLon, minLat, maxLon, maxLat);
    }

    private static Dictionary<string, byte[]> LoadFlags()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var zip = new ZipArchive(Open("flags.zip"), ZipArchiveMode.Read);
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            map[Path.GetFileNameWithoutExtension(e.Name)] = ms.ToArray();
        }
        return map;
    }
}

/// <summary>
/// ~34 000 places with at least 15 000 inhabitants from GeoNames (CC BY 4.0), for search and "what is here" without
/// internet. Loaded on first use; a one-degree grid makes nearest-city lookups instant.
/// </summary>
public sealed class CityIndex
{
    public sealed record City(string Name, string Ascii, string Arabic, string CountryCode, double Latitude, double Longitude, long Population, string FeatureCode)
    {
        internal string FoldedName = string.Empty, FoldedAscii = string.Empty, FoldedArabic = string.Empty;
        public bool IsCapital => FeatureCode == "PPLC";
    }

    private readonly Lazy<(List<City> All, Dictionary<(int, int), List<City>> Grid)> _data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public IReadOnlyList<City> All => _data.Value.All;

    public IReadOnlyList<City> Search(string text, int limit)
    {
        var q = TextFold.Fold(text);
        if (q.Length < 2) return [];
        var hits = new List<(City City, int Score)>();
        foreach (var c in _data.Value.All)
        {
            int s = Score(q, c.FoldedName);
            s = Math.Max(s, Score(q, c.FoldedAscii));
            if (c.FoldedArabic.Length > 0) s = Math.Max(s, Score(q, c.FoldedArabic));
            if (s > 0) hits.Add((c, s));
        }
        // population breaks ties (the list is already sorted by population)
        return hits.OrderByDescending(h => h.Score).Take(limit).Select(h => h.City).ToList();

        static int Score(string q, string name)
        {
            if (name.Length == 0) return 0;
            if (name == q) return 100;
            if (name.StartsWith(q, StringComparison.Ordinal)) return 70;
            if (q.Length >= 3 && name.Contains(" " + q, StringComparison.Ordinal)) return 40;
            return 0;
        }
    }

    /// <summary>The nearest place within <paramref name="maxKm"/>, or null.</summary>
    public City? Nearest(double lat, double lon, double maxKm = 60)
    {
        City? best = null;
        var bestKm = maxKm;
        int cy = (int)Math.Floor(lat), cx = (int)Math.Floor(lon);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!_data.Value.Grid.TryGetValue((cy + dy, cx + dx), out var cell)) continue;
                foreach (var c in cell)
                {
                    var km = GeoMath.DistanceKm(lat, lon, c.Latitude, c.Longitude);
                    // prefer bigger places when distances are similar ("Zermatt" over a hamlet next door)
                    var weighted = km / (1 + Math.Log10(Math.Max(10, c.Population)) / 10);
                    if (weighted < bestKm) { bestKm = weighted; best = c; }
                }
            }
        return best;
    }

    private static (List<City>, Dictionary<(int, int), List<City>>) Load()
    {
        using var gz = new GZipStream(CountryCatalog.Open("cities.tsv.gz"), CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var list = new List<City>(35_000);
        var grid = new Dictionary<(int, int), List<City>>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var f = line.Split('\t');
            if (f.Length < 8) continue;
            var c = new City(f[0], f[1], f[2], f[3], double.Parse(f[4], CultureInfo.InvariantCulture), double.Parse(f[5], CultureInfo.InvariantCulture),
                long.TryParse(f[6], out var p) ? p : 0, f[7])
            {
                FoldedName = TextFold.Fold(f[0]),
                FoldedAscii = TextFold.Fold(f[1]),
                FoldedArabic = TextFold.Fold(f[2]),
            };
            list.Add(c);
            var key = ((int)Math.Floor(c.Latitude), (int)Math.Floor(c.Longitude));
            if (!grid.TryGetValue(key, out var cell)) grid[key] = cell = [];
            cell.Add(c);
        }
        return (list, grid);
    }
}
