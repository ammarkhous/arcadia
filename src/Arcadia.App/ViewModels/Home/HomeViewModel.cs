using System.Collections.ObjectModel;
using System.Globalization;
using Arcadia.App.Messages;
using Arcadia.App.Services;
using Arcadia.Core.Formatting;
using Arcadia.Core.Games;
using Arcadia.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Home;

public sealed partial class HomeViewModel : ObservableObject, IActivatable, IRecipient<LibraryChangedMessage>
{
    private readonly GameActions _actions;
    private readonly IGameLibraryService _library;
    private readonly StartupState _startup;
    private readonly ILocalizer _loc;
    private readonly TimeProvider _time;
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;
    private readonly IShellLauncher _launcher;
    private readonly ILogger<HomeViewModel> _logger;

    private HomeDashboardSnapshot? _snapshot;
    private int? _featuredGameId;
    private bool _isStale = true;

    public HomeViewModel(
        IGameLibraryService library,
        StartupState startup,
        ILocalizer loc,
        TimeProvider time,
        INavigationService navigation,
        INotificationService notifications,
        IShellLauncher launcher,
        GameActions actions,
        IMessenger messenger,
        HomeTodayViewModel today,
        ILogger<HomeViewModel> logger)
    {
        Today = today;
        _actions = actions;
        messenger.Register(this);
        _library = library;
        _startup = startup;
        _loc = loc;
        _time = time;
        _navigation = navigation;
        _notifications = notifications;
        _launcher = launcher;
        _logger = logger;

        BuildModules();
        loc.CultureChanged += (_, _) =>
        {
            BuildModules();
            if (_snapshot != null) Build(_snapshot);
        };
    }

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private FeaturedGameViewModel? _featured;
    [ObservableProperty] private string _totalPlaytime = string.Empty;
    [ObservableProperty] private string _achievementsSummary = string.Empty;
    [ObservableProperty] private ScreenshotViewModel? _selectedScreenshot;
    [ObservableProperty] private IReadOnlyList<ScreenshotViewModel> _otherScreenshots = [];
    [ObservableProperty] private IReadOnlyList<ModuleTileViewModel> _modules = [];

    public ObservableCollection<StatRowViewModel> Stats { get; } = [];
    public ObservableCollection<GameRowViewModel> RecentGames { get; } = [];
    public ObservableCollection<GameRowViewModel> Upcoming { get; } = [];
    public ObservableCollection<ScreenshotViewModel> Screenshots { get; } = [];

    /// <summary>Marks data as outdated so the next activation reloads it (e.g. after sample data removal).</summary>
    public void Invalidate() => _isStale = true;

    /// <summary>The compact Today strip (date, next event, tasks due, next deadline, pinned note).</summary>
    public HomeTodayViewModel Today { get; }

    public async Task ActivateAsync()
    {
        _ = Today.LoadAsync(); // independent of the game dashboard; cheap when nothing changed
        if (!_isStale) return;
        await LoadAsync();
    }

    /// <summary>Library data changed elsewhere: refresh quietly if the dashboard has been shown.</summary>
    public void Receive(LibraryChangedMessage message)
    {
        _isStale = true;
        if (message.Deleted && message.GameId == _featuredGameId) _featuredGameId = null;
        if (_snapshot != null) _ = LoadAsync(silent: true);
    }

