using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Arcadia.App.Diagnostics;
using Arcadia.App.Dialogs;
using Arcadia.App.Localization;
using Arcadia.App.Services;
using Arcadia.App.Shell;
using Arcadia.App.ViewModels;
using Arcadia.App.ViewModels.Home;
using Arcadia.Core.Localization;
using Arcadia.Core.Platform;
using Arcadia.Core.Settings;
using Arcadia.Data;
using Arcadia.Infrastructure;
using Arcadia.Infrastructure.Logging;
using Arcadia.Infrastructure.Platform;
using Arcadia.Infrastructure.Settings;
using Arcadia.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.App;

public partial class App : Application
{
    private IHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private LaunchOptions _options = LaunchOptions.Parse([]);

    /// <summary>The service provider, for the few window-level handlers outside dependency injection.</summary>
    public static IServiceProvider Services => ((App)Current)._host!.Services;

    /// <summary>QA capture runs (no animations, no Windows integration).</summary>
    public static bool IsCaptureMode { get; private set; }
    private bool _showingError;
    private SingleInstance? _instance;

    private readonly List<(string Stage, double Ms)> _startupMarks = [];
    private void Mark(string stage) => _startupMarks.Add((stage, SinceProcessStart()));

    protected override async void OnStartup(StartupEventArgs e)
    {
        Mark("app entry");
        base.OnStartup(e);
        _options = LaunchOptions.Parse(e.Args);
        IsCaptureMode = _options.IsCapture;
        RegisterGlobalExceptionHandlers();

        if (_options.WaitPid is { } previous)
        {
            // restarting (after a restore): the old instance must be gone before the data is touched
            try { using var old = System.Diagnostics.Process.GetProcessById(previous); old.WaitForExit(30000); }
            catch (ArgumentException) { } // already exited
        }

        if (!_options.IsCapture && _options.ArtSheetPath == null)
        {
            var scope = _options.DataDirectory is { } dir ? Path.GetFullPath(dir) : "default";
            _instance = SingleInstance.TryAcquire(scope, () => Dispatcher.InvokeAsync(BringToFront));
            if (_instance == null)
            {
                Shutdown(0); // the running Arcadia has been asked to come to the front
                return;
            }
        }

        if (_options.ArtSheetPath != null)
        {
            ArtSheet.Render(_options.ArtSheetPath);
            Shutdown(0);
            return;
        }

        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Arcadia failed to start");
            ShowError(ex, fatal: true);
            Shutdown(1);
        }
    }

    private async Task StartAsync()
    {
        if (_options.DataDirectory != null)
            Environment.SetEnvironmentVariable(AppPaths.EnvironmentOverride, Path.GetFullPath(_options.DataDirectory));

        // Settings live in a fixed location; they may relocate the data folder.
        var bootstrapPaths = new AppPaths();
        var settings = new JsonSettingsService(bootstrapPaths.SettingsFilePath, NullLogger<JsonSettingsService>.Instance);
        await settings.LoadAsync();
        if (!settings.Current.FirstRunCompleted && !_options.IsCapture && !await RunFirstRunAsync(settings)) return;
        var paths = new AppPaths(settings.Current.DataDirectory);
        paths.EnsureCreated();
        // a restore staged in Settings is applied now: before logging, settings or the database are opened
        var restore = BackupRestorer.ApplyPending(paths.DataDirectory, paths.SettingsFilePath);
        Mark("restore check");
        Arcadia.UI.Artwork.ImageCache.WebCacheDirectory = Path.Combine(paths.CacheDirectory, "web");

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(new FileLoggerProvider(paths.LogsDirectory));
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

        var services = builder.Services;
        services.AddArcadiaInfrastructure(paths);
        services.AddArcadiaData(paths.DatabasePath);
        services.AddSingleton<ILocalizer>(LocalizationManager.Instance);
        services.AddSingleton<StartupState>();
        services.AddSingleton<IShellLauncher, ShellLauncher>();
        services.AddSingleton<Arcadia.Core.Media.IMediaStore, MediaStore>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<CommunityToolkit.Mvvm.Messaging.IMessenger>(CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default);
        services.AddSingleton<Arcadia.Core.Tracking.IProcessScanner, Win32ProcessScanner>();
        services.AddSingleton<SessionTrackingService>();
        services.AddSingleton<CaptureWatcherService>();
        services.AddSingleton<MetadataService>();
        services.AddSingleton<GameActions>();
        services.AddSingleton<ViewModels.Games.GamesViewModel>();
        services.AddTransient<ViewModels.Games.GameDetailViewModel>();
        services.AddSingleton<Func<ViewModels.Games.GameDetailViewModel>>(sp => () => sp.GetRequiredService<ViewModels.Games.GameDetailViewModel>());
        services.AddSingleton<HeaderViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<ShellViewModel>());
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<ShellViewModel>());
        services.AddSingleton<ViewModels.Home.HomeTodayViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // IT Tools (all local; nothing is sent anywhere)
        Arcadia.ItTools.ItToolsServiceCollectionExtensions.AddArcadiaItTools(services);
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IExplorerLauncher, ExplorerLauncher>();
        services.AddSingleton<Arcadia.Core.ItTools.Reports.IReportExporter, Arcadia.Core.ItTools.Reports.TextReportExporter>();
        services.AddSingleton<Arcadia.Core.ItTools.Reports.IReportExporter, Arcadia.Core.ItTools.Reports.JsonReportExporter>();
        services.AddSingleton<ReportExportService>();
        services.AddSingleton<DiagnosticSessionContext>();
        services.AddSingleton<ViewModels.ItTools.ToolContext>();
        services.AddSingleton<ViewModels.ItTools.ItToolsViewModel>();

        // Projects & Documents (local-first; referenced files are never copied, moved or deleted)
        services.AddSingleton(new Arcadia.Core.Platform.DataLocations(paths.DataDirectory, paths.DatabasePath, paths.SettingsFilePath,
            Path.Combine(paths.SettingsDirectory, "secrets.dat"), Path.Combine(paths.DataDirectory, "Media"), paths.CacheDirectory,
            Path.Combine(paths.CacheDirectory, "Thumbnails"), paths.LogsDirectory));
        services.AddSingleton<Arcadia.Core.Platform.IArcadiaBackupService>(sp => new ArcadiaBackupService(
            sp.GetRequiredService<Arcadia.Core.Platform.DataLocations>(), AppInfo.Version, sp.GetRequiredService<ILogger<ArcadiaBackupService>>()));
        services.AddSingleton<ViewModels.MaintenanceViewModel>();
        services.AddSingleton<AutomaticBackupService>();
        services.AddSingleton(sp => new ThumbnailService(Path.Combine(paths.CacheDirectory, "Thumbnails"), sp.GetRequiredService<ILogger<ThumbnailService>>()));
        services.AddSingleton<DocumentActions>();
        services.AddSingleton<DocumentAvailabilityService>();
        services.AddSingleton<DocumentWatcherService>();
        services.AddSingleton<ViewModels.Projects.WorkspaceContext>();
        services.AddTransient<ViewModels.Projects.ProjectWorkspaceViewModel>();
        services.AddSingleton<Func<ViewModels.Projects.ProjectWorkspaceViewModel>>(sp => () => sp.GetRequiredService<ViewModels.Projects.ProjectWorkspaceViewModel>());
        services.AddSingleton<ViewModels.Projects.ProjectsViewModel>();
        services.AddSingleton<ViewModels.Documents.DocumentsViewModel>();
        // Travel (local-first; the map may request online tiles and place search asks OpenStreetMap only on request)
        services.AddSingleton<MapTileImages>();
        services.AddSingleton<FlagImages>();
        services.AddSingleton<TravelPhotoService>();
        services.AddSingleton<ViewModels.Travel.TravelViewModel>();
        services.AddSingleton<CommandPaletteViewModel>();
        // Phase 6 organizer
        services.AddSingleton<ViewModels.Organizer.OrganizerContext>();
        services.AddSingleton<ViewModels.Organizer.CalendarViewModel>();
        services.AddSingleton<ViewModels.Organizer.TasksViewModel>();
        services.AddSingleton<ViewModels.Organizer.NotesViewModel>();
        services.AddSingleton<ViewModels.Organizer.QuickCaptureViewModel>();
        services.AddSingleton<NotificationCenter>();
        services.AddSingleton<IReminderPresenter, Views.Organizer.ReminderPresenter>();
        services.AddSingleton<ReminderService>();
        // Phase 7 finance (local only; nothing is sent anywhere)
        services.AddSingleton<FinanceContext>();
        services.AddSingleton<ViewModels.Finance.FinanceActions>();
        services.AddSingleton<ViewModels.Finance.FinanceViewModel>();
        // Phase 8 Oracle: deterministic commands, created on first use (no AI, no network)
        services.AddSingleton<AppOracleEnvironment>();
        services.AddSingleton<OracleHost>();
        services.AddSingleton<ViewModels.Oracle.OracleConsoleViewModel>();
        // Phase 9 automation, performance modes and Windows integration (all local)
        services.AddSingleton<PerformanceService>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<ArcadiaNotifier>();
        services.AddSingleton<AutomationService>();
        services.AddSingleton<Arcadia.Core.Automation.IAutomationService>(sp => sp.GetRequiredService<AutomationService>());
        services.AddSingleton<DesktopIntegration>();
        services.AddSingleton<ViewModels.Automation.AutomationViewModel>();
        services.AddSingleton<MainWindow>();

        _host = builder.Build();
        await _host.StartAsync();
        Mark("host started");
        var sp = _host.Services;
        _logger = sp.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Arcadia {Version} starting (data: {Data})", AppInfo.Version, paths.DataDirectory);
        if (restore != null)
            _logger.Log(restore.Success ? LogLevel.Information : LogLevel.Error, "Restore from backup: {Result} (backup created {Created:u}; safety backup {Safety}; {Error})",
                restore.Success ? "applied" : "rolled back", restore.BackupCreatedUtc, restore.SafetyBackup, restore.Error ?? "no error");

        var appSettings = sp.GetRequiredService<ISettingsService>();
        await appSettings.LoadAsync();
        Views.Travel.PhotoThumb.Thumbnails = sp.GetRequiredService<ThumbnailService>();
        Views.Controls.MarkdownView.OpenInternal = target => sp.GetRequiredService<INavigationService>().Open(target);
        ViewModels.Finance.ExpensePanelViewModel.Shared = sp.GetRequiredService<ViewModels.Finance.FinanceActions>();
        ViewModels.Finance.ExpensePanelViewModel.OpenRegisterRequested = q => _ = sp.GetRequiredService<ViewModels.Finance.FinanceViewModel>().ShowRegisterFromAsync(q);
        // warm the bundled geography off the UI thread so the first map paints instantly
        _ = Task.Run(() => { _ = sp.GetRequiredService<Arcadia.Core.Travel.ICountryCatalog>().All.Count; });
        LocalizationManager.Instance.SetLanguage(_options.Language ?? appSettings.Current.Language);
        Motion.AnimationsEnabled = appSettings.Current.AnimationsEnabled && !_options.IsCapture;

        // The database is prepared in the background; pages await StartupState.
        var startup = sp.GetRequiredService<StartupState>();
        var initializer = sp.GetRequiredService<DatabaseInitializer>();
        _ = Task.Run(async () =>
        {
            try
            {
                                if (!string.Equals(appSettings.Current.LastRunVersion, AppInfo.Version, StringComparison.Ordinal))
                    await initializer.BackupBeforeUpdateAsync(paths.DatabasePath, AppInfo.Version);
#if DEBUG
                await initializer.InitializeAsync(appSettings.Current.Development.SeedSampleData);
#else
                await initializer.InitializeAsync(seedSampleData: false); // release builds never add sample data
#endif
                _logger.LogInformation("Perf: database ready {Ms:0} ms after process start", SinceProcessStart());
                if (!string.Equals(appSettings.Current.LastRunVersion, AppInfo.Version, StringComparison.Ordinal))
                {
                    appSettings.Current.LastRunVersion = AppInfo.Version;
                    await appSettings.SaveAsync();
                }
                startup.MarkDatabaseReady();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database initialization failed");
                startup.MarkDatabaseFailed(ex);
            }
        });

        Mark("settings + language");
        var shell = sp.GetRequiredService<ShellViewModel>();
        var window = sp.GetRequiredService<MainWindow>();
        Mark("window constructed");
        MainWindow = window;

        if (_options.IsCapture)
            window.PrepareForCapture(_options.CaptureWidth / _options.CaptureScale, _options.CaptureHeight / _options.CaptureScale);
        else if (appSettings.Current.Startup.StartMaximized)
            window.WindowState = WindowState.Maximized;

        shell.Navigate(_options.Page ?? appSettings.Current.Startup.StartSection);
        if (!_options.IsCapture) sp.GetRequiredService<DesktopIntegration>().Attach(window, shell);
        var startHidden = !_options.IsCapture && e_StartMinimized && appSettings.Current.Windows.StartMinimized;
        if (_options.Page == null && appSettings.Current.Windows.RestoreLastWorkspace && !_options.IsCapture)
            _ = RestoreWorkspaceAsync(shell, startup, appSettings.Current.Workspace.Last);
        if (_options.DocumentId is { } documentId)
        {
            await startup.DatabaseReady;
            shell.Navigate(Sections.Documents);
            await sp.GetRequiredService<ViewModels.Documents.DocumentsViewModel>().ActivateAsync();
            await sp.GetRequiredService<ViewModels.Documents.DocumentsViewModel>().ShowDocumentAsync(documentId);
        }
        if (_options.ProjectId is { } projectId)
        {
            await startup.DatabaseReady;
            shell.Navigate(Sections.Projects);
            var projects = sp.GetRequiredService<ViewModels.Projects.ProjectsViewModel>();
            await projects.OpenProjectAsync(projectId);
            if (projects.Workspace != null && Enum.TryParse<ViewModels.Projects.WorkspaceTab>(_options.Tab, true, out var wtab)) projects.Workspace.SelectedTab = wtab;
        }
        if (string.Equals(_options.Page, Sections.Travel, StringComparison.OrdinalIgnoreCase) || _options.TripId != null || _options.PlaceId != null)
        {
            await startup.DatabaseReady;
            var travel = sp.GetRequiredService<ViewModels.Travel.TravelViewModel>();
            if (_options.TripId != null || _options.PlaceId != null) shell.Navigate(Sections.Travel);
            await travel.LoadAsync();
            if (_options.TripId is null && Enum.TryParse<ViewModels.Travel.TravelSection>(_options.Tab, true, out var tsection)) travel.ShowSection(tsection);
            if (_options.PlaceId is { } placeId)
            {
                if (travel.Section == ViewModels.Travel.TravelSection.Map) travel.ShowOnMap(placeId); else travel.OpenPlace(placeId);
            }
            if (_options.TripId is { } tripId)
            {
                travel.OpenTrip(tripId);
                if (travel.Workspace != null && Enum.TryParse<ViewModels.Travel.TripTab>(_options.Tab, true, out var ttab)) travel.Workspace.Tab = ttab;
            }
        }
        if (_options.Tab != null && string.Equals(_options.Page, Sections.Calendar, StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<ViewModels.Organizer.CalendarView>(_options.Tab, true, out var calView))
        {
            await startup.DatabaseReady;
            await sp.GetRequiredService<ViewModels.Organizer.CalendarViewModel>().ShowDateAsync(DateOnly.FromDateTime(DateTime.Now), calView);
        }
        if (_options.Tab != null && string.Equals(_options.Page, Sections.Tasks, StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<Arcadia.Core.Organizer.TaskView>(_options.Tab, true, out var taskView))
        {
            await startup.DatabaseReady;
            var tasks = sp.GetRequiredService<ViewModels.Organizer.TasksViewModel>();
            await tasks.ActivateAsync();
            tasks.View = taskView;
        }
        if (_options.Tab != null && string.Equals(_options.Page, Sections.Notes, StringComparison.OrdinalIgnoreCase))
        {
            await startup.DatabaseReady;
            var notes = sp.GetRequiredService<ViewModels.Organizer.NotesViewModel>();
            if (int.TryParse(_options.Tab, out var noteId)) await notes.ShowNoteAsync(noteId);
            else await notes.ShowShelfAsync(_options.Tab);
        }
        if (_options.Tab != null && string.Equals(_options.Page, Sections.Finance, StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<ViewModels.Finance.FinanceSection>(_options.Tab, true, out var finSection))
        {
            await startup.DatabaseReady;
            var fin = sp.GetRequiredService<ViewModels.Finance.FinanceViewModel>();
            await fin.ActivateAsync();
            await fin.ShowSectionAsync(finSection);
        }
        if (_options.Tab != null && string.Equals(_options.Page, Sections.Automation, StringComparison.OrdinalIgnoreCase))
        {
            await startup.DatabaseReady;
            var auto = sp.GetRequiredService<ViewModels.Automation.AutomationViewModel>();
            await auto.ActivateAsync();
            if (_options.Tab == "Edit") auto.Selected = auto.Automations.FirstOrDefault();
            else await shell.NavigateToAsync(Sections.Automation, _options.Tab);
        }
        if (_options.Tab != null && string.Equals(_options.Page, Sections.ItTools, StringComparison.OrdinalIgnoreCase))
            _ = sp.GetRequiredService<ViewModels.ItTools.ItToolsViewModel>().OpenToolAsync(_options.Tab);
        if (_options.GameId is { } gameId && _options.Dialog == null)
        {
            shell.Navigate(Sections.Games);
            var games = sp.GetRequiredService<ViewModels.Games.GamesViewModel>();
            await games.ShowGameAsync(gameId);
            if (games.Detail != null && Enum.TryParse<ViewModels.Games.GameDetailTab>(_options.Tab, ignoreCase: true, out var tab))
                games.Detail.SelectedTab = tab;
        }
        // QA: ARCADIA_QA_ORACLE="cmd ;; cmd" runs Oracle commands on its page ("overlay:cmd ;; cmd" in the overlay)
        if (Environment.GetEnvironmentVariable("ARCADIA_QA_ORACLE") is { Length: > 0 } qaOracle)
        {
            await startup.DatabaseReady;
            var overlay = qaOracle.StartsWith("overlay:", StringComparison.Ordinal);
            if (overlay) await shell.Oracle.OpenOverlayAsync();
            else shell.Navigate(Sections.Oracle);
            foreach (var cmd in qaOracle[(overlay ? 8 : 0)..].Split(";;", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                await shell.Oracle.RunTextAsync(cmd);
        }
        var firstFrame = new TaskCompletionSource();
        window.ContentRendered += (_, _) =>
        {
            firstFrame.TrySetResult();
            Mark("first frame");
            _logger.LogInformation("Perf: startup {Stages}", string.Join(" | ", _startupMarks.Select(m => $"{m.Stage} {m.Ms:0}")));
            _logger.LogInformation("Perf: first frame {Ms:0} ms after process start ({Dicts} theme dictionaries parsed, {Scenes} scenes painted in {SceneMs:0} ms)",
                SinceProcessStart(), Arcadia.UI.SharedResourceDictionary.ParsedCount, Arcadia.UI.RenderStats.ScenesPainted, Arcadia.UI.RenderStats.SceneMilliseconds);
        };
        if (Environment.GetEnvironmentVariable("ARCADIA_QA_STARTUP") == "1") StartupTrace.Attach(Dispatcher, window, _logger);
        if (Environment.GetEnvironmentVariable("ARCADIA_QA_STALLS") == "1") StartupTrace.Attach(Dispatcher, window, _logger, wholeSession: true);
        if (startHidden) sp.GetRequiredService<DesktopIntegration>().StartHidden(window);
        else window.Show();
        Mark("window shown");
        _ = shell.Header.RefreshWeatherAsync();

        if (_options.IsCapture) await CaptureAsync(window, startup);
        else if (_options.PerfScroll != null) await RunScrollBenchmarkAsync(window, shell, sp, startup);
        else _ = StartBackgroundServicesAsync(sp, startup, startHidden ? Task.CompletedTask : firstFrame.Task);
    }

    private static bool e_StartMinimized => Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reopens where the user was (a section, or an arcadia:// record with its tab).</summary>
    private async Task RestoreWorkspaceAsync(ShellViewModel shell, StartupState startup, string? last)
    {
        if (string.IsNullOrEmpty(last)) return;
        try
        {
            await startup.DatabaseReady;
            var parts = last.Split('#', 2);
            if (Arcadia.Core.Organizer.EntityRef.TryParse(parts[0], out var target)) await shell.OpenAsync(target, parts.Length > 1 ? parts[1] : null);
            else if (Sections.Find(parts[0]) != null) await shell.NavigateToAsync(parts[0], parts.Length > 1 ? parts[1] : null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restoring the last workspace failed");
        }
    }

    private async Task RunScrollBenchmarkAsync(MainWindow window, ShellViewModel shell, IServiceProvider sp, StartupState startup)
    {
        await startup.DatabaseReady;
        await Task.Delay(1000);
        shell.Navigate(Sections.Games);
        await ScrollBenchmark.RunAsync(window, sp.GetRequiredService<ViewModels.Games.GamesViewModel>(),
            string.Equals(_options.PerfScroll, "list", StringComparison.OrdinalIgnoreCase), _logger);
        Shutdown(0);
    }

    /// <summary>
    /// Play tracking, watchers, reminders and automations start once the database is ready and the first frame is on
    /// screen. (On a warm start the database is ready before the window is, and starting them inline delayed the first
    /// frame by ~0.5 s.)
    /// </summary>
    private async Task StartBackgroundServicesAsync(IServiceProvider sp, StartupState startup, Task firstFrame)
    {
        try
        {
            await startup.DatabaseReady;
            await firstFrame;
            await Dispatcher.Yield(DispatcherPriority.Background);
            sp.GetRequiredService<SessionTrackingService>().Start();
            sp.GetRequiredService<CaptureWatcherService>().Start();
            sp.GetRequiredService<ReminderService>().Start();
            await sp.GetRequiredService<DocumentWatcherService>().StartAsync();
            _ = sp.GetRequiredService<DocumentAvailabilityService>().CheckAllAsync();
            sp.GetRequiredService<PerformanceService>().Start();
            var automation = sp.GetRequiredService<AutomationService>();
            await automation.StartAsync();
            await Task.Delay(1500);
            await automation.RaiseStartedAsync();
            sp.GetRequiredService<AutomaticBackupService>().Start();
            ReportRestoreOutcome(sp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background services did not start");
        }
    }

    /// <summary>
    /// First run: the welcome window, before the data folder is created. An existing library (upgrading from an earlier
    /// version, or a reinstall that kept the data) skips it. Returns false when the user closed it (Arcadia exits, nothing created).
    /// </summary>
    private async Task<bool> RunFirstRunAsync(JsonSettingsService settings)
    {
        var probe = new AppPaths(settings.Current.DataDirectory);
        if (!File.Exists(probe.DatabasePath))
        {
            var vm = new WelcomeViewModel(probe.DataDirectory)
            {
                CanChangeFolder = string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.EnvironmentOverride)),
            };
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // the welcome is the only window for now
            var finished = new WelcomeWindow(vm).ShowDialog() == true;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (!finished)
            {
                Shutdown(0);
                return false;
            }
            settings.Current.Language = vm.Language;
            settings.Current.DataDirectory = vm.IsDefaultFolder || !vm.CanChangeFolder ? settings.Current.DataDirectory : vm.DataFolder;
            settings.Current.Performance.Mode = vm.PerformanceMode;
            if (vm.StartWithWindows) StartupRegistration.Set(true);
        }
        settings.Current.FirstRunCompleted = true;
        await settings.SaveAsync();
        return true;
    }

    /// <summary>After a restart that applied (or rolled back) a restore, say so once.</summary>
    private static void ReportRestoreOutcome(IServiceProvider sp)
    {
        var outcome = BackupRestorer.TakeLastOutcome(sp.GetRequiredService<IAppPaths>().DataDirectory);
        if (outcome == null) return;
        var loc = LocalizationManager.Instance;
        var text = outcome.Success
            ? loc.Format("Backup_RestoredToast", outcome.BackupCreatedUtc.ToLocalTime().ToString("g", loc.Culture))
            : loc["Backup_RolledBackToast"];
        sp.GetRequiredService<INotificationService>().Show(text, outcome.Success ? NotificationKind.Success : NotificationKind.Warning);
    }

    internal static double SinceProcessStart() =>
        (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    private async Task CaptureAsync(MainWindow window, StartupState startup)
    {
        try { await startup.DatabaseReady; } catch { /* the page shows its error state; capture it anyway */ }
        await Task.Delay(string.Equals(_options.Page, Sections.ItTools, StringComparison.OrdinalIgnoreCase) || _options.ProjectId != null || _options.DocumentId != null
            || string.Equals(_options.Page, Sections.Travel, StringComparison.OrdinalIgnoreCase) || _options.TripId != null || _options.PlaceId != null
            || string.Equals(_options.Page, Sections.Documents, StringComparison.OrdinalIgnoreCase)
            || _options.Page is Sections.Calendar or Sections.Tasks or Sections.Notes or Sections.Finance ? 3500 : 1200);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        FrameworkElement root = window.CaptureRoot;
        if (_options.Dialog != null)
        {
            var dialog = await CreateQaDialogAsync(_options.Dialog);
            dialog.Owner = window;
            dialog.Show();
            await Task.Delay(900);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            root = (FrameworkElement)dialog.Content;
        }
        root.UpdateLayout();
        double scale = _options.CaptureScale;
        var bitmap = new RenderTargetBitmap((int)Math.Round(root.ActualWidth * scale), (int)Math.Round(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        if (root.FlowDirection == FlowDirection.RightToLeft)
        {
            // The RTL mirror transform lives above the rendered element; re-apply it for the bitmap.
            var mirrored = new DrawingVisual();
            using (var dc = mirrored.RenderOpen())
            {
                dc.PushTransform(new MatrixTransform(-1, 0, 0, 1, root.ActualWidth, 0));
                // Pin the brush to the page bounds (off-page decorations such as animation tracks must not shift it).
                var page = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                dc.DrawRectangle(new VisualBrush(root) { Viewbox = page, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, page);
            }
            bitmap.Render(mirrored);
        }
        else
        {
            bitmap.Render(root);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.GetFullPath(_options.CapturePath!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var fs = File.Create(path)) encoder.Save(fs);
        _logger.LogInformation("Captured {Width}x{Height} @ {Scale}x to {Path}", bitmap.PixelWidth, bitmap.PixelHeight, scale, path);
        Shutdown(0);
    }

    private async Task<Window> CreateQaDialogAsync(string kind)
    {
        if (kind == "welcome")
        {
            var step = int.TryParse(_options.Tab, out var t) ? t : 0;
            var lang = LocalizationManager.Instance.Culture.TwoLetterISOLanguageName;
            return new WelcomeWindow(new WelcomeViewModel(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arcadia", "Library"))
            { Step = step, Language = lang });
        }
        var sp = _host!.Services;
        var loc = sp.GetRequiredService<ILocalizer>();
        var catalog = sp.GetRequiredService<Arcadia.Core.Games.IGameCatalog>();
        var media = sp.GetRequiredService<Arcadia.Core.Media.IMediaStore>();
        var game = await catalog.GetGameDetailAsync(_options.GameId ?? 1)
                   ?? new Arcadia.Core.Games.Game { Title = "Sample" };
        var org = sp.GetRequiredService<ViewModels.Organizer.OrganizerContext>();
        var categories = kind is "event" or "event-repeat" ? await sp.GetRequiredService<Arcadia.Core.Organizer.ICalendarStore>().GetCategoriesAsync() : [];
        var at = DateTime.Today.AddDays(1).AddHours(9);
        var fctx = sp.GetRequiredService<FinanceContext>();
        IReadOnlyList<Arcadia.Core.Finance.AccountBalance> fAccounts = kind.StartsWith("fin-", StringComparison.Ordinal) ? await fctx.Store.GetAccountBalancesAsync(true) : [];
        IReadOnlyList<Arcadia.Core.Finance.CategoryInfo> fCategories = kind.StartsWith("fin-", StringComparison.Ordinal) ? await fctx.Store.GetCategoriesAsync(fctx.Language, true) : [];
        Dialogs.DialogViewModel vm = kind switch
        {
            "event" => new Dialogs.EventEditorViewModel(org, categories, null, at, at.AddHours(1), false),
            "event-repeat" => new Dialogs.EventEditorViewModel(org, categories, new Arcadia.Core.Organizer.CalendarEvent
            {
                Id = 1, Title = "Standup", Start = at, End = at.AddMinutes(15), CategoryId = 2, ReminderMinutes = 5, Location = "Team room",
                Frequency = Arcadia.Core.Organizer.RecurrenceFrequency.Weekly, Weekdays = Arcadia.Core.Organizer.WeekdayMask.Monday | Arcadia.Core.Organizer.WeekdayMask.Wednesday,
            }, at, at.AddMinutes(15), false) { ShowMore = true },
            "scope" => new Dialogs.RecurrenceScopeViewModel(loc, deleting: false, isFirst: false),
            "picker" => new Dialogs.EntityPickerViewModel(org, [Arcadia.Core.Organizer.EntityKind.Project, Arcadia.Core.Organizer.EntityKind.Trip, Arcadia.Core.Organizer.EntityKind.Game]),
            "category" => new Dialogs.CategoryEditorViewModel(loc, null),
            "fin-transaction" => new Dialogs.TransactionEditorViewModel(fctx, fAccounts, fCategories, null,
                new Arcadia.Core.Finance.TransactionDraft(Arcadia.Core.Finance.TransactionType.Expense, fctx.Today, 35.5m, fAccounts.FirstOrDefault()?.Account.Id ?? 0, 2, "Weekly groceries", "REWE")) { ShowMore = true },
            "fin-account" => new Dialogs.AccountEditorViewModel(fctx, null, false),
            "fin-budget" => new Dialogs.BudgetEditorViewModel(fctx, fCategories, null, 2),
            "fin-recurring" => new Dialogs.RecurringEditorViewModel(fctx, fAccounts, fCategories, null, Arcadia.Core.Finance.RecurringKind.Subscription),
            "fin-goal" => new Dialogs.GoalEditorViewModel(fctx, fAccounts, null),
            "fin-categories" => new Dialogs.CategoryManagerViewModel(fctx),
            "fin-import" => await QaImportAsync(new Dialogs.ImportWizardViewModel(fctx, fAccounts, fCategories)),
            "fin-export" => new Dialogs.FinanceExportViewModel(fctx, sp.GetRequiredService<Arcadia.Core.Finance.IFinanceExporter>(), fAccounts, fCategories),
            "log" => new Dialogs.LogSessionViewModel(game, loc, TimeProvider.System),
            "shot" => new Dialogs.ScreenshotEditorViewModel(game.Screenshots.FirstOrDefault() ?? new Arcadia.Core.Games.Screenshot { Title = "Screenshot" }, null, loc),
            "confirm" => new Dialogs.ConfirmDialogViewModel(loc["Confirm_Title"], loc.Format("Confirm_DeleteGame", game.Title), loc["Confirm_Delete"], loc["Editor_Cancel"]),
            _ => new Dialogs.GameEditorViewModel(game, loc, sp.GetRequiredService<IDialogService>(), media.Resolve),
        };
        return new DialogWindow(vm);
    }

    /// <summary>QA only: ARCADIA_QA_CSV pre-loads a file and walks the wizard to its review step.</summary>
    private static async Task<Dialogs.ImportWizardViewModel> QaImportAsync(Dialogs.ImportWizardViewModel vm)
    {
        if (Environment.GetEnvironmentVariable("ARCADIA_QA_CSV") is { Length: > 0 } path && File.Exists(path))
        {
            vm.Load(path);
            await vm.NextCommand.ExecuteAsync(null);
            await vm.NextCommand.ExecuteAsync(null);
        }
        return vm;
    }

    private void BringToFront()
    {
        if (MainWindow is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        window.Topmost = true;   // reliably surfaces the window above the launching shell
        window.Topmost = false;
        window.Focus();
    }

    /// <summary>Set by "Reset settings": the fresh settings file written just before the restart must not be overwritten on exit.</summary>
    public static bool SuppressSettingsSave { get; set; }

    /// <summary>Starts a new Arcadia that waits for this one to exit (same data folder), then shuts this one down.</summary>
    public static void Restart()
    {
        var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        info.ArgumentList.Add("--wait-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (((App)Current)._options.DataDirectory is { } dir)
        {
            info.ArgumentList.Add("--data-dir");
            info.ArgumentList.Add(Path.GetFullPath(dir));
        }
        System.Diagnostics.Process.Start(info)?.Dispose();
        Current.Shutdown();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Unsaved note/description text is written before anything shuts down.
        try
        {
            if (_host?.Services.GetService<ViewModels.Projects.ProjectsViewModel>()?.Workspace is { } workspace)
                workspace.FlushForExit().Wait(TimeSpan.FromSeconds(3));
            ViewModels.Travel.TravelViewModel.Current?.FlushForExit().Wait(TimeSpan.FromSeconds(3));
            ViewModels.Organizer.NotesViewModel.Current?.FlushForExit().Wait(TimeSpan.FromSeconds(3));
            _host?.Services.GetService<ViewModels.Organizer.TasksViewModel>()?.Details.FlushForExit().Wait(TimeSpan.FromSeconds(3));
            // preferences changed moments ago (privacy mode, last section…) are written before exit
            if (!SuppressSettingsSave && _host?.Services.GetService<ISettingsService>() is { } settings) Task.Run(() => settings.SaveAsync()).Wait(TimeSpan.FromSeconds(3));
            // ARCADIA CLOSING automations (quick actions only, bounded), then Windows integration is released
            _host?.Services.GetService<AutomationService>()?.RaiseClosing(TimeSpan.FromSeconds(1.5));
            _host?.Services.GetService<AutomationService>()?.Dispose();
            _host?.Services.GetService<TrayService>()?.Dispose();
            _host?.Services.GetService<PerformanceService>()?.Dispose();
            _host?.Services.GetService<AutomaticBackupService>()?.Dispose();
            _host?.Services.GetService<ReminderService>()?.Dispose();
            _host?.Services.GetService<DocumentWatcherService>()?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Saving pending edits on exit failed");
        }
        _instance?.Dispose();
        if (_host != null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                _host.Dispose();
            }
        }
        base.OnExit(e);
    }

    // ── Error handling ─────────────────────────────────────────────────

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unhandled UI exception");
            args.Handled = true;
            ShowError(args.Exception, fatal: false);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.LogCritical(args.ExceptionObject as Exception, "Fatal unhandled exception (terminating: {Terminating})", args.IsTerminating);
    }

    private void ShowError(Exception ex, bool fatal)
    {
        if (_showingError || _options.IsCapture) return;
        _showingError = true;
        try
        {
            var logs = _host?.Services.GetService<IAppPaths>()?.LogsDirectory ?? new AppPaths().LogsDirectory;
            var dialog = new ErrorWindow(ex, logs, fatal);
            if (MainWindow is { IsVisible: true } owner && !ReferenceEquals(owner, dialog)) dialog.Owner = owner;
            dialog.ShowDialog();
        }
        finally
        {
            _showingError = false;
        }
    }
}
