namespace Arcadia.Core.Security;

/// <summary>
/// Stores secrets (API keys) outside the settings file, encrypted for the current Windows user.
/// Values never appear in settings.json, logs or source control.
/// </summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string? value);
    event EventHandler? Changed;
}

public static class SecretNames
{
    public const string IgdbClientId = "igdb.clientId";
    public const string IgdbClientSecret = "igdb.clientSecret";
    public const string SteamGridDbApiKey = "steamgriddb.apiKey";
    /// <summary>Optional key for a custom map tile server (substituted for {key} in the tile URL).</summary>
    public const string MapTilesApiKey = "maptiles.apiKey";
}
