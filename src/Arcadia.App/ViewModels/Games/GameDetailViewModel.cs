using System.Collections.ObjectModel;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Home;
using Arcadia.Core.Formatting;
using Arcadia.Core.Games;
using Arcadia.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Games;

public enum GameDetailTab { Overview, Sessions, Journal, Screenshots }

public sealed record SessionItemViewModel(GameSession Session, string Day, string Time, string Duration, string? Note);

public sealed record JournalItemViewModel(JournalEntry Entry, string Text, string When, bool IsPinned);

public sealed record ScreenshotItemViewModel(
    Screenshot Shot,
    string Title,
    string? Location,
    string? Note,
    string Date,
    string? ImagePath,
    string? ThumbnailPath,
    string Theme,
    int Seed);

public sealed record ActivityBarViewModel(double Hours, string Label, string Tooltip);

public sealed partial class GameDetailViewModel(
    IGameCatalog catalog,
    GameActions actions,
    ILocalizer loc,
    TimeProvider time,
    ILogger<GameDetailViewModel> logger) : ObservableObject
{
    private Game? _game;

    public int GameId { get; private set; }
    public bool IsMissing { get; private set; }

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _platform = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> _tags = [];
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _statusTone = "Blue";
    [ObservableProperty] private string? _description;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _progressTone = "Green";
    [ObservableProperty] private string _playtime = string.Empty;
    [ObservableProperty] private string _sessionCount = "0";
    [ObservableProperty] private string _achievements = "—";
    [ObservableProperty] private string _lastPlayed = "—";
    [ObservableProperty] private string _added = string.Empty;
    [ObservableProperty] private string _estimated = "—";
    [ObservableProperty] private string _objective = "—";
    [ObservableProperty] private string? _launchTarget;
    [ObservableProperty] private string? _installDirectory;
    [ObservableProperty] private bool _isFeatured;
    [ObservableProperty] private string _theme = "mountains-dusk";
    [ObservableProperty] private int _seed;
    [ObservableProperty] private string? _heroImagePath;
    [ObservableProperty] private string? _coverImagePath;
    [ObservableProperty] private string? _boxArtPath;
    [ObservableProperty] private string? _logoPath;
    [ObservableProperty] private string? _developer;
    [ObservableProperty] private string? _publisher;
    [ObservableProperty] private string? _released;
    [ObservableProperty] private bool _isMatchedOnline;
    [ObservableProperty] private string _primaryActionText = string.Empty;

    [ObservableProperty] private GameDetailTab _selectedTab = GameDetailTab.Overview;
    [ObservableProperty] private string _loggedSummary = string.Empty;
    [ObservableProperty] private IReadOnlyList<ActivityBarViewModel> _activity = [];
    [ObservableProperty] private double _activityMax = 1;
    [ObservableProperty] private string _newNoteText = string.Empty;
    [ObservableProperty] private ScreenshotItemViewModel? _viewerItem;

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];
    public ObservableCollection<JournalItemViewModel> Journal { get; } = [];
    public ObservableCollection<ScreenshotItemViewModel> Screenshots { get; } = [];

    public bool IsOverviewTab { get => SelectedTab == GameDetailTab.Overview; set { if (value) SelectedTab = GameDetailTab.Overview; } }
    public bool IsSessionsTab { get => SelectedTab == GameDetailTab.Sessions; set { if (value) SelectedTab = GameDetailTab.Sessions; } }
    public bool IsJournalTab { get => SelectedTab == GameDetailTab.Journal; set { if (value) SelectedTab = GameDetailTab.Journal; } }
    public bool IsScreenshotsTab { get => SelectedTab == GameDetailTab.Screenshots; set { if (value) SelectedTab = GameDetailTab.Screenshots; } }

    partial void OnSelectedTabChanged(GameDetailTab value)
    {
        OnPropertyChanged(nameof(IsOverviewTab));
        OnPropertyChanged(nameof(IsSessionsTab));
        OnPropertyChanged(nameof(IsJournalTab));
        OnPropertyChanged(nameof(IsScreenshotsTab));
    }

    public async Task LoadAsync(int id)
    {
        GameId = id;
        IsLoading = true;
        try
        {
            _game = await Task.Run(() => catalog.GetGameDetailAsync(id));
            IsMissing = _game == null;
            if (_game != null) Build(_game);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading game {Id} failed", id);
            IsMissing = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Build(Game g)
    {
        var now = time.GetUtcNow().UtcDateTime;
        Title = g.Title;
        Platform = g.Platform;
        Tags = g.TagList;
        StatusText = loc[$"Status_{g.Status}"];
        StatusTone = HomeViewModel.StatusTone(g.Status);
        Description = g.Description;
        Progress = g.CompletionPercent;
        ProgressText = $"{g.CompletionPercent}%";
        ProgressTone = HomeViewModel.ProgressTone(g.CompletionPercent);
        Playtime = RelativeTime.Hours(g.PlaytimeHours, loc);
        SessionCount = g.Sessions.Count.ToString(loc.Culture);
        Achievements = g.AchievementsTotal > 0 ? $"{g.AchievementsUnlocked} / {g.AchievementsTotal}" : "—";
        LastPlayed = g.LastPlayedAtUtc is { } lp ? RelativeTime.Format(lp, now, loc) : loc["Home_NeverPlayed"];
        Added = g.AddedAtUtc.ToLocalTime().ToString("d MMM yyyy", loc.Culture);
        Estimated = g.EstimatedHours is { } est ? RelativeTime.Hours(est, loc) : "—";
        Objective = string.IsNullOrWhiteSpace(g.CurrentObjective) ? "—" : g.CurrentObjective;
        LaunchTarget = g.LaunchTarget;
        InstallDirectory = g.InstallDirectory;
        IsFeatured = g.IsFeatured;
        Theme = g.ArtworkTheme;
        Seed = g.ArtworkSeed;
        HeroImagePath = actions.ResolveMedia(g.HeroImagePath) ?? actions.ResolveMedia(g.CoverImagePath);
        CoverImagePath = actions.ResolveMedia(g.CoverImagePath) ?? actions.ResolveMedia(g.HeroImagePath);
        BoxArtPath = actions.ResolveMedia(g.BoxArtPath);
        LogoPath = actions.ResolveMedia(g.LogoImagePath);
        Developer = g.Developer;
        Publisher = g.Publisher;
        Released = g.ReleaseDate?.ToString("d MMM yyyy", loc.Culture);
        IsMatchedOnline = g.IgdbId != null;
        PrimaryActionText = g.PlaytimeHours > 0 ? loc["Home_ContinuePlaying"] : loc["Home_StartPlaying"];

        Sessions.Clear();
        foreach (var s in g.Sessions)
        {
            var local = s.StartedAtUtc.ToLocalTime();
            Sessions.Add(new SessionItemViewModel(s, local.ToString("ddd, d MMM yyyy", loc.Culture), local.ToString("t", loc.Culture),
                RelativeTime.Hours(s.Hours, loc), s.Note));
        }
        LoggedSummary = loc.Format("Game_LoggedTotal", RelativeTime.Hours(g.Sessions.Sum(s => s.Hours), loc), g.Sessions.Count);
        Activity = BuildActivity(g.Sessions, now);
        ActivityMax = Math.Max(1, Activity.Max(a => a.Hours));

        Journal.Clear();
        foreach (var j in g.JournalEntries)
            Journal.Add(new JournalItemViewModel(j, j.Text, RelativeTime.Format(j.CreatedAtUtc, now, loc), j.IsPinned));

        Screenshots.Clear();
        foreach (var s in g.Screenshots)
        {
            Screenshots.Add(new ScreenshotItemViewModel(s, s.Title, s.Location, s.Note, s.CapturedAtUtc.ToLocalTime().ToString("d MMM yyyy", loc.Culture),
                actions.ResolveMedia(s.ImagePath), actions.ResolveMedia(s.ThumbnailPath ?? s.ImagePath), s.ArtworkTheme, s.ArtworkSeed));
        }
        if (ViewerItem != null) ViewerItem = Screenshots.FirstOrDefault(x => x.Shot.Id == ViewerItem.Shot.Id);
    }

    /// <summary>Hours per week for the last 12 weeks (oldest first).</summary>
    private IReadOnlyList<ActivityBarViewModel> BuildActivity(IEnumerable<GameSession> sessions, DateTime nowUtc)
    {
        const int weeks = 12;
        var today = nowUtc.ToLocalTime().Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
        var firstWeek = weekStart.AddDays(-7 * (weeks - 1));
        var hours = new double[weeks];
        foreach (var s in sessions)
        {
            var day = s.StartedAtUtc.ToLocalTime().Date;
            int index = (int)Math.Floor((day - firstWeek).TotalDays / 7);
            if (index is >= 0 and < weeks) hours[index] += s.Hours;
        }
        return Enumerable.Range(0, weeks).Select(i =>
        {
            var start = firstWeek.AddDays(7 * i);
            var label = start.ToString("d MMM", loc.Culture);
            return new ActivityBarViewModel(hours[i], i % 2 == 0 ? label : string.Empty, $"{label}: {RelativeTime.Hours(hours[i], loc)}");
        }).ToList();
    }

    // ── actions ─────────────────────────────────────────────────────

    [RelayCommand] private void ShowTab(GameDetailTab tab) => SelectedTab = tab;

    [RelayCommand]
    private void Launch()
    {
        if (_game != null) actions.Launch(_game);
    }

    [RelayCommand] private Task LogSessionAsync() => actions.LogSessionAsync(GameId);
    [RelayCommand] private Task EditAsync() => actions.EditAsync(GameId);
    [RelayCommand] private Task FindOnlineAsync() => actions.FindOnlineAsync(GameId);
    [RelayCommand] private Task SetFeaturedAsync() => actions.SetFeaturedAsync(GameId, Title);
    [RelayCommand] private Task DeleteAsync() => actions.DeleteGameAsync(GameId, Title);
    [RelayCommand] private Task DeleteSessionAsync(SessionItemViewModel? item) => item == null ? Task.CompletedTask : actions.DeleteSessionAsync(item.Session);

    [RelayCommand]
    private async Task AddNoteAsync()
    {
        if (string.IsNullOrWhiteSpace(NewNoteText)) return;
        var text = NewNoteText;
        NewNoteText = string.Empty;
        await actions.AddJournalEntryAsync(GameId, text);
    }

    [RelayCommand] private Task TogglePinAsync(JournalItemViewModel? item) => item == null ? Task.CompletedTask : actions.SetJournalPinnedAsync(item.Entry, !item.IsPinned);
    [RelayCommand] private Task DeleteNoteAsync(JournalItemViewModel? item) => item == null ? Task.CompletedTask : actions.DeleteJournalEntryAsync(item.Entry);

    [RelayCommand] private Task ImportScreenshotsAsync() => actions.ImportScreenshotsAsync(GameId);

    /// <summary>Files dropped onto the page.</summary>
    [RelayCommand]
    private async Task ImportFilesAsync(string[]? files)
    {
        if (files == null) return;
        if (await actions.ImportScreenshotFilesAsync(GameId, files) > 0) SelectedTab = GameDetailTab.Screenshots;
    }
    [RelayCommand] private Task EditScreenshotAsync(ScreenshotItemViewModel? item) => item == null ? Task.CompletedTask : actions.EditScreenshotAsync(item.Shot);

    [RelayCommand]
    private async Task DeleteScreenshotAsync(ScreenshotItemViewModel? item)
    {
        if (item == null) return;
        if (await actions.DeleteScreenshotAsync(item.Shot) && ViewerItem?.Shot.Id == item.Shot.Id) ViewerItem = null;
    }

    [RelayCommand] private void OpenViewer(ScreenshotItemViewModel? item) => ViewerItem = item;
    [RelayCommand] private void CloseViewer() => ViewerItem = null;
    [RelayCommand] private void NextShot() => Step(+1);
    [RelayCommand] private void PreviousShot() => Step(-1);

    private void Step(int delta)
    {
        if (ViewerItem == null || Screenshots.Count == 0) return;
        var index = Screenshots.IndexOf(ViewerItem);
        ViewerItem = Screenshots[(index + delta + Screenshots.Count) % Screenshots.Count];
    }
}
