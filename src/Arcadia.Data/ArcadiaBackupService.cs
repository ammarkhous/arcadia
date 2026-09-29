using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arcadia.Core.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace Arcadia.Data;

/// <summary>
/// <c>.arcadia-backup</c> archives: a zip with a versioned manifest and a SHA-256 per entry. Contents: a consistent copy of
/// the database (online backup API), settings.json, the managed Media folder and the Oracle history; optionally copies of
/// referenced document files. Never included: secrets.dat (DPAPI-bound to this Windows user), caches, logs.
/// An archive is only reported as written after it has been re-opened and fully validated.
/// </summary>
public sealed class ArcadiaBackupService(DataLocations locations, string appVersion, ILogger<ArcadiaBackupService> logger) : IArcadiaBackupService
{
    public const string Extension = ".arcadia-backup";
    public const string DatabaseEntry = "arcadia.db";
    public const string SettingsEntry = "settings/settings.json";
    public const string MediaPrefix = "media/";
    public const string OraclePrefix = "oracle/";
    public const string ReferencesPrefix = "references/";

    // Guards for archives from elsewhere (malformed, hostile or simply not ours).
    internal const int MaxEntries = 500_000;
    internal const long MaxManifestBytes = 16L * 1024 * 1024;
    internal const long SuspiciousRatioFloor = 64L * 1024 * 1024;
    internal const int MaxCompressionRatio = 1000;

