using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Windows.Threading;
using Arcadia.App.Messages;
using Arcadia.Core.Games;
using Arcadia.Core.Localization;
using Arcadia.Core.Media;
using Arcadia.Core.Settings;
using Arcadia.Core.Tracking;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>
/// Watches the configured capture folders and imports new screenshots into the matching game
/// (the game being played, Steam's app folder, or the title in the file/folder name).
/// Work happens on a background channel; files are imported once they are fully written, and never twice.
/// </summary>
public sealed class CaptureWatcherService : IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
    private static readonly TimeSpan RecentSessionGrace = TimeSpan.FromMinutes(15);

    private readonly ISettingsService _settings;
    private readonly IGameCatalog _catalog;
    private readonly IMediaStore _media;
    private readonly SessionTrackingService _tracking;
    private readonly INotificationService _notifications;
    private readonly ILocalizer _loc;
    private readonly IMessenger _messenger;
    private readonly TimeProvider _time;
    private readonly ILogger<CaptureWatcherService> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<int, int> _batch = [];
    private DispatcherTimer? _batchTimer;
    private Task? _worker;

    public CaptureWatcherService(
        ISettingsService settings, IGameCatalog catalog, IMediaStore media, SessionTrackingService tracking,
        INotificationService notifications, ILocalizer loc, IMessenger messenger, TimeProvider time, ILogger<CaptureWatcherService> logger)
    {
        _settings = settings;
        _catalog = catalog;
        _media = media;
        _tracking = tracking;
        _notifications = notifications;
        _loc = loc;
        _messenger = messenger;
        _time = time;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        settings.SettingsChanged += (_, _) => _dispatcher.InvokeAsync(Reconfigure);
    }

    public void Start()
    {
        Reconfigure();
        _worker = Task.Run(() => ProcessQueueAsync(_stop.Token));
    }

    private void Reconfigure()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        var s = _settings.Current.Captures;
        if (!s.WatchEnabled) return;

        foreach (var folder in s.Folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder))
            {
                _logger.LogWarning("Capture folder not found: {Folder}", folder);
                continue;
            }
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => Enqueue(e.FullPath);
                watcher.Renamed += (_, e) => Enqueue(e.FullPath);
                watcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "Capture watcher error in {Folder}", folder);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cannot watch {Folder}", folder);
            }
        }
        _logger.LogInformation("Watching {Count} capture folder(s)", _watchers.Count);
    }

    private void Enqueue(string path)
    {
        if (!ImageExtensions.Contains(Path.GetExtension(path))) return;
        // Steam writes a small copy into a "thumbnails" subfolder; only the full image matters.
        if (path.Contains($"{Path.DirectorySeparatorChar}thumbnails{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) return;
        if (_pending.TryAdd(path, 0)) _queue.Writer.TryWrite(path);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ImportAsync(path, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Importing capture {Path} failed", path);
                }
                finally
                {
                    _pending.TryRemove(path, out _);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ImportAsync(string path, CancellationToken ct)
    {
        if (!await WaitUntilReadableAsync(path, ct)) return;
        if (await _catalog.HasScreenshotFromSourceAsync(path, ct)) return;

        var games = await _catalog.GetGamesAsync(ct);
        var candidates = games.Select(g => new CaptureGame(g.Id, g.Title, LaunchTargetInfo.SteamAppId(g.LaunchTarget))).ToList();
        int? playing = _tracking.PlayingGameId;
        if (playing == null && _tracking.LastEnded is { } last && _time.GetUtcNow().UtcDateTime - last.EndedAtUtc < RecentSessionGrace)
            playing = last.GameId;

        var gameId = ScreenshotMatcher.Match(path, candidates, playing);
        if (gameId == null)
        {
            _logger.LogInformation("Capture {Path} did not match any game", path);
            return;
        }

        var imported = await _media.ImportAsync(path, MediaKind.Screenshot, ct);
        var created = File.GetCreationTimeUtc(path);
        await _catalog.AddScreenshotAsync(new Screenshot
        {
            GameId = gameId.Value,
            Title = Path.GetFileNameWithoutExtension(path),
            ImagePath = imported.Path,
            ThumbnailPath = imported.ThumbnailPath,
            SourcePath = path,
            CapturedAtUtc = created.Year > 2000 ? created : _time.GetUtcNow().UtcDateTime,
            ArtworkSeed = Random.Shared.Next(1, 10_000),
        }, ct);
        _logger.LogInformation("Imported capture {Path} into game {GameId}", path, gameId);

        var title = games.First(g => g.Id == gameId).Title;
        await _dispatcher.InvokeAsync(() => AnnounceBatched(gameId.Value, title));
    }

    /// <summary>Coalesces a burst of captures into one message per game.</summary>
    private void AnnounceBatched(int gameId, string title)
    {
        _messenger.Send(new LibraryChangedMessage(gameId));
        _batch[gameId] = _batch.GetValueOrDefault(gameId) + 1;
        _batchTitles[gameId] = title;
        _batchTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Background, (_, _) => FlushBatch(), _dispatcher);
        _batchTimer.Stop();
        _batchTimer.Start();
    }

    private readonly Dictionary<int, string> _batchTitles = [];

    private void FlushBatch()
    {
        _batchTimer?.Stop();
        foreach (var (gameId, count) in _batch)
            _notifications.Show(_loc.Format("Capture_Added", count, _batchTitles[gameId]), NotificationKind.Success);
        _batch.Clear();
        _batchTitles.Clear();
    }

    /// <summary>Capture tools write files in steps; wait until the file can be opened exclusively for reading.</summary>
    private static async Task<bool> WaitUntilReadableAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (fs.Length > 0) return true;
            }
            catch (IOException) { }
            await Task.Delay(250, ct);
        }
        return false;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _queue.Writer.TryComplete();
        foreach (var w in _watchers) w.Dispose();
        _stop.Dispose();
    }
}
