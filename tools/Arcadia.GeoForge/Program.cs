// Arcadia GeoForge — builds the bundled, offline geographic data used by Arcadia Travel.
//
// Sources (download them into one folder and pass it as the first argument):
//   ne_110m_admin_0_countries.geojson, ne_50m_admin_0_countries.geojson
//       Natural Earth (public domain) — https://github.com/nvkelso/natural-earth-vector/tree/master/geojson
//   cities15000.zip, countryInfo.txt
//       GeoNames (CC BY 4.0) — https://download.geonames.org/export/dump/
//   flags/country-flags-main/svg/*.svg
//       https://github.com/hampusborgos/country-flags (flags are public domain)
//
// Output (second argument, normally src/Arcadia.Infrastructure/Geo/Data):
//   countries.dat.gz  country metadata + outlines at two levels of detail
//   cities.tsv.gz     ~33 000 places with population ≥ 15 000 (offline search)
//   flags.zip         48 px high PNG flags named <iso2>.png
//
// Usage: dotnet run --project tools/Arcadia.GeoForge -- <sourceFolder> <outputFolder>

using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: GeoForge <sourceFolder> <outputFolder>");
    return 1;
}
var src = args[0];
var dst = args[1];
Directory.CreateDirectory(dst);
var inv = CultureInfo.InvariantCulture;

// UN member states (193) + the two observer states (Holy See, Palestine) — the denominator for "countries visited".
var unStates = new HashSet<string>(("AF AL DZ AD AO AG AR AM AU AT AZ BS BH BD BB BY BE BZ BJ BT BO BA BW BR BN BG BF BI CV KH CM CA CF TD CL CN CO KM CG CD " +
    "CR CI HR CU CY CZ DK DJ DM DO EC EG SV GQ ER EE SZ ET FJ FI FR GA GM GE DE GH GR GD GT GN GW GY HT HN HU IS IN ID IR IQ IE IL IT JM " +
    "JP JO KZ KE KI KW KG LA LV LB LS LR LY LI LT LU MG MW MY MV ML MT MH MR MU MX FM MD MC MN ME MA MZ MM NA NR NP NL NZ NI NE NG KP MK " +
    "NO OM PK PW PA PG PY PE PH PL PT QA KR RO RU RW KN LC VC WS SM ST SA SN RS SC SL SG SK SI SB SO ZA SS ES LK SD SR SE CH SY TJ TZ TH " +
    "TL TG TO TT TN TR TM TV UG UA AE GB US UY UZ VU VE VN YE ZM ZW VA PS").Split(' ', StringSplitOptions.RemoveEmptyEntries));
if (unStates.Count != 195) throw new InvalidOperationException($"UN list has {unStates.Count} entries, expected 195");

// ---------- countries ----------
var coarse = LoadFeatures(Path.Combine(src, "ne_110m_admin_0_countries.geojson"));
var detail = LoadFeatures(Path.Combine(src, "ne_50m_admin_0_countries.geojson"));
var coarseByA3 = coarse.GroupBy(f => f.A3).ToDictionary(g => g.Key, g => g.First());

// GeoNames country info: names/continents for states Natural Earth draws too small to include
var info = File.ReadAllLines(Path.Combine(src, "countryInfo.txt"))
    .Where(l => l.Length > 0 && l[0] != '#')
    .Select(l => l.Split('\t'))
    .ToDictionary(c => c[0], c => (Name: c[4], Continent: c[8], Population: long.TryParse(c[7], out var p) ? p : 0));

// ---------- cities ----------
var cities = new List<string[]>();
using (var zip = ZipFile.OpenRead(Path.Combine(src, "cities15000.zip")))
using (var reader = new StreamReader(zip.GetEntry("cities15000.txt")!.Open(), Encoding.UTF8))
{
    string? line;
    while ((line = reader.ReadLine()) != null) cities.Add(line.Split('\t'));
}
var capitals = cities.Where(c => c[7] == "PPLC").GroupBy(c => c[8]).ToDictionary(g => g.Key, g => g.First());

