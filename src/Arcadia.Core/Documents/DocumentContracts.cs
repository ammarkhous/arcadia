namespace Arcadia.Core.Documents;

/// <summary>What the file system says about a path (gathered off the UI thread, with timeouts for network paths).</summary>
public sealed record FileFacts(string Path, bool Exists, bool IsFolder, long? SizeBytes, DateTime? CreatedUtc, DateTime? ModifiedUtc, string? ContentText = null);

public enum DocumentView { All, Favorites, Recent, Missing, Folders }

public enum DocumentSort { Name, Modified, Added, Size, Type, Opened }

public sealed record DocumentQuery(
    DocumentView View = DocumentView.All,
    FileKind? Kind = null,
    int? TagId = null,
    int? ProjectId = null,
    string? Text = null,
    DocumentSort Sort = DocumentSort.Name,
    bool Descending = false,
    int Skip = 0,
    int Take = 500);

/// <summary>A library row: flat and cheap to render thousands of.</summary>
public sealed record DocumentRow(
    int Id,
    string DisplayName,
    string Path,
    string Extension,
    bool IsFolder,
    long? SizeBytes,
    DateTime? FileModifiedUtc,
    DateTime AddedAtUtc,
    DateTime? LastOpenedAtUtc,
    bool IsFavorite,
    bool IsMissing,
    string Tags,
    string Projects);

public sealed record DocumentPage(IReadOnlyList<DocumentRow> Rows, int Total);

public sealed record TagCount(int Id, string Name, int Count);

public sealed record LibraryCounts(int All, int Favorites, int Recent, int Missing, int Folders, IReadOnlyDictionary<FileKind, int> ByKind, IReadOnlyDictionary<int, int> ByProject);

public sealed record AddDocumentsResult(int Added, int AlreadyInLibrary, int Skipped, IReadOnlyList<int> DocumentIds);

public sealed record ProjectRef(int Id, string Name, string Accent, bool IsPinned);

public interface IDocumentStore
{
    Task<DocumentPage> QueryAsync(DocumentQuery query, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<LibraryCounts> GetCountsAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TagCount>> GetTagsAsync(CancellationToken cancellationToken = default);
    Task<Document?> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<Document?> FindByPathAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectRef>> GetProjectsAsync(int documentId, CancellationToken cancellationToken = default);

    /// <summary>Adds references (existing ones are kept and, with a project, associated). Never touches the files.</summary>
    Task<AddDocumentsResult> AddAsync(IReadOnlyList<FileFacts> files, int? projectId, int? watchedFolderId, CancellationToken cancellationToken = default);
    Task UpdateDetailsAsync(int id, string displayName, string? description, CancellationToken cancellationToken = default);
    Task SetFavoriteAsync(int id, bool favorite, CancellationToken cancellationToken = default);
    /// <summary>Replaces the document's tags; tags are shared across the library and created on demand.</summary>
    Task SetTagsAsync(int id, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);
    Task AssociateAsync(int documentId, int projectId, CancellationToken cancellationToken = default);
    /// <summary>Removes the association only; the document stays in the library and the file stays on disk.</summary>
    Task DissociateAsync(int documentId, int projectId, CancellationToken cancellationToken = default);
    Task SetProjectPinnedAsync(int documentId, int projectId, bool pinned, CancellationToken cancellationToken = default);
    /// <summary>Removes the references from Arcadia. The files themselves are never deleted.</summary>
    Task RemoveAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default);
    /// <summary>Reconnects a reference to a file that moved ("Locate file").</summary>
    Task RelocateAsync(int id, FileFacts facts, CancellationToken cancellationToken = default);
    Task MarkOpenedAsync(int id, DateTime nowUtc, CancellationToken cancellationToken = default);
    /// <summary>Stores availability/metadata results of a background check.</summary>
    Task UpdateAvailabilityAsync(IReadOnlyList<(int Id, FileFacts Facts)> checks, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentRow>> ListForProjectAsync(int projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(int Id, string Path)>> GetPathsAsync(IReadOnlyCollection<int>? ids = null, CancellationToken cancellationToken = default);

    // Watched folders (used by the watcher; paths are updated on rename, marked missing on delete)
    Task<IReadOnlyList<WatchedFolder>> GetWatchedFoldersAsync(CancellationToken cancellationToken = default);
    Task<WatchedFolder> AddWatchedFolderAsync(string path, bool includeSubfolders, CancellationToken cancellationToken = default);
    Task RemoveWatchedFolderAsync(int id, bool removeDocuments, CancellationToken cancellationToken = default);
    Task SetWatchedFolderStateAsync(int id, bool enabled, string? lastError, DateTime? lastScanUtc, CancellationToken cancellationToken = default);
    Task RenamePathAsync(string oldPath, FileFacts facts, CancellationToken cancellationToken = default);
    Task MarkMissingByPathAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>File-system access for the library, kept behind an interface (timeouts, network paths, tests).</summary>
public interface IFileFactsService
{
    /// <summary>Metadata for a path; never throws for missing or unreachable paths (Exists = false).</summary>
    Task<FileFacts> GetFactsAsync(string path, bool readContent, CancellationToken cancellationToken = default);

    /// <summary>Files inside a folder (optionally recursive), skipping temporary files, links and inaccessible folders.</summary>
    Task<IReadOnlyList<FileFacts>> ScanFolderAsync(string folder, bool recursive, Func<string, bool> include, IProgress<int>? progress, CancellationToken cancellationToken = default);

    /// <summary>Availability of many paths; unreachable drives/shares are checked once and cached for the call.</summary>
    Task<IReadOnlyList<FileFacts>> CheckAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}
