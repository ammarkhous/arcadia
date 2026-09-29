using System.IO.Compression;
using System.Text.Json;
using Arcadia.Core.Platform;
using Arcadia.Data;
using Arcadia.Infrastructure.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

/// <summary>Phase 10 backup &amp; recovery: archive format, validation guards, staged restore and rollback. Temp folders only.</summary>
public sealed class BackupArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcadia-archive-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _settingsFile;
    private readonly DataLocations _locations;

    public BackupArchiveTests()
    {
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(_data, "Media", "Games"));
        Directory.CreateDirectory(Path.Combine(_data, "Oracle"));
        Directory.CreateDirectory(Path.Combine(_root, "settings"));
        _settingsFile = Path.Combine(_root, "settings", "settings.json");
        _locations = new DataLocations(_data, Path.Combine(_data, "arcadia.db"), _settingsFile, Path.Combine(_root, "settings", "secrets.dat"),
            Path.Combine(_data, "Media"), Path.Combine(_data, "Cache"), Path.Combine(_data, "Cache", "Thumbnails"), Path.Combine(_data, "Logs"));
        using (var db = new ArcadiaDbContext(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={_locations.DatabasePath};Pooling=False").Options))
            db.Database.Migrate();
        AddGame("Größe · مرحبا");
        File.WriteAllText(_settingsFile, """{"language":"ar","dataDirectory":null}""");
        File.WriteAllText(_locations.SecretsFile, "secret");
        File.WriteAllText(Path.Combine(_data, "Media", "Games", "cover.jpg"), "img");
        File.WriteAllText(Path.Combine(_data, "Oracle", "oracle-history.json"), "[]");
    }

    private ArcadiaBackupService Service() => new(_locations, "1.0.0", NullLogger<ArcadiaBackupService>.Instance);
    private string Out => Path.Combine(_root, "out");

    private void Exec(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_locations.DatabasePath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void AddGame(string title) => Exec("INSERT INTO Games (Title, Platform, Tags, Status, Priority, CompletionPercent, PlaytimeHours, AchievementsUnlocked, AchievementsTotal, AddedAtUtc, IsFeatured, ArtworkTheme, ArtworkSeed, IsSample, AutoGamingMode) " +
        $"VALUES ('{title}', 'PC', '', 'Playing', 0, 0, 0, 0, 0, '2026-01-01', 0, 'Default', 1, 0, 0)");

    private string Titles()
    {
        using var c = new SqliteConnection($"Data Source={_locations.DatabasePath};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_concat(Title, '|') FROM (SELECT Title FROM Games ORDER BY Id)";
        return cmd.ExecuteScalar() as string ?? string.Empty;
    }

    [Fact]
    public async Task Archive_has_manifest_with_checksums_and_never_contains_secrets()
    {
        var result = await Service().CreateAsync(Out, new BackupOptions());

        Assert.EndsWith(".arcadia-backup", result.Path);
        Assert.False(File.Exists(result.Path + ".partial"));
        using var zip = ZipFile.OpenRead(result.Path);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("manifest.json", names);
        Assert.Contains("arcadia.db", names);
        Assert.Contains("settings/settings.json", names);
        Assert.Contains("media/Games/cover.jpg", names);
        Assert.Contains("oracle/oracle-history.json", names);
        Assert.DoesNotContain(names, n => n.Contains("secrets", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, result.Manifest.FormatVersion);
        Assert.Equal("1.0.0", result.Manifest.ArcadiaVersion);
        Assert.EndsWith("_Automation", result.Manifest.DatabaseVersion);
        Assert.All(result.Manifest.Entries, e => Assert.Equal(64, e.Sha256.Length));
        Assert.Equal(["database", "settings", "media", "oracle"], result.Manifest.Components);
        Assert.True((await Service().ValidateAsync(result.Path)).IsValid);
    }

    [Fact]
    public async Task Restore_round_trip_brings_back_database_settings_and_media_and_keeps_a_safety_backup()
    {
        var archive = (await Service().CreateAsync(Out, new BackupOptions())).Path;
        // the library changes after the backup
        AddGame("Later");
        File.WriteAllText(_settingsFile, """{"language":"en","dataDirectory":"D:\\Elsewhere"}""");
        File.Delete(Path.Combine(_data, "Media", "Games", "cover.jpg"));

        var safety = await Service().StageRestoreAsync(archive);
        Assert.True(File.Exists(safety));
        Assert.Equal("Größe · مرحبا|Later", Titles()); // nothing changes until the next start

        var outcome = BackupRestorer.ApplyPending(_data, _settingsFile);

        Assert.NotNull(outcome);
        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("Größe · مرحبا", Titles());
        Assert.True(File.Exists(Path.Combine(_data, "Media", "Games", "cover.jpg")));
        var settings = JsonDocument.Parse(File.ReadAllText(_settingsFile)).RootElement;
        Assert.Equal("ar", settings.GetProperty("language").GetString());
        Assert.Equal(@"D:\Elsewhere", settings.GetProperty("dataDirectory").GetString()); // this installation's data-folder choice stays
        Assert.True(File.Exists(_locations.SecretsFile));
        // the safety archive holds the state from just before the restore
        Assert.True((await Service().ValidateAsync(safety)).IsValid);
        Assert.Null(BackupRestorer.ApplyPending(_data, _settingsFile));
        Assert.True(BackupRestorer.TakeLastOutcome(_data)!.Success);
        Assert.False(Directory.Exists(BackupRestorer.RestoreRoot(_data)));
    }

    [Fact]
    public async Task A_damaged_database_in_the_staged_restore_rolls_everything_back()
    {
        var archive = (await Service().CreateAsync(Out, new BackupOptions())).Path;
        AddGame("Later");
        await Service().StageRestoreAsync(archive);
        // damage the staged copy after validation (disk error, tampering)
        File.WriteAllBytes(Path.Combine(BackupRestorer.RestoreRoot(_data), "staged", "arcadia.db"), new byte[8192]);

        var outcome = BackupRestorer.ApplyPending(_data, _settingsFile);

        Assert.False(outcome!.Success);
        Assert.Equal("Größe · مرحبا|Later", Titles()); // the current library is back, untouched
        Assert.True(File.Exists(Path.Combine(_data, "Media", "Games", "cover.jpg")));
        Assert.Contains("\"language\":\"ar\"", File.ReadAllText(_settingsFile));
    }

    [Fact]
    public async Task An_interrupted_restore_is_rolled_back_on_the_next_start()
    {
        var root = BackupRestorer.RestoreRoot(_data);
        Directory.CreateDirectory(Path.Combine(root, "rollback"));
        // simulate a crash right after the live database was moved aside
        File.Move(_locations.DatabasePath, Path.Combine(root, "rollback", "arcadia.db"));
        File.WriteAllText(Path.Combine(root, "applying"), "x");

        var outcome = BackupRestorer.ApplyPending(_data, _settingsFile);

        Assert.False(outcome!.Success);
        Assert.Equal("Größe · مرحبا", Titles());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Tampered_entries_fail_validation()
    {
        var archive = (await Service().CreateAsync(Out, new BackupOptions())).Path;
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            zip.GetEntry("oracle/oracle-history.json")!.Delete();
            using var w = new StreamWriter(zip.CreateEntry("oracle/oracle-history.json").Open());
            w.Write("[1]");
        }
        var check = await Service().ValidateAsync(archive);
        Assert.False(check.IsValid);
        Assert.Contains(check.Errors, e => e is "Backup_ErrChecksum" or "Backup_ErrMissingEntry");
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("media/../../evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("/abs.txt")]
    public async Task Zip_slip_entries_are_rejected(string name)
    {
        var archive = (await Service().CreateAsync(Out, new BackupOptions())).Path;
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write("x");

        var check = await Service().ValidateAsync(archive);

        Assert.Equal(["Backup_ErrUnsafePath"], check.Errors);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service().StageRestoreAsync(archive));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task Unknown_extra_entries_newer_formats_and_non_archives_are_rejected()
    {
        var archive = (await Service().CreateAsync(Out, new BackupOptions())).Path;
        var extra = Path.Combine(Out, "extra.arcadia-backup");
        File.Copy(archive, extra);
        using (var zip = ZipFile.Open(extra, ZipArchiveMode.Update))
        using (var w = new StreamWriter(zip.CreateEntry("run-me.exe").Open())) w.Write("MZ");
        Assert.Contains("Backup_ErrUnexpectedEntry", (await Service().ValidateAsync(extra)).Errors);

        var newer = Path.Combine(Out, "newer.arcadia-backup");
        File.Copy(archive, newer);
        using (var zip = ZipFile.Open(newer, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            string json; using (var r = new StreamReader(entry.Open())) json = r.ReadToEnd();
            entry.Delete();
            using var w = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            w.Write(json.Replace("\"formatVersion\": 1", "\"formatVersion\": 99"));
        }
        Assert.Equal(["Backup_ErrNewerFormat"], (await Service().ValidateAsync(newer)).Errors);

        var junk = Path.Combine(Out, "junk.arcadia-backup");
        File.WriteAllText(junk, "not a zip");
        Assert.Equal(["Backup_ErrNotArchive"], (await Service().ValidateAsync(junk)).Errors);
        Assert.Equal(["Backup_ErrNotFound"], (await Service().ValidateAsync(Path.Combine(Out, "missing.arcadia-backup"))).Errors);
    }

    [Fact]
    public async Task Database_from_a_newer_Arcadia_is_refused()
    {
        Exec("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29990101000000_Future', '10.0')");
        var archive = (await Record.ExceptionAsync(() => Service().CreateAsync(Out, new BackupOptions())));
        // creating fails validation too: a backup that could never be restored is not reported as a backup
        Assert.IsType<InvalidDataException>(archive);
        Assert.Empty(Directory.GetFiles(Out).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Referenced_files_are_optional_and_restore_beside_the_library_never_over_originals()
    {
        var doc = Path.Combine(_root, "docs", "invoice.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(doc)!);
        File.WriteAllText(doc, "pdf");
        Exec($"INSERT INTO Documents (DisplayName, Path, Extension, IsFolder, AddedAtUtc, IsFavorite, IsMissing) VALUES ('invoice', '{doc}', '.pdf', 0, '2026-01-01', 0, 0)");
        Exec($"INSERT INTO Documents (DisplayName, Path, Extension, IsFolder, AddedAtUtc, IsFavorite, IsMissing) VALUES ('gone', '{Path.Combine(_root, "gone.pdf")}', '.pdf', 0, '2026-01-01', 0, 0)");

        var estimate = await Service().EstimateReferencedFilesAsync();
        Assert.Equal((1, 3L, 1), (estimate.Files, estimate.Bytes, estimate.Missing));
        Assert.DoesNotContain("references", (await Service().CreateAsync(Out, new BackupOptions())).Manifest.Components);

        var with = await Service().CreateAsync(Out, new BackupOptions(IncludeReferencedFiles: true));
        Assert.Contains("references", with.Manifest.Components);
        Assert.Equal(doc, Assert.Single(with.Manifest.References).Original);

        File.WriteAllText(doc, "edited later");
        await Service().StageRestoreAsync(with.Path);
        Assert.True(BackupRestorer.ApplyPending(_data, _settingsFile)!.Success);
        Assert.Equal("edited later", File.ReadAllText(doc)); // the original is never overwritten
        var restored = Directory.GetFiles(Path.Combine(_data, "Restored files"), "invoice.pdf", SearchOption.AllDirectories);
        Assert.Equal("pdf", File.ReadAllText(Assert.Single(restored)));
    }

    [Fact]
    public async Task Cancelled_backup_leaves_no_partial_file()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().CreateAsync(Out, new BackupOptions(), new SyncProgress(cts), cts.Token));
        Assert.Empty(Directory.GetFiles(Out).Select(Path.GetFileName));
    }

    private sealed class SyncProgress(CancellationTokenSource cts) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) { if (value.Stage == BackupStage.Settings) cts.Cancel(); }
    }

    [Fact]
    public void Prune_keeps_the_newest_automatic_archives_and_never_touches_manual_ones()
    {
        Directory.CreateDirectory(Out);
        for (var i = 0; i < 6; i++)
        {
            var f = Path.Combine(Out, $"Arcadia auto 2026-01-0{i + 1}.arcadia-backup");
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 1, i + 1));
        }
        File.WriteAllText(Path.Combine(Out, "Arcadia backup manual.arcadia-backup"), "x");
        File.WriteAllText(Path.Combine(Out, "notes.txt"), "x");

        Assert.Equal(3, Service().Prune(Out, 3, "Arcadia auto "));

        var left = Directory.GetFiles(Out).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["Arcadia auto 2026-01-04.arcadia-backup", "Arcadia auto 2026-01-05.arcadia-backup", "Arcadia auto 2026-01-06.arcadia-backup",
            "Arcadia backup manual.arcadia-backup", "notes.txt"], left);
    }

    [Theory]
    [InlineData("media/Games/cover.jpg", true)]
    [InlineData("references/C/Users/me/a.pdf", true)]
    [InlineData("a/../b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a/./b", false)]
    [InlineData("a/b.", false)]
    [InlineData("", false)]
    public void Entry_names_must_be_plain_relative_paths(string name, bool safe) => Assert.Equal(safe, ArcadiaBackupService.IsSafeEntryName(name));

    [Fact]
    public void Reference_entry_names_map_drive_and_unc_paths()
    {
        Assert.Equal("references/C/Users/me/a.pdf", ArcadiaBackupService.ReferenceEntryName(@"C:\Users\me\a.pdf"));
        Assert.Equal("references/UNC/server/share/a.pdf", ArcadiaBackupService.ReferenceEntryName(@"\\server\share\a.pdf"));
    }

    [Fact]
    public void Log_retention_removes_old_files_and_keeps_the_folder_bounded()
    {
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        var now = new DateTime(2026, 9, 29, 12, 0, 0);
        void Log(string name, int daysAgo, int bytes)
        {
            var f = Path.Combine(logs, name);
            File.WriteAllBytes(f, new byte[bytes]);
            File.SetLastWriteTime(f, now.AddDays(-daysAgo));
        }
        Log("arcadia-20260901.log", 28, 10);
        Log("arcadia-20260920.log", 9, 10);
        Log("arcadia-20260929.log", 0, 10);
        Log("other.txt", 60, 10);

        Assert.Equal(1, FileLoggerProvider.Prune(logs, now));
        Assert.Equal(["arcadia-20260920.log", "arcadia-20260929.log", "other.txt"], Directory.GetFiles(logs).Select(Path.GetFileName).Order().ToList());
    }

    [Fact]
    public async Task First_start_of_a_new_version_copies_the_library_and_a_new_library_is_left_alone()
    {
        var init = new DatabaseInitializer(null!, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance);
        await init.BackupBeforeUpdateAsync(_locations.DatabasePath, "1.0.0-rc.1");
        var copy = Assert.Single(Directory.GetFiles(Path.Combine(_data, "Backups"), "arcadia-before-update-1.0.0-rc.1-*.db"));
        Assert.Null(ArcadiaBackupService.CheckDatabaseFile(copy));

        var missing = Path.Combine(_root, "none", "arcadia.db");
        await init.BackupBeforeUpdateAsync(missing, "1.0.0");
        Assert.False(Directory.Exists(Path.Combine(_root, "none")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
