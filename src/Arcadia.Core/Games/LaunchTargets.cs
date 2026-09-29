namespace Arcadia.Core.Games;

/// <summary>
/// Which launch targets Arcadia will hand to the Windows shell. Only game-store protocols and local
/// executables/shortcuts are accepted, so a library entry can never be used to open arbitrary URLs or scripts.
/// </summary>
public static class LaunchTargets
{
    public static readonly IReadOnlyList<string> AllowedSchemes =
        ["steam", "com.epicgames.launcher", "uplay", "origin2", "battlenet", "heroic", "goggalaxy", "xbox", "ms-xbl-3d8b930f"];

    public static readonly IReadOnlyList<string> AllowedExtensions = [".exe", ".lnk", ".url"];

    public static bool IsAllowed(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        target = target.Trim().Trim('"');

        var colon = target.IndexOf("://", StringComparison.Ordinal);
        if (colon > 0)
        {
            var scheme = target[..colon].ToLowerInvariant();
            return AllowedSchemes.Contains(scheme);
        }

        if (!Path.IsPathFullyQualified(target)) return false;
        return AllowedExtensions.Contains(Path.GetExtension(target).ToLowerInvariant());
    }
}

/// <summary>Suggested platform names for the editor (free text is allowed too).</summary>
public static class Platforms
{
    public static readonly IReadOnlyList<string> Common =
        ["PC", "PC VR", "PS5", "PS4", "Xbox Series X|S", "Xbox One", "Nintendo Switch", "Steam Deck", "Mobile", "Retro"];
}
