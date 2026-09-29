namespace Arcadia.Core.Platform;

/// <summary>Where Arcadia keeps its own data (documented in Settings → Data and in docs/DEVELOPMENT.md).</summary>
public sealed record DataLocations(
    string DataDirectory,
    string DatabasePath,
    string SettingsFile,
    string SecretsFile,
    string MediaDirectory,
    string CacheDirectory,
    string ThumbnailDirectory,
    string LogsDirectory);
