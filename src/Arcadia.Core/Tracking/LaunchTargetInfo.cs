using System.Text.RegularExpressions;

namespace Arcadia.Core.Tracking;

/// <summary>Extracts store identifiers from a launch target.</summary>
public static partial class LaunchTargetInfo
{
    /// <summary>steam://rungameid/292030 or steam://run/292030 → 292030.</summary>
    public static int? SteamAppId(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var m = SteamPattern().Match(target);
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>com.epicgames.launcher://apps/{namespace}%3A{catalog}%3A{AppName}?action=launch → AppName (or the whole id).</summary>
    public static string? EpicAppName(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var m = EpicPattern().Match(target);
        if (!m.Success) return null;
        var id = Uri.UnescapeDataString(m.Groups[1].Value);
        var parts = id.Split(':');
        return parts[^1];
    }

    /// <summary>The folder of an .exe/.lnk/.url launch target (for .exe only; shortcuts need resolving).</summary>
    public static string? ExecutableDirectory(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        target = target.Trim().Trim('"');
        return Path.IsPathFullyQualified(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(target)
            : null;
    }

    [GeneratedRegex(@"^steam://(?:rungameid|run|launch)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPattern();

    [GeneratedRegex(@"^com\.epicgames\.launcher://apps/([^?/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EpicPattern();
}
