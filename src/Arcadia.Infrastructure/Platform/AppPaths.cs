using Arcadia.Core.Platform;

namespace Arcadia.Infrastructure.Platform;

/// <summary>
/// Resolves Arcadia folders. Settings roam (%AppData%\Arcadia); data, logs, media and caches are
/// machine-local in %LocalAppData%\Arcadia\Library — a dedicated subfolder, because %LocalAppData%\Arcadia
/// itself may belong to another program. ARCADIA_DATA_DIR overrides everything (tests, portable runs).
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public const string EnvironmentOverride = "ARCADIA_DATA_DIR";

    public AppPaths(string? dataDirectoryOverride = null)
    {
        var envRoot = Environment.GetEnvironmentVariable(EnvironmentOverride);
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            SettingsDirectory = Path.Combine(envRoot, "Settings");
            DataDirectory = envRoot;
        }
        else
        {
            SettingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arcadia");
            DataDirectory = string.IsNullOrWhiteSpace(dataDirectoryOverride)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arcadia", "Library")
                : dataDirectoryOverride;
        }

        LogsDirectory = Path.Combine(DataDirectory, "Logs");
        CacheDirectory = Path.Combine(DataDirectory, "Cache");
        DatabasePath = Path.Combine(DataDirectory, "arcadia.db");
        SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
    }

    public string SettingsDirectory { get; }
    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string CacheDirectory { get; }
    public string DatabasePath { get; }
    public string SettingsFilePath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
