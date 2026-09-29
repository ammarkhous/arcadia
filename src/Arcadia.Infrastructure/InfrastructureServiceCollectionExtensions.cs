using Arcadia.Core.Platform;
using Arcadia.Core.Settings;
using Arcadia.Core.Weather;
using Arcadia.Infrastructure.Platform;
using Arcadia.Infrastructure.Settings;
using Arcadia.Infrastructure.Weather;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arcadia.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddArcadiaInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsService>(sp =>
            new JsonSettingsService(paths.SettingsFilePath, sp.GetRequiredService<ILogger<JsonSettingsService>>()));
        services.AddSingleton<IWeatherService, PlaceholderWeatherService>();
        services.AddSingleton<Games.IGameInstallLocator>(sp => new Games.GameInstallLocator(sp.GetRequiredService<ILogger<Games.GameInstallLocator>>()));
        // Secrets (API keys) live encrypted outside settings.json.
        services.AddSingleton<Arcadia.Core.Security.ISecretStore>(sp => OperatingSystem.IsWindows()
            ? new Security.DpapiSecretStore(Path.Combine(paths.SettingsDirectory, "secrets.dat"), sp.GetRequiredService<ILogger<Security.DpapiSecretStore>>())
            : throw new PlatformNotSupportedException("Arcadia stores secrets with Windows DPAPI."));

        services.AddHttpClient();
        foreach (var name in new[] { Metadata.IgdbClient.HttpClientName, Metadata.SteamGridDbClient.HttpClientName, "images" })
            services.AddHttpClient(name, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(20);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Arcadia/0.1 (+desktop)");
            });
        // Travel: identifying User-Agent required by the OpenStreetMap tile and Nominatim usage policies.
        services.AddHttpClient(Geo.GeocodingService.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Arcadia/0.1 (Windows desktop app; personal travel journal)");
        });
        services.AddHttpClient(Geo.MapTileService.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Arcadia/0.1 (Windows desktop app; personal travel journal)");
        });
        services.AddSingleton<Arcadia.Core.Travel.ICountryCatalog, Geo.CountryCatalog>();
        services.AddSingleton<Geo.CityIndex>();
        services.AddSingleton<Arcadia.Core.Travel.IGeocodingService>(sp => new Geo.GeocodingService(
            sp.GetRequiredService<Arcadia.Core.Travel.ICountryCatalog>(), sp.GetRequiredService<Geo.CityIndex>(), sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ISettingsService>(), paths.CacheDirectory, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<Geo.GeocodingService>>()));
        services.AddSingleton<Arcadia.Core.Travel.IMapTileService>(sp => new Geo.MapTileService(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<Arcadia.Core.Security.ISecretStore>(),
            paths.CacheDirectory, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<Geo.MapTileService>>()));
        services.AddSingleton<Arcadia.Core.Travel.IPhotoMetadataReader, Geo.PhotoMetadataReader>();
        services.AddSingleton<Arcadia.Core.Documents.IFileFactsService, Files.FileFactsService>();
        services.AddSingleton<Arcadia.Core.Metadata.IGameInfoProvider, Metadata.IgdbClient>();
        services.AddSingleton<Arcadia.Core.Metadata.IGameArtProvider, Metadata.SteamGridDbClient>();
        return services;
    }
}
