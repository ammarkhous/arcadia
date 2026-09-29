using System.Text.Json;

namespace Arcadia.Data;

/// <summary>
/// Applies a staged restore at startup, before settings are read and before anything opens the database.
/// Every current item is moved aside first; the restored database is checked; any failure (or a crash half-way,
/// detected on the next start) moves everything back. The pre-restore safety archive stays in Backups either way.
/// </summary>
public static class BackupRestorer
{
    public const string PendingFile = "pending.json";
    private const string ApplyingFile = "applying";
    private const string ResultFile = "result.json";

    public sealed record PendingRestore(string Archive, string SafetyBackup, DateTime BackupCreatedUtc, string BackupVersion, List<string> Components, DateTime StagedUtc);

    public sealed record RestoreOutcome(bool Success, DateTime BackupCreatedUtc, string SafetyBackup, string? Error, DateTime AtUtc);

    public static string RestoreRoot(string dataDirectory) => Path.Combine(dataDirectory, ".restore");

    /// <summary>Runs a staged restore if there is one. Returns what happened (also kept for the UI), or null when nothing was pending.</summary>
    public static RestoreOutcome? ApplyPending(string dataDirectory, string settingsFile)
    {
        var root = RestoreRoot(dataDirectory);
        var pendingPath = Path.Combine(root, PendingFile);
        var applying = Path.Combine(root, ApplyingFile);
        var rollback = Path.Combine(root, "rollback");
        var staged = Path.Combine(root, "staged");
        var items = Items(dataDirectory, settingsFile);

        // a previous attempt stopped half-way (power loss, crash): put the original data back
        if (File.Exists(applying))
        {
            RollBack(items, rollback);
            var crashed = new RestoreOutcome(false, default, string.Empty, "interrupted", DateTime.UtcNow);
            Finish(root, crashed);
            return crashed;
        }
        if (!File.Exists(pendingPath)) return null;

        PendingRestore? pending;
        try { pending = JsonSerializer.Deserialize<PendingRestore>(File.ReadAllText(pendingPath)); }
        catch (JsonException) { pending = null; }
        if (pending == null || !Directory.Exists(staged))
        {
            var bad = new RestoreOutcome(false, default, string.Empty, "staging incomplete", DateTime.UtcNow);
            Finish(root, bad);
            return bad;
        }

        try
        {
            Directory.CreateDirectory(rollback);
            File.WriteAllText(applying, DateTime.UtcNow.ToString("O"));
            foreach (var item in items)
            {
                var from = Path.Combine(staged, item.Staged);
                var aside = Path.Combine(rollback, item.Name);
                MovePath(item.Live, aside);
                if (Exists(from)) MovePath(from, item.Live);
            }
            RestoreReferencedFiles(staged, dataDirectory);

            var problem = ArcadiaBackupService.CheckDatabaseFile(items[0].Live);
            if (problem != null) throw new InvalidDataException(problem);
            UseWriteAheadLog(items[0].Live);

            File.Delete(applying);
            var ok = new RestoreOutcome(true, pending.BackupCreatedUtc, pending.SafetyBackup, null, DateTime.UtcNow);
            Finish(root, ok);
            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RollBack(items, rollback);
            var failed = new RestoreOutcome(false, pending.BackupCreatedUtc, pending.SafetyBackup, ex.Message, DateTime.UtcNow);
            Finish(root, failed);
            return failed;
        }
    }

    /// <summary>The last restore's outcome, once (the file is removed when read).</summary>
    public static RestoreOutcome? TakeLastOutcome(string dataDirectory)
    {
        var path = Path.Combine(RestoreRoot(dataDirectory), ResultFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RestoreOutcome>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
        finally { try { Directory.Delete(RestoreRoot(dataDirectory), recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private sealed record Item(string Name, string Live, string Staged);

    // The database comes first (it is checked after the move); -wal/-shm travel with it so no stale journal is replayed.
    private static List<Item> Items(string dataDirectory, string settingsFile) =>
    [
        new("arcadia.db", Path.Combine(dataDirectory, "arcadia.db"), ArcadiaBackupService.DatabaseEntry),
        new("arcadia.db-wal", Path.Combine(dataDirectory, "arcadia.db-wal"), "arcadia.db-wal"),
        new("arcadia.db-shm", Path.Combine(dataDirectory, "arcadia.db-shm"), "arcadia.db-shm"),
        new("settings.json", settingsFile, Path.Combine("settings", "settings.json")),
        new("Media", Path.Combine(dataDirectory, "Media"), "media"),
        new("Oracle", Path.Combine(dataDirectory, "Oracle"), "oracle"),
    ];

    private static void RollBack(List<Item> items, string rollback)
    {
        foreach (var item in items)
        {
            var aside = Path.Combine(rollback, item.Name);
            if (!Exists(aside)) continue; // nothing was moved aside for this one
            if (Exists(item.Live)) DeletePath(item.Live);
            MovePath(aside, item.Live);
        }
    }

    /// <summary>Referenced files go to "Restored files\&lt;date&gt;\…", never over the originals.</summary>
    private static void RestoreReferencedFiles(string staged, string dataDirectory)
    {
        var refs = Path.Combine(staged, "references");
        if (!Directory.Exists(refs)) return;
        var target = Path.Combine(dataDirectory, "Restored files", DateTime.Now.ToString("yyyy-MM-dd HHmmss"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(refs, target);
    }

    private static void Finish(string root, RestoreOutcome outcome)
    {
        foreach (var dir in new[] { "staged", "rollback" })
        {
            var path = Path.Combine(root, dir);
            // after a failure, keep whatever could not be moved back: it is the user's data
            if (dir == "rollback" && !outcome.Success && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) continue;
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var file in new[] { PendingFile, ApplyingFile })
            try { File.Delete(Path.Combine(root, file)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ResultFile), JsonSerializer.Serialize(outcome));
    }

    private static void UseWriteAheadLog(string db)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL";
        cmd.ExecuteScalar();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void MovePath(string from, string to)
    {
        if (!Exists(from)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, overwrite: false);
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }
}
