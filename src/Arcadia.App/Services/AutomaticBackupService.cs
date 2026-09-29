using Arcadia.App.Diagnostics;
using Arcadia.App.ViewModels;
using Arcadia.Core.Platform;
using Arcadia.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>
/// Automatic backups (Settings → Backup &amp; recovery): daily, weekly or once per new Arcadia version. Checked every
/// 30 minutes; never while Gaming Mode is on or memory is under pressure; the newest N automatic archives are kept
/// (archives the user made by hand are never removed). Off by default: nothing runs and no timer exists.
/// </summary>
public sealed class AutomaticBackupService(IArcadiaBackupService backup, ISettingsService settings, PerformanceService performance,
    TimeProvider time, ILogger<AutomaticBackupService> logger) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private ITimer? _timer;
    private int _running;

    public void Start()
    {
        settings.SettingsChanged += (_, _) => Arm();
        Arm();
    }

    private void Arm()
    {
        var on = settings.Current.Backup.Schedule is "daily" or "weekly" or "update";
        if (on && _timer == null) _timer = time.CreateTimer(_ => _ = RunIfDueAsync(), null, TimeSpan.FromMinutes(2), Interval);
        else if (!on && _timer != null) { _timer.Dispose(); _timer = null; }
    }

    /// <summary>True when the schedule says a backup is due now.</summary>
    public static bool IsDue(BackupSettings b, DateTime nowUtc, string version) => b.Schedule switch
    {
        "daily" => b.LastAutomaticUtc is not { } last || nowUtc - last >= TimeSpan.FromHours(23),
        "weekly" => b.LastAutomaticUtc is not { } last || nowUtc - last >= TimeSpan.FromDays(7) - TimeSpan.FromHours(1),
        "update" => !string.Equals(b.LastVersion, version, StringComparison.Ordinal),
        _ => false,
    };

    private async Task RunIfDueAsync()
    {
        var b = settings.Current.Backup;
        if (!IsDue(b, time.GetUtcNow().UtcDateTime, AppInfo.Version)) return;
        if (performance.IsGamingMode || performance.IsMemoryPressure) return; // later, when the game is over
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var folder = MaintenanceViewModel.ResolveFolder(b.Folder);
            var result = await backup.CreateAsync(folder, new BackupOptions(b.IncludeReferencedFiles), null, CancellationToken.None,
                $"{MaintenanceViewModel.AutoPrefix}{DateTime.Now:yyyy-MM-dd HHmmss}{Arcadia.Data.ArcadiaBackupService.Extension}").ConfigureAwait(false);
            b.LastAutomaticUtc = time.GetUtcNow().UtcDateTime;
            b.LastVersion = AppInfo.Version;
            await settings.SaveAsync().ConfigureAwait(false);
            var removed = backup.Prune(folder, Math.Max(1, b.Keep), MaintenanceViewModel.AutoPrefix);
            logger.LogInformation("Automatic backup written ({Bytes} bytes; {Removed} older automatic backups removed)", result.Bytes, removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(ex, "Automatic backup failed; it is tried again later");
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    public void Dispose() => _timer?.Dispose();
}
