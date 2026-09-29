using Arcadia.Core.Documents;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

/// <summary>
/// The document library: references to files and folders, their tags and project associations.
/// Nothing here ever creates, moves or deletes a file on disk.
/// </summary>
public sealed class DocumentStore(IDbContextFactory<ArcadiaDbContext> factory, TimeProvider time) : IDocumentStore
{
    public const int MaxTagLength = 60;
    private static readonly TimeSpan RecentlyAdded = TimeSpan.FromDays(30);
    private static readonly TimeSpan RecentlyOpened = TimeSpan.FromDays(90);
    private static readonly TimeSpan RecentlyModified = TimeSpan.FromDays(14);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ─────────────────────────── queries ───────────────────────────

    public async Task<DocumentPage> QueryAsync(DocumentQuery query, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<Document> q = db.Documents.AsNoTracking();

        q = query.View switch
        {
            DocumentView.Favorites => q.Where(d => d.IsFavorite),
            DocumentView.Missing => q.Where(d => d.IsMissing),
            DocumentView.Folders => q.Where(d => d.IsFolder),
            DocumentView.Recent => RecentFilter(q, nowUtc),
            _ => q,
        };
        if (query.Kind is { } kind)
        {
            if (kind == FileKind.Folder) q = q.Where(d => d.IsFolder);
            else if (kind == FileKind.Other)
            {
                var known = Enum.GetValues<FileKind>().Where(k => k is not (FileKind.Other or FileKind.Folder)).SelectMany(FileKinds.ExtensionsOf).ToList();
                q = q.Where(d => !d.IsFolder && !known.Contains(d.Extension));
            }
            else
            {
                var exts = FileKinds.ExtensionsOf(kind).ToList();
                q = q.Where(d => !d.IsFolder && exts.Contains(d.Extension));
            }
        }
        if (query.TagId is { } tagId) q = q.Where(d => d.Tags.Any(t => t.TagId == tagId));
        if (query.ProjectId is { } projectId) q = q.Where(d => d.Projects.Any(p => p.ProjectId == projectId));

        List<int>? rank = null;
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var fts = FtsQuery.Build(query.Text);
            rank = fts == null ? [] : (await SearchIndex.MatchDocumentIdsAsync(db, fts, 5000, cancellationToken).ConfigureAwait(false)).ToList();
            var ids = rank;
            var like = "%" + Escape(query.Text.Trim()) + "%";
            // Also match raw path fragments (e.g. "D:\Invoices") that the tokenizer splits up.
            q = q.Where(d => ids.Contains(d.Id) || EF.Functions.Like(d.Path, like, "\\"));
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        q = (query.View == DocumentView.Recent && query.Sort == DocumentSort.Name ? DocumentSort.Opened : query.Sort, query.Descending) switch
        {
            (DocumentSort.Modified, false) => q.OrderBy(d => d.FileModifiedUtc),
            (DocumentSort.Modified, true) => q.OrderByDescending(d => d.FileModifiedUtc),
            (DocumentSort.Added, false) => q.OrderBy(d => d.AddedAtUtc),
            (DocumentSort.Added, true) => q.OrderByDescending(d => d.AddedAtUtc),
            (DocumentSort.Size, false) => q.OrderBy(d => d.SizeBytes),
            (DocumentSort.Size, true) => q.OrderByDescending(d => d.SizeBytes),
            (DocumentSort.Type, false) => q.OrderBy(d => d.Extension).ThenBy(d => d.DisplayName),
            (DocumentSort.Type, true) => q.OrderByDescending(d => d.Extension).ThenBy(d => d.DisplayName),
            (DocumentSort.Opened, _) => q.OrderByDescending(d => d.LastOpenedAtUtc ?? d.AddedAtUtc),
            (_, true) => q.OrderByDescending(d => d.DisplayName),
            _ => q.OrderBy(d => d.IsFolder ? 0 : 1).ThenBy(d => d.DisplayName),
        };
        var page = await q.Skip(query.Skip).Take(query.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentPage(await ToRowsAsync(db, page, cancellationToken).ConfigureAwait(false), total);
    }

    private static IQueryable<Document> RecentFilter(IQueryable<Document> q, DateTime now)
    {
        DateTime added = now - RecentlyAdded, opened = now - RecentlyOpened, modified = now - RecentlyModified;
        return q.Where(d => d.AddedAtUtc >= added || (d.LastOpenedAtUtc != null && d.LastOpenedAtUtc >= opened) || (d.FileModifiedUtc != null && d.FileModifiedUtc >= modified));
    }

    internal static async Task<IReadOnlyList<DocumentRow>> ToRowsAsync(ArcadiaDbContext db, List<Document> docs, CancellationToken ct)
    {
        if (docs.Count == 0) return [];
        var ids = docs.Select(d => d.Id).ToList();
        var tags = (await db.DocumentTags.AsNoTracking().Where(t => ids.Contains(t.DocumentId)).Select(t => new { t.DocumentId, t.Tag!.Name }).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(t => t.DocumentId).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Name).Order(StringComparer.CurrentCultureIgnoreCase)));
        var projects = (await db.DocumentProjects.AsNoTracking().Where(p => ids.Contains(p.DocumentId)).Select(p => new { p.DocumentId, p.Project!.Name }).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(p => p.DocumentId).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Name)));
        return docs.Select(d => new DocumentRow(d.Id, d.DisplayName, d.Path, d.Extension, d.IsFolder, d.SizeBytes, d.FileModifiedUtc, d.AddedAtUtc, d.LastOpenedAtUtc,
            d.IsFavorite, d.IsMissing, tags.GetValueOrDefault(d.Id, string.Empty), projects.GetValueOrDefault(d.Id, string.Empty))).ToList();
    }

    public async Task<LibraryCounts> GetCountsAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var all = await db.Documents.CountAsync(cancellationToken).ConfigureAwait(false);
        var fav = await db.Documents.CountAsync(d => d.IsFavorite, cancellationToken).ConfigureAwait(false);
        var recent = await RecentFilter(db.Documents, nowUtc).CountAsync(cancellationToken).ConfigureAwait(false);
        var missing = await db.Documents.CountAsync(d => d.IsMissing, cancellationToken).ConfigureAwait(false);
        var folders = await db.Documents.CountAsync(d => d.IsFolder, cancellationToken).ConfigureAwait(false);
        var byExt = await db.Documents.Where(d => !d.IsFolder).GroupBy(d => d.Extension).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byKind = byExt.GroupBy(x => FileKinds.Classify(x.Key)).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        var byProject = await db.DocumentProjects.GroupBy(p => p.ProjectId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);
        return new LibraryCounts(all, fav, recent, missing, folders, byKind, byProject);
    }

    public async Task<IReadOnlyList<TagCount>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.Tags.AsNoTracking().Select(t => new TagCount(t.Id, t.Name, t.Documents.Count)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Document?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Documents.AsNoTracking().Include(d => d.Tags).ThenInclude(t => t.Tag).Include(d => d.Projects).ThenInclude(p => p.Project)
            .AsSplitQuery().FirstOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Document?> FindByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var normal = PathRules.Normalize(path);
        if (normal == null) return null;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Path == normal, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectRef>> GetProjectsAsync(int documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.DocumentProjects.AsNoTracking().Where(p => p.DocumentId == documentId)
            .Select(p => new ProjectRef(p.ProjectId, p.Project!.Name, p.Project.Accent, p.IsPinned)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DocumentRow>> ListForProjectAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var docs = await db.DocumentProjects.AsNoTracking().Where(p => p.ProjectId == projectId)
            .OrderByDescending(p => p.IsPinned).ThenBy(p => p.Document!.DisplayName)
            .Select(p => p.Document!).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ToRowsAsync(db, docs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(int Id, string Path)>> GetPathsAsync(IReadOnlyCollection<int>? ids = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var q = db.Documents.AsNoTracking();
        if (ids != null) q = q.Where(d => ids.Contains(d.Id));
        return (await q.Select(d => new { d.Id, d.Path }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(x => (x.Id, x.Path)).ToList();
    }

    // ─────────────────────────── changes ───────────────────────────

    public async Task<AddDocumentsResult> AddAsync(IReadOnlyList<FileFacts> files, int? projectId, int? watchedFolderId, CancellationToken cancellationToken = default)
    {
        var normalized = files.Select(f => (Facts: f, Path: PathRules.Normalize(f.Path))).ToList();
        var valid = normalized.Where(x => x.Path != null && !(x.Facts.IsFolder == false && FileKinds.IsTemporary(System.IO.Path.GetFileName(x.Path!))))
            .DistinctBy(x => x.Path!, StringComparer.OrdinalIgnoreCase).ToList();
        int skipped = files.Count - valid.Count;
        if (valid.Count == 0) return new AddDocumentsResult(0, 0, skipped, []);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = projectId is { } pid ? await db.Projects.FindAsync([pid], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException() : null;
        var paths = valid.Select(v => v.Path!).ToList();
        var existing = await db.Documents.Where(d => paths.Contains(d.Path)).ToDictionaryAsync(d => d.Path, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
        var now = Now;
        var added = new List<(Document Doc, string? Content)>();
        var ids = new List<int>();
        int already = 0;
        foreach (var (facts, path) in valid)
        {
            if (existing.TryGetValue(path!, out var doc)) already++;
            else
            {
                doc = new Document
                {
                    Path = path!,
                    DisplayName = ProjectStore.Clip(PathRules.DisplayName(path!), 400),
                    Extension = facts.IsFolder ? string.Empty : PathRules.Extension(path!),
                    AddedAtUtc = now,
                    WatchedFolderId = watchedFolderId,
                };
                ApplyFacts(doc, facts, now);
                db.Documents.Add(doc);
                added.Add((doc, facts.ContentText));
            }
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var all = valid.Select(v => existing.TryGetValue(v.Path!, out var d) ? d : added.First(a => a.Doc.Path == v.Path).Doc).ToList();
        if (project != null)
        {
            var allIds = all.Select(d => d.Id).ToList();
            var linked = await db.DocumentProjects.Where(p => p.ProjectId == project.Id && allIds.Contains(p.DocumentId)).Select(p => p.DocumentId).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var doc in all.Where(d => !linked.Contains(d.Id)))
            {
                db.DocumentProjects.Add(new DocumentProject { DocumentId = doc.Id, ProjectId = project.Id, AddedAtUtc = now });
                ProjectStore.Log(db, project.Id, ProjectActivityKind.FileAttached, doc.DisplayName, now);
            }
            project.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var (doc, content) in added) await SearchIndex.IndexDocumentAsync(db, doc.Id, content, cancellationToken).ConfigureAwait(false);
        if (project != null)
            foreach (var doc in all.Where(d => existing.ContainsKey(d.Path))) await SearchIndex.IndexDocumentAsync(db, doc.Id, null, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        ids.AddRange(all.Select(d => d.Id));
        return new AddDocumentsResult(added.Count, already, skipped, ids);
    }

    private static void ApplyFacts(Document doc, FileFacts facts, DateTime now)
    {
        doc.IsFolder = facts.IsFolder || doc.IsFolder;
        doc.IsMissing = !facts.Exists;
        doc.LastCheckedUtc = now;
        if (!facts.Exists) return;
        doc.SizeBytes = facts.IsFolder ? null : facts.SizeBytes;
        doc.FileCreatedUtc = facts.CreatedUtc;
        doc.FileModifiedUtc = facts.ModifiedUtc;
    }

    public Task UpdateDetailsAsync(int id, string displayName, string? description, CancellationToken cancellationToken = default) =>
        MutateAsync(id, d =>
        {
            if (!string.IsNullOrWhiteSpace(displayName)) d.DisplayName = ProjectStore.Clip(displayName, 400);
            d.Description = string.IsNullOrWhiteSpace(description) ? null : ProjectStore.Clip(description, 4000);
        }, reindex: true, cancellationToken);

    public Task SetFavoriteAsync(int id, bool favorite, CancellationToken cancellationToken = default) =>
        MutateAsync(id, d => d.IsFavorite = favorite, reindex: false, cancellationToken);

    public async Task SetTagsAsync(int id, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        var names = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Select(t => t.Length > MaxTagLength ? t[..MaxTagLength] : t)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var doc = await db.Documents.Include(d => d.Tags).FirstOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var existing = await db.Tags.Where(t => names.Contains(t.Name)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var tagEntities = names.Select(n => existing.FirstOrDefault(t => string.Equals(t.Name, n, StringComparison.CurrentCultureIgnoreCase)) ?? db.Tags.Add(new Tag { Name = n }).Entity).ToList();
        doc.Tags.Clear();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var t in tagEntities) doc.Tags.Add(new DocumentTag { Document = doc, Tag = t });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // tags nobody uses any more are removed so the tag list stays tidy
        await db.Tags.Where(t => !t.Documents.Any()).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexDocumentAsync(db, id, null, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AssociateAsync(int documentId, int projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await db.DocumentProjects.AnyAsync(p => p.DocumentId == documentId && p.ProjectId == projectId, cancellationToken).ConfigureAwait(false)) return;
        var doc = await db.Documents.FindAsync([documentId], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var project = await db.Projects.FindAsync([projectId], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        db.DocumentProjects.Add(new DocumentProject { DocumentId = documentId, ProjectId = projectId, AddedAtUtc = Now });
        ProjectStore.Log(db, projectId, ProjectActivityKind.FileAttached, doc.DisplayName, Now);
        project.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexDocumentAsync(db, documentId, null, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DissociateAsync(int documentId, int projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var link = await db.DocumentProjects.Include(p => p.Document).FirstOrDefaultAsync(p => p.DocumentId == documentId && p.ProjectId == projectId, cancellationToken).ConfigureAwait(false);
        if (link == null) return;
        db.DocumentProjects.Remove(link);
        ProjectStore.Log(db, projectId, ProjectActivityKind.FileRemoved, link.Document?.DisplayName, Now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexDocumentAsync(db, documentId, null, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetProjectPinnedAsync(int documentId, int projectId, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.DocumentProjects.Where(p => p.DocumentId == documentId && p.ProjectId == projectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsPinned, pinned), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var links = await db.DocumentProjects.Where(p => ids.Contains(p.DocumentId)).Select(p => new { p.ProjectId, p.Document!.DisplayName }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var l in links) ProjectStore.Log(db, l.ProjectId, ProjectActivityKind.FileRemoved, l.DisplayName, Now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await db.Documents.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Tags.Where(t => !t.Documents.Any()).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids) await SearchIndex.DeleteAsync(db, SearchResultKind.Document, id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RelocateAsync(int id, FileFacts facts, CancellationToken cancellationToken = default)
    {
        var path = PathRules.Normalize(facts.Path) ?? throw new ArgumentException("Invalid path.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var doc = await db.Documents.FindAsync([id], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        if (await db.Documents.AnyAsync(d => d.Id != id && d.Path == path, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("That file is already in the library.");
        ApplyPath(doc, path, facts);
        // Reconnecting a file is part of each project's history (the new location is shown in the details panel).
        foreach (var projectId in await db.DocumentProjects.Where(dp => dp.DocumentId == id).Select(dp => dp.ProjectId).ToListAsync(cancellationToken).ConfigureAwait(false))
            ProjectStore.Log(db, projectId, ProjectActivityKind.FileRelocated, doc.DisplayName, Now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexDocumentAsync(db, id, facts.ContentText, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves the reference to a new path; a name the user never changed follows the file's new name.</summary>
    private void ApplyPath(Document doc, string path, FileFacts facts)
    {
        if (string.Equals(doc.DisplayName, PathRules.DisplayName(doc.Path), StringComparison.OrdinalIgnoreCase))
            doc.DisplayName = ProjectStore.Clip(PathRules.DisplayName(path), 400);
        doc.Path = path;
        doc.Extension = doc.IsFolder ? string.Empty : PathRules.Extension(path);
        ApplyFacts(doc, facts, Now);
    }

    public Task MarkOpenedAsync(int id, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        MutateAsync(id, d => d.LastOpenedAtUtc = nowUtc, reindex: false, cancellationToken);

    public async Task UpdateAvailabilityAsync(IReadOnlyList<(int Id, FileFacts Facts)> checks, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (checks.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = checks.Select(c => c.Id).ToList();
        var docs = await db.Documents.Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken).ConfigureAwait(false);
        foreach (var (id, facts) in checks)
            if (docs.TryGetValue(id, out var doc)) ApplyFacts(doc, facts, nowUtc);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ─────────────────────────── watched folders ───────────────────────────

    public async Task<IReadOnlyList<WatchedFolder>> GetWatchedFoldersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.WatchedFolders.AsNoTracking().OrderBy(w => w.Path).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WatchedFolder> AddWatchedFolderAsync(string path, bool includeSubfolders, CancellationToken cancellationToken = default)
    {
        var normal = PathRules.Normalize(path) ?? throw new ArgumentException("Invalid folder path.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.WatchedFolders.FirstOrDefaultAsync(w => w.Path == normal, cancellationToken).ConfigureAwait(false);
        if (existing != null) return existing;
        var folder = new WatchedFolder { Path = normal, IncludeSubfolders = includeSubfolders, IsEnabled = true, AddedAtUtc = Now };
        db.WatchedFolders.Add(folder);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return folder;
    }

    public async Task RemoveWatchedFolderAsync(int id, bool removeDocuments, CancellationToken cancellationToken = default)
    {
        if (removeDocuments)
        {
            List<int> ids;
            await using (var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
                ids = await db.Documents.Where(d => d.WatchedFolderId == id && !d.Projects.Any() && !d.IsFavorite).Select(d => d.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (ids.Count > 0) await RemoveAsync(ids, cancellationToken).ConfigureAwait(false);
        }
        await using var db2 = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db2.WatchedFolders.Where(w => w.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetWatchedFolderStateAsync(int id, bool enabled, string? lastError, DateTime? lastScanUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var w = await db.WatchedFolders.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (w == null) return;
        w.IsEnabled = enabled;
        w.LastError = lastError == null ? null : ProjectStore.Clip(lastError, 500);
        if (lastScanUtc != null) w.LastScanUtc = lastScanUtc;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RenamePathAsync(string oldPath, FileFacts facts, CancellationToken cancellationToken = default)
    {
        var oldNormal = PathRules.Normalize(oldPath);
        var newNormal = PathRules.Normalize(facts.Path);
        if (oldNormal == null || newNormal == null) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Path == oldNormal, cancellationToken).ConfigureAwait(false);
        if (doc == null || await db.Documents.AnyAsync(d => d.Path == newNormal, cancellationToken).ConfigureAwait(false)) return;
        ApplyPath(doc, newNormal, facts);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexDocumentAsync(db, doc.Id, null, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkMissingByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var normal = PathRules.Normalize(path);
        if (normal == null) return;
        var now = Now;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Documents.Where(d => d.Path == normal)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsMissing, true).SetProperty(d => d.LastCheckedUtc, now), cancellationToken).ConfigureAwait(false);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private async Task MutateAsync(int id, Action<Document> change, bool reindex, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var doc = await db.Documents.FindAsync([id], ct).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        change(doc);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (reindex) await SearchIndex.IndexDocumentAsync(db, id, null, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
