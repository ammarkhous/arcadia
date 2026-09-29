using System.Globalization;
using System.Text.RegularExpressions;

namespace Arcadia.Core.Travel;

/// <summary>Coordinate validation, parsing and formatting, Web Mercator projection and distances (WGS84 degrees).</summary>
public static partial class GeoMath
{
    /// <summary>Web Mercator cannot show the poles; this is the usual cut-off latitude.</summary>
    public const double MaxMercatorLatitude = 85.05112878;
    private const double EarthRadiusKm = 6371.0088;

    public static bool IsValidLatitude(double lat) => double.IsFinite(lat) && lat is >= -90 and <= 90;
    public static bool IsValidLongitude(double lon) => double.IsFinite(lon) && lon is >= -180 and <= 180;
    public static bool IsValid(double lat, double lon) => IsValidLatitude(lat) && IsValidLongitude(lon);

    /// <summary>Wraps any longitude into [-180, 180).</summary>
    public static double NormalizeLongitude(double lon)
    {
        if (!double.IsFinite(lon)) return lon;
        var x = (lon + 180) % 360;
        if (x < 0) x += 360;
        return x - 180;
    }

    // ── Web Mercator in "world units": the whole world is a square of side 1 (x right, y down). ──

    public static (double X, double Y) ToWorld(double lat, double lon)
    {
        lat = Math.Clamp(lat, -MaxMercatorLatitude, MaxMercatorLatitude);
        var x = (lon + 180) / 360;
        var s = Math.Sin(lat * Math.PI / 180);
        var y = 0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI);
        return (x, Math.Clamp(y, 0, 1));
    }

    public static (double Lat, double Lon) FromWorld(double x, double y)
    {
        var lon = x * 360 - 180;
        var n = Math.PI - 2 * Math.PI * y;
        var lat = 180 / Math.PI * Math.Atan(Math.Sinh(n));
        return (lat, lon);
    }

    /// <summary>Great-circle distance in kilometres.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Even-odd point-in-ring test on a flat ring of (lon, lat) pairs.</summary>
    public static bool RingContains(ReadOnlySpan<float> ring, double lon, double lat)
    {
        var inside = false;
        int n = ring.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[2 * i], yi = ring[2 * i + 1], xj = ring[2 * j], yj = ring[2 * j + 1];
            if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Reads coordinates as people paste them: "46.0212, 7.7493", "46.0212 7.7493", "46,0212; 7,7493",
    /// "46°01'16\"N 7°44'57\"E", "N 46.02 E 7.74", "46.02N, 7.74W". Returns false for anything out of range.
    /// </summary>
    public static bool TryParse(string? text, out double latitude, out double longitude)
    {
        latitude = longitude = double.NaN;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().Replace('′', '\'').Replace('’', '\'').Replace('″', '"').Replace("''", "\"", StringComparison.Ordinal);

        if (s.Any(c => c is 'N' or 'S' or 'E' or 'W' or 'n' or 's' or 'e' or 'w'))
        {
            // "N 46.02 E 7.74" (letter first) or "46.02N 7.74E" (letter last) — never mixed, so a letter is not read twice
            var dms = (PrefixStyle().IsMatch(s) ? DmsPrefixPattern() : DmsPattern()).Matches(s);
            if (dms.Count != 2) return false;
            if (!TryDms(dms[0], out var a, out var ha) || !TryDms(dms[1], out var b, out var hb)) return false;
            // hemisphere letters decide which value is which (latitude may be written second)
            if (ha is 'E' or 'W' || hb is 'N' or 'S') (a, b, ha, hb) = (b, a, hb, ha);
            if (ha is 'S') a = -a;
            if (hb is 'W') b = -b;
            latitude = a; longitude = b;
            return IsValid(latitude, longitude);
        }

        // decimal pair: "lat, lon" / "lat lon" / "lat; lon" (comma decimals only with a semicolon or space separator)
        string[] parts;
        if (s.Contains(';')) parts = s.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        else if (s.Count(c => c == ',') == 1) parts = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        else parts = s.Split([' ', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        if (!TryDecimal(parts[0], out var lat) || !TryDecimal(parts[1], out var lon)) return false;
        latitude = lat; longitude = lon;
        return IsValid(latitude, longitude);
    }

    private static bool TryDecimal(string part, out double value)
    {
        var p = part.Trim().TrimEnd('°').Replace(',', '.');
        return double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static bool TryDms(Match m, out double value, out char hemisphere)
    {
        hemisphere = char.ToUpperInvariant((m.Groups["h1"].Success ? m.Groups["h1"].Value : m.Groups["h2"].Value).FirstOrDefault());
        var deg = double.Parse(m.Groups["d"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var min = m.Groups["m"].Success ? double.Parse(m.Groups["m"].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : 0;
        var sec = m.Groups["s"].Success ? double.Parse(m.Groups["s"].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : 0;
        value = deg + min / 60 + sec / 3600;
        return hemisphere is 'N' or 'S' or 'E' or 'W' && min < 60 && sec < 60;
    }

    [GeneratedRegex(@"^\s*[NSEWnsew]\s*\d")]
    private static partial Regex PrefixStyle();

    [GeneratedRegex(@"(?<h1>[NSEWnsew])\s*(?<d>\d{1,3}(?:[.,]\d+)?)\s*°?\s*(?:(?<m>\d{1,2}(?:[.,]\d+)?)\s*'\s*)?(?:(?<s>\d{1,2}(?:[.,]\d+)?)\s*""\s*)?(?=[\s,;]|$)")]
    private static partial Regex DmsPrefixPattern();

    [GeneratedRegex(@"(?<h1>[NSEWnsew])?\s*(?<d>\d{1,3}(?:[.,]\d+)?)\s*°?\s*(?:(?<m>\d{1,2}(?:[.,]\d+)?)\s*'\s*)?(?:(?<s>\d{1,2}(?:[.,]\d+)?)\s*""\s*)?(?<h2>[NSEWnsew])?(?=[\s,;]|$)")]
    private static partial Regex DmsPattern();

    /// <summary>"46.0212° N, 7.7493° E" — always with invariant digits, so it reads the same in every language.</summary>
    public static string Format(double lat, double lon, int decimals = 4)
    {
        var f = "F" + decimals.ToString(CultureInfo.InvariantCulture);
        return $"{Math.Abs(lat).ToString(f, CultureInfo.InvariantCulture)}° {(lat >= 0 ? 'N' : 'S')}, " +
               $"{Math.Abs(lon).ToString(f, CultureInfo.InvariantCulture)}° {(lon >= 0 ? 'E' : 'W')}";
    }
}
