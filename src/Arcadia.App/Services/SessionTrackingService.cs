using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Arcadia.App.Messages;
using Arcadia.Core.Formatting;
using Arcadia.Core.Games;
using Arcadia.Core.Localization;
using Arcadia.Core.Platform;
using Arcadia.Core.Settings;
using Arcadia.Core.Tracking;
using Arcadia.Infrastructure.Games;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>Live "now playing" state for the UI (always updated on the UI thread).</summary>
public sealed partial class NowPlayingState : ObservableObject
{
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private int _gameId;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private DateTime _startedAtUtc;
    [ObservableProperty] private string _elapsed = "0:00";

    public void Refresh(TimeProvider time)
    {
        if (!IsPlaying) return;
        var span = time.GetUtcNow().UtcDateTime - StartedAtUtc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        Elapsed = $"{(int)span.TotalHours}:{span.Minutes:00}";
    }
}

/// <summary>
/// Records play sessions automatically: a background loop feeds process snapshots to <see cref="PlaySessionTracker"/>
/// and logs the sessions it reports. The active session is persisted (tracking.json) so it survives Arcadia restarting.
/// </summary>
public sealed class SessionTrackingService : IRecipient<LibraryChangedMessage>, IDisposable
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(10);

    private readonly PlaySessionTracker _tracker;
    private readonly IGameCatalog _catalog;
    private readonly IGameInstallLocator _locator;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly ILocalizer _loc;
    private readonly IMessenger _messenger;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionTrackingService> _logger;
    private readonly string _statePath;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _elapsedTimer;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime _lastPersist;
    private Task? _loop;

    public SessionTrackingService(
        IProcessScanner scanner,
        IGameCatalog catalog,
        IGameInstallLocator locator,
        ISettingsService settings,
        INotificationService notifications,
        ILocalizer loc,
        IMessenger messenger,
        IAppPaths paths,
        TimeProvider time,
        ILogger<SessionTrackingService> logger)
    {
        _tracker = new PlaySessionTracker(scanner, time);
        _catalog = catalog;
        _locator = locator;
        _settings = settings;
        _notifications = notifications;
        _loc = loc;
        _messenger = messenger;
        _time = time;
        _logger = logger;
        _statePath = Path.Combine(paths.DataDirectory, "tracking.json");
        _dispatcher = Dispatcher.CurrentDispatcher;
        _elapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => NowPlaying.Refresh(_time), _dispatcher);
        messenger.Register(this);
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        settings.SettingsChanged += (_, _) => ApplySettings();
    }

    public NowPlayingState NowPlaying { get; } = new();

    /// <summary>A detected game started (UI thread). Drives Gaming Mode and the game.started automation trigger.</summary>
    public event Action<ActiveSession>? GameStarted;
    /// <summary>A game session ended (UI thread) with its counted minutes (0 when too short to record).</summary>
    public event Action<ActiveSession, int>? GameEnded;

    /// <summary>Games that asked for Gaming Mode (per-game switch).</summary>
    public IReadOnlySet<int> AutoGamingGames => _autoGaming;
    private HashSet<int> _autoGaming = [];

    /// <summary>Slower scans while Gaming Mode / Power Saver ask for less monitoring.</summary>
    public bool ReduceMonitoring { get; set; }

    /// <summary>The most recently finished session (for assigning late screenshots).</summary>
    public (int GameId, DateTime EndedAtUtc)? LastEnded { get; private set; }

    public int? PlayingGameId => NowPlaying.IsPlaying ? NowPlaying.GameId : null;

    public void Start()
    {
        ApplySettings();
        if (LoadState() is { } restored)
        {
            ActiveSession? stale;
            lock (_gate) stale = _tracker.Restore(restored);
            if (stale != null)
            {
                // Arcadia was gone too long to know what happened: count only up to the last heartbeat
                _logger.LogInformation("Recovered {Title}: session closed at its last heartbeat {Seen:u}", stale.Title, stale.LastSeenUtc);
                _ = RecordAsync(stale);
            }
            else PublishStarted(restored, announce: false);
        }
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Called when Arcadia launches a game: watch for it (resolving its folder first if needed).</summary>
    public async Task NotifyLaunchedAsync(Game game)
    {
        if (!_settings.Current.Tracking.Enabled) return;
        var dir = game.InstallDirectory;
        if (string.IsNullOrEmpty(dir) && TrackableGame.ParseExecutables(game.Executables).Count == 0)
        {
            dir = await Task.Run(() => _locator.Locate(game.LaunchTarget));
            if (dir != null)
            {
                await _catalog.SetInstallDirectoryAsync(game.Id, dir);
                _messenger.Send(new LibraryChangedMessage(game.Id));
            }
        }
        lock (_gate) _tracker.ExpectLaunch(new TrackableGame(game.Id, game.Title, dir, TrackableGame.ParseExecutables(game.Executables)));
        _logger.LogInformation("Watching for {Title} (folder: {Dir})", game.Title, dir ?? "learn on launch");
        Wake();
    }

    /// <summary>Ends the current session now (the user pressed stop) and records it.</summary>
    public async Task StopCurrentAsync()
    {
        ActiveSession? ended;
        lock (_gate) ended = _tracker.EndNow();
        if (ended != null) await RecordAsync(ended);
    }

    public void Receive(LibraryChangedMessage message) => _ = RefreshLibraryAsync();

    private void ApplySettings()
    {
        var s = _settings.Current.Tracking;
        lock (_gate) _tracker.DetectExternalLaunches = s.Enabled && s.DetectExternalLaunches;
        Wake();
    }

    private async Task RefreshLibraryAsync()
    {
        try
        {
            var games = await _catalog.GetGamesAsync();
            lock (_gate) _tracker.UpdateLibrary(games.Select(g => new TrackableGame(g.Id, g.Title, g.InstallDirectory, TrackableGame.ParseExecutables(g.Executables))));
            _autoGaming = games.Where(g => g.AutoGamingMode).Select(g => g.Id).ToHashSet();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refreshing the tracking library failed");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await RefreshLibraryAsync();
        await ResolveMissingFoldersAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                TickResult result;
                bool fast;
                lock (_gate)
                {
                    result = _settings.Current.Tracking.Enabled || _tracker.Active != null ? _tracker.Tick() : TickResult.None;
                    fast = _tracker.Active != null || _tracker.HasPendingLaunch;
                }
                await HandleAsync(result);
                delay = fast ? (ReduceMonitoring ? TimeSpan.FromSeconds(5) : FastInterval) : (ReduceMonitoring ? TimeSpan.FromSeconds(20) : IdleInterval);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session tracking tick failed");
                delay = IdleInterval;
            }

            var wake = _wake.Task;
            await Task.WhenAny(Task.Delay(delay, ct), wake).ConfigureAwait(false);
            if (wake.IsCompleted) _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private async Task HandleAsync(TickResult result)
    {
        if (result.Learned is { } learned)
        {
            _logger.LogInformation("Learned install folder for game {Id}: {Dir}", learned.GameId, learned.Directory);
            await _catalog.SetInstallDirectoryAsync(learned.GameId, learned.Directory);
            _ = _dispatcher.InvokeAsync(() => _messenger.Send(new LibraryChangedMessage(learned.GameId)));
        }
        if (result.Started is { } started)
        {
            _logger.LogInformation("Session started: {Title} at {Start:u}", started.Title, started.StartedAtUtc);
            SaveState(started);
            PublishStarted(started, announce: true);
        }
        if (result.Ended is { } ended) await RecordAsync(ended);

        ActiveSession? active;
        lock (_gate) active = _tracker.Active;
        if (active != null && _time.GetUtcNow().UtcDateTime - _lastPersist > TimeSpan.FromMinutes(1)) SaveState(active);
    }

    private async Task RecordAsync(ActiveSession ended)
    {
        DeleteState();
        LastEnded = (ended.GameId, ended.LastSeenUtc);
        _ = _dispatcher.InvokeAsync(() => { NowPlaying.IsPlaying = false; _elapsedTimer.Stop(); });

        var minutes = (int)Math.Round((ended.LastSeenUtc - ended.StartedAtUtc).TotalMinutes);
        if (minutes < Math.Max(1, _settings.Current.Tracking.MinimumSessionMinutes))
        {
            _logger.LogInformation("Session for {Title} too short to record ({Minutes} min)", ended.Title, minutes);
            _ = _dispatcher.InvokeAsync(() => GameEnded?.Invoke(ended, 0));
            return;
        }
        minutes = Math.Min(minutes, 24 * 60);
        try
        {
            await _catalog.LogSessionAsync(new SessionLog(ended.GameId, ended.StartedAtUtc, minutes, _loc["Tracking_AutoNote"], IsAutomatic: true));
            _logger.LogInformation("Recorded {Minutes} min for {Title}", minutes, ended.Title);
            await _dispatcher.InvokeAsync(() =>
            {
                _messenger.Send(new LibraryChangedMessage(ended.GameId));
                _notifications.Show(_loc.Format("Tracking_Recorded", RelativeTime.Hours(minutes / 60.0, _loc), ended.Title), NotificationKind.Success);
                GameEnded?.Invoke(ended, minutes);
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            _logger.LogError(ex, "Recording the session for {Title} failed", ended.Title);
        }
    }

    private void PublishStarted(ActiveSession session, bool announce) =>
        _dispatcher.InvokeAsync(() =>
        {
            NowPlaying.GameId = session.GameId;
            NowPlaying.Title = session.Title;
            NowPlaying.StartedAtUtc = session.StartedAtUtc;
            NowPlaying.IsPlaying = true;
            NowPlaying.Refresh(_time);
            _elapsedTimer.Start();
            if (announce) _notifications.Show(_loc.Format("Tracking_Started", session.Title));
            GameStarted?.Invoke(session);
        });

    /// <summary>Sleep ends the session at the moment of suspend; after resume a still-running game starts a new one.</summary>
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            ActiveSession? ended;
            lock (_gate) ended = _tracker.Suspend();
            if (ended != null)
            {
                _logger.LogInformation("Sleeping: ending the session for {Title}", ended.Title);
                _ = RecordAsync(ended);
            }
        }
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            lock (_gate) _tracker.Resumed();
            Wake();
        }
    }

    /// <summary>Fills in install folders for Steam/Epic games that don't have one yet (cheap file reads).</summary>
    private async Task ResolveMissingFoldersAsync(CancellationToken ct)
    {
        try
        {
            var games = await _catalog.GetGamesAsync(ct);
            int found = 0;
            foreach (var g in games.Where(g => string.IsNullOrEmpty(g.InstallDirectory) && !string.IsNullOrEmpty(g.LaunchTarget)))
            {
                if (_locator.Locate(g.LaunchTarget) is { } dir)
                {
                    await _catalog.SetInstallDirectoryAsync(g.Id, dir, ct);
                    found++;
                }
            }
            if (found > 0) await RefreshLibraryAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Resolving install folders failed");
        }
    }

    private void Wake() => _wake.TrySetResult();

    // ── persistence of the active session ──────────────────────────

    private void SaveState(ActiveSession session)
    {
        try
        {
            _lastPersist = _time.GetUtcNow().UtcDateTime;
            File.WriteAllText(_statePath, JsonSerializer.Serialize(session));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist the active session");
        }
    }

    private ActiveSession? LoadState()
    {
        try
        {
            return File.Exists(_statePath) ? JsonSerializer.Deserialize<ActiveSession>(File.ReadAllText(_statePath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable tracking state");
            return null;
        }
    }

    private void DeleteState()
    {
        try { if (File.Exists(_statePath)) File.Delete(_statePath); }
        catch (IOException ex) { _logger.LogWarning(ex, "Could not clear tracking state"); }
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
