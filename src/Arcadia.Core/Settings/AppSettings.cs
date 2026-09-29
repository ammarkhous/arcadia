namespace Arcadia.Core.Settings;

/// <summary>
/// User preferences persisted as JSON in the roaming application-data folder.
/// Secrets (API keys) are intentionally NOT stored here; see <see cref="ServiceSettings"/>.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>IETF language tag: "en", "ar" (later "de").</summary>
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "Midnight";
    public bool AnimationsEnabled { get; set; } = true;
    public StartupSettings Startup { get; set; } = new();
    /// <summary>Overrides the default local data folder when set.</summary>
    public string? DataDirectory { get; set; }
    public PrivacySettings Privacy { get; set; } = new();
    public ServiceSettings Services { get; set; } = new();
    public DevelopmentSettings Development { get; set; } = new();
    public GamesSettings Games { get; set; } = new();
    public TrackingSettings Tracking { get; set; } = new();
    public CaptureSettings Captures { get; set; } = new();
    public ItToolsSettings ItTools { get; set; } = new();
    public DocumentsSettings Documents { get; set; } = new();
    public ProjectsSettings Projects { get; set; } = new();
    public TravelSettings Travel { get; set; } = new();
    public OrganizerSettings Organizer { get; set; } = new();
    public HomeSettings Home { get; set; } = new();
    public FinanceSettings Finance { get; set; } = new();
    public OracleSettings Oracle { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public WindowsSettings Windows { get; set; } = new();
    public ShortcutSettings Shortcuts { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public WorkspaceSettings Workspace { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
    /// <summary>Set when the first-run welcome has been completed (or skipped for an existing installation).</summary>
    public bool FirstRunCompleted { get; set; }
    /// <summary>The Arcadia version that last opened this library (a change means an update was installed).</summary>
    public string? LastRunVersion { get; set; }
}

/// <summary>Backup &amp; recovery (Phase 10). Archives go to a folder the user picks; nothing leaves the machine.</summary>
public sealed class BackupSettings
{
    /// <summary>"off", "daily", "weekly" or "update" (once after each Arcadia version change).</summary>
    public string Schedule { get; set; } = "off";
    /// <summary>How many automatic archives to keep (older automatic ones are removed; manual ones never).</summary>
    public int Keep { get; set; } = 5;
    /// <summary>Where backups go; empty means Documents\Arcadia Backups.</summary>
    public string? Folder { get; set; }
    public bool IncludeReferencedFiles { get; set; }
    public DateTime? LastAutomaticUtc { get; set; }
    public string? LastVersion { get; set; }
}

/// <summary>Automation engine (Phase 9). Automation definitions live in the database; these are engine preferences.</summary>
public sealed class AutomationSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>All automations paused (remembered across restarts).</summary>
    public bool Paused { get; set; }
    public int LogRetentionDays { get; set; } = 30;
    public int LogMaxEntries { get; set; } = 2000;
}

/// <summary>Gaming Mode, Power Saver and resource awareness (Phase 9).</summary>
public sealed class PerformanceSettings
{
    /// <summary>"normal", "powersaver" or "auto" (Power Saver while on battery).</summary>
    public string Mode { get; set; } = "auto";
    /// <summary>Turn Gaming Mode on when any detected game starts (per-game switch in the game editor applies always).</summary>
    public bool GamingForAllGames { get; set; } = true;
    /// <summary>Seconds to wait after a game closes before leaving Gaming Mode (another game keeps it on).</summary>
    public int GamingGraceSeconds { get; set; } = 60;
    public bool PauseIndexingInGames { get; set; } = true;
    public bool PauseThumbnailsInGames { get; set; } = true;
    public bool ReduceMonitoringInGames { get; set; } = true;
    public bool QuietNotificationsInGames { get; set; } = true;
    public bool ReduceAnimationsInPowerSaver { get; set; } = true;
    public bool TrimCachesUnderPressure { get; set; } = true;
    /// <summary>Offer a quick session note when a game closes.</summary>
    public bool OfferSessionNote { get; set; } = true;
}

/// <summary>Windows integration (Phase 9). Nothing is registered with Windows unless the user turns it on.</summary>
public sealed class WindowsSettings
{
    public bool ShowTrayIcon { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    /// <summary>Only used when Arcadia starts with Windows (or with --minimized).</summary>
    public bool StartMinimized { get; set; } = true;
    public bool RestoreLastWorkspace { get; set; }
    /// <summary>Where the window was when Arcadia last closed (device-independent units); ignored when off-screen.</summary>
    public WindowBounds? Placement { get; set; }
}

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Keyboard shortcuts (Phase 9 shortcut manager). In-window unless stated; empty = off.</summary>
public sealed class ShortcutSettings
{
    public string Palette { get; set; } = "Ctrl+K";
    public string QuickCapture { get; set; } = "Ctrl+Shift+N";
    public string GamingMode { get; set; } = "Ctrl+Shift+G";
    /// <summary>The only system-wide hotkey Arcadia may register; off by default.</summary>
    public string GlobalShowArcadia { get; set; } = string.Empty;
}

/// <summary>Per-category notification preference: "inapp", "windows" (a Windows notification when Arcadia is not in front) or "off".</summary>
public sealed class NotificationSettings
{
    public string Info { get; set; } = "inapp";
    public string Reminder { get; set; } = "windows";
    public string Automation { get; set; } = "windows";
    public string Gaming { get; set; } = "inapp";
    public string Warning { get; set; } = "windows";
}

/// <summary>Where the user was (restore on start) and recent places (quick access), small and bounded.</summary>
public sealed class WorkspaceSettings
{
    public string? Last { get; set; }
    public List<RecentWorkspace> Recent { get; set; } = [];
}

/// <summary>A recent place: an arcadia:// record link (with optional tab) or a section key.</summary>
public sealed class RecentWorkspace
{
    public string Target { get; set; } = string.Empty;
    public string? Tab { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Icon { get; set; } = "ArrowRight";
    public DateTime AtUtc { get; set; }
}

/// <summary>Oracle command console (Phase 8). Everything stays on this computer.</summary>
public sealed class OracleSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>"Ctrl+Space", "Ctrl+Shift+Space", "Ctrl+Shift+O" or "Ctrl+Alt+Space" (Arcadia window only).</summary>
    public string Shortcut { get; set; } = "Ctrl+Space";
    public bool KeepHistory { get; set; } = true;
    public int HistoryLimit { get; set; } = 100;
    /// <summary>"auto" (detected per command), "en", "de" or "ar".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>"smart" (creates and unclear changes ask first), "always" or "minimal".</summary>
    public string Confirmation { get; set; } = "smart";
    public bool ShowSuggestions { get; set; } = true;
    /// <summary>Shows the parse trace (normalised text, intent, entities, confidence, handler, timing) under each result.</summary>
    public bool DeveloperDiagnostics { get; set; }
    /// <summary>Keeps commands Oracle could not match in a local list (for adding vocabulary). Off by default.</summary>
    public bool CollectUnknown { get; set; }
}

/// <summary>Finance preferences (Phase 7). Finance data itself lives only in the local database.</summary>
public sealed class FinanceSettings
{
    /// <summary>"windows" (the Windows regional format), "point" (1,234.56), "comma" (1.234,56), "space" (1 234,56) or "swiss" (1'234.56).</summary>
    public string NumberFormat { get; set; } = "windows";
    /// <summary>Currency preselected for new accounts, budgets and goals.</summary>
    public string DefaultCurrency { get; set; } = "EUR";
    /// <summary>Account used by quick expenses and new transactions.</summary>
    public int? DefaultAccountId { get; set; }
    /// <summary>Amounts are hidden (••••) until shown again. Remembered so a hidden screen stays hidden after a restart.</summary>
    public bool PrivacyMode { get; set; }
    /// <summary>"overview", "transactions", "accounts", "budgets", "recurring", "goals", "reports" or "history".</summary>
    public string LastSection { get; set; } = "overview";
}

/// <summary>Calendar, tasks and notes preferences (Phase 6).</summary>
public sealed class OrganizerSettings
{
    /// <summary>"Today", "Month", "Week", "Day" or "Agenda".</summary>
    public string CalendarView { get; set; } = "Today";
    /// <summary>The week and day views open on these hours; the rest of the day is a scroll away.</summary>
    public int DayStartHour { get; set; } = 7;
    public int DayEndHour { get; set; } = 22;
    public bool ShowEvents { get; set; } = true;
    public bool ShowTasks { get; set; } = true;
    public bool ShowProjects { get; set; } = true;
    public bool ShowTravel { get; set; } = true;
    public bool ShowGaming { get; set; } = true;
    /// <summary>Bills, subscriptions and income due dates from Finance (Phase 7).</summary>
    public bool ShowFinance { get; set; } = true;
    /// <summary>Default reminder for new timed events, in minutes before (null = none).</summary>
    public int? DefaultReminderMinutes { get; set; } = 15;
    /// <summary>"Today", "Upcoming", "All", "Overdue", "Completed", "Pinned" or "ByProject".</summary>
    public string TaskView { get; set; } = "Today";
    /// <summary>"All", "Recent", "Pinned", "Favorites", "ByModule" or "ByTag".</summary>
    public string NoteView { get; set; } = "All";
    /// <summary>"Edit", "Preview" or "Split".</summary>
    public string NoteMode { get; set; } = "Edit";
    /// <summary>Reminders up to this local time have been shown; later ones are pending. Survives restarts so nothing fires twice.</summary>
    public DateTime? RemindersShownUntil { get; set; }
    /// <summary>Show a Windows sound and flash the taskbar button with reminders.</summary>
    public bool ReminderSound { get; set; } = true;
}

/// <summary>Which cells of Home's "Today" strip are shown (the Phase 1 composition itself is fixed). All off hides the strip.</summary>
public sealed class HomeSettings
{
    public bool ShowToday { get; set; } = true;
    public bool ShowTasks { get; set; } = true;
    public bool ShowDeadline { get; set; } = true;
    public bool ShowPinnedNote { get; set; } = true;
}

/// <summary>Travel map and search. Every online request is documented in docs/DEVELOPMENT.md (Phase 5).</summary>
public sealed class TravelSettings
{
    /// <summary>Show online detail tiles on top of the bundled vector world map (only the tiles in view are requested).</summary>
    public bool OnlineMapTiles { get; set; } = true;
    /// <summary>"osm-dark" (OpenStreetMap, darkened locally), "osm" (original colours) or "custom".</summary>
    public string TileStyle { get; set; } = "osm-dark";
    /// <summary>XYZ template for a custom tile server, e.g. https://tiles.example.com/{z}/{x}/{y}.png?key={key}. The key lives in the encrypted secret store.</summary>
    public string? CustomTileUrl { get; set; }
    public string? CustomTileAttribution { get; set; }
    /// <summary>Allow place search on OpenStreetMap Nominatim when the user asks for it (offline search always works).</summary>
    public bool OnlineSearch { get; set; } = true;
    /// <summary>Last map view, restored when Travel opens.</summary>
    public double? MapLatitude { get; set; }
    public double? MapLongitude { get; set; }
    public double? MapZoom { get; set; }
    public string LastSection { get; set; } = "desk";
}

public sealed class DocumentsSettings
{
    /// <summary>Master switch for watched folders (each folder can also be paused).</summary>
    public bool WatchEnabled { get; set; } = true;
    /// <summary>"List" or "Tiles".</summary>
    public string ViewMode { get; set; } = "List";
}

public sealed class ProjectsSettings
{
    public bool ShowArchived { get; set; }
    /// <summary>Last workspace tab per project is not persisted; this is the default tab.</summary>
    public string DefaultTab { get; set; } = "Overview";
}

public sealed class ItToolsSettings
{
    /// <summary>The diagnostic session tool results are recorded into (survives restarts).</summary>
    public int? ActiveDiagnosticSessionId { get; set; }
    /// <summary>Last targets, so the tools open where the user left off. Stored locally only.</summary>
    public string? LastHost { get; set; }
    public string? LastLargeFileFolder { get; set; }
}

public sealed class TrackingSettings
{
    /// <summary>Record sessions automatically while a game's process runs.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Also notice library games started outside Arcadia.</summary>
    public bool DetectExternalLaunches { get; set; } = true;
    /// <summary>Shorter runs (crashes, quick launches) are not recorded.</summary>
    public int MinimumSessionMinutes { get; set; } = 2;
}

public sealed class CaptureSettings
{
    /// <summary>Import new screenshots from the watched folders automatically.</summary>
    public bool WatchEnabled { get; set; } = true;
    public List<string> Folders { get; set; } = [];
}

public sealed class GamesSettings
{
    /// <summary>"Grid" or "List".</summary>
    public string ViewMode { get; set; } = "Grid";
    /// <summary>LastPlayed, Title, Progress, Playtime or Added.</summary>
    public string Sort { get; set; } = "LastPlayed";
}

public sealed class StartupSettings
{
    public bool StartMaximized { get; set; }
    public string StartSection { get; set; } = "home";
}

public sealed class PrivacySettings
{
    public bool AllowOnlineMetadata { get; set; }
}

/// <summary>
/// Non-secret configuration for future external services. API keys will be stored through
/// Windows DPAPI-protected storage, referenced here only by name.
/// </summary>
public sealed class ServiceSettings
{
    public string WeatherLocation { get; set; } = "Dresden, Germany";
    public string? GameMetadataProvider { get; set; }
}

public sealed class DevelopmentSettings
{
    /// <summary>Seeds the sample library on first run of an empty database. Development builds only: release builds never seed.</summary>
    public bool SeedSampleData { get; set; }
}
