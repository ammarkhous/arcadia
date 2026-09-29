namespace Arcadia.Core.Weather;

public enum WeatherCondition { Clear, PartlyCloudy, Cloudy, Rain, Snow, Storm, Fog }

public sealed record WeatherSnapshot(string Location, double TemperatureCelsius, WeatherCondition Condition, bool IsSample);

public interface IWeatherService
{
    Task<WeatherSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default);
}
