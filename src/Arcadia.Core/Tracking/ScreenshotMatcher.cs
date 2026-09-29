using System.Text;
using System.Text.RegularExpressions;

namespace Arcadia.Core.Tracking;

public sealed record CaptureGame(int Id, string Title, int? SteamAppId);

/// <summary>
/// Works out which game a newly captured screenshot belongs to:
/// 1. the game being played right now,
/// 2. Steam's folder layout (…\760\remote\{appId}\screenshots\…),
/// 3. the title in Xbox Game Bar / NVIDIA file names ("The Witcher 3 2026-09-27 21-30-11.png") or folder names.
/// </summary>
public static partial class ScreenshotMatcher
{
    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "captures", "screenshots", "screenshot", "pictures", "videos", "images", "desktop", "downloads", "thumbnails", "nvidia", "geforce",
    };

    public static int? Match(string filePath, IReadOnlyList<CaptureGame> games, int? playingGameId)
    {
        if (playingGameId is { } playing && games.Any(g => g.Id == playing)) return playing;

        var steam = SteamPathPattern().Match(filePath);
        if (steam.Success && int.TryParse(steam.Groups[1].Value, out var appId))
        {
            var byApp = games.FirstOrDefault(g => g.SteamAppId == appId);
            if (byApp != null) return byApp.Id;
        }

        foreach (var candidate in NameCandidates(filePath))
        {
            var hit = BestTitleMatch(candidate, games);
            if (hit != null) return hit.Id;
        }
        return null;
    }

    private static IEnumerable<string> NameCandidates(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var cut = NameEndPattern().Match(name);
        yield return cut.Success ? name[..cut.Index] : name;

        var folder = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);
        if (!string.IsNullOrEmpty(folder) && !GenericFolders.Contains(folder)) yield return folder;
    }

    private static CaptureGame? BestTitleMatch(string candidate, IReadOnlyList<CaptureGame> games)
    {
        var c = Normalize(candidate);
        if (c.Length < 4) return null;
        CaptureGame? best = null;
        int bestScore = 0;
        foreach (var g in games)
        {
            var t = Normalize(g.Title);
            if (t.Length < 3) continue;
            // "thewitcher3" vs "thewitcher3wildhunt": either may be the shorter form.
            int score = t.StartsWith(c, StringComparison.Ordinal) ? c.Length
                      : c.StartsWith(t, StringComparison.Ordinal) ? t.Length
                      : 0;
            if (score > bestScore) { best = g; bestScore = score; }
        }
        return bestScore >= 4 ? best : null;
    }

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormKD))
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    [GeneratedRegex(@"\\760\\remote\\(\d+)\\screenshots\\", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathPattern();

    // Where the game name ends in capture file names: a date, "Screenshot", or an underscore suffix.
    [GeneratedRegex(@"(\s+(19|20)\d\d[-.]|\s+Screenshot\b|_\d)", RegexOptions.IgnoreCase)]
    private static partial Regex NameEndPattern();
}
