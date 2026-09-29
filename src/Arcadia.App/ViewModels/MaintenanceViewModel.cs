using System.Diagnostics;
using System.Text;
using Arcadia.App.Diagnostics;
using Arcadia.App.Localization;
using Arcadia.App.Services;
using Arcadia.Core.ItTools.Files;
using Arcadia.Core.Localization;
using Arcadia.Core.Platform;
using Arcadia.Core.Settings;
using Arcadia.Data;
using Arcadia.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels;

/// <summary>
/// Settings → Backup &amp; recovery and Diagnostics &amp; maintenance (Phase 10). Everything is local: archives go to a folder
/// the user picks; restores are validated, preceded by a safety backup, applied on restart and rolled back on failure.
/// Nothing here deletes library content: "Clear cache" only removes regenerable files, "Reset settings" only preferences.
/// </summary>
public sealed partial class MaintenanceViewModel : ObservableObject
{
    public const string AutoPrefix = "Arcadia auto ";

    private readonly IArcadiaBackupService _backup;
    private readonly DataLocations _locations;
    private readonly ISettingsService _settings;
    private readonly LocalizationManager _loc;
    private readonly IDialogService _dialogs;
    private readonly IShellLauncher _launcher;
    private readonly IExplorerLauncher _explorer;
    private readonly IClipboardService _clipboard;
    private readonly INotificationService _notifications;
    private readonly PerformanceService _performance;
    private readonly Arcadia.Core.Automation.IAutomationService _automation;
    private readonly ILogger<MaintenanceViewModel> _logger;
    private CancellationTokenSource? _cts;
    private readonly bool _initializing = true;

    public MaintenanceViewModel(IArcadiaBackupService backup, DataLocations locations, ISettingsService settings, ILocalizer loc, IDialogService dialogs,
        IShellLauncher launcher, IExplorerLauncher explorer, IClipboardService clipboard, INotificationService notifications,
        PerformanceService performance, Arcadia.Core.Automation.IAutomationService automation, ILogger<MaintenanceViewModel> logger)
    {
        _backup = backup;
        _locations = locations;
        _settings = settings;
        _loc = (LocalizationManager)loc;
        _dialogs = dialogs;
        _launcher = launcher;
        _explorer = explorer;
        _clipboard = clipboard;
        _notifications = notifications;
        _performance = performance;
        _automation = automation;
        _logger = logger;
        var b = settings.Current.Backup;
        IncludeReferencedFiles = b.IncludeReferencedFiles;
        Schedule = Schedules.FirstOrDefault(o => o.Value == b.Schedule) ?? Schedules[0];
        Keep = KeepChoices.FirstOrDefault(o => o.Value == b.Keep) ?? KeepChoices[1];
        _initializing = false;
    }

    /// <summary>Called when Settings is shown.</summary>
    public Task ActivateAsync()
    {
        RefreshLastBackup();
        if (IncludeReferencedFiles) _ = EstimateAsync();
        return RefreshDiagnosticsAsync();
    }

    // ═══════════════════════ Backup ═══════════════════════

    public string BackupFolder => ResolveFolder(_settings.Current.Backup.Folder);

    public static string ResolveFolder(string? configured) => string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Arcadia Backups")
        : configured;

    [ObservableProperty] private bool _includeReferencedFiles;
    [ObservableProperty] private string? _referencedEstimate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _stageText;
    [ObservableProperty] private string? _backupResult;
    [ObservableProperty] private bool _backupFailed;
    [ObservableProperty] private string? _lastBackupText;
    [ObservableProperty] private string? _lastArchivePath;
    [ObservableProperty] private Dialogs.Option<string>? _schedule;
    [ObservableProperty] private Dialogs.Option<int>? _keep;

    public IReadOnlyList<Dialogs.Option<string>> Schedules => _schedules ??=
        [new("off", _loc["Backup_ScheduleOff"]), new("daily", _loc["Backup_ScheduleDaily"]), new("weekly", _loc["Backup_ScheduleWeekly"]), new("update", _loc["Backup_ScheduleUpdate"])];
    private IReadOnlyList<Dialogs.Option<string>>? _schedules;

    public IReadOnlyList<Dialogs.Option<int>> KeepChoices => _keepChoices ??=
        [new(3, _loc.Format("Backup_KeepN", 3)), new(5, _loc.Format("Backup_KeepN", 5)), new(10, _loc.Format("Backup_KeepN", 10)), new(20, _loc.Format("Backup_KeepN", 20))];
    private IReadOnlyList<Dialogs.Option<int>>? _keepChoices;