var outCountries = new List<CountryOut>();
var usedCodes = new HashSet<string>();
// Countries first, dependencies last, so e.g. Australia keeps "AU" and its Indian Ocean territories get their own code.
foreach (var f in detail.OrderBy(f => f.Type is "Country" or "Sovereign country" ? 0 : 1))
{
    var code = f.Iso2 is { Length: 2 } && usedCodes.Add(f.Iso2) ? f.Iso2 : "X-" + f.A3; // territories without their own ISO code
    var lod0 = coarseByA3.TryGetValue(f.A3, out var c0) ? c0.Polygons : Simplify(f.Polygons, 0.35);
    outCountries.Add(new CountryOut(code, f.A3, f.NameEn, f.NameAr, f.NameDe, f.Continent, f.Subregion, f.Population, f.LabelLon, f.LabelLat,
        unStates.Contains(code), f.Type, lod0, f.Polygons));
}
foreach (var missing in unStates.Where(c => outCountries.All(o => o.Code != c)))
{
    // tiny states (e.g. Tuvalu) that have no outline at this scale: metadata + a label point at the capital
    var (name, continent, population) = info[missing];
    var capital = capitals.GetValueOrDefault(missing);
    float lon = capital != null ? float.Parse(capital[5], inv) : 0, lat = capital != null ? float.Parse(capital[4], inv) : 0;
    outCountries.Add(new CountryOut(missing, missing, name, name, name, ContinentName(continent), "", population, lon, lat, true, "Sovereign country", [], []));
    Console.WriteLine($"  added {missing} ({name}) without outline");
}
outCountries = outCountries.OrderBy(c => c.NameEn, StringComparer.Ordinal).ToList();

using (var file = File.Create(Path.Combine(dst, "countries.dat.gz")))
using (var gz = new GZipStream(file, CompressionLevel.SmallestSize))
using (var w = new BinaryWriter(gz, Encoding.UTF8))
{
    w.Write("ARCGEO1");
    w.Write(outCountries.Count);
    foreach (var c in outCountries)
    {
        w.Write(c.Code); w.Write(c.A3); w.Write(c.NameEn); w.Write(c.NameAr); w.Write(c.NameDe);
        w.Write(c.Continent); w.Write(c.Subregion); w.Write(c.Population);
        w.Write(c.LabelLon); w.Write(c.LabelLat); w.Write(c.IsUnState); w.Write(c.Type);
        WritePolygons(w, c.Coarse);
        WritePolygons(w, c.Detail);
    }
}
Console.WriteLine($"countries: {outCountries.Count} ({outCountries.Count(c => c.IsUnState)} UN states), " +
                  $"{outCountries.Sum(c => c.Detail.Sum(p => p.Sum(r => r.Length)))} detail points, {outCountries.Sum(c => c.Coarse.Sum(p => p.Sum(r => r.Length)))} coarse points");

// ---------- cities (tsv: name, ascii, arabic, country, lat, lon, population, feature code) ----------
using (var file = File.Create(Path.Combine(dst, "cities.tsv.gz")))
using (var gz = new GZipStream(file, CompressionLevel.SmallestSize))
using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
{
    foreach (var c in cities.OrderByDescending(c => long.Parse(c[14], inv)))
    {
        var arabic = c[3].Split(',').FirstOrDefault(a => a.Any(ch => ch is >= '؀' and <= 'ۿ')) ?? string.Empty;
        w.Write(string.Join('\t', Clean(c[1]), Clean(c[2]), Clean(arabic), c[8], Round(c[4]), Round(c[5]), c[14], c[7]));
        w.Write('\n');
    }
}
Console.WriteLine($"cities: {cities.Count}");

