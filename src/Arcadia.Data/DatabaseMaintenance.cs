using Microsoft.Data.Sqlite;

namespace Arcadia.Data;

public sealed record DatabaseHealth(bool Ok, string Integrity, int ForeignKeyProblems, long Bytes, long FreeBytes, string SchemaVersion, TimeSpan Took);

/// <summary>"Check database" (Settings → Diagnostics): SQLite's own integrity and foreign-key checks on a read-only connection. Never repairs or rewrites anything.</summary>
public static class DatabaseMaintenance
{
    public static Task<DatabaseHealth> CheckAsync(string databasePath, CancellationToken ct = default) => Task.Run(() =>
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var c = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        c.Open();
        string Scalar(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
        ct.ThrowIfCancellationRequested();
        var integrity = Scalar("PRAGMA integrity_check");
        ct.ThrowIfCancellationRequested();
        int fk = 0;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_key_check";
            using var r = cmd.ExecuteReader();
            while (r.Read()) fk++;
        }
        var pageSize = long.Parse(Scalar("PRAGMA page_size"), System.Globalization.CultureInfo.InvariantCulture);
        var free = long.Parse(Scalar("PRAGMA freelist_count"), System.Globalization.CultureInfo.InvariantCulture) * pageSize;
        var version = Scalar("SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1");
        var bytes = new FileInfo(databasePath).Length;
        var ok = string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase) && fk == 0;
        return new DatabaseHealth(ok, integrity, fk, bytes, free, version, clock.Elapsed);
    }, ct);
}
