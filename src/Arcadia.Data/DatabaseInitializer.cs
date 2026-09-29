using Arcadia.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arcadia.Data;

public sealed class DatabaseInitializer(
    IDbContextFactory<ArcadiaDbContext> contextFactory,
    TimeProvider time,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(bool seedSampleData, CancellationToken cancellationToken = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var upToDate = await EnsureDatabaseIsOursAsync(db, cancellationToken).ConfigureAwait(false);
        var checkedMs = clock.Elapsed.TotalMilliseconds;
        // Migrate() also diffs the model against the snapshot, which is expensive; skip it when nothing is pending.
        if (!upToDate)
        {
            await BackupBeforeMigrationAsync(db, cancellationToken).ConfigureAwait(false);
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        var migratedMs = clock.Elapsed.TotalMilliseconds;
        await EnsureSearchIndexAsync(db, cancellationToken).ConfigureAwait(false);
        await DetachMissingContextsAsync(db, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Database ready at {DataSource}", db.Database.GetDbConnection().DataSource);

        if (seedSampleData && !await db.Games.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            SampleDataSeeder.Seed(db, time.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Seeded development sample library");
        }
        else if (seedSampleData)
        {
            var added = SampleDataSeeder.AddMissingSessions(db);
            if (added > 0)
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Added {Count} sample play sessions to the existing sample library", added);
            }
        }
        logger.LogInformation("Perf: database init check {Check:0} ms, migrate {Migrate:0} ms ({Skipped}), seed {Seed:0} ms",
            checkedMs, migratedMs - checkedMs, upToDate ? "up to date" : "applied", clock.Elapsed.TotalMilliseconds - migratedMs);
    }

    /// <summary>
    /// The first start of a new Arcadia version (installer upgrade) copies the library to
    /// <c>Backupsrcadia-before-update-&lt;version&gt;-&lt;time&gt;.db</c> first, schema change or not. Newest five kept.
    /// Nothing happens for a new library. A failed copy is logged and does not block the start.
    /// </summary>
    public async Task BackupBeforeUpdateAsync(string databasePath, string newVersion, CancellationToken ct = default)
    {
        if (!File.Exists(databasePath)) return;
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(databasePath)!, "Backups");
            Directory.CreateDirectory(folder);
            var safe = string.Concat(newVersion.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-'));
            var target = Path.Combine(folder, $"arcadia-before-update-{safe}-{time.GetLocalNow():yyyyMMdd-HHmmss}.db");
            await using (var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False"))
            await using (var copy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={target};Pooling=False"))
            {
                await source.OpenAsync(ct).ConfigureAwait(false);
                await copy.OpenAsync(ct).ConfigureAwait(false);
                source.BackupDatabase(copy);
            }
            foreach (var old in new DirectoryInfo(folder).GetFiles("arcadia-before-update-*.db").OrderByDescending(f => f.CreationTimeUtc).Skip(5)) old.Delete();
            logger.LogInformation("Backed up the library before the first start of Arcadia {Version}: {Path}", newVersion, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(ex, "Could not back up the library before the update; continuing");
        }
    }

    /// <summary>
    /// Before an existing library is upgraded, a consistent copy goes to <c>Backups\arcadia-before-&lt;migration&gt;-&lt;time&gt;.db</c>
    /// next to it (the newest five are kept). Migrations are additive and transactional; this is the safety net anyway.
    /// A failed copy is logged and does not block the upgrade.
    /// </summary>
    private async Task BackupBeforeMigrationAsync(ArcadiaDbContext db, CancellationToken ct)
    {
        var dataSource = db.Database.GetDbConnection().DataSource;
        if (!File.Exists(dataSource)) return;
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        if (applied.Count == 0) return; // a brand-new file has nothing to lose
        var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).LastOrDefault() ?? "update";
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(dataSource)!, "Backups");
            Directory.CreateDirectory(folder);
            var name = pending.Contains('_', StringComparison.Ordinal) ? pending[(pending.IndexOf('_', StringComparison.Ordinal) + 1)..] : pending;
            var target = Path.Combine(folder, $"arcadia-before-{name}-{time.GetLocalNow():yyyyMMdd-HHmmss}.db");
            await using (var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dataSource};Mode=ReadOnly;Pooling=False"))
            await using (var copy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={target};Pooling=False"))
            {
                await source.OpenAsync(ct).ConfigureAwait(false);
                await copy.OpenAsync(ct).ConfigureAwait(false);
                source.BackupDatabase(copy);
            }
            foreach (var old in new DirectoryInfo(folder).GetFiles("arcadia-before-*.db").OrderByDescending(f => f.CreationTimeUtc).Skip(5)) old.Delete();
            logger.LogInformation("Backed up the library before upgrading it to {Migration}: {Path}", pending, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(ex, "Could not back up the library before upgrading it; continuing with the upgrade");
        }
    }

    /// <summary>
    /// Refuses to migrate a database that Arcadia did not create (tables without Arcadia's migration history,
    /// or migrations Arcadia does not know). Such a file is left completely untouched.
    /// Returns true when every known migration is already applied.
    /// </summary>
    private static async Task<bool> EnsureDatabaseIsOursAsync(ArcadiaDbContext db, CancellationToken ct)
    {
        var dataSource = db.Database.GetDbConnection().DataSource;
        if (!File.Exists(dataSource) || new FileInfo(dataSource).Length == 0) return false;

        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        if (applied.Any(m => !known.Contains(m))) throw new IncompatibleDatabaseException(dataSource);
        if (applied.Count > 0) return applied.Count == known.Count;

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EFMigrations%'";
            var tables = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (tables > 0) throw new IncompatibleDatabaseException(dataSource);
            return false;
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The FTS index is derived data: if it is empty while projects, documents or travel data exist (e.g. a restored backup),
    /// rebuild it. Cheap when there is nothing to do.
    /// </summary>
    private async Task EnsureSearchIndexAsync(ArcadiaDbContext db, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM SearchIndex), (SELECT COUNT(*) FROM Projects) + (SELECT COUNT(*) FROM Documents) + (SELECT COUNT(*) FROM Trips) + (SELECT COUNT(*) FROM TravelPlaces) + (SELECT COUNT(*) FROM TravelJournalEntries) + (SELECT COUNT(*) FROM Tasks) + (SELECT COUNT(*) FROM Notes) + (SELECT COUNT(*) FROM CalendarEvents) + (SELECT COUNT(*) FROM FinanceTransactions) + (SELECT COUNT(*) FROM FinanceAccounts)";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return;
        var (indexed, entities) = (r.GetInt64(0), r.GetInt64(1));
        await r.DisposeAsync().ConfigureAwait(false);
        if (indexed > 0) await Search.SearchIndex.RekeyLegacyRowsAsync(db, logger, ct).ConfigureAwait(false);
        else if (entities > 0) await Search.SearchIndex.RebuildAsync(db, logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tasks, notes and events that pointed at a record since deleted (a trip, a game, a document…) become free-standing.
    /// One set-based statement per table and kind at startup; nothing else in Arcadia has to know about these links.
    /// </summary>
    internal static async Task DetachMissingContextsAsync(ArcadiaDbContext db, CancellationToken ct)
    {
        (string Kind, string Table)[] owners =
        [
            ("Game", "Games"), ("Document", "Documents"), ("Trip", "Trips"), ("Place", "TravelPlaces"), ("Diagnostic", "DiagnosticSessions"),
            ("Journal", "TravelJournalEntries"), ("Task", "Tasks"), ("Note", "Notes"), ("Event", "CalendarEvents"), ("Project", "Projects"),
        ];
        foreach (var table in new[] { "Tasks", "Notes", "CalendarEvents" })
            foreach (var (kind, owner) in owners)
#pragma warning disable EF1002 // identifiers are fixed constants above, the kind is a parameter
                await db.Database.ExecuteSqlRawAsync(
                    $"UPDATE \"{table}\" SET \"ContextKind\" = NULL, \"ContextId\" = NULL WHERE \"ContextKind\" = {{0}} AND \"ContextId\" NOT IN (SELECT \"Id\" FROM \"{owner}\")",
                    [kind], ct).ConfigureAwait(false);
#pragma warning restore EF1002
    }

    /// <summary>Removes all rows flagged as sample content, leaving user data untouched.</summary>
    public async Task RemoveSampleDataAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.GameSessions.Where(x => x.IsSample).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.JournalEntries.Where(x => x.IsSample).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Screenshots.Where(x => x.IsSample).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Games.Where(x => x.IsSample).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The database file exists but was not created by Arcadia; it has not been modified.</summary>
public sealed class IncompatibleDatabaseException(string path)
    : InvalidOperationException($"The database at '{path}' was not created by Arcadia and was left untouched.")
{
    public string DatabasePath { get; } = path;
}