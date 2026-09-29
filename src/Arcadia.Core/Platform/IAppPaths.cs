namespace Arcadia.Core.Platform;

/// <summary>Well-known Windows locations used by Arcadia. User data never lives beside the executable.</summary>
public interface IAppPaths
{
    /// <summary>%AppData%\Arcadia — roaming preferences.</summary>
    string SettingsDirectory { get; }
    /// <summary>%LocalAppData%\Arcadia (or user override) — database and media.</summary>
    string DataDirectory { get; }
    string LogsDirectory { get; }
    string CacheDirectory { get; }
    string DatabasePath { get; }
    string SettingsFilePath { get; }
}