    partial void OnIncludeReferencedFilesChanged(bool value)
    {
        if (_initializing) return;
        _settings.Current.Backup.IncludeReferencedFiles = value;
        Save();
        if (value) _ = EstimateAsync(); else ReferencedEstimate = null;
    }

    partial void OnScheduleChanged(Dialogs.Option<string>? value)
    {
        if (_initializing || value == null) return;
        _settings.Current.Backup.Schedule = value.Value;
        Save();
    }

    partial void OnKeepChanged(Dialogs.Option<int>? value)
    {
        if (_initializing || value == null) return;
        _settings.Current.Backup.Keep = value.Value;
        Save();
    }

    private async Task EstimateAsync()
    {
        try
        {
            var e = await _backup.EstimateReferencedFilesAsync();
            ReferencedEstimate = e.Missing > 0
                ? _loc.Format("Backup_RefEstimateMissing", e.Files, ByteSize.Format(e.Bytes, _loc.Culture), e.Missing)
                : _loc.Format("Backup_RefEstimate", e.Files, ByteSize.Format(e.Bytes, _loc.Culture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogWarning(ex, "Could not estimate referenced files");
        }
    }

    [RelayCommand]
    private void ChangeFolder()
    {
        var folder = _dialogs.PickFolder(BackupFolder);
        if (folder == null) return;
        _settings.Current.Backup.Folder = folder;
        Save();
        OnPropertyChanged(nameof(BackupFolder));
        RefreshLastBackup();
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        Directory.CreateDirectory(BackupFolder);
        if (LastArchivePath != null && File.Exists(LastArchivePath)) _explorer.Reveal(LastArchivePath);
        else _launcher.OpenFolder(BackupFolder);
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        BackupFailed = false;
        BackupResult = null;
        _cts = new CancellationTokenSource();
        var progress = new Progress<BackupProgress>(p =>
        {
            Progress = p.Fraction * 100; // 0–100 for the bar
            StageText = _loc["Backup_Stage" + p.Stage];
        });
        try
        {
            var result = await _backup.CreateAsync(BackupFolder, new BackupOptions(IncludeReferencedFiles), progress, _cts.Token);
            LastArchivePath = result.Path;
            BackupResult = _loc.Format("Backup_ResultOk", Path.GetFileName(result.Path), ByteSize.Format(result.Bytes, _loc.Culture), result.Manifest.Entries.Count);
            _notifications.Show(_loc["Backup_ResultToast"], NotificationKind.Success);
            RefreshLastBackup();
        }
        catch (OperationCanceledException)
        {
            BackupResult = _loc["Backup_Cancelled"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogError(ex, "Backup failed");
            BackupFailed = true;
            BackupResult = _loc.Format("Backup_ResultFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            StageText = null;
            Progress = 0;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void RefreshLastBackup()
    {
        try
        {
            var newest = Directory.Exists(BackupFolder)
                ? new DirectoryInfo(BackupFolder).GetFiles("*" + ArcadiaBackupService.Extension).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            LastArchivePath = newest?.FullName;
            LastBackupText = newest == null
                ? _loc["Backup_LastNever"]
                : _loc.Format("Backup_Last", newest.LastWriteTime.ToString("g", _loc.Culture), ByteSize.Format(newest.Length, _loc.Culture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastBackupText = _loc["Backup_LastNever"];
        }
    }

    // ═══════════════════════ Restore ═══════════════════════

    [ObservableProperty] private BackupValidation? _candidate;
    [ObservableProperty] private string? _candidateSummary;
    [ObservableProperty] private string? _candidateContents;
    [ObservableProperty] private string? _candidateError;
    [ObservableProperty] private bool _isConfirmingRestore;
    public bool HasCandidate => Candidate?.IsValid == true;

    partial void OnCandidateChanged(BackupValidation? value) => OnPropertyChanged(nameof(HasCandidate));

    [RelayCommand]
    private async Task ChooseRestoreAsync()
    {
        if (IsBusy) return;
        var file = _dialogs.PickFile(_loc["Backup_FileFilter"] + "|*" + ArcadiaBackupService.Extension);
        if (file == null) return;
        Candidate = null;
        CandidateError = null;
        IsConfirmingRestore = false;
        IsBusy = true;
        StageText = _loc["Backup_StageValidating"];
        try
        {
            var check = await _backup.ValidateAsync(file);
            if (!check.IsValid)
            {
                CandidateError = string.Join(" ", check.Errors.Select(e => _loc[e]));
                return;
            }
            var m = check.Manifest!;
            Candidate = check;
            CandidateSummary = _loc.Format("Backup_CandidateSummary", m.CreatedUtc.ToLocalTime().ToString("f", _loc.Culture), m.ArcadiaVersion,
                ByteSize.Format(check.ArchiveBytes, _loc.Culture), ByteSize.Format(m.Entries.Sum(e => e.Size), _loc.Culture));
            CandidateContents = string.Join(" · ", m.Components.Select(c => _loc["Backup_Component_" + c]))
                + (m.References.Count > 0 ? " · " + _loc.Format("Backup_RefCount", m.References.Count) : string.Empty);
        }
        finally
        {
            IsBusy = false;
            StageText = null;
        }
    }

    [RelayCommand]
    private void DismissRestore()
    {
        Candidate = null;
        CandidateError = null;
        IsConfirmingRestore = false;
    }

    /// <summary>Two steps: the first press arms the button (the warning is on screen), the second stages and restarts.</summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (Candidate is not { IsValid: true } candidate || IsBusy) return;
        if (!IsConfirmingRestore)
        {
            IsConfirmingRestore = true;
            return;
        }
        IsBusy = true;
        StageText = _loc["Backup_StageSafety"];
        try
        {
            var safety = await _backup.StageRestoreAsync(candidate.Path);
            _logger.LogInformation("Restore staged; restarting (safety backup {Safety})", safety);
            App.Restart();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogError(ex, "Restore could not be prepared");
            CandidateError = _loc.Format("Backup_RestoreFailed", ex.Message);
            IsConfirmingRestore = false;
        }
        finally
        {
            IsBusy = false;
            StageText = null;
        }
    }

    // ═══════════════════════ Diagnostics ═══════════════════════

    [ObservableProperty] private IReadOnlyList<ItTools.InfoRow> _diagnostics = [];
    [ObservableProperty] private string? _databaseCheck;
    [ObservableProperty] private bool _databaseCheckFailed;
    [ObservableProperty] private string? _maintenanceStatus;
    [ObservableProperty] private bool _isConfirmingReset;

    public string VersionText => _loc.Format("Settings_Version", AppInfo.Version);
    /// <summary>Shown on its own line, left-to-right (a path in the RTL key/value list was clipped from its start).</summary>
    public string DataFolderPath => _locations.DataDirectory;

    [RelayCommand]
    private async Task RefreshDiagnosticsAsync()
    {
        var (db, cache, logs, media, backups) = await Task.Run(() => (
            FileSize(_locations.DatabasePath) + FileSize(_locations.DatabasePath + "-wal"),
            FolderSize(_locations.CacheDirectory), FolderSize(_locations.LogsDirectory), FolderSize(_locations.MediaDirectory),
            FolderSize(Path.Combine(_locations.DataDirectory, "Backups"))));
        using var self = Process.GetCurrentProcess();
        var active = _automation.Automations.Count(a => a.Enabled);
        var mode = _performance.IsGamingMode ? _loc["Diag_ModeGaming"] : _performance.IsPowerSaver ? _loc["Diag_ModePowerSaver"] : _loc["Diag_ModeNormal"];
        string Size(long b) => ByteSize.Format(b, _loc.Culture);
        Diagnostics =
        [
            new(_loc["Diag_Version"], $"{AppInfo.Version} · {AppInfo.Configuration} · {(AppInfo.IsSelfContained ? _loc["Diag_SelfContained"] : _loc["Diag_FrameworkDependent"])}"),
            new(_loc["Diag_Runtime"], $"{AppInfo.Runtime} · {Environment.OSVersion.VersionString}"),
            new(_loc["Diag_Database"], Size(db)),
            new(_loc["Diag_Media"], Size(media)),
            new(_loc["Diag_Cache"], Size(cache)),
            new(_loc["Diag_Logs"], Size(logs)),
            new(_loc["Diag_SafetyBackups"], Size(backups)),
            new(_loc["Diag_Automations"], _automation.IsPaused ? _loc.Format("Diag_AutomationsPaused", active) : active.ToString(_loc.Culture)),
            new(_loc["Diag_Mode"], mode),
            new(_loc["Diag_Memory"], _loc.Format("Diag_MemoryValue", Size(self.WorkingSet64), Size(self.PrivateMemorySize64))),
        ];
    }

    [RelayCommand]
    private void OpenLogs() => _launcher.OpenFolder(_locations.LogsDirectory);

    [RelayCommand]
    private void OpenDataFolder() => _launcher.OpenFolder(_locations.DataDirectory);

    [RelayCommand]
    private async Task CheckDatabaseAsync()
    {
        DatabaseCheck = _loc["Diag_Checking"];
        DatabaseCheckFailed = false;
        try
        {
            var h = await DatabaseMaintenance.CheckAsync(_locations.DatabasePath);
            DatabaseCheckFailed = !h.Ok;
            DatabaseCheck = h.Ok
                ? _loc.Format("Diag_DbOk", ByteSize.Format(h.Bytes, _loc.Culture), h.Took.TotalSeconds.ToString("0.0", _loc.Culture))
                : _loc.Format("Diag_DbProblem", h.Integrity, h.ForeignKeyProblems);
            _logger.Log(h.Ok ? LogLevel.Information : LogLevel.Error, "Database check: integrity {Integrity}, foreign-key problems {Fk}, {Ms:0} ms", h.Integrity, h.ForeignKeyProblems, h.Took.TotalMilliseconds);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _logger.LogError(ex, "Database check failed");
            DatabaseCheckFailed = true;
            DatabaseCheck = _loc.Format("Diag_DbProblem", ex.SqliteErrorCode, 0);
        }
    }

    /// <summary>Thumbnails, downloaded artwork and map tiles: all regenerated on demand. Library content is never touched.</summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        var freed = await Task.Run(() =>
        {
            long total = 0;
            if (!Directory.Exists(_locations.CacheDirectory)) return 0L;
            foreach (var file in Directory.EnumerateFiles(_locations.CacheDirectory, "*", SearchOption.AllDirectories))
            {
                try { var len = new FileInfo(file).Length; File.Delete(file); total += len; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // in use right now; next time
            }
            return total;
        });
        freed += Arcadia.UI.Artwork.ImageCache.Trim();
        MaintenanceStatus = _loc.Format("Diag_CacheCleared", ByteSize.Format(freed, _loc.Culture));
        _logger.LogInformation("Cache cleared ({Bytes} bytes)", freed);
        await RefreshDiagnosticsAsync();
    }

    /// <summary>Copies a plain-text summary for a bug report: versions, sizes, modes. No paths beyond the data folder, no content, no secrets.</summary>
    [RelayCommand]
    private void CopyDiagnostics()
    {
        var sb = new StringBuilder("Arcadia diagnostics").AppendLine();
        sb.Append(_loc["Diag_DataFolder"]).Append(": ").AppendLine(_locations.DataDirectory);
        foreach (var row in Diagnostics) sb.Append(row.Label).Append(": ").AppendLine(row.Value);
        if (DatabaseCheck != null) sb.Append("Database check: ").AppendLine(DatabaseCheck);
        if (_clipboard.SetText(sb.ToString())) _notifications.Show(_loc["Diag_Copied"], NotificationKind.Success);
    }

    /// <summary>
    /// Restores default preferences (a copy of the old file is kept as settings.before-reset.json). Library content,
    /// saved API keys and the data-folder choice are kept. Arcadia restarts to apply everything cleanly.
    /// </summary>
    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        if (!IsConfirmingReset)
        {
            IsConfirmingReset = true;
            _ = DisarmLaterAsync();
            return;
        }
        try
        {
            var file = _locations.SettingsFile;
            if (File.Exists(file)) File.Copy(file, Path.Combine(Path.GetDirectoryName(file)!, "settings.before-reset.json"), overwrite: true);
            var fresh = new AppSettings
            {
                DataDirectory = _settings.Current.DataDirectory,
                Language = _settings.Current.Language,
                FirstRunCompleted = true,
            };
            await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(fresh, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            }));
            _logger.LogInformation("Settings reset to defaults; restarting");
            App.SuppressSettingsSave = true;
            App.Restart();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Settings reset failed");
            MaintenanceStatus = ex.Message;
            IsConfirmingReset = false;
        }
    }

    private async Task DisarmLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        IsConfirmingReset = false;
    }

    private void Save() => _ = _settings.SaveAsync();

    private static long FileSize(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch (IOException) { return 0; } }

    private static long FolderSize(string path)
    {
        try { return Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
