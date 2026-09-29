using Arcadia.Core.Projects;

namespace Arcadia.Core.Documents;

/// <summary>
/// A reference to a file (or a folder) somewhere on this computer or network. Arcadia never copies, moves or
/// deletes the file itself; removing a document only removes this reference.
/// Project files are documents associated with a project, so both modules share one library.
/// </summary>
public sealed class Document
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Full, normalised path. Unique (case-insensitive) in the library.</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>Lower-case extension with dot (".pdf"); empty for folders or extensionless files.</summary>
    public string Extension { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime? FileCreatedUtc { get; set; }
    public DateTime? FileModifiedUtc { get; set; }
    public DateTime AddedAtUtc { get; set; }
    /// <summary>Last time the user opened it through Arcadia (Arcadia cannot know about other opens).</summary>
    public DateTime? LastOpenedAtUtc { get; set; }
    public string? Description { get; set; }
    public bool IsFavorite { get; set; }
    /// <summary>Result of the last availability check; the file may be back later (e.g. a USB drive).</summary>
    public bool IsMissing { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    /// <summary>Set when the document was found by a watched folder.</summary>
    public int? WatchedFolderId { get; set; }
    public WatchedFolder? WatchedFolder { get; set; }

    public List<DocumentTag> Tags { get; set; } = [];
    public List<DocumentProject> Projects { get; set; } = [];
}

/// <summary>A reusable tag (Work, Invoice, Manual…).</summary>
public sealed class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<DocumentTag> Documents { get; set; } = [];
}

public sealed class DocumentTag
{
    public int DocumentId { get; set; }
    public Document? Document { get; set; }
    public int TagId { get; set; }
    public Tag? Tag { get; set; }
}

/// <summary>Association of a document with a project (many-to-many). Removing it never touches the file.</summary>
public sealed class DocumentProject
{
    public int DocumentId { get; set; }
    public Document? Document { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public bool IsPinned { get; set; }
    public DateTime AddedAtUtc { get; set; }
}

/// <summary>A folder Arcadia keeps an eye on (optional) to pick up new or removed documents.</summary>
public sealed class WatchedFolder
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public bool IncludeSubfolders { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public DateTime AddedAtUtc { get; set; }
    public DateTime? LastScanUtc { get; set; }
    /// <summary>Last problem (drive unavailable, access denied); null when healthy.</summary>
    public string? LastError { get; set; }
}
