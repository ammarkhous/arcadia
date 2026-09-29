using System.Text.Json.Serialization;

namespace Arcadia.Core.Platform;

/// <summary>What a <c>.arcadia-backup</c> archive says about itself (manifest.json). Versioned; checked on restore.</summary>
public sealed class BackupManifest
{
    public const string FileName = "manifest.json";
    public const string Format = "arcadia-backup";
    public const int CurrentFormatVersion = 1;

    [JsonPropertyName("format")] public string BackupFormat { get; set; } = Format;
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = CurrentFormatVersion;
    [JsonPropertyName("arcadiaVersion")] public string ArcadiaVersion { get; set; } = string.Empty;
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; }
    /// <summary>The newest applied database migration (the schema version).</summary>
    [JsonPropertyName("databaseVersion")] public string DatabaseVersion { get; set; } = string.Empty;
    [JsonPropertyName("components")] public List<string> Components { get; set; } = [];
    [JsonPropertyName("includesReferencedFiles")] public bool IncludesReferencedFiles { get; set; }
    [JsonPropertyName("entries")] public List<BackupEntry> Entries { get; set; } = [];
    /// <summary>Referenced files that were copied (entry → original path), for "include referenced files" backups.</summary>
    [JsonPropertyName("references")] public List<BackupReference> References { get; set; } = [];
}

public sealed class BackupEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
}

public sealed class BackupReference
{
    [JsonPropertyName("entry")] public string Entry { get; set; } = string.Empty;
    [JsonPropertyName("original")] public string Original { get; set; } = string.Empty;
}

public sealed record BackupOptions(bool IncludeReferencedFiles = false);

public enum BackupStage { Preparing, Database, Settings, Media, History, ReferencedFiles, Writing, Validating, Done }

public sealed record BackupProgress(BackupStage Stage, double Fraction, string? Item = null);

public sealed record ArchiveResult(string Path, long Bytes, BackupManifest Manifest);

/// <summary>A validated archive: its manifest, or the reasons it cannot be used (resource keys).</summary>
public sealed record BackupValidation(string Path, BackupManifest? Manifest, IReadOnlyList<string> Errors, long ArchiveBytes)
{
    public bool IsValid => Manifest != null && Errors.Count == 0;
}

/// <summary>Size of what "include referenced files" would add (shown before the user decides).</summary>
public sealed record ReferencedFilesEstimate(int Files, long Bytes, int Missing);

/// <summary>
/// Backup &amp; recovery (Phase 10). Creates validated <c>.arcadia-backup</c> archives, validates archives before use, and
/// stages restores that are applied on the next start (before the database opens) with a safety backup and rollback.
/// </summary>
public interface IArcadiaBackupService
{
    Task<ReferencedFilesEstimate> EstimateReferencedFilesAsync(CancellationToken ct = default);
    Task<ArchiveResult> CreateAsync(string targetFolder, BackupOptions options, IProgress<BackupProgress>? progress = null, CancellationToken ct = default, string? fileName = null);
    Task<BackupValidation> ValidateAsync(string archivePath, CancellationToken ct = default);
    /// <summary>Validates, writes a safety backup of the current data, and stages the restore for the next start.</summary>
    Task<string> StageRestoreAsync(string archivePath, CancellationToken ct = default);
    /// <summary>Automatic backups: removes the oldest archives in a folder beyond <paramref name="keep"/> (only files Arcadia named).</summary>
    int Prune(string folder, int keep, string prefix);
}
