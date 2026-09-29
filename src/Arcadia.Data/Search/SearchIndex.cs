using Arcadia.Core.Organizer;
using System.Data.Common;
using System.Globalization;
using Arcadia.Core.Documents;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arcadia.Data.Search;

/// <summary>
/// Maintains the SQLite FTS5 table <c>SearchIndex</c> (created by the ProjectsDocuments migration).
/// Rows are written in the same context/transaction as the data they describe, so the index never
/// drifts; <see cref="RebuildAsync"/> recreates it from the tables when needed.
/// </summary>
public static class SearchIndex
{
    public const string Table = "SearchIndex";
    public const int MaxBodyChars = 64_000;

    public static string KindKey(SearchResultKind kind) => kind switch
    {
        SearchResultKind.Project => "project",
        SearchResultKind.Task => "task",
        SearchResultKind.Note => "note",
        SearchResultKind.Link => "link",
        SearchResultKind.Document => "document",
        SearchResultKind.Trip => "trip",
        SearchResultKind.Place => "place",
        SearchResultKind.Journal => "journal",
        SearchResultKind.Event => "event",
        SearchResultKind.Transaction => "txn",
        SearchResultKind.Account => "account",
        SearchResultKind.Budget => "budget",
        SearchResultKind.Recurring => "recurring",
        SearchResultKind.Goal => "goal",
        _ => "other",
    };

    public static SearchResultKind? KindOf(string key) => key switch
    {
        "project" => SearchResultKind.Project,
        "task" => SearchResultKind.Task,
        "note" => SearchResultKind.Note,
        "link" => SearchResultKind.Link,
        "document" => SearchResultKind.Document,
        "trip" => SearchResultKind.Trip,
        "place" => SearchResultKind.Place,
        "journal" => SearchResultKind.Journal,
        "event" => SearchResultKind.Event,
        "txn" => SearchResultKind.Transaction,
        "account" => SearchResultKind.Account,
        "budget" => SearchResultKind.Budget,
        "recurring" => SearchResultKind.Recurring,
        "goal" => SearchResultKind.Goal,
        _ => null,
    };

    // Each row's rowid is derived from (kind, id): updates and deletes use the rowid B-tree instead of scanning the
    // index (kind and entity_id are UNINDEXED columns, so "WHERE kind = … AND entity_id = …" read every row: O(n) per
    // save, O(n²) for a rebuild — 145 s for 25,000 entries). Codes are stable; never renumber them.
    private static readonly (string Key, int Code)[] KindCodes =
    [
        ("project", 1), ("task", 2), ("note", 3), ("link", 4), ("document", 5), ("trip", 6), ("place", 7), ("journal", 8),
        ("event", 9), ("txn", 10), ("account", 11), ("budget", 12), ("recurring", 13), ("goal", 14), ("other", 15),
    ];
    /// <summary>Rows below this rowid were written before keyed rowids (Phase 10) and are re-keyed once at startup.</summary>
    internal const long FirstKeyedRowId = 1L << 32;

    internal static long RowId(string kindKey, int id) => ((long)KindCodes.First(k => k.Key == kindKey).Code << 32) | (uint)id;

