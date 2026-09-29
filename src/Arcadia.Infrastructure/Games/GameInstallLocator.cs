using System.Text.Json;
using System.Text.RegularExpressions;
using Arcadia.Core.Tracking;
using Microsoft.Extensions.Logging;

namespace Arcadia.Infrastructure.Games;

/// <summary>Finds where games are installed and where captures are saved, by reading store manifests.</summary>
public interface IGameInstallLocator
{
    /// <summary>Install folder for a launch target (Steam app, Epic app or .exe), or null if unknown.</summary>
    string? Locate(string? launchTarget);

    /// <summary>Existing capture folders worth watching (Steam screenshots, Xbox Game Bar captures).</summary>
    IReadOnlyList<string> SuggestedCaptureFolders();
}

public sealed partial class GameInstallLocator(ILogger<GameInstallLocator> logger, string? steamRoot = null, string? epicManifestFolder = null) : IGameInstallLocator
{
    private readonly Lazy<string?> _steamRoot = new(() => steamRoot ?? FindSteamRoot(logger));
    private readonly string _epicManifests = epicManifestFolder ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public string? Locate(string? launchTarget)
    {
        try
        {
            if (LaunchTargetInfo.SteamAppId(launchTarget) is { } appId) return SteamInstallDirectory(appId);
            if (LaunchTargetInfo.EpicAppName(launchTarget) is { } appName) return EpicInstallDirectory(appName);
            return LaunchTargetInfo.ExecutableDirectory(launchTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Could not locate install folder for {Target}", launchTarget);
            return null;
        }
    }

    public IReadOnlyList<string> SuggestedCaptureFolders()
    {
        var folders = new List<string>();
        if (_steamRoot.Value is { } root)
        {
            var userdata = Path.Combine(root, "userdata");
            if (Directory.Exists(userdata))
                foreach (var user in Directory.EnumerateDirectories(userdata))
                {
                    var remote = Path.Combine(user, "760", "remote");
                    if (Directory.Exists(remote)) folders.Add(remote);
                }
        }
        var gameBar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");
        if (Directory.Exists(gameBar)) folders.Add(gameBar);
        return folders;
    }

    // ── Steam ──────────────────────────────────────────────────────

    public string? SteamInstallDirectory(int appId)
    {
        if (_steamRoot.Value is not { } root) return null;
        foreach (var library in SteamLibraries(root))
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            var installDir = VdfValue(File.ReadAllText(manifest), "installdir");
            if (installDir == null) continue;
            var dir = Path.Combine(library, "steamapps", "common", installDir);
            return Directory.Exists(dir) ? dir : null;
        }
        return null;
    }

    public static IEnumerable<string> SteamLibraries(string steamRoot)
    {
        yield return steamRoot;
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (Match m in VdfPathPattern().Matches(File.ReadAllText(vdf)))
        {
            var path = m.Groups[1].Value.Replace(@"\\", @"\");
            if (!string.Equals(Path.GetFullPath(path).TrimEnd('\\'), Path.GetFullPath(steamRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    public static string? VdfValue(string vdf, string key)
    {
        var m = Regex.Match(vdf, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Replace(@"\\", @"\") : null;
    }

    private static string? FindSteamRoot(ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && Directory.Exists(path)) return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Steam registry key not readable");
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPathPattern();

    // ── Epic ───────────────────────────────────────────────────────

    public string? EpicInstallDirectory(string appName)
    {
        if (!Directory.Exists(_epicManifests)) return null;
        foreach (var file in Directory.EnumerateFiles(_epicManifests, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            bool matches = new[] { "AppName", "CatalogItemId", "MainGameAppName" }
                .Any(p => root.TryGetProperty(p, out var v) && string.Equals(v.GetString(), appName, StringComparison.OrdinalIgnoreCase));
            if (matches && root.TryGetProperty("InstallLocation", out var location) && location.GetString() is { } dir && Directory.Exists(dir))
                return dir;
        }
        return null;
    }
}
