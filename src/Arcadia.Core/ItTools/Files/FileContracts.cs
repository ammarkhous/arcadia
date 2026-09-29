using System.Globalization;
using System.Text;

namespace Arcadia.Core.ItTools.Files;

// ─────────────────────────── storage ───────────────────────────

public sealed record DriveEntry(string Name, string? Label, string? FileSystem, string DriveType, long TotalBytes, long FreeBytes, bool IsReady, bool IsSystemDrive)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedFraction => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes;
}

public interface IStorageService
{
    IReadOnlyList<DriveEntry> GetDrives();
}

// ─────────────────────────── large files ───────────────────────────

public sealed record LargeFile(string Name, string FullPath, long SizeBytes, DateTime ModifiedUtc)
{
    public string Folder => Path.GetDirectoryName(FullPath) ?? string.Empty;
}

public sealed record LargeFileScanProgress(long FilesScanned, int Found, string? CurrentFolder);

public interface ILargeFileFinder
{
    /// <summary>
    /// Scans only the folder the user chose (recursively, skipping links and inaccessible folders) and returns
    /// files at least <paramref name="minimumBytes"/> large, largest first. Never modifies anything.
    /// </summary>
    Task<IReadOnlyList<LargeFile>> FindAsync(string root, long minimumBytes, IProgress<LargeFileScanProgress>? progress, CancellationToken cancellationToken = default);
}

// ─────────────────────────── hashing ───────────────────────────

public enum HashAlgorithmKind { Sha256, Sha512, Md5 }

public sealed record FileHashResult(string FullPath, long SizeBytes, HashAlgorithmKind Algorithm, string Hash, TimeSpan Elapsed);

public interface IHashService
{
    /// <summary>Streams the file (large files are fine); progress is 0..1.</summary>
    Task<FileHashResult> ComputeAsync(string path, HashAlgorithmKind algorithm, IProgress<double>? progress, CancellationToken cancellationToken = default);
}

public static class HashText
{
    /// <summary>MD5 is kept for matching published checksums only; it does not protect against tampering.</summary>
    public static bool IsSecure(HashAlgorithmKind kind) => kind != HashAlgorithmKind.Md5;

    public static int HexLength(HashAlgorithmKind kind) => kind switch
    {
        HashAlgorithmKind.Sha256 => 64,
        HashAlgorithmKind.Sha512 => 128,
        _ => 32,
    };

    /// <summary>Strips whitespace, separators (":" "-") and an optional "sha256:" style prefix; lower-cases.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var t = text.Trim();
        var colon = t.IndexOf(':', StringComparison.Ordinal);
        if (colon is > 0 and < 8 && t[..colon].All(char.IsLetterOrDigit) && !t[..colon].All(Uri.IsHexDigit)) t = t[(colon + 1)..];
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
            if (!char.IsWhiteSpace(c) && c is not (':' or '-')) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static bool IsHex(string normalized) => normalized.Length > 0 && normalized.All(Uri.IsHexDigit);

    public static HashComparison Compare(string computed, string? expected)
    {
        var e = Normalize(expected);
        if (e.Length == 0) return HashComparison.Empty;
        if (!IsHex(e)) return HashComparison.NotAHash;
        var c = Normalize(computed);
        if (e.Length != c.Length) return HashComparison.DifferentLength;
        // Constant-time is not required here, but it costs nothing.
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c), Encoding.ASCII.GetBytes(e))
            ? HashComparison.Match
            : HashComparison.Mismatch;
    }
}

public enum HashComparison { Empty, Match, Mismatch, DifferentLength, NotAHash }

// ─────────────────────────── formatting ───────────────────────────

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Binary units (1 KB = 1024 B), as Windows Explorer shows them.</summary>
    public static string Format(double bytes, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0) return "—";
        int unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        var format = unit == 0 ? "0" : bytes >= 100 ? "0" : bytes >= 10 ? "0.#" : "0.##";
        return bytes.ToString(format, culture) + " " + Units[unit];
    }

    public static string FormatRate(double bytesPerSecond, IFormatProvider? culture = null) => Format(bytesPerSecond, culture) + "/s";

    /// <summary>Network link speeds use decimal units (1 Gbps = 1000 Mbps).</summary>
    public static string FormatBits(long bitsPerSecond, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return bitsPerSecond switch
        {
            >= 1_000_000_000 => (bitsPerSecond / 1e9).ToString("0.#", culture) + " Gbps",
            >= 1_000_000 => (bitsPerSecond / 1e6).ToString("0.#", culture) + " Mbps",
            >= 1_000 => (bitsPerSecond / 1e3).ToString("0.#", culture) + " Kbps",
            _ => bitsPerSecond.ToString(culture) + " bps",
        };
    }

    /// <summary>"3 d 04:12:09" / "04:12:09".</summary>
    public static string FormatUptime(TimeSpan span, string dayUnit = "d") =>
        span.TotalDays >= 1
            ? $"{(int)span.TotalDays} {dayUnit} {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
}
