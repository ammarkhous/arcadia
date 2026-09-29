using System.Windows.Threading;
using Arcadia.Core.Localization;
using Arcadia.Core.Settings;
using Arcadia.Core.Weather;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels;

/// <summary>Clock, weather and identity shown in the header banner.</summary>
public sealed partial class HeaderViewModel : ObservableObject
{
    private readonly IWeatherService _weather;
    private readonly ILocalizer _loc;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HeaderViewModel> _logger;
    private readonly DispatcherTimer _clock;
    private WeatherSnapshot? _snapshot;

    public HeaderViewModel(IWeatherService weather, ISettingsService settings, ILocalizer loc, TimeProvider time, ILogger<HeaderViewModel> logger)
    {
        _weather = weather;
        _loc = loc;
        _timeProvider = time;
        _logger = logger;

        var name = Environment.UserName;
        UserName = name;
        UserInitial = string.IsNullOrEmpty(name) ? "A" : name[..1].ToUpperInvariant();

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateClock(), Dispatcher.CurrentDispatcher);
        UpdateClock();
        _clock.Start();

        loc.CultureChanged += (_, _) => { UpdateClock(force: true); ApplyWeather(); };
        settings.SettingsChanged += async (_, _) => await RefreshWeatherAsync();
    }

    /// <summary>The clock only ticks while the window can be seen (hidden to tray or minimized: no timer).</summary>
    public void SetVisible(bool visible)
    {
        if (visible && !_clock.IsEnabled) { UpdateClock(force: true); _clock.Start(); }
        else if (!visible) _clock.Stop();
    }

    [ObservableProperty] private string _time = string.Empty;
    [ObservableProperty] private string _date = string.Empty;
    [ObservableProperty] private bool _hasWeather;
    [ObservableProperty] private string _weatherLocation = string.Empty;
    [ObservableProperty] private string _weatherTemperature = string.Empty;
    [ObservableProperty] private string _weatherCondition = string.Empty;
    [ObservableProperty] private string? _weatherTooltip;

    public string UserName { get; }
    public string UserInitial { get; }

    private int _lastMinute = -1;

    private void UpdateClock(bool force = false)
    {
        var now = _timeProvider.GetLocalNow();
        if (!force && now.Minute == _lastMinute) return;
        _lastMinute = now.Minute;
        Time = now.ToString("t", _loc.Culture);
        Date = now.ToString("ddd, d MMM yyyy", _loc.Culture);
    }

    public async Task RefreshWeatherAsync()
    {
        try
        {
            _snapshot = await _weather.GetCurrentAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Weather unavailable");
            _snapshot = null;
        }
        ApplyWeather();
    }

    private void ApplyWeather()
    {
        HasWeather = _snapshot != null;
        if (_snapshot == null) return;
        WeatherLocation = _snapshot.Location;
        WeatherTemperature = $"{Math.Round(_snapshot.TemperatureCelsius):0}°C";
        WeatherCondition = _loc[$"Weather_{_snapshot.Condition}"];
        WeatherTooltip = _snapshot.IsSample ? _loc["Header_SampleWeather"] : null;
    }
}
