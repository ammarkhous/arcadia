using Arcadia.Core.Settings;
using Arcadia.Core.Weather;

namespace Arcadia.Infrastructure.Weather;

/// <summary>
/// Offline stand-in until a weather provider is chosen. Clearly marked as sample data.
/// </summary>
public sealed class PlaceholderWeatherService(ISettingsService settings) : IWeatherService
{
    public Task<WeatherSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var location = settings.Current.Services.WeatherLocation;
        if (string.IsNullOrWhiteSpace(location)) return Task.FromResult<WeatherSnapshot?>(null);
        return Task.FromResult<WeatherSnapshot?>(new WeatherSnapshot(location, 16, WeatherCondition.PartlyCloudy, IsSample: true));
    }
}