    [RelayCommand]
    private async Task LoadAsync(bool silent = false)
    {
        if (!silent || _snapshot == null) IsLoading = true;
        HasError = false;
        try
        {
            await _startup.DatabaseReady;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = await Task.Run(() => _library.GetHomeDashboardAsync());
            var queryMs = sw.Elapsed.TotalMilliseconds;
            _snapshot = snapshot;
            Build(snapshot);
            _logger.LogInformation("Perf: home query {Query:0} ms, build {Build:0} ms, visible {Total:0} ms after process start",
                queryMs, sw.Elapsed.TotalMilliseconds - queryMs, App.SinceProcessStart());
            _isStale = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Home dashboard failed to load");
            ErrorText = ex is Arcadia.Data.IncompatibleDatabaseException foreign
                ? _loc.Format("Error_ForeignDatabase", foreign.DatabasePath)
                : _loc["Home_LoadFailed"];
            HasError = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Build(HomeDashboardSnapshot s)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        HasData = s.Stats.Total > 0;

        // ── Featured ───────────────────────────────────────────────
        var featuredGame = (_featuredGameId is { } id ? FindGame(s, id) : null) ?? s.Featured;
        Featured = featuredGame == null ? null : BuildFeatured(featuredGame, s, now);

        // ── Overview ───────────────────────────────────────────────
        var st = s.Stats;
        Stats.Clear();
        Stats.Add(new(_loc["Stat_Total"], N(st.Total), string.Empty, "Games", "Blue", s.Trends.Total));
        Stats.Add(new(_loc["Stat_Completed"], N(st.Completed), Pct(st.PercentOf(st.Completed)), "Check", "Green", s.Trends.Completed));
        Stats.Add(new(_loc["Stat_Playing"], N(st.Playing), Pct(st.PercentOf(st.Playing)), "Play", "Amber", s.Trends.Playtime));
        Stats.Add(new(_loc["Stat_Backlog"], N(st.Backlog), Pct(st.PercentOf(st.Backlog)), "Hourglass", "Purple", s.Trends.Backlog));
        Stats.Add(new(_loc["Stat_Abandoned"], N(st.Abandoned), Pct(st.PercentOf(st.Abandoned)), "Close", "Red", s.Trends.Abandoned));
        TotalPlaytime = RelativeTime.Hours(st.TotalPlaytimeHours, _loc);
        AchievementsSummary = $"{N(st.AchievementsUnlocked)} / {N(st.AchievementsTotal)}";

        // ── Recent ─────────────────────────────────────────────────
        RecentGames.Clear();
        foreach (var g in s.RecentGames)
        {
            var when = g.LastPlayedAtUtc is { } lp ? RelativeTime.Format(lp, now, _loc) : _loc["Home_NeverPlayed"];
            RecentGames.Add(new(g.Id, g.Title, $"{g.Platform} · {RelativeTime.Hours(g.PlaytimeHours, _loc)} · {when}",
                g.CompletionPercent, Pct(g.CompletionPercent), ProgressTone(g.CompletionPercent),
                g.ArtworkTheme, g.ArtworkSeed, Media(g.CoverThumbnailPath ?? g.CoverImagePath), StatusText(g.Status), StatusTone(g.Status)));
        }

        // ── Upcoming / backlog ─────────────────────────────────────
        Upcoming.Clear();
        foreach (var g in s.Backlog)
        {
            var meta = g.Status == GameStatus.Backlog
                ? $"{_loc[$"Priority_{g.Priority}"]}{(g.EstimatedHours is { } est ? " · " + _loc.Format("Home_EstimatedHours", N((int)est)) : string.Empty)}"
                : $"{g.Platform} · {Pct(g.CompletionPercent)}";
            Upcoming.Add(new(g.Id, g.Title, meta, g.CompletionPercent, Pct(g.CompletionPercent), ProgressTone(g.CompletionPercent),
                g.ArtworkTheme, g.ArtworkSeed, Media(g.CoverThumbnailPath ?? g.CoverImagePath), StatusText(g.Status), StatusTone(g.Status)));
        }

        // ── Screenshots ────────────────────────────────────────────
        Screenshots.Clear();
        foreach (var shot in s.Screenshots)
        {
            Screenshots.Add(new(shot.Id, shot.Title, shot.Note, shot.Location, shot.Game?.Title ?? string.Empty,
                shot.CapturedAtUtc.ToLocalTime().ToString("d MMM yyyy", _loc.Culture), shot.ArtworkTheme, shot.ArtworkSeed,
                Media(shot.ImagePath), Media(shot.ThumbnailPath ?? shot.ImagePath)));
        }
        SelectScreenshot(Screenshots.FirstOrDefault());
    }

    private FeaturedGameViewModel BuildFeatured(Game g, HomeDashboardSnapshot s, DateTime now)
    {
        var thumbs = new List<ArtworkThumbViewModel> { new(g.ArtworkTheme, g.ArtworkSeed, Media(g.HeroImagePath ?? g.CoverImagePath), g.Title) };
        foreach (var shot in s.Screenshots.Where(x => x.GameId == g.Id))
            thumbs.Add(new(shot.ArtworkTheme, shot.ArtworkSeed, Media(shot.ImagePath), shot.Title));
        // Until real media exists, pad the strip with alternate views of the game's own scene.
        for (int i = 1; thumbs.Count < 5; i++)
            thumbs.Add(new(g.ArtworkTheme, g.ArtworkSeed + i * 17, null, g.Title));

        var note = s.Journal.FirstOrDefault(j => j.GameId == g.Id)?.Text;
        var lastPlayed = g.LastPlayedAtUtc is { } lp ? _loc.Format("Home_LastPlayed", RelativeTime.Format(lp, now, _loc)) : _loc["Home_NeverPlayed"];

        return new FeaturedGameViewModel(g.Id, g.Title, thumbs)
        {
            Tags = g.TagList,
            Meta = $"{g.Platform} · {lastPlayed}",
            Description = g.Description,
            Progress = g.CompletionPercent,
            ProgressText = Pct(g.CompletionPercent),
            ProgressTone = ProgressTone(g.CompletionPercent),
            Playtime = RelativeTime.Hours(g.PlaytimeHours, _loc),
            Achievements = g.AchievementsTotal > 0 ? $"{g.AchievementsUnlocked} / {g.AchievementsTotal}" : "—",
            CurrentQuest = string.IsNullOrWhiteSpace(g.CurrentObjective) ? "—" : g.CurrentObjective,
            LatestNote = note,
            PrimaryActionText = g.PlaytimeHours > 0 ? _loc["Home_ContinuePlaying"] : _loc["Home_StartPlaying"],
            LogoPath = Media(g.LogoImagePath),
        };
    }

    private static Game? FindGame(HomeDashboardSnapshot s, int id) =>
        s.RecentGames.Concat(s.Backlog).Append(s.Featured).FirstOrDefault(g => g?.Id == id);

    private void BuildModules() => Modules =
    [
        new("ittools", "ITTools", "Settings", "Blue", 11, _loc["Nav_ITTools"], _loc["Tile_ITTools_Desc"], _loc["Tile_ITTools_Action"]),
        new("projects", "Projects", "Tasks", "Cyan", 12, _loc["Nav_Projects"], _loc["Tile_Projects_Desc"], _loc["Tile_Projects_Action"]),
        new("documents", "Documents", "Notes", "Amber", 13, _loc["Nav_Documents"], _loc["Tile_Documents_Desc"], _loc["Tile_Documents_Action"]),
        new("travel", "Travel", "Camera", "Green", 14, _loc["Nav_Travel"], _loc["Tile_Travel_Desc"], _loc["Tile_Travel_Action"]),
        new(Sections.Oracle, "Oracle", "Sparkle", "Cyan", 15, _loc["Nav_Oracle"], _loc["Tile_Oracle_Desc"], _loc["Tile_Oracle_Action"]),
    ];

    // ── Commands ────────────────────────────────────────────────────

    [RelayCommand]
    private void ShowInFeatured(GameRowViewModel? row)
    {
        if (row == null || _snapshot == null) return;
        _featuredGameId = row.Id;
        var game = FindGame(_snapshot, row.Id);
        if (game != null) Featured = BuildFeatured(game, _snapshot, _time.GetUtcNow().UtcDateTime);
    }

    [RelayCommand]
    private void SelectScreenshot(ScreenshotViewModel? shot)
    {
        SelectedScreenshot = shot;
        OtherScreenshots = Screenshots.Where(x => !ReferenceEquals(x, shot)).Take(3).ToList();
    }

    [RelayCommand]
    private Task ContinuePlayingAsync() => Featured == null ? Task.CompletedTask : _actions.LaunchAsync(Featured.Id);

    [RelayCommand]
    private Task AddScreenshotAsync() => Featured == null ? Task.CompletedTask : _actions.ImportScreenshotsAsync(Featured.Id);

    [RelayCommand]
    private Task ImportFilesAsync(string[]? files) =>
        Featured == null || files == null ? Task.CompletedTask : _actions.ImportScreenshotFilesAsync(Featured.Id, files);

    [RelayCommand]
    private Task LogSessionAsync() => Featured == null ? Task.CompletedTask : _actions.LogSessionAsync(Featured.Id);

    [RelayCommand]
    private Task FindOnlineAsync() => Featured == null ? Task.CompletedTask : _actions.FindOnlineAsync(Featured.Id);

    [RelayCommand]
    private void OpenFeaturedDetails()
    {
        if (Featured != null) _navigation.ShowGame(Featured.Id);
    }

    [RelayCommand]
    private void OpenGame(GameRowViewModel? row)
    {
        if (row != null) _navigation.ShowGame(row.Id);
    }

    [RelayCommand]
    private void Navigate(string? key)
    {
        if (!string.IsNullOrEmpty(key)) _navigation.Navigate(key);
    }

    [RelayCommand]
    private void OpenWikipedia()
    {
        if (Featured == null) return;
        _launcher.OpenUri(new Uri($"https://en.wikipedia.org/w/index.php?search={Uri.EscapeDataString(Featured.Title)}"));
    }

    [RelayCommand]
    private void OpenSteam()
    {
        if (Featured == null) return;
        _launcher.OpenUri(new Uri($"https://store.steampowered.com/search/?term={Uri.EscapeDataString(Featured.Title)}"));
    }

    // ── Formatting helpers ──────────────────────────────────────────

    private string? Media(string? stored) => _actions.ResolveMedia(stored);
    private string N(int value) => value.ToString("N0", _loc.Culture);
    private string Pct(int value) => string.Format(_loc.Culture, "{0}%", value);

    internal static string ProgressTone(int percent) => percent >= 75 ? "Green" : percent >= 25 ? "Amber" : "Blue";

    private string StatusText(GameStatus status) => _loc[$"Status_{status}"];

    internal static string StatusTone(GameStatus status) => status switch
    {
        GameStatus.Playing => "Amber",
        GameStatus.Completed => "Green",
        GameStatus.Abandoned => "Red",
        _ => "Blue",
    };
}