    private static readonly HashSet<string> Stored = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".mp4", ".mkv", ".zip", ".7z", ".rar", ".pdf", ".docx", ".xlsx", ".pptx", ".mp3" };

    private string OracleDirectory => Path.Combine(locations.DataDirectory, "Oracle");

    public Task<ReferencedFilesEstimate> EstimateReferencedFilesAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        int files = 0, missing = 0; long bytes = 0;
        foreach (var path in ReferencedPaths(locations.DatabasePath))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Exists) { files++; bytes += info.Length; } else missing++;
        }
        return new ReferencedFilesEstimate(files, bytes, missing);
    }, ct);

    public Task<ArchiveResult> CreateAsync(string targetFolder, BackupOptions options, IProgress<BackupProgress>? progress = null, CancellationToken ct = default, string? fileName = null) => Task.Run(async () =>
    {
        progress?.Report(new BackupProgress(BackupStage.Preparing, 0));
        Directory.CreateDirectory(targetFolder);
        var name = fileName ?? $"Arcadia backup {DateTime.Now:yyyy-MM-dd HHmmss}{Extension}";
        var final = UniquePath(Path.Combine(targetFolder, name));
        var partial = final + ".partial";
        var dbCopy = Path.Combine(targetFolder, $".arcadia-db-{Guid.NewGuid():N}.tmp");
        try
        {
            // 1. consistent database copy (safe while Arcadia is running)
            progress?.Report(new BackupProgress(BackupStage.Database, 0.02));
            using (var source = new SqliteConnection($"Data Source={locations.DatabasePath};Mode=ReadOnly;Pooling=False"))
            using (var target = new SqliteConnection($"Data Source={dbCopy};Pooling=False"))
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
                // a single self-contained file (the live library runs in WAL mode; the restorer switches it back)
                using var journal = target.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode=DELETE";
                journal.ExecuteScalar();
            }
            var dbVersion = LatestMigration(dbCopy) ?? string.Empty;

            // 2. the plan: (entry name, source file, stage)
            var plan = new List<(string Entry, string File, BackupStage Stage)> { (DatabaseEntry, dbCopy, BackupStage.Database) };
            var components = new List<string> { "database" };
            if (File.Exists(locations.SettingsFile)) { plan.Add((SettingsEntry, locations.SettingsFile, BackupStage.Settings)); components.Add("settings"); }
            if (AddFolder(plan, locations.MediaDirectory, MediaPrefix, BackupStage.Media)) components.Add("media");
            if (AddFolder(plan, OracleDirectory, OraclePrefix, BackupStage.History)) components.Add("oracle");
            var references = new List<BackupReference>();
            if (options.IncludeReferencedFiles)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in ReferencedPaths(dbCopy))
                {
                    if (!File.Exists(path)) continue;
                    var entry = ReferenceEntryName(path);
                    if (entry == null || !seen.Add(entry)) continue;
                    plan.Add((entry, path, BackupStage.ReferencedFiles));
                    references.Add(new BackupReference { Entry = entry, Original = path });
                }
                if (references.Count > 0) components.Add("references");
            }

            // 3. write, hashing as we go
            var total = Math.Max(1, plan.Sum(p => SafeLength(p.File)));
            long done = 0;
            var manifest = new BackupManifest
            {
                ArcadiaVersion = appVersion, CreatedUtc = DateTime.UtcNow, DatabaseVersion = dbVersion,
                Components = components, IncludesReferencedFiles = options.IncludeReferencedFiles, References = references,
            };
            await using (var zipStream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var (entryName, file, stage) in plan)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new BackupProgress(stage, 0.05 + 0.8 * done / total, Path.GetFileName(file)));
                    FileStream input;
                    try { input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && stage == BackupStage.ReferencedFiles)
                    {
                        // a referenced file that vanished or is locked is skipped, not fatal
                        references.RemoveAll(r => r.Entry == entryName);
                        logger.LogWarning("Backup skipped a referenced file that could not be read ({Error})", ex.GetType().Name);
                        continue;
                    }
                    await using (input)
                    {
                        var level = entryName == DatabaseEntry || !Stored.Contains(Path.GetExtension(file)) ? CompressionLevel.Optimal : CompressionLevel.NoCompression;
                        var entry = zip.CreateEntry(entryName, level);
                        entry.LastWriteTime = File.GetLastWriteTime(file);
                        await using var output = entry.Open();
                        var (size, hash) = await CopyHashedAsync(input, output, long.MaxValue, ct).ConfigureAwait(false);
                        manifest.Entries.Add(new BackupEntry { Path = entryName, Size = size, Sha256 = hash });
                        done += size;
                    }
                }
                progress?.Report(new BackupProgress(BackupStage.Writing, 0.86));
                var manifestEntry = zip.CreateEntry(BackupManifest.FileName, CompressionLevel.Optimal);
                await using (var m = manifestEntry.Open())
                    await JsonSerializer.SerializeAsync(m, manifest, JsonOptions, ct).ConfigureAwait(false);
            }

            // 4. validate the archive exactly as a restore would, before calling it a backup
            progress?.Report(new BackupProgress(BackupStage.Validating, 0.9));
            var check = await ValidateCoreAsync(partial, ct).ConfigureAwait(false);
            if (!check.IsValid)
                throw new InvalidDataException("The backup failed validation: " + string.Join(", ", check.Errors));
            File.Move(partial, final);
            var bytes = new FileInfo(final).Length;
            progress?.Report(new BackupProgress(BackupStage.Done, 1));
            logger.LogInformation("Backup archive written ({Entries} entries, {Bytes} bytes, components {Components})", manifest.Entries.Count, bytes, string.Join("+", components));
            return new ArchiveResult(final, bytes, manifest);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
        finally
        {
            TryDelete(dbCopy);
            TryDelete(dbCopy + "-wal");
            TryDelete(dbCopy + "-shm");
        }
    }, ct);

    public Task<BackupValidation> ValidateAsync(string archivePath, CancellationToken ct = default) => Task.Run(() => ValidateCoreAsync(archivePath, ct), ct);

    public Task<string> StageRestoreAsync(string archivePath, CancellationToken ct = default) => Task.Run(async () =>
    {
        var check = await ValidateCoreAsync(archivePath, ct).ConfigureAwait(false);
        if (!check.IsValid) throw new InvalidDataException("The backup is not valid: " + string.Join(", ", check.Errors));
        var manifest = check.Manifest!;

        var root = BackupRestorer.RestoreRoot(locations.DataDirectory);
        var staged = Path.Combine(root, "staged");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(staged);

        var needed = manifest.Entries.Sum(e => e.Size);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(staged))!).AvailableFreeSpace;
        if (needed * 2 > free) throw new IOException("Not enough free space to restore this backup.");

        // safety net first: the current data, as an archive, so the restore can always be undone by hand
        var safetyFolder = Path.Combine(locations.DataDirectory, "Backups");
        var safety = await CreateAsync(safetyFolder, new BackupOptions(), null, ct, $"arcadia-before-restore-{DateTime.Now:yyyyMMdd-HHmmss}{Extension}").ConfigureAwait(false);
        foreach (var old in new DirectoryInfo(safetyFolder).GetFiles("arcadia-before-restore-*" + Extension).OrderByDescending(f => f.CreationTimeUtc).Skip(3)) old.Delete();

        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var fullStaged = Path.GetFullPath(staged) + Path.DirectorySeparatorChar;
            foreach (var item in manifest.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.GetFullPath(Path.Combine(staged, item.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(fullStaged, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe path in backup");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var entry = zip.GetEntry(item.Path) ?? throw new InvalidDataException("Entry missing");
                await using (var input = entry.Open())
                await using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
                {
                    // re-verified while extracting: the archive may have changed since it was validated
                    var (size, hash) = await CopyHashedAsync(input, output, item.Size, ct).ConfigureAwait(false);
                    if (size != item.Size || !string.Equals(hash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Checksum mismatch while extracting");
                }
            }
            KeepCurrentDataDirectory(Path.Combine(staged, SettingsEntry.Replace('/', Path.DirectorySeparatorChar)));
            var pending = new BackupRestorer.PendingRestore(archivePath, safety.Path, manifest.CreatedUtc, manifest.ArcadiaVersion, manifest.Components, DateTime.UtcNow);
            var marker = Path.Combine(root, BackupRestorer.PendingFile);
            await File.WriteAllTextAsync(marker + ".tmp", JsonSerializer.Serialize(pending, JsonOptions), ct).ConfigureAwait(false);
            File.Move(marker + ".tmp", marker, overwrite: true);
            logger.LogInformation("Restore staged (backup from {Created:u}, components {Components}); it is applied on the next start", manifest.CreatedUtc, string.Join("+", manifest.Components));
            return safety.Path;
        }
        catch
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }, ct);

    public int Prune(string folder, int keep, string prefix)
    {
        if (!Directory.Exists(folder) || keep < 1) return 0;
        var removed = 0;
        foreach (var old in new DirectoryInfo(folder).GetFiles(prefix + "*" + Extension).OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
        {
            try { old.Delete(); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("Could not remove an old automatic backup ({Error})", ex.GetType().Name); }
        }
        return removed;
    }

    // ---------------------------------------------------------------------------------------------------------------

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private async Task<BackupValidation> ValidateCoreAsync(string archivePath, CancellationToken ct)
    {
        var errors = new List<string>();
        long archiveBytes = 0;
        BackupManifest? manifest = null;
        try
        {
            var info = new FileInfo(archivePath);
            if (!info.Exists) return new BackupValidation(archivePath, null, ["Backup_ErrNotFound"], 0);
            archiveBytes = info.Length;
            using var zip = ZipFile.OpenRead(archivePath);
            if (zip.Entries.Count > MaxEntries) return new BackupValidation(archivePath, null, ["Backup_ErrSuspicious"], archiveBytes);
            foreach (var e in zip.Entries)
                if (!IsSafeEntryName(e.FullName)) return new BackupValidation(archivePath, null, ["Backup_ErrUnsafePath"], archiveBytes);

            var manifestEntry = zip.GetEntry(BackupManifest.FileName);
            if (manifestEntry == null || manifestEntry.Length > MaxManifestBytes) return new BackupValidation(archivePath, null, ["Backup_ErrManifest"], archiveBytes);
            await using (var ms = manifestEntry.Open())
            {
                try { manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(ms, JsonOptions, ct).ConfigureAwait(false); }
                catch (JsonException) { manifest = null; }
            }
            if (manifest == null || manifest.BackupFormat != BackupManifest.Format) return new BackupValidation(archivePath, null, ["Backup_ErrManifest"], archiveBytes);
            if (manifest.FormatVersion > BackupManifest.CurrentFormatVersion) return new BackupValidation(archivePath, manifest, ["Backup_ErrNewerFormat"], archiveBytes);

            var listed = new HashSet<string>(StringComparer.Ordinal) { BackupManifest.FileName };
            foreach (var item in manifest.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsSafeEntryName(item.Path) || !listed.Add(item.Path)) { errors.Add("Backup_ErrUnsafePath"); break; }
                var entry = zip.GetEntry(item.Path);
                if (entry == null || entry.Length != item.Size) { errors.Add("Backup_ErrMissingEntry"); break; }
                if (entry.Length > SuspiciousRatioFloor && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxCompressionRatio) { errors.Add("Backup_ErrSuspicious"); break; }
                await using var s = entry.Open();
                var (size, hash) = await CopyHashedAsync(s, Stream.Null, item.Size, ct).ConfigureAwait(false);
                if (size != item.Size || !string.Equals(hash, item.Sha256, StringComparison.OrdinalIgnoreCase)) { errors.Add("Backup_ErrChecksum"); break; }
            }
            if (errors.Count == 0 && zip.Entries.Any(e => !e.FullName.EndsWith('/') && !listed.Contains(e.FullName))) errors.Add("Backup_ErrUnexpectedEntry");
            if (errors.Count == 0 && !manifest.Entries.Any(e => e.Path == DatabaseEntry)) errors.Add("Backup_ErrNoDatabase");
            if (errors.Count == 0) errors.AddRange(await CheckDatabaseAsync(zip.GetEntry(DatabaseEntry)!, ct).ConfigureAwait(false));
        }
        catch (InvalidDataException) { errors.Add("Backup_ErrNotArchive"); }
        catch (IOException) { errors.Add("Backup_ErrUnreadable"); }
        catch (UnauthorizedAccessException) { errors.Add("Backup_ErrUnreadable"); }
        return new BackupValidation(archivePath, manifest, errors, archiveBytes);
    }

    /// <summary>The database copy inside must be intact and no newer than this Arcadia understands.</summary>
    private async Task<IEnumerable<string>> CheckDatabaseAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"arcadia-check-{Guid.NewGuid():N}.db");
        try
        {
            await using (var input = entry.Open())
            await using (var output = File.Create(temp))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            var problem = CheckDatabaseFile(temp);
            return problem == null ? [] : [problem];
        }
        finally { TryDelete(temp); }
    }

    /// <summary>Null when the file is an intact Arcadia database this build can open; otherwise the error key.</summary>
    public static string? CheckDatabaseFile(string path)
    {
        try
        {
            using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(cmd.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase)) return "Backup_ErrDatabaseDamaged";
            }
            var applied = new List<string>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
                using var r = cmd.ExecuteReader();
                while (r.Read()) applied.Add(r.GetString(0));
            }
            if (applied.Count == 0) return "Backup_ErrDatabaseDamaged";
            var known = KnownMigrations.Value;
            return applied.All(known.Contains) ? null : "Backup_ErrNewerDatabase";
        }
        catch (SqliteException) { return "Backup_ErrDatabaseDamaged"; }
    }

    private static readonly Lazy<HashSet<string>> KnownMigrations = new(() => typeof(ArcadiaDbContext).Assembly.GetTypes()
        .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Id).OfType<string>().ToHashSet(StringComparer.Ordinal));

    private static string? LatestMigration(string db)
    {
        try
        {
            using var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException) { return null; }
    }

    /// <summary>Document files the library references (folders are never copied). Read-only query; parameter-free.</summary>
    private static List<string> ReferencedPaths(string db)
    {
        var list = new List<string>();
        try
        {
            using var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Path FROM Documents WHERE IsFolder = 0";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0)) list.Add(r.GetString(0));
        }
        catch (SqliteException) { }
        return list;
    }

    /// <summary>"C:\Users\me\a.pdf" → "references/C/Users/me/a.pdf"; "\\server\share\x" → "references/UNC/server/share/x".</summary>
    internal static string? ReferenceEntryName(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        string rest;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) rest = "UNC/" + full[2..];
        else if (full.Length > 2 && full[1] == ':') rest = full[0] + "/" + full[3..];
        else return null;
        var name = ReferencesPrefix + rest.Replace('\\', '/');
        return IsSafeEntryName(name) ? name : null;
    }

    /// <summary>Relative, forward-slash names only: no rooted paths, drive letters, "." or ".." segments (Zip Slip).</summary>
    internal static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 1024) return false;
        if (name.StartsWith('/') || name.StartsWith('\\') || name.Contains('\\') || name.Contains(':') || name.Contains('\0')) return false;
        foreach (var segment in name.TrimEnd('/').Split('/'))
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Trim().Length == 0 || segment.EndsWith('.') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return true;
    }

    private static bool AddFolder(List<(string, string, BackupStage)> plan, string folder, string prefix, BackupStage stage)
    {
        if (!Directory.Exists(folder)) return false;
        var any = false;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var entry = prefix + Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (!IsSafeEntryName(entry)) continue;
            plan.Add((entry, file, stage));
            any = true;
        }
        return any;
    }

    /// <summary>Copies at most <paramref name="limit"/> + 1 bytes (a lying size is detected, not trusted) and hashes them.</summary>
    internal static async Task<(long Size, string Sha256)> CopyHashedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit && limit != long.MaxValue) return (total, string.Empty);
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>A restored settings.json keeps this installation's data-folder choice, so the restore lands where it was started.</summary>
    private void KeepCurrentDataDirectory(string stagedSettings)
    {
        if (!File.Exists(stagedSettings)) return;
        try
        {
            var restored = JsonNode.Parse(File.ReadAllText(stagedSettings)) as JsonObject;
            if (restored == null) return;
            JsonNode? current = null;
            if (File.Exists(locations.SettingsFile) && JsonNode.Parse(File.ReadAllText(locations.SettingsFile)) is JsonObject now)
                current = now["dataDirectory"]?.DeepClone();
            restored["dataDirectory"] = current;
            restored["firstRunCompleted"] = true;
            File.WriteAllText(stagedSettings, restored.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException) { File.Delete(stagedSettings); } // unreadable settings are not restored; defaults apply
    }

    private static long SafeLength(string file) { try { return new FileInfo(file).Length; } catch (IOException) { return 0; } }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (var i = 2; ; i++) { var p = Path.Combine(dir, $"{stem} ({i}){ext}"); if (!File.Exists(p)) return p; }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
