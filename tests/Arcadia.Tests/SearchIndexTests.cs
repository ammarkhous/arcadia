using Arcadia.Core.Search;
using Arcadia.Data;
using Arcadia.Data.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

/// <summary>Phase 10: keyed rowids (O(log n) updates) and the one-time re-key of indexes written by earlier versions.</summary>
public sealed class SearchIndexTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<ArcadiaDbContext> _options;

    public SearchIndexTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite(_connection).Options;
        using var db = new ArcadiaDbContext(_options);
        db.Database.Migrate();
    }

    private List<(long RowId, string Kind, long Id, string Title, string Body)> Rows()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT rowid, kind, entity_id, title, body FROM SearchIndex ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, long, string, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    [Fact]
    public async Task Upsert_replaces_the_row_in_place_by_keyed_rowid()
    {
        await using var db = new ArcadiaDbContext(_options);
        await SearchIndex.UpsertAsync(db, SearchResultKind.Note, 7, null, "First", "body", null, default);
        await SearchIndex.UpsertAsync(db, SearchResultKind.Note, 7, null, "Second", "body", null, default);
        await SearchIndex.UpsertAsync(db, SearchResultKind.Task, 7, null, "A task", "", null, default);

        var rows = Rows();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, x => x.Kind == "note" && x.Title == "Second" && x.RowId == (3L << 32 | 7));
        Assert.Contains(rows, x => x.Kind == "task" && x.RowId == (2L << 32 | 7));

        await SearchIndex.DeleteAsync(db, SearchResultKind.Note, 7, default);
        Assert.Equal("task", Assert.Single(Rows()).Kind);
    }

    [Fact]
    public async Task Legacy_rows_are_rekeyed_once_keeping_document_text_and_dropping_duplicates()
    {
        using (var cmd = _connection.CreateCommand())
        {
            // an index as Phases 4–9 wrote it: implicit small rowids, a duplicate, extracted document content
            cmd.CommandText = "INSERT INTO SearchIndex (kind, entity_id, project_id, title, body, tags) VALUES " +
                "('document', 12, NULL, 'Invoice', 'invoice.pdf \u0001 extracted words', ''), " +
                "('note', 3, NULL, 'Old title', 'x', ''), ('note', 3, NULL, 'New title', 'y', ''), ('txn', 99, NULL, 'REWE', '', '')";
            cmd.ExecuteNonQuery();
        }
        await using var db = new ArcadiaDbContext(_options);

        Assert.True(await SearchIndex.RekeyLegacyRowsAsync(db, NullLogger.Instance, default));

        var rows = Rows();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, x => Assert.True(x.RowId >= SearchIndex.FirstKeyedRowId));
        Assert.Equal("invoice.pdf \u0001 extracted words", rows.Single(x => x.Kind == "document").Body);
        Assert.Equal("New title", rows.Single(x => x.Kind == "note").Title);
        Assert.False(await SearchIndex.RekeyLegacyRowsAsync(db, NullLogger.Instance, default)); // once
        var hits = await SearchIndex.QueryAsync(db, "extracted", null, 10, default);
        Assert.Equal(12, Assert.Single(hits).Id);
    }

    public void Dispose() => _connection.Dispose();
}
