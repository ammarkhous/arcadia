using System.Globalization;
using Arcadia.App.Services;
using Arcadia.Core.Games;
using Arcadia.Core.Localization;
using Arcadia.UI.Artwork;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.Dialogs;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

// ═══════════════════════════ Confirm ═══════════════════════════

public sealed partial class ConfirmDialogViewModel : DialogViewModel
{
    public ConfirmDialogViewModel(string title, string message, string confirmText, string cancelText)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
    }

    public override string Icon => "Warning";
    public override double DialogWidth => 460;
    public string Message { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }

    [RelayCommand] private void Confirm() => Close(true);
    [RelayCommand] private void Cancel() => Close(false);
}

// ═══════════════════════════ Game editor ═══════════════════════════

/// <summary>
/// Add/edit form. Numbers are edited as text so invalid input can be explained instead of silently rejected.
/// Picked images are only recorded here; the caller imports them on save.
/// </summary>
public sealed partial class GameEditorViewModel : DialogViewModel
{
    private readonly ILocalizer _loc;
    private readonly IDialogService _dialogs;
    private readonly Func<string?, string?> _resolveMedia;
    private readonly Game _original;

    public GameEditorViewModel(Game? game, ILocalizer loc, IDialogService dialogs, Func<string?, string?> resolveMedia)
    {
        _loc = loc;
        _dialogs = dialogs;
        _resolveMedia = resolveMedia;
        _original = game ?? new Game { Title = string.Empty, ArtworkTheme = "mountains-dusk", ArtworkSeed = Random.Shared.Next(1, 10_000) };
        IsNew = game == null;
        Title = IsNew ? loc["Editor_AddTitle"] : loc["Editor_EditTitle"];

        StatusOptions = Enum.GetValues<GameStatus>().Select(s => new Option<GameStatus>(s, loc[$"Status_{s}"])).ToList();
        PriorityOptions = Enum.GetValues<BacklogPriority>().Reverse().Select(p => new Option<BacklogPriority>(p, loc[$"Priority_{p}"])).ToList();
        SceneOptions = SceneThemes.Names.Where(n => !n.StartsWith("arcadia-") && !n.StartsWith("sidebar-")).Order().ToList();

        var g = _original;
        GameTitle = g.Title;
        Platform = g.Platform;
        Status = StatusOptions.First(o => o.Value == g.Status);
        Priority = PriorityOptions.First(o => o.Value == g.Priority);
        Tags = string.Join(", ", g.TagList);
        Completion = g.CompletionPercent;
        PlaytimeText = Format(g.PlaytimeHours, loc);
        EstimatedText = g.EstimatedHours is { } est ? Format(est, loc) : string.Empty;
        AchievementsUnlockedText = g.AchievementsTotal > 0 || g.AchievementsUnlocked > 0 ? g.AchievementsUnlocked.ToString(loc.Culture) : string.Empty;
        AchievementsTotalText = g.AchievementsTotal > 0 ? g.AchievementsTotal.ToString(loc.Culture) : string.Empty;
        Objective = g.CurrentObjective ?? string.Empty;
        Description = g.Description ?? string.Empty;
        LaunchTarget = g.LaunchTarget ?? string.Empty;
        InstallDirectory = g.InstallDirectory ?? string.Empty;
        foreach (var exe in Arcadia.Core.Tracking.TrackableGame.ParseExecutables(g.Executables)) Executables.Add(exe);
        AutoGamingMode = g.AutoGamingMode;
        Scene = SceneOptions.Contains(g.ArtworkTheme) ? g.ArtworkTheme : SceneOptions[0];
        CoverPreview = resolveMedia(g.CoverImagePath);
        HeroPreview = resolveMedia(g.HeroImagePath);
    }

    public override string Icon => IsNew ? "Plus" : "Journal";
    public override double DialogWidth => 760;
    public bool IsNew { get; }
    public int Seed => _original.ArtworkSeed;

    public IReadOnlyList<string> PlatformOptions => Platforms.Common;
    public IReadOnlyList<Option<GameStatus>> StatusOptions { get; }
    public IReadOnlyList<Option<BacklogPriority>> PriorityOptions { get; }
    public IReadOnlyList<string> SceneOptions { get; }

    [ObservableProperty] private string _gameTitle = string.Empty;
    [ObservableProperty] private string _platform = "PC";
    [ObservableProperty] private Option<GameStatus> _status = null!;
    [ObservableProperty] private Option<BacklogPriority> _priority = null!;
    [ObservableProperty] private string _tags = string.Empty;
    [ObservableProperty] private double _completion;
    [ObservableProperty] private string _playtimeText = "0";
    [ObservableProperty] private string _estimatedText = string.Empty;
    [ObservableProperty] private string _achievementsUnlockedText = string.Empty;
    [ObservableProperty] private string _achievementsTotalText = string.Empty;
    [ObservableProperty] private string _objective = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _launchTarget = string.Empty;
    [ObservableProperty] private string _installDirectory = string.Empty;

    // ── Phase 9: executable association (primary first) and per-game Gaming Mode ──
    public System.Collections.ObjectModel.ObservableCollection<string> Executables { get; } = [];
    [ObservableProperty] private bool _autoGamingMode;
    [ObservableProperty] private IReadOnlyList<string> _runningExecutables = [];
    [ObservableProperty] private string? _pickedRunning;

    [RelayCommand]
    private void BrowseExecutable()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe", InitialDirectory = Directory.Exists(InstallDirectory) ? InstallDirectory : null };
        if (dlg.ShowDialog() == true) AddExecutable(dlg.FileName);
    }

    /// <summary>Lists running programs (names only) so the game's process can be picked while it runs.</summary>
    [RelayCommand]
    private void DetectRunning() => RunningExecutables = Arcadia.App.Services.AutomationService.ProcessNames().Where(n => n.EndsWith(".exe")).Order().ToList();

    partial void OnPickedRunningChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value)) AddExecutable(value);
    }

    private void AddExecutable(string path)
    {
        // a file name is enough (works with any launcher); a full path pins one exact program (emulators)
        var entry = Path.GetFileName(path).Equals(path, StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(path) ? Path.GetFileName(path) : path;
        if (!entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Executables.Contains(entry, StringComparer.OrdinalIgnoreCase)) return;
        Executables.Add(entry);
    }

    [RelayCommand] private void RemoveExecutable(string? exe) { if (exe != null) Executables.Remove(exe); }

    [RelayCommand]
    private void UseFullPath(string? exe)
    {
        if (exe == null || Path.IsPathFullyQualified(exe) || !Directory.Exists(InstallDirectory)) return;
        var found = Directory.EnumerateFiles(InstallDirectory, exe, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }).FirstOrDefault();
        if (found != null) Executables[Executables.IndexOf(exe)] = found;
    }
    [ObservableProperty] private string _scene = "mountains-dusk";

    /// <summary>Absolute path shown in the preview (existing media or a newly picked file).</summary>
    [ObservableProperty] private string? _coverPreview;
    [ObservableProperty] private string? _heroPreview;

    [ObservableProperty] private string? _titleError;
    [ObservableProperty] private string? _numberError;
    [ObservableProperty] private string? _achievementsError;
    [ObservableProperty] private string? _launchError;

    /// <summary>Newly picked source files, imported by the caller on save.</summary>
    public string? NewCoverSource { get; private set; }
    public string? NewHeroSource { get; private set; }
    public bool CoverRemoved { get; private set; }
    public bool HeroRemoved { get; private set; }

    /// <summary>The edited game (valid after the dialog is accepted). Media paths are left for the caller.</summary>
    public Game? Result { get; private set; }

    public string CompletionText => $"{Math.Round(Completion)}%";
    partial void OnCompletionChanged(double value) => OnPropertyChanged(nameof(CompletionText));

    [RelayCommand]
    private void ChooseCover()
    {
        var file = _dialogs.PickImages(multiple: false).FirstOrDefault();
        if (file == null) return;
        NewCoverSource = file;
        CoverRemoved = false;
        CoverPreview = file;
    }

    [RelayCommand]
    private void RemoveCover()
    {
        NewCoverSource = null;
        CoverRemoved = true;
        CoverPreview = null;
    }

    [RelayCommand]
    private void ChooseHero()
    {
        var file = _dialogs.PickImages(multiple: false).FirstOrDefault();
        if (file == null) return;
        NewHeroSource = file;
        HeroRemoved = false;
        HeroPreview = file;
    }

    [RelayCommand]
    private void RemoveHero()
    {
        NewHeroSource = null;
        HeroRemoved = true;
        HeroPreview = null;
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var folder = _dialogs.PickFolder(InstallDirectory);
        if (folder != null) InstallDirectory = folder;
    }

    [RelayCommand]
    private void BrowseProgram()
    {
        var file = _dialogs.PickProgram();
        if (file != null) LaunchTarget = file;
    }

    [RelayCommand]
    private void Save()
    {
        TitleError = string.IsNullOrWhiteSpace(GameTitle) ? _loc["Editor_ErrTitle"] : null;

        bool numbersOk = TryNumber(PlaytimeText, _loc.Culture, out var playtime)
                         & TryNumber(EstimatedText, _loc.Culture, out var estimated)
                         & TryNumber(AchievementsUnlockedText, _loc.Culture, out var unlocked)
                         & TryNumber(AchievementsTotalText, _loc.Culture, out var total);
        NumberError = numbersOk ? null : _loc["Editor_ErrNumber"];
        AchievementsError = numbersOk && total > 0 && unlocked > total ? _loc["Editor_ErrAchievements"] : null;
        LaunchError = !string.IsNullOrWhiteSpace(LaunchTarget) && !LaunchTargets.IsAllowed(LaunchTarget) ? _loc["Editor_ErrLaunch"] : null;
        if (TitleError != null || NumberError != null || AchievementsError != null || LaunchError != null) return;

        Result = new Game
        {
            Id = _original.Id,
            Title = GameTitle.Trim(),
            Platform = string.IsNullOrWhiteSpace(Platform) ? "PC" : Platform.Trim(),
            Status = Status.Value,
            Priority = Priority.Value,
            Tags = Tags,
            CompletionPercent = (int)Math.Round(Completion),
            PlaytimeHours = playtime,
            EstimatedHours = string.IsNullOrWhiteSpace(EstimatedText) ? null : estimated,
            AchievementsUnlocked = (int)unlocked,
            AchievementsTotal = (int)total,
            CurrentObjective = Objective,
            Description = Description,
            LaunchTarget = LaunchTarget,
            InstallDirectory = InstallDirectory,
            Executables = Executables.Count == 0 ? null : string.Join("; ", Executables),
            AutoGamingMode = AutoGamingMode,
            ArtworkTheme = Scene,
            ArtworkSeed = _original.ArtworkSeed,
            CoverImagePath = CoverRemoved ? null : _original.CoverImagePath,
            CoverThumbnailPath = CoverRemoved ? null : _original.CoverThumbnailPath,
            HeroImagePath = HeroRemoved ? null : _original.HeroImagePath,
            IsFeatured = _original.IsFeatured,
        };
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);

    /// <summary>Media that the edit replaced or removed (the caller deletes these after a successful save).</summary>
    public IEnumerable<string?> ObsoleteMedia()
    {
        if (CoverRemoved || NewCoverSource != null) { yield return _original.CoverImagePath; yield return _original.CoverThumbnailPath; }
        if (HeroRemoved || NewHeroSource != null) yield return _original.HeroImagePath;
    }

    private static string Format(double value, ILocalizer loc) => value.ToString("0.##", loc.Culture);

    /// <summary>Empty is allowed (0). Accepts the UI culture's format, the invariant format, and the OS regional format.</summary>
    internal static bool TryNumber(string text, CultureInfo culture, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        text = text.Trim();
        bool parsed = double.TryParse(text, NumberStyles.Float, culture, out value)
                      || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                      || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        return parsed && value >= 0 && value < 100_000;
    }
}

// ═══════════════════════════ Log session ═══════════════════════════

public sealed partial class LogSessionViewModel : DialogViewModel
{
    private readonly ILocalizer _loc;

    public LogSessionViewModel(Game game, ILocalizer loc, TimeProvider time)
    {
        _loc = loc;
        Title = loc["Session_Title"];
        GameTitle = game.Title;
        Theme = game.ArtworkTheme;
        Seed = game.ArtworkSeed;

        var today = time.GetLocalNow().Date;
        DayOptions = Enumerable.Range(0, 14).Select(i =>
        {
            var day = today.AddDays(-i);
            var label = i switch
            {
                0 => loc["Session_Today"],
                1 => loc["Session_Yesterday"],
                _ => day.ToString("dddd, d MMM", loc.Culture),
            };
            return new Option<DateTime>(day, label);
        }).ToList();
        Day = DayOptions[0];
        HourOptions = Enumerable.Range(0, 13).ToList();
        MinuteOptions = Enumerable.Range(0, 12).Select(i => i * 5).ToList();
        Hours = 1;
        Minutes = 0;
        Completion = game.CompletionPercent;
        _endLocalNow = time.GetLocalNow();
    }

    private readonly DateTimeOffset _endLocalNow;

    public override string Icon => "Clock";
    public override double DialogWidth => 520;
    public string GameTitle { get; }
    public string Theme { get; }
    public int Seed { get; }
    public IReadOnlyList<Option<DateTime>> DayOptions { get; }
    public IReadOnlyList<int> HourOptions { get; }
    public IReadOnlyList<int> MinuteOptions { get; }

    [ObservableProperty] private Option<DateTime> _day = null!;
    [ObservableProperty] private int _hours;
    [ObservableProperty] private int _minutes;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private bool _updateProgress;
    [ObservableProperty] private double _completion;
    [ObservableProperty] private string _objective = string.Empty;
    [ObservableProperty] private string? _durationError;

    public string CompletionText => $"{Math.Round(Completion)}%";
    partial void OnCompletionChanged(double value) => OnPropertyChanged(nameof(CompletionText));

    public int DurationMinutes => Hours * 60 + Minutes;

    /// <summary>Start time in UTC: sessions logged "today" end now; earlier days end in the evening.</summary>
    public DateTime StartedAtUtc
    {
        get
        {
            var end = Day.Value.Date == _endLocalNow.Date ? _endLocalNow.DateTime : Day.Value.Date.AddHours(21);
            return DateTime.SpecifyKind(end.AddMinutes(-DurationMinutes), DateTimeKind.Local).ToUniversalTime();
        }
    }

    [RelayCommand]
    private void Save()
    {
        DurationError = DurationMinutes <= 0 ? _loc["Session_ErrDuration"] : null;
        if (DurationError == null) Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}

// ═══════════════════════════ Screenshot details ═══════════════════════════

public sealed partial class ScreenshotEditorViewModel : DialogViewModel
{
    private readonly ILocalizer _loc;

    public ScreenshotEditorViewModel(Screenshot shot, string? imagePath, ILocalizer loc)
    {
        _loc = loc;
        Title = loc["Shot_Title"];
        ImagePath = imagePath;
        Theme = shot.ArtworkTheme;
        Seed = shot.ArtworkSeed;
        ShotTitle = shot.Title;
        Location = shot.Location ?? string.Empty;
        Note = shot.Note ?? string.Empty;
        Tags = shot.Tags;
    }

    public override string Icon => "Image";
    public override double DialogWidth => 560;
    public string? ImagePath { get; }
    public string Theme { get; }
    public int Seed { get; }

    [ObservableProperty] private string _shotTitle = string.Empty;
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _tags = string.Empty;
    [ObservableProperty] private string? _titleError;

    [RelayCommand]
    private void Save()
    {
        TitleError = string.IsNullOrWhiteSpace(ShotTitle) ? _loc["Editor_ErrTitle"] : null;
        if (TitleError == null) Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}
