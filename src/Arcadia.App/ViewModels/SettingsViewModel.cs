using System.Reflection;
using Arcadia.App.Localization;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Home;
using Arcadia.Core.Localization;
using Arcadia.Core.Platform;
using Arcadia.Core.Settings;
using Arcadia.Data;
using Arcadia.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IActivatable
{
    private readonly ISettingsService _settings;
    private readonly LocalizationManager _loc;
    private readonly IShellLauncher _launcher;
    private readonly INotificationService _notifications;
    private readonly DatabaseInitializer _database;
    private readonly HomeViewModel _home;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly bool _initializing;
    private readonly IDialogService _dialogs;
    private readonly Arcadia.Infrastructure.Games.IGameInstallLocator _locator;
    private readonly Arcadia.Core.Security.ISecretStore _secrets;
    private readonly MetadataService _metadata;

    public SettingsViewModel(
        ISettingsService settings,
        ILocalizer loc,
        IAppPaths paths,
        IShellLauncher launcher,
        INotificationService notifications,
        DatabaseInitializer database,
        HomeViewModel home,
        IDialogService dialogs,
        Arcadia.Infrastructure.Games.IGameInstallLocator locator,
        Arcadia.Core.Security.ISecretStore secrets,
        MetadataService metadata,
        ViewModels.Finance.FinanceActions financeActions,
        OracleHost oracle,
        MaintenanceViewModel maintenance,
        ILogger<SettingsViewModel> logger)
    {
        Maintenance = maintenance;
        _oracle = oracle;
        _finance = financeActions.Ctx;
        _financeActions = financeActions;
        _secrets = secrets;
        _metadata = metadata;
        _dialogs = dialogs;
        _locator = locator;
        _settings = settings;
        _loc = (LocalizationManager)loc;
        _launcher = launcher;
        _notifications = notifications;
        _database = database;
        _home = home;
        _logger = logger;

        _initializing = true;
        var s = settings.Current;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Tag == s.Language) ?? Languages[0];
        AnimationsEnabled = s.AnimationsEnabled;
        StartMaximized = s.Startup.StartMaximized;
        AllowOnlineMetadata = s.Privacy.AllowOnlineMetadata;
        TrackingEnabled = s.Tracking.Enabled;
        DetectExternal = s.Tracking.DetectExternalLaunches;
        MinimumSession = MinimumSessionOptions.FirstOrDefault(o => o.Value == s.Tracking.MinimumSessionMinutes) ?? MinimumSessionOptions[1];
        CapturesEnabled = s.Captures.WatchEnabled;
        var org = s.Organizer;
        DayStartHour = StartHours.FirstOrDefault(o => o.Value == org.DayStartHour) ?? StartHours[7];
        DayEndHour = EndHours.FirstOrDefault(o => o.Value == org.DayEndHour) ?? EndHours[^3];
        DefaultReminder = ReminderDefaults.FirstOrDefault(o => o.Value == org.DefaultReminderMinutes) ?? ReminderDefaults[0];
        ReminderSound = org.ReminderSound;
        HomeShowToday = s.Home.ShowToday;
        HomeShowTasks = s.Home.ShowTasks;
        HomeShowDeadline = s.Home.ShowDeadline;
        HomeShowPinnedNote = s.Home.ShowPinnedNote;
        TravelOnlineTiles = s.Travel.OnlineMapTiles;
        TravelOnlineSearch = s.Travel.OnlineSearch;
        TileStyle = TileStyles.FirstOrDefault(o => o.Value == s.Travel.TileStyle) ?? TileStyles[0];
        CustomTileUrl = s.Travel.CustomTileUrl ?? string.Empty;
        CustomTileAttribution = s.Travel.CustomTileAttribution ?? string.Empty;
        FinanceNumberFormat = NumberFormats.FirstOrDefault(o => o.Value == s.Finance.NumberFormat) ?? NumberFormats[0];
        FinanceCurrency = s.Finance.DefaultCurrency;
        FinancePrivacyMode = s.Finance.PrivacyMode;
        FinanceOnCalendar = s.Organizer.ShowFinance;
        var o = s.Oracle;
        OracleEnabled = o.Enabled;
        OracleShortcut = OracleHost.Shortcuts.Contains(o.Shortcut) ? o.Shortcut : OracleHost.Shortcuts[0];
        OracleKeepHistory = o.KeepHistory;
        OracleHistoryLimit = OracleHistoryLimits.FirstOrDefault(x => x.Value == o.HistoryLimit) ?? OracleHistoryLimits[1];
        OracleLanguage = OracleLanguages.FirstOrDefault(x => x.Value == o.Language) ?? OracleLanguages[0];
        OracleConfirmation = OracleConfirmations.FirstOrDefault(x => x.Value == o.Confirmation) ?? OracleConfirmations[0];
        OracleSuggestions = o.ShowSuggestions;
        OracleDiagnostics = o.DeveloperDiagnostics;
        OracleCollectUnknown = o.CollectUnknown;
        var w = s.Windows;
        try { StartWithWindows = StartupRegistration.IsEnabled(); } catch (Exception ex) { logger.LogWarning(ex, "Reading the startup entry failed"); }
        ShowTrayIcon = w.ShowTrayIcon;
        MinimizeToTray = w.MinimizeToTray;
        StartMinimized = w.StartMinimized;
        RestoreLastWorkspace = w.RestoreLastWorkspace;
        var pf = s.Performance;
        PerformanceMode = PerformanceModes.FirstOrDefault(x => x.Value == pf.Mode) ?? PerformanceModes[0];
        PauseIndexingInGames = pf.PauseIndexingInGames;
        PauseThumbnailsInGames = pf.PauseThumbnailsInGames;
        ReduceMonitoringInGames = pf.ReduceMonitoringInGames;
        QuietNotificationsInGames = pf.QuietNotificationsInGames;
        ReduceAnimationsInPowerSaver = pf.ReduceAnimationsInPowerSaver;
        TrimCachesUnderPressure = pf.TrimCachesUnderPressure;
        var k = s.Shortcuts;
        ShortcutPalette = k.Palette;
        ShortcutCapture = k.QuickCapture;
        ShortcutGaming = k.GamingMode;
        ShortcutGlobal = k.GlobalShowArcadia;
        var n = s.Notifications;
        NotifyInfo = NotifyModes.FirstOrDefault(x => x.Value == n.Info) ?? NotifyModes[0];
        NotifyReminder = NotifyModes.FirstOrDefault(x => x.Value == n.Reminder) ?? NotifyModes[0];
        NotifyAutomation = NotifyModes.FirstOrDefault(x => x.Value == n.Automation) ?? NotifyModes[0];
        NotifyGaming = NotifyModes.FirstOrDefault(x => x.Value == n.Gaming) ?? NotifyModes[0];
        NotifyWarning = NotifyModes.FirstOrDefault(x => x.Value == n.Warning) ?? NotifyModes[0];
        RefreshKeyState();
        foreach (var folder in s.Captures.Folders) CaptureFolders.Add(folder);
        _initializing = false;

        DataFolder = paths.DataDirectory;
        SettingsFolder = paths.SettingsDirectory;
        LogsFolder = paths.LogsDirectory;
        var version = Diagnostics.AppInfo.Version;
        VersionText = _loc.Format("Settings_Version", version);
        loc.CultureChanged += (_, _) => VersionText = _loc.Format("Settings_Version", version);
    }

    public IReadOnlyList<LanguageOption> Languages => LocalizationManager.SupportedLanguages;

    public IReadOnlyList<Dialogs.Option<int>> MinimumSessionOptions =>
        [.. new[] { 1, 2, 5, 10 }.Select(m => new Dialogs.Option<int>(m, _loc.Format("Settings_Minutes", m)))];

    public System.Collections.ObjectModel.ObservableCollection<string> CaptureFolders { get; } = [];

    [ObservableProperty] private bool _trackingEnabled;
    [ObservableProperty] private bool _detectExternal;
    [ObservableProperty] private Dialogs.Option<int>? _minimumSession;
    [ObservableProperty] private bool _capturesEnabled;

    partial void OnTrackingEnabledChanged(bool value) { if (!_initializing) { _settings.Current.Tracking.Enabled = value; Save(); } }
    partial void OnDetectExternalChanged(bool value) { if (!_initializing) { _settings.Current.Tracking.DetectExternalLaunches = value; Save(); } }
    partial void OnMinimumSessionChanged(Dialogs.Option<int>? value) { if (!_initializing && value != null) { _settings.Current.Tracking.MinimumSessionMinutes = value.Value; Save(); } }
    partial void OnCapturesEnabledChanged(bool value) { if (!_initializing) { _settings.Current.Captures.WatchEnabled = value; Save(); } }

    // ── Travel maps & place search ────────────────────────────────

    public IReadOnlyList<Dialogs.Option<string>> TileStyles =>
    [
        new("osm-dark", _loc["Settings_TilesDark"]),
        new("osm", _loc["Settings_TilesOriginal"]),
        new("custom", _loc["Settings_TilesCustom"]),
    ];

    [ObservableProperty] private bool _travelOnlineTiles;
    [ObservableProperty] private bool _travelOnlineSearch;
    [ObservableProperty] private Dialogs.Option<string>? _tileStyle;
    [ObservableProperty] private string _customTileUrl = string.Empty;
    [ObservableProperty] private string _customTileAttribution = string.Empty;
    [ObservableProperty] private bool _mapKeySaved;
    [ObservableProperty] private string? _mapTilesStatus;

    private bool _applyingTiles;
    public bool IsCustomTiles => TileStyle?.Value == "custom";

    // ── Finance (Phase 7) ──
    private readonly FinanceContext _finance;

    /// <summary>A readable finance.json in the snapshot (the database copy holds the same data). Asked each time, off by default.</summary>

    public IReadOnlyList<Dialogs.Option<string>> NumberFormats => _numberFormats ??=
    [
        new("windows", _loc.Format("Settings_FinWindows", new Arcadia.Core.Finance.MoneyFormatter(FinanceFormats.Regional()).Format(1234.56m, "EUR"))),
        new("point", "1,234.56"),
        new("comma", "1.234,56"),
        new("space", "1 234,56"),
        new("swiss", "1'234.56"),
    ];
    private IReadOnlyList<Dialogs.Option<string>>? _numberFormats;

    public IReadOnlyList<string> CurrencyOptions => Arcadia.Core.Finance.Currency.Common;
    public System.Collections.ObjectModel.ObservableCollection<ViewModels.Finance.FinanceOption> FinanceAccounts { get; } = [];

    [ObservableProperty] private Dialogs.Option<string>? _financeNumberFormat;
    [ObservableProperty] private string _financeCurrency = "EUR";
    [ObservableProperty] private ViewModels.Finance.FinanceOption? _financeDefaultAccount;
    [ObservableProperty] private bool _financePrivacyMode;
    [ObservableProperty] private bool _financeOnCalendar;
    [ObservableProperty] private string? _financeExample;
    private bool _loadingAccounts;

    /// <summary>Backup &amp; recovery, diagnostics and maintenance (Phase 10).</summary>
    public MaintenanceViewModel Maintenance { get; }

    public async Task ActivateAsync()
    {
        _ = Maintenance.ActivateAsync();
        FinancePrivacyMode = FinancePrivacy.Instance.IsHidden;
        try
        {
            await _finance.Ready;
            var accounts = await _finance.Store.GetAccountBalancesAsync();
            _loadingAccounts = true;
            FinanceAccounts.Clear();
            FinanceAccounts.Add(new ViewModels.Finance.FinanceOption(null, _loc["Settings_FinNoDefault"]));
            foreach (var a in accounts) FinanceAccounts.Add(new ViewModels.Finance.FinanceOption(a.Account.Id, $"{a.Account.Name}  ·  {a.Account.Currency}", a.Account.Icon, a.Account.Accent));
            FinanceDefaultAccount = FinanceAccounts.FirstOrDefault(o => o.Id == _settings.Current.Finance.DefaultAccountId) ?? FinanceAccounts[0];
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Loading finance accounts for settings failed"); }
        finally { _loadingAccounts = false; }
        UpdateFinanceExample();
    }

    private void UpdateFinanceExample() => FinanceExample = _finance.Format(-1234.5m, FinanceCurrency);

    partial void OnFinanceNumberFormatChanged(Dialogs.Option<string>? value)
    {
        if (_initializing || value == null) return;
        _settings.Current.Finance.NumberFormat = value.Value;
        _finance.RefreshFormat();
        UpdateFinanceExample();
        Save();
    }

    partial void OnFinanceCurrencyChanged(string value)
    {
        if (_initializing || !Arcadia.Core.Finance.Currency.IsSupported(value)) return;
        _settings.Current.Finance.DefaultCurrency = Arcadia.Core.Finance.Currency.Normalize(value);
        UpdateFinanceExample();
        Save();
    }

    partial void OnFinanceDefaultAccountChanged(ViewModels.Finance.FinanceOption? value)
    {
        if (_initializing || _loadingAccounts || value == null) return;
        _settings.Current.Finance.DefaultAccountId = value.Id;
        Save();
    }

    partial void OnFinancePrivacyModeChanged(bool value) { if (!_initializing) FinancePrivacy.Instance.IsHidden = value; }

    partial void OnFinanceOnCalendarChanged(bool value)
    {
        if (_initializing) return;
        _settings.Current.Organizer.ShowFinance = value;
        Save();
        _finance.Changed();
    }

    [RelayCommand]
    private Task ExportFinanceAsync() => _financeActions.ExportAsync();

    // ── Windows, performance, shortcuts, notifications (Phase 9) ──
    public IReadOnlyList<Dialogs.Option<string>> PerformanceModes => _performanceModes ??=
        [new("auto", _loc["Settings_PerfAuto"]), new("normal", _loc["Settings_PerfNormal"]), new("powersaver", _loc["Perf_PowerSaver"])];
    private IReadOnlyList<Dialogs.Option<string>>? _performanceModes;
    public IReadOnlyList<Dialogs.Option<string>> NotifyModes => _notifyModes ??=
        [new("inapp", _loc["Settings_NotifyInApp"]), new("windows", _loc["Settings_NotifyWindows"]), new("off", _loc["Settings_NotifyOff"])];
    private IReadOnlyList<Dialogs.Option<string>>? _notifyModes;
    public IReadOnlyList<string> ShortcutChoices => ShortcutManager.Choices;

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _showTrayIcon;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _restoreLastWorkspace;
    [ObservableProperty] private Dialogs.Option<string>? _performanceMode;
    [ObservableProperty] private bool _pauseIndexingInGames;
    [ObservableProperty] private bool _pauseThumbnailsInGames;
    [ObservableProperty] private bool _reduceMonitoringInGames;
    [ObservableProperty] private bool _quietNotificationsInGames;
    [ObservableProperty] private bool _reduceAnimationsInPowerSaver;
    [ObservableProperty] private bool _trimCachesUnderPressure;
    [ObservableProperty] private string _shortcutPalette = "";
    [ObservableProperty] private string _shortcutCapture = "";
    [ObservableProperty] private string _shortcutGaming = "";
    [ObservableProperty] private string _shortcutGlobal = "";
    [ObservableProperty] private string? _shortcutError;
    [ObservableProperty] private Dialogs.Option<string>? _notifyInfo;
    [ObservableProperty] private Dialogs.Option<string>? _notifyReminder;
    [ObservableProperty] private Dialogs.Option<string>? _notifyAutomation;
    [ObservableProperty] private Dialogs.Option<string>? _notifyGaming;
    [ObservableProperty] private Dialogs.Option<string>? _notifyWarning;

    private void SaveWin(Action<Arcadia.Core.Settings.AppSettings> change)
    {
        if (_initializing) return;
        change(_settings.Current);
        Save();
        _settings.NotifyChanged();
    }

    /// <summary>Writes (or removes) the per-user Run entry — only when the user flips this switch.</summary>
    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_initializing) return;
        try { StartupRegistration.Set(value); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Changing the startup entry failed");
            _notifications.Show(_loc["Settings_StartupFailed"], NotificationKind.Warning);
        }
    }

    partial void OnShowTrayIconChanged(bool value) => SaveWin(s => s.Windows.ShowTrayIcon = value);
    partial void OnMinimizeToTrayChanged(bool value) => SaveWin(s => s.Windows.MinimizeToTray = value);
    partial void OnStartMinimizedChanged(bool value) => SaveWin(s => s.Windows.StartMinimized = value);
    partial void OnRestoreLastWorkspaceChanged(bool value) => SaveWin(s => s.Windows.RestoreLastWorkspace = value);
    partial void OnPerformanceModeChanged(Dialogs.Option<string>? value) { if (value != null) SaveWin(s => s.Performance.Mode = value.Value); }
    partial void OnPauseIndexingInGamesChanged(bool value) => SaveWin(s => s.Performance.PauseIndexingInGames = value);
    partial void OnPauseThumbnailsInGamesChanged(bool value) => SaveWin(s => s.Performance.PauseThumbnailsInGames = value);
    partial void OnReduceMonitoringInGamesChanged(bool value) => SaveWin(s => s.Performance.ReduceMonitoringInGames = value);
    partial void OnQuietNotificationsInGamesChanged(bool value) => SaveWin(s => s.Performance.QuietNotificationsInGames = value);
    partial void OnReduceAnimationsInPowerSaverChanged(bool value) => SaveWin(s => s.Performance.ReduceAnimationsInPowerSaver = value);
    partial void OnTrimCachesUnderPressureChanged(bool value) => SaveWin(s => s.Performance.TrimCachesUnderPressure = value);
    partial void OnNotifyInfoChanged(Dialogs.Option<string>? value) { if (value != null) SaveWin(s => s.Notifications.Info = value.Value); }
    partial void OnNotifyReminderChanged(Dialogs.Option<string>? value) { if (value != null) SaveWin(s => s.Notifications.Reminder = value.Value); }
    partial void OnNotifyAutomationChanged(Dialogs.Option<string>? value) { if (value != null) SaveWin(s => s.Notifications.Automation = value.Value); }
    partial void OnNotifyGamingChanged(Dialogs.Option<string>? value) { if (value != null) SaveWin(s => s.Notifications.Gaming = value.Value); }
    partial void OnNotifyWarningChanged(Dialogs.Option<string>? value) { if (value != null) SaveWin(s => s.Notifications.Warning = value.Value); }

    partial void OnShortcutPaletteChanged(string value) => SaveShortcuts();
    partial void OnShortcutCaptureChanged(string value) => SaveShortcuts();
    partial void OnShortcutGamingChanged(string value) => SaveShortcuts();
    partial void OnShortcutGlobalChanged(string value) => SaveShortcuts();

    /// <summary>Saves the shortcuts unless two actions (or a fixed shortcut) would share a gesture.</summary>
    private void SaveShortcuts()
    {
        if (_initializing) return;
        var map = new Dictionary<string, string>
        {
            [_loc["Settings_KeyPalette"]] = ShortcutPalette, [_loc["Settings_KeyCapture"]] = ShortcutCapture, [_loc["Settings_KeyGaming"]] = ShortcutGaming,
            [_loc["Settings_KeyGlobal"]] = ShortcutGlobal, ["Oracle"] = _settings.Current.Oracle.Shortcut,
        };
        var conflicts = ShortcutManager.Conflicts(map);
        if (conflicts.Count > 0) { ShortcutError = _loc.Format("Settings_KeyConflict", string.Join(", ", conflicts)); return; }
        ShortcutError = null;
        SaveWin(s =>
        {
            s.Shortcuts.Palette = ShortcutPalette;
            s.Shortcuts.QuickCapture = ShortcutCapture;
            s.Shortcuts.GamingMode = ShortcutGaming;
            s.Shortcuts.GlobalShowArcadia = ShortcutGlobal;
        });
    }

    // ── Oracle (Phase 8) ──
    private readonly OracleHost _oracle;

    public IReadOnlyList<string> OracleShortcuts => OracleHost.Shortcuts;
    public IReadOnlyList<Dialogs.Option<int>> OracleHistoryLimits { get; } = [new(50, "50"), new(100, "100"), new(250, "250"), new(500, "500")];
    public IReadOnlyList<Dialogs.Option<string>> OracleLanguages => _oracleLanguages ??=
        [new("auto", _loc["Settings_OrcLangAuto"]), new("en", "English"), new("de", "Deutsch"), new("ar", "العربية")];
    private IReadOnlyList<Dialogs.Option<string>>? _oracleLanguages;
    public IReadOnlyList<Dialogs.Option<string>> OracleConfirmations => _oracleConfirmations ??=
        [new("smart", _loc["Settings_OrcConfirmSmart"]), new("always", _loc["Settings_OrcConfirmAlways"]), new("minimal", _loc["Settings_OrcConfirmMinimal"])];
    private IReadOnlyList<Dialogs.Option<string>>? _oracleConfirmations;

    [ObservableProperty] private bool _oracleEnabled;
    [ObservableProperty] private string _oracleShortcut = "Ctrl+Space";
    [ObservableProperty] private bool _oracleKeepHistory;
    [ObservableProperty] private Dialogs.Option<int>? _oracleHistoryLimit;
    [ObservableProperty] private Dialogs.Option<string>? _oracleLanguage;
    [ObservableProperty] private Dialogs.Option<string>? _oracleConfirmation;
    [ObservableProperty] private bool _oracleSuggestions;
    [ObservableProperty] private bool _oracleDiagnostics;
    [ObservableProperty] private bool _oracleCollectUnknown;

    private void SaveOracle(Action<Arcadia.Core.Settings.OracleSettings> change)
    {
        if (_initializing) return;
        change(_settings.Current.Oracle);
        Save();
    }

    partial void OnOracleEnabledChanged(bool value) => SaveOracle(o => o.Enabled = value);
    partial void OnOracleShortcutChanged(string value) => SaveOracle(o => o.Shortcut = value);
    partial void OnOracleKeepHistoryChanged(bool value) => SaveOracle(o => o.KeepHistory = value);
    partial void OnOracleHistoryLimitChanged(Dialogs.Option<int>? value) { if (value != null) SaveOracle(o => o.HistoryLimit = value.Value); }
    partial void OnOracleLanguageChanged(Dialogs.Option<string>? value) { if (value != null) SaveOracle(o => o.Language = value.Value); }
    partial void OnOracleConfirmationChanged(Dialogs.Option<string>? value) { if (value != null) SaveOracle(o => o.Confirmation = value.Value); }
    partial void OnOracleSuggestionsChanged(bool value) => SaveOracle(o => o.ShowSuggestions = value);
    partial void OnOracleDiagnosticsChanged(bool value) => SaveOracle(o => o.DeveloperDiagnostics = value);
    partial void OnOracleCollectUnknownChanged(bool value) => SaveOracle(o => o.CollectUnknown = value);

    [RelayCommand]
    private async Task ClearOracleHistoryAsync()
    {
        await _oracle.LoadAsync();
        await _oracle.ClearHistoryAsync();
        await _oracle.ClearUnknownAsync();
        _notifications.Show(_loc["Settings_OrcCleared"], NotificationKind.Success);
    }

    private readonly ViewModels.Finance.FinanceActions _financeActions;

    // ── calendar, tasks & notes (Phase 6) ──
    public IReadOnlyList<Dialogs.Option<int>> StartHours { get; } = Enumerable.Range(0, 23).Select(h => new Dialogs.Option<int>(h, $"{h:00}:00")).ToList();
    public IReadOnlyList<Dialogs.Option<int>> EndHours { get; } = Enumerable.Range(1, 24).Select(h => new Dialogs.Option<int>(h, h == 24 ? "24:00" : $"{h:00}:00")).ToList();
    public IReadOnlyList<Dialogs.Option<int?>> ReminderDefaults => _reminderDefaults ??=
    [
        new(null, _loc["Rem_None"]), new(0, _loc["Rem_AtStart"]), new(5, _loc.Format("Rem_Minutes", 5)), new(10, _loc.Format("Rem_Minutes", 10)),
        new(15, _loc.Format("Rem_Minutes", 15)), new(30, _loc.Format("Rem_Minutes", 30)), new(60, _loc["Rem_Hour"]),
    ];
    private IReadOnlyList<Dialogs.Option<int?>>? _reminderDefaults;

    [ObservableProperty] private Dialogs.Option<int>? _dayStartHour;
    [ObservableProperty] private Dialogs.Option<int>? _dayEndHour;
    [ObservableProperty] private Dialogs.Option<int?>? _defaultReminder;
    [ObservableProperty] private bool _reminderSound;
    [ObservableProperty] private bool _homeShowToday;
    [ObservableProperty] private bool _homeShowTasks;
    [ObservableProperty] private bool _homeShowDeadline;
    [ObservableProperty] private bool _homeShowPinnedNote;

    partial void OnDayStartHourChanged(Dialogs.Option<int>? value)
    {
        if (_initializing || value == null) return;
        _settings.Current.Organizer.DayStartHour = value.Value;
        if (DayEndHour is { } end && end.Value <= value.Value) DayEndHour = EndHours.First(o => o.Value == value.Value + 1);
        Save();
    }

    partial void OnDayEndHourChanged(Dialogs.Option<int>? value)
    {
        if (_initializing || value == null) return;
        _settings.Current.Organizer.DayEndHour = value.Value;
        if (DayStartHour is { } start && start.Value >= value.Value) DayStartHour = StartHours.First(o => o.Value == value.Value - 1);
        Save();
    }

    partial void OnDefaultReminderChanged(Dialogs.Option<int?>? value) { if (!_initializing && value != null) { _settings.Current.Organizer.DefaultReminderMinutes = value.Value; Save(); } }
    partial void OnReminderSoundChanged(bool value) { if (!_initializing) { _settings.Current.Organizer.ReminderSound = value; Save(); } }
    partial void OnHomeShowTodayChanged(bool value) { if (!_initializing) { _settings.Current.Home.ShowToday = value; Save(); } }
    partial void OnHomeShowTasksChanged(bool value) { if (!_initializing) { _settings.Current.Home.ShowTasks = value; Save(); } }
    partial void OnHomeShowDeadlineChanged(bool value) { if (!_initializing) { _settings.Current.Home.ShowDeadline = value; Save(); } }
    partial void OnHomeShowPinnedNoteChanged(bool value) { if (!_initializing) { _settings.Current.Home.ShowPinnedNote = value; Save(); } }

    partial void OnTravelOnlineTilesChanged(bool value) { if (!_initializing) { _settings.Current.Travel.OnlineMapTiles = value; Save(); } }
    partial void OnTravelOnlineSearchChanged(bool value) { if (!_initializing) { _settings.Current.Travel.OnlineSearch = value; Save(); } }

    partial void OnTileStyleChanged(Dialogs.Option<string>? value)
    {
        OnPropertyChanged(nameof(IsCustomTiles));
        if (_initializing || _applyingTiles || value == null) return;
        if (value.Value == "custom" && !Arcadia.Infrastructure.Geo.MapTileService.IsValidTemplate(CustomTileUrl)) { MapTilesStatus = _loc["Settings_TilesNeedUrl"]; return; }
        _settings.Current.Travel.TileStyle = value.Value;
        MapTilesStatus = null;
        Save();
    }

    /// <summary>Stores a custom tile server (URL in settings, optional key in the encrypted secret store).</summary>
    public void SaveCustomTiles(string key)
    {
        if (!Arcadia.Infrastructure.Geo.MapTileService.IsValidTemplate(CustomTileUrl))
        {
            MapTilesStatus = _loc["Settings_TilesBadUrl"];
            return;
        }
        if (!string.IsNullOrWhiteSpace(key)) _secrets.Set(Arcadia.Core.Security.SecretNames.MapTilesApiKey, key.Trim());
        var t = _settings.Current.Travel;
        t.CustomTileUrl = CustomTileUrl.Trim();
        t.CustomTileAttribution = string.IsNullOrWhiteSpace(CustomTileAttribution) ? null : CustomTileAttribution.Trim();
        t.TileStyle = "custom";
        _applyingTiles = true;
        TileStyle = TileStyles.First(o => o.Value == "custom");
        _applyingTiles = false;
        OnPropertyChanged(nameof(IsCustomTiles));
        RefreshKeyState();
        MapTilesStatus = _loc["Settings_TilesSaved"];
        Save();
    }

    [RelayCommand]
    private void ClearMapKey()
    {
        _secrets.Set(Arcadia.Core.Security.SecretNames.MapTilesApiKey, null);
        RefreshKeyState();
    }

    // ── Online services (IGDB + SteamGridDB) ──────────────────────

    [ObservableProperty] private string _igdbClientId = string.Empty;
    [ObservableProperty] private bool _igdbSecretSaved;
    [ObservableProperty] private bool _sgdbKeySaved;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _igdbStatus;
    [ObservableProperty] private string? _sgdbStatus;

    private void RefreshKeyState()
    {
        IgdbClientId = _secrets.Get(Arcadia.Core.Security.SecretNames.IgdbClientId) ?? string.Empty;
        IgdbSecretSaved = !string.IsNullOrEmpty(_secrets.Get(Arcadia.Core.Security.SecretNames.IgdbClientSecret));
        SgdbKeySaved = !string.IsNullOrEmpty(_secrets.Get(Arcadia.Core.Security.SecretNames.SteamGridDbApiKey));
        MapKeySaved = !string.IsNullOrEmpty(_secrets.Get(Arcadia.Core.Security.SecretNames.MapTilesApiKey));
    }

    /// <summary>Stores the credentials (empty secret fields keep the saved value), enables online details and tests both services.</summary>
    public async Task SaveOnlineServicesAsync(string clientSecret, string steamGridDbKey)
    {
        _secrets.Set(Arcadia.Core.Security.SecretNames.IgdbClientId, IgdbClientId);
        if (!string.IsNullOrWhiteSpace(clientSecret)) _secrets.Set(Arcadia.Core.Security.SecretNames.IgdbClientSecret, clientSecret);
        if (!string.IsNullOrWhiteSpace(steamGridDbKey)) _secrets.Set(Arcadia.Core.Security.SecretNames.SteamGridDbApiKey, steamGridDbKey);
        RefreshKeyState();
        if (!AllowOnlineMetadata) AllowOnlineMetadata = true; // saving keys means "use them"
        await TestOnlineServicesAsync();
    }

    [RelayCommand]
    private async Task TestOnlineServicesAsync()
    {
        IsTesting = true;
        IgdbStatus = SgdbStatus = null;
        try
        {
            IgdbStatus = !_metadata.Info.IsConfigured ? _loc.Format("Settings_NotSet", "IGDB") : await Probe("IGDB", async () => (await _metadata.Info.SearchAsync("Portal 2")).Count > 0);
            SgdbStatus = !_metadata.Art.IsConfigured ? _loc.Format("Settings_NotSet", "SteamGridDB") : await Probe("SteamGridDB", async () => await _metadata.Art.FindGameAsync("Portal 2", 620) != null);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private async Task<string> Probe(string service, Func<Task<bool>> call)
    {
        try
        {
            return await call() ? _loc.Format("Settings_TestOk", service) : _loc.Format("Settings_TestFail", service, _loc["Meta_NoResults"]);
        }
        catch (Exception ex) when (ex is Arcadia.Core.Metadata.MetadataException or HttpRequestException)
        {
            _logger.LogWarning(ex, "{Service} connection test failed", service);
            return _loc.Format("Settings_TestFail", service, _metadata.Describe(ex));
        }
    }

    [RelayCommand]
    private void ClearOnlineKeys()
    {
        _secrets.Set(Arcadia.Core.Security.SecretNames.IgdbClientId, null);
        _secrets.Set(Arcadia.Core.Security.SecretNames.IgdbClientSecret, null);
        _secrets.Set(Arcadia.Core.Security.SecretNames.SteamGridDbApiKey, null);
        RefreshKeyState();
        IgdbStatus = SgdbStatus = null;
    }

    [RelayCommand]
    private void AddCaptureFolder()
    {
        var folder = _dialogs.PickFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        if (folder != null) AddFolders([folder]);
    }

    [RelayCommand]
    private void AddDetectedFolders()
    {
        var detected = _locator.SuggestedCaptureFolders();
        if (detected.Count == 0) _notifications.Show(_loc["Settings_NothingDetected"]);
        else AddFolders(detected);
    }

    [RelayCommand]
    private void RemoveCaptureFolder(string? folder)
    {
        if (folder == null || !CaptureFolders.Remove(folder)) return;
        _settings.Current.Captures.Folders = [.. CaptureFolders];
        Save();
    }

    private void AddFolders(IEnumerable<string> folders)
    {
        bool changed = false;
        foreach (var f in folders)
        {
            if (CaptureFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
            CaptureFolders.Add(f);
            changed = true;
        }
        if (!changed) return;
        _settings.Current.Captures.Folders = [.. CaptureFolders];
        Save();
    }

    public string DataFolder { get; }
    public string SettingsFolder { get; }
    public string LogsFolder { get; }

    [ObservableProperty] private string _versionText = string.Empty;
    [ObservableProperty] private LanguageOption? _selectedLanguage;
    [ObservableProperty] private bool _animationsEnabled;
    [ObservableProperty] private bool _startMaximized;
    [ObservableProperty] private bool _allowOnlineMetadata;
    [ObservableProperty] private bool _isConfirmingRemoval;
    [ObservableProperty] private bool _sampleRemoved;

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_initializing || value == null) return;
        _settings.Current.Language = value.Tag;
        _loc.SetLanguage(value.Tag);
        Save();
    }

    partial void OnAnimationsEnabledChanged(bool value)
    {
        if (_initializing) return;
        _settings.Current.AnimationsEnabled = value;
        Motion.AnimationsEnabled = value;
        Save();
    }

    partial void OnStartMaximizedChanged(bool value)
    {
        if (_initializing) return;
        _settings.Current.Startup.StartMaximized = value;
        Save();
    }

    partial void OnAllowOnlineMetadataChanged(bool value)
    {
        if (_initializing) return;
        _settings.Current.Privacy.AllowOnlineMetadata = value;
        Save();
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path)) _launcher.OpenFolder(path);
    }

    [RelayCommand]
    private async Task RemoveSampleDataAsync()
    {
        if (!IsConfirmingRemoval)
        {
            IsConfirmingRemoval = true;
            _ = ResetConfirmationLaterAsync();
            return;
        }

        IsConfirmingRemoval = false;
        try
        {
            await _database.RemoveSampleDataAsync();
            _settings.Current.Development.SeedSampleData = false;
            Save();
            _home.Invalidate();
            SampleRemoved = true;
            _notifications.Show(_loc["Settings_SampleRemoved"], NotificationKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing sample data failed");
            _notifications.Show(_loc["Error_Message"], NotificationKind.Warning);
        }
    }

    private async Task ResetConfirmationLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        IsConfirmingRemoval = false;
    }

    private async void Save()
    {
        try
        {
            await _settings.SaveAsync();
            _settings.NotifyChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Saving settings failed");
            _notifications.Show(_loc["Error_Message"], NotificationKind.Warning);
        }
    }
}