    public static async Task UpsertAsync(ArcadiaDbContext db, SearchResultKind kind, int id, int? projectId, string title, string? body, string? tags, CancellationToken ct)
    {
        var k = KindKey(kind);
        var rowId = RowId(k, id);
        var text = body is { Length: > MaxBodyChars } ? body[..MaxBodyChars] : body ?? string.Empty;
        await db.Database.ExecuteSqlAsync($"DELETE FROM SearchIndex WHERE rowid = {rowId}", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO SearchIndex (rowid, kind, entity_id, project_id, title, body, tags) VALUES ({rowId}, {k}, {id}, {projectId}, {title}, {text}, {tags ?? string.Empty})", ct).ConfigureAwait(false);
    }

    public static Task DeleteAsync(ArcadiaDbContext db, SearchResultKind kind, int id, CancellationToken ct)
    {
        var rowId = RowId(KindKey(kind), id);
        return db.Database.ExecuteSqlAsync($"DELETE FROM SearchIndex WHERE rowid = {rowId}", ct);
    }

    /// <summary>Removes a project and everything that belonged to it (tasks, notes, links).</summary>
    public static async Task DeleteProjectAsync(ArcadiaDbContext db, int projectId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"DELETE FROM SearchIndex WHERE kind IN ('task','note','link') AND project_id = {projectId}", ct).ConfigureAwait(false);
        var rowId = RowId("project", projectId);
        await db.Database.ExecuteSqlAsync($"DELETE FROM SearchIndex WHERE rowid = {rowId}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One-time upgrade of an index written before keyed rowids: every row is copied to its keyed rowid in one
    /// transaction (duplicates collapse to the newest). Nothing is re-extracted, so indexed document text is kept.
    /// </summary>
    public static async Task<bool> RekeyLegacyRowsAsync(ArcadiaDbContext db, ILogger logger, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct).ConfigureAwait(false);
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = $"SELECT EXISTS(SELECT 1 FROM SearchIndex WHERE rowid < {FirstKeyedRowId})";
            if (Convert.ToInt64(await probe.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0) return false;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var code = "CASE kind " + string.Join(' ', KindCodes.Select(k => $"WHEN '{k.Key}' THEN {k.Code}")) + " ELSE 15 END"; // fixed constants only
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
#pragma warning disable EF1002, EF1003 // the SQL is built from the constant table above; no user input
        await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE SearchRekey AS SELECT kind, entity_id, project_id, title, body, tags FROM SearchIndex " +
            "WHERE rowid IN (SELECT MAX(rowid) FROM SearchIndex GROUP BY kind, entity_id)", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SearchIndex", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO SearchIndex (rowid, kind, entity_id, project_id, title, body, tags) " +
            $"SELECT ({code} << 32) | (CAST(entity_id AS INTEGER) & 4294967295), kind, entity_id, project_id, title, body, tags FROM SearchRekey", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE SearchRekey", ct).ConfigureAwait(false);
#pragma warning restore EF1002, EF1003
        await tx.CommitAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Search index re-keyed for fast updates in {Ms:0} ms", clock.Elapsed.TotalMilliseconds);
        return true;
    }

    public static Task IndexProjectAsync(ArcadiaDbContext db, Project p, CancellationToken ct) =>
        UpsertAsync(db, SearchResultKind.Project, p.Id, p.Id, p.Name, p.Description, p.Tags, ct);

    public static Task IndexTaskAsync(ArcadiaDbContext db, TaskItem t, CancellationToken ct) =>
        UpsertAsync(db, SearchResultKind.Task, t.Id, t.ProjectId, t.Title, Join(t.Description, t.Notes), t.Tags, ct);

    public static Task IndexNoteAsync(ArcadiaDbContext db, Note n, CancellationToken ct) =>
        UpsertAsync(db, SearchResultKind.Note, n.Id, n.ProjectId, n.Title, n.Content, n.Tags, ct);

    /// <summary>Title, description, location and notes are searchable; tags and the category name are matched but not shown.</summary>
    public static Task IndexEventAsync(ArcadiaDbContext db, CalendarEvent e, string? categoryName, CancellationToken ct) =>
        UpsertAsync(db, SearchResultKind.Event, e.Id, null, e.Title, Join(e.Location, e.Description, e.Notes), Join(e.Tags, categoryName), ct);

    public static Task IndexLinkAsync(ArcadiaDbContext db, ProjectLink l, CancellationToken ct) =>
        UpsertAsync(db, SearchResultKind.Link, l.Id, l.ProjectId, l.Title, Join(l.Url, l.Description), l.Tags, ct);

    /// <summary>
    /// Documents are found by name, extension, path, description, tags, associated project names and (for
    /// text/Markdown files) content. The path is split into words so folder names match.
    /// </summary>
    public static async Task IndexDocumentAsync(ArcadiaDbContext db, int documentId, string? contentText, CancellationToken ct)
    {
        var doc = await db.Documents.AsNoTracking().Where(d => d.Id == documentId)
            .Select(d => new { d.Id, d.DisplayName, d.Path, d.Extension, d.Description }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (doc == null) return;
        var tags = await db.DocumentTags.Where(t => t.DocumentId == documentId).Select(t => t.Tag!.Name).ToListAsync(ct).ConfigureAwait(false);
        var projects = await db.DocumentProjects.Where(p => p.DocumentId == documentId).Select(p => p.Project!.Name).ToListAsync(ct).ConfigureAwait(false);
        projects.AddRange(await db.TripDocuments.Where(t => t.DocumentId == documentId).Select(t => t.Trip!.Name).ToListAsync(ct).ConfigureAwait(false));
        if (contentText == null)
        {
            // keep previously indexed content when only metadata changed
            contentText = await ExistingContentAsync(db, documentId, ct).ConfigureAwait(false);
        }
        var body = Join(doc.Path, doc.Extension.TrimStart('.'), doc.Description, ContentMarker, contentText);
        await UpsertAsync(db, SearchResultKind.Document, doc.Id, null, doc.DisplayName, body, string.Join(' ', tags.Concat(projects)), ct).ConfigureAwait(false);
    }

    // ── Travel ── (country names in every language and a trip's places are passed in by the store, so "Schweiz" or
    // "سويسرا" find places in CH. They go into the tags column: matched, but never shown as a snippet.)

    private static string? Aliases(string? tags, string? extra) => string.IsNullOrWhiteSpace(extra) ? tags : Join(tags, extra);

    public static Task IndexTripAsync(ArcadiaDbContext db, Arcadia.Core.Travel.Trip t, string? extra, CancellationToken ct) =>
        UpsertAsync(db, SearchResultKind.Trip, t.Id, null, t.Name, t.Description ?? string.Empty, Aliases(t.Tags, extra), ct);

    public static Task IndexPlaceAsync(ArcadiaDbContext db, Arcadia.Core.Travel.TravelPlace p, string? extra, CancellationToken ct) =>
        UpsertAsync(db, SearchResultKind.Place, p.Id, null, p.Name, Join(p.City, p.Region, p.Description, p.Notes), Aliases(p.Tags, extra), ct);

    public static Task IndexJournalAsync(ArcadiaDbContext db, Arcadia.Core.Travel.TravelJournalEntry j, string? extra, CancellationToken ct) =>
        UpsertAsync(db, SearchResultKind.Journal, j.Id, null, j.Title, j.Text ?? string.Empty, Aliases(j.Tags, extra), ct);

    /// <summary>Separates metadata from file content inside the body column.</summary>
    private const string ContentMarker = "\u001F";

    private static readonly System.Text.RegularExpressions.Regex Spaces = new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);
    // Markdown markers are noise in a one-line excerpt: headings, emphasis, code ticks, checkboxes and list bullets
    private static readonly System.Text.RegularExpressions.Regex Markers = new(@"(?m)(^|\s)(#{1,6}|>|[-*+]\s\[[ xX]\]|[-*+])(?=\s)|\*\*|__|`+|~~", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A one-line excerpt for display (line breaks and markers collapsed), or null if nothing is left.</summary>
    internal static string? CleanSnippet(string? raw)
    {
        if (raw == null) return null;
        var text = Spaces.Replace(Markers.Replace(raw.Replace(ContentMarker, " ", StringComparison.Ordinal), "$1"), " ").Trim();
        return text.Trim('…', ' ').Length == 0 ? null : text;
    }

    private static async Task<string?> ExistingContentAsync(ArcadiaDbContext db, int documentId, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT body FROM SearchIndex WHERE rowid = $id";
        AddParameter(cmd, "$id", RowId("document", documentId));
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct).ConfigureAwait(false);
        var body = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        var at = body?.IndexOf(ContentMarker, StringComparison.Ordinal) ?? -1;
        return at >= 0 ? body![(at + 1)..].Trim() : null;
    }

    public static async Task RebuildAsync(ArcadiaDbContext db, ILogger logger, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // one transaction: two statements per entity in autocommit meant one disk flush each (minutes on a large library)
        await using var tx = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        await db.Database.ExecuteSqlRawAsync("DELETE FROM SearchIndex", ct).ConfigureAwait(false);
        foreach (var p in await db.Projects.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)) await IndexProjectAsync(db, p, ct).ConfigureAwait(false);
        foreach (var t in await db.Tasks.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)) await IndexTaskAsync(db, t, ct).ConfigureAwait(false);
        foreach (var n in await db.Notes.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)) await IndexNoteAsync(db, n, ct).ConfigureAwait(false);
        foreach (var l in await db.ProjectLinks.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)) await IndexLinkAsync(db, l, ct).ConfigureAwait(false);
        foreach (var id in await db.Documents.Select(d => d.Id).ToListAsync(ct).ConfigureAwait(false)) await IndexDocumentAsync(db, id, null, ct).ConfigureAwait(false);
        await TravelStore.ReindexAllAsync(db, ct).ConfigureAwait(false);
        foreach (var e in await db.CalendarEvents.AsNoTracking().Include(x => x.Category).ToListAsync(ct).ConfigureAwait(false))
            await IndexEventAsync(db, e, e.Category == null ? null : e.Category.Name ?? e.Category.Key, ct).ConfigureAwait(false);
        await Finance.FinanceStore.ReindexAllAsync(db, ct).ConfigureAwait(false);
        if (tx != null) await tx.CommitAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Search index rebuilt in {Ms:0} ms", clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>Runs an FTS query. <paramref name="kinds"/> restricts the result types.</summary>
    public static async Task<IReadOnlyList<SearchResult>> QueryAsync(ArcadiaDbContext db, string ftsQuery, IReadOnlySet<SearchResultKind>? kinds, int limit, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        var kindFilter = kinds == null ? string.Empty
            : " AND kind IN (" + string.Join(',', kinds.Select(k => "'" + KindKey(k) + "'")) + ")"; // fixed identifiers only
        // Title matches weigh most, then tags, then body text.
        cmd.CommandText = "SELECT kind, entity_id, project_id, title, snippet(SearchIndex, 4, '', '', '…', 12), bm25(SearchIndex, 0, 0, 0, 10.0, 1.0, 4.0) AS rank " +
                          "FROM SearchIndex WHERE SearchIndex MATCH $q" + kindFilter + " ORDER BY rank LIMIT $n";
        AddParameter(cmd, "$q", ftsQuery);
        AddParameter(cmd, "$n", limit);
        var list = new List<SearchResult>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if (KindOf(r.GetString(0)) is not { } kind) continue;
            int? projectId = r.IsDBNull(2) ? null : Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture);
            var snippet = CleanSnippet(r.IsDBNull(4) ? null : r.GetString(4));
            list.Add(new SearchResult(kind, Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture), projectId, r.GetString(3), snippet, -r.GetDouble(5)));
        }
        return list;
    }

    public static Task<List<int>> MatchDocumentIdsAsync(ArcadiaDbContext db, string ftsQuery, int limit, CancellationToken ct) =>
        MatchIdsAsync(db, ftsQuery, SearchResultKind.Document, limit, ct);

    /// <summary>
    /// Ids of one kind matching the query, best first, without snippets (list filters: "notes about …", the task and
    /// document search boxes). Building snippets for thousands of hits cost ~0.4 s on a large library.
    /// </summary>
    public static async Task<List<int>> MatchIdsAsync(ArcadiaDbContext db, string ftsQuery, SearchResultKind kind, int limit, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entity_id FROM SearchIndex WHERE SearchIndex MATCH $q AND kind = $k ORDER BY bm25(SearchIndex, 0, 0, 0, 10.0, 1.0, 4.0) LIMIT $n";
        AddParameter(cmd, "$q", ftsQuery);
        AddParameter(cmd, "$k", KindKey(kind));
        AddParameter(cmd, "$n", limit);
        var ids = new List<int>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) ids.Add(Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture));
        return ids;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static string Join(params string?[] parts) => string.Join('\n', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>Projects, tasks, notes, links and documents from the FTS index.</summary>
public sealed class IndexSearchProvider(IDbContextFactory<ArcadiaDbContext> factory) : ISearchProvider
{
    public IReadOnlySet<SearchResultKind> Kinds { get; } = new HashSet<SearchResultKind>
        { SearchResultKind.Project, SearchResultKind.Task, SearchResultKind.Note, SearchResultKind.Link, SearchResultKind.Document,
          SearchResultKind.Trip, SearchResultKind.Place, SearchResultKind.Journal, SearchResultKind.Event,
          SearchResultKind.Transaction, SearchResultKind.Account, SearchResultKind.Budget, SearchResultKind.Recurring, SearchResultKind.Goal };

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        if (FtsQuery.Build(request.Text) is not { } q) return [];
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await SearchIndex.QueryAsync(db, q, request.Kinds, request.Limit, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Games by title/platform/tags (a small table: a LIKE query is enough).</summary>
public sealed class GameSearchProvider(IDbContextFactory<ArcadiaDbContext> factory) : ISearchProvider
{
    public IReadOnlySet<SearchResultKind> Kinds { get; } = new HashSet<SearchResultKind> { SearchResultKind.Game };

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var text = request.Text.Trim();
        if (text.Length < 2) return [];
        var pattern = "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var games = await db.Games.AsNoTracking()
            .Where(g => EF.Functions.Like(g.Title, pattern, "\\") || EF.Functions.Like(g.Tags, pattern, "\\") || EF.Functions.Like(g.Platform, pattern, "\\"))
            .OrderBy(g => g.Title).Take(request.Limit)
            .Select(g => new { g.Id, g.Title, g.Platform }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return games.Select(g => new SearchResult(SearchResultKind.Game, g.Id, null, g.Title, g.Platform,
            g.Title.StartsWith(text, StringComparison.CurrentCultureIgnoreCase) ? 12 : 6)).ToList();
    }
}

/// <summary>Countries from the bundled catalogue (English, Arabic and German names).</summary>
public sealed class CountrySearchProvider(Arcadia.Core.Travel.ICountryCatalog countries) : ISearchProvider
{
    public IReadOnlySet<SearchResultKind> Kinds { get; } = new HashSet<SearchResultKind> { SearchResultKind.Country };

    public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var text = request.Text.Trim();
        if (text.Length < 2) return Task.FromResult<IReadOnlyList<SearchResult>>([]);
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        IReadOnlyList<SearchResult> hits = countries.Search(text, 5)
            .Select((c, i) => new SearchResult(SearchResultKind.Country, i, null, c.Name(lang), c.Continent, 11 - i) { Key = c.Code })
            .ToList();
        return Task.FromResult(hits);
    }
}