using System.Runtime.InteropServices;
using System.Windows;
using Arcadia.Core.Settings;
using Arcadia.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arcadia.App.Services;

/// <summary>
/// Arcadia's performance state (Phase 9): <b>Gaming Mode</b> (manual, per game, or from automation, with a grace period
/// after a game closes), <b>Power Saver</b> (manual, or automatic on battery) and <b>memory pressure</b>. Other services read
/// the derived switches (<see cref="PauseIndexing"/>, <see cref="PauseThumbnails"/>, <see cref="ReduceMonitoring"/>,
/// <see cref="QuietNotifications"/>) and <see cref="Changed"/>. Event-driven: power changes come from Windows; memory is a
/// single cheap call every two minutes. No GPU work, no busy loops.
/// </summary>
public sealed partial class PerformanceService : ObservableObject, IDisposable
{
    private static readonly TimeSpan MemoryInterval = TimeSpan.FromMinutes(2);
    private const int PressureLoad = 90;

    private readonly ISettingsService _settings;
    private readonly ILogger<PerformanceService> _logger;
    private readonly HashSet<int> _runningGames = [];
    private ITimer? _grace;
    private ITimer? _memory;
    private readonly TimeProvider _time;

    public PerformanceService(ISettingsService settings, TimeProvider time, ILogger<PerformanceService> logger)
    {
        _settings = settings;
        _time = time;
        _logger = logger;
        settings.SettingsChanged += (_, _) => Apply();
    }

    private PerformanceSettings Prefs => _settings.Current.Performance;

    [ObservableProperty] private bool _isGamingMode;
    /// <summary>"manual", "game", "automation" — Gaming Mode started by a game ends with it; manual stays until turned off.</summary>
    [ObservableProperty] private string _gamingReason = string.Empty;
    [ObservableProperty] private bool _isPowerSaver;
    [ObservableProperty] private bool _isOnBattery;
    [ObservableProperty] private bool _isMemoryPressure;
    [ObservableProperty] private int _memoryLoad;

    public bool PauseIndexing => (IsGamingMode && Prefs.PauseIndexingInGames) || IsPowerSaver || IsMemoryPressure;
    public bool PauseThumbnails => (IsGamingMode && Prefs.PauseThumbnailsInGames) || IsMemoryPressure;
    public bool ReduceMonitoring => (IsGamingMode && Prefs.ReduceMonitoringInGames) || IsPowerSaver;
    public bool QuietNotifications => IsGamingMode && Prefs.QuietNotificationsInGames;

    /// <summary>Any switch changed (gaming, power, pressure).</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        ReadPower();
        MemoryLoad = ReadMemoryLoad();
        _memory = _time.CreateTimer(_ => CheckMemory(), null, TimeSpan.FromSeconds(30), MemoryInterval);
        Apply();
    }

    // ── Gaming Mode ──

    public void SetGamingMode(bool on, string reason)
    {
        _grace?.Dispose();
        _grace = null;
        if (on)
        {
            // turning it on by hand makes it "manual" (sticky: a game closing will not turn it off); an automation
            // confirming Gaming Mode while a game already turned it on keeps the game's reason, so it still ends with the game
            var newReason = reason == "manual" || (IsGamingMode && GamingReason == "manual") ? "manual" : IsGamingMode ? GamingReason : reason;
            if (IsGamingMode && GamingReason == newReason) return;
            IsGamingMode = true;
            GamingReason = newReason;
        }
        else
        {
            if (!IsGamingMode) return;
            IsGamingMode = false;
            GamingReason = string.Empty;
        }
        _logger.LogInformation("Gaming Mode {State} ({Reason})", on ? "on" : "off", reason);
        Apply();
    }

    /// <summary>A detected game started: Gaming Mode turns on when the game (or the global preference) asks for it.</summary>
    public void GameStarted(int gameId, bool gameWantsGamingMode)
    {
        _runningGames.Add(gameId);
        _grace?.Dispose();
        _grace = null;
        if ((gameWantsGamingMode || Prefs.GamingForAllGames) && !IsGamingMode) SetGamingMode(true, "game");
    }

    /// <summary>A game closed: Gaming Mode started by games ends after the grace period, unless another game runs by then.</summary>
    public void GameEnded(int gameId)
    {
        _runningGames.Remove(gameId);
        if (!IsGamingMode || GamingReason == "manual" || _runningGames.Count > 0) return;
        _grace?.Dispose();
        var grace = TimeSpan.FromSeconds(Math.Clamp(Prefs.GamingGraceSeconds, 0, 3600));
        _grace = _time.CreateTimer(_ => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_runningGames.Count == 0 && IsGamingMode && GamingReason != "manual") SetGamingMode(false, "game");
        }), null, grace, Timeout.InfiniteTimeSpan);
    }

    public bool HasPendingGamingExit => _grace != null;

    // ── Power Saver ──

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        Application.Current?.Dispatcher.InvokeAsync(() => { ReadPower(); Apply(); });
    }

    private void ReadPower() => IsOnBattery = SystemParameters.PowerLineStatus == PowerLineStatus.Offline;

    /// <summary>Toggles between Normal and Power Saver (the palette and tray command).</summary>
    public void TogglePowerSaver()
    {
        Prefs.Mode = IsPowerSaver ? "normal" : "powersaver";
        _ = _settings.SaveAsync();
        Apply();
    }

    // ── memory pressure ──

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    public static int ReadMemoryLoad()
    {
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref s) ? (int)s.MemoryLoad : 0;
    }

    private void CheckMemory()
    {
        var load = ReadMemoryLoad();
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            MemoryLoad = load;
            var pressure = load >= PressureLoad;
            if (pressure == IsMemoryPressure) return;
            IsMemoryPressure = pressure;
            _logger.LogInformation("Memory pressure {State} ({Load}% in use)", pressure ? "high" : "normal", load);
            if (pressure && Prefs.TrimCachesUnderPressure) TrimCaches();
            Apply();
        });
    }

    /// <summary>Drops optional caches (decoded images). No forced garbage collection.</summary>
    public void TrimCaches()
    {
        var freed = Arcadia.UI.Artwork.ImageCache.Trim();
        _logger.LogInformation("Trimmed image cache ({Mb:0.0} MB)", freed / 1048576.0);
    }

    private void Apply()
    {
        IsPowerSaver = Prefs.Mode == "powersaver" || (Prefs.Mode == "auto" && IsOnBattery);
        Motion.AnimationsEnabled = _settings.Current.AnimationsEnabled && !(IsPowerSaver && Prefs.ReduceAnimationsInPowerSaver) && !App.IsCaptureMode;
        ThumbnailService.Paused = PauseThumbnails;
        DocumentWatcherService.PauseScans = PauseIndexing;
        OnPropertyChanged(nameof(PauseIndexing));
        OnPropertyChanged(nameof(ReduceMonitoring));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _grace?.Dispose();
        _memory?.Dispose();
    }
}