// ---------- flags ----------
var flagDir = Path.Combine(src, "flags", "country-flags-main", "svg");
int flags = 0;
using (var file = File.Create(Path.Combine(dst, "flags.zip")))
using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
{
    foreach (var code in outCountries.Select(c => c.Code).Where(c => c.Length == 2))
    {
        var svg = Path.Combine(flagDir, code.ToLowerInvariant() + ".svg");
        if (!File.Exists(svg)) { Console.WriteLine($"  no flag for {code}"); continue; }
        var doc = Svg.SvgDocument.Open(svg);
        const int height = 48;
        var width = (int)Math.Round(height * doc.Width.Value / doc.Height.Value);
        using var bmp = doc.Draw(width, height);
        var entry = zip.CreateEntry(code + ".png", CompressionLevel.NoCompression);
        using var s = entry.Open();
        bmp.Save(s, ImageFormat.Png);
        flags++;
    }
}
Console.WriteLine($"flags: {flags}");
foreach (var f in Directory.GetFiles(dst)) Console.WriteLine($"  {Path.GetFileName(f)}: {new FileInfo(f).Length / 1024} KB");
return 0;

static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Trim();
static string Round(string s) => Math.Round(double.Parse(s, CultureInfo.InvariantCulture), 4).ToString(CultureInfo.InvariantCulture);

static string ContinentName(string code) => code switch
{
    "AF" => "Africa", "AS" => "Asia", "EU" => "Europe", "NA" => "North America", "OC" => "Oceania", "SA" => "South America", "AN" => "Antarctica", _ => "",
};

static void WritePolygons(BinaryWriter w, List<float[][]> polygons)
{
    w.Write(polygons.Count);
    foreach (var polygon in polygons)
    {
        w.Write(polygon.Length);
        foreach (var ring in polygon)
        {
            w.Write(ring.Length / 2);
            foreach (var v in ring) w.Write(v);
        }
    }
}

// Drops points closer than `tolerance` degrees to the previous kept point (only used when no coarse outline exists).
static List<float[][]> Simplify(List<float[][]> polygons, double tolerance) =>
    polygons.Select(p => p.Select(r =>
    {
        var kept = new List<float>();
        for (int i = 0; i < r.Length; i += 2)
        {
            if (kept.Count >= 2 && i < r.Length - 2 && Math.Abs(r[i] - kept[^2]) < tolerance && Math.Abs(r[i + 1] - kept[^1]) < tolerance) continue;
            kept.Add(r[i]); kept.Add(r[i + 1]);
        }
        return kept.ToArray();
    }).Where(r => r.Length >= 8).ToArray()).Where(p => p.Length > 0).ToList();

static List<Feature> LoadFeatures(string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var list = new List<Feature>();
    foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
    {
        var p = f.GetProperty("properties");
        string S(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : string.Empty;
        double D(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var g = f.GetProperty("geometry");
        var polygons = new List<float[][]>();
        var coords = g.GetProperty("coordinates");
        if (g.GetProperty("type").GetString() == "Polygon") polygons.Add(Rings(coords));
        else foreach (var poly in coords.EnumerateArray()) polygons.Add(Rings(poly));
        var iso = S("ISO_A2_EH");
        if (iso is "-99" or "") iso = S("ISO_A2");
        list.Add(new Feature(iso, S("ADM0_A3"), S("NAME_EN") is { Length: > 0 } en ? en : S("NAME"), S("NAME_AR"), S("NAME_DE"),
            S("CONTINENT"), S("SUBREGION"), (long)D("POP_EST"), (float)D("LABEL_X"), (float)D("LABEL_Y"), S("TYPE"), polygons));
    }
    return list;

    static float[][] Rings(JsonElement polygon) =>
        polygon.EnumerateArray().Select(ring => ring.EnumerateArray().SelectMany(pt => new[] { (float)pt[0].GetDouble(), (float)pt[1].GetDouble() }).ToArray()).ToArray();
}

sealed record Feature(string Iso2, string A3, string NameEn, string NameAr, string NameDe, string Continent, string Subregion, long Population,
    float LabelLon, float LabelLat, string Type, List<float[][]> Polygons);

sealed record CountryOut(string Code, string A3, string NameEn, string NameAr, string NameDe, string Continent, string Subregion, long Population,
    float LabelLon, float LabelLat, bool IsUnState, string Type, List<float[][]> Coarse, List<float[][]> Detail);
