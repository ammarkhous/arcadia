using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data.Organizer;

/// <summary>
/// Arcadia-wide notes (general, project, game, travel, IT…). Project notes are the rows the Projects module edits through
/// <see cref="ProjectStore"/>. Autosave keeps a small history: before an overwrite, the previous text is kept as a revision
/// if the last revision is older than <see cref="RevisionSpacing"/>; at most <see cref="MaxRevisions"/> per note.
/// </summary>
public sealed class NoteStore(IDbContextFactory<ArcadiaDbContext> factory, TimeProvider time) : INoteStore
{
    public static readonly TimeSpan RevisionSpacing = TimeSpan.FromMinutes(10);
    public const int MaxRevisions = 10;
    public const int MaxContentChars = 2_000_000;
    private const int ExcerptChars = 240;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<Note?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Note> CreateAsync(NoteDraft draft, CancellationToken cancellationToken = default)
    {
        Validate(draft.Title, draft.Content);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = Now;
        var note = new Note
        {
            Title = TitleOf(draft.Title), Content = draft.Content, Tags = TagText.Normalize(draft.Tags), IsPinned = draft.IsPinned, IsFavorite = draft.IsFavorite,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        await ApplyContextAsync(db, note, draft.Context, cancellationToken).ConfigureAwait(false);
        db.Notes.Add(note);
        ProjectStore.Log(db, note.ProjectId, ProjectActivityKind.NoteAdded, note.Title);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexNoteAsync(db, note, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return note;
    }

    public async Task<DateTime> SaveAsync(int id, string title, string content, string tags, CancellationToken cancellationToken = default)
    {
        Validate(title, content);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var note = await db.Notes.Include(n => n.Project).FirstOrDefaultAsync(n => n.Id == id, cancellationToken).ConfigureAwait(false)
                   ?? throw new KeyNotFoundException("The note no longer exists.");
        var now = Now;
        if (note.Content != content && note.Content.Length > 0) await KeepRevisionAsync(db, note, now, cancellationToken).ConfigureAwait(false);
        note.Title = TitleOf(title);
        note.Content = content;
        note.Tags = TagText.Normalize(tags);
        note.UpdatedAtUtc = now;
        if (note.Project != null) note.Project.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexNoteAsync(db, note, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return now;
    }

    private static async Task KeepRevisionAsync(ArcadiaDbContext db, Note note, DateTime now, CancellationToken ct)
    {
        var last = await db.NoteRevisions.Where(r => r.NoteId == note.Id).MaxAsync(r => (DateTime?)r.SavedAtUtc, ct).ConfigureAwait(false);
        // the first revision marks the state before this editing session; later ones at most every few minutes
        if (last != null && now - last.Value < RevisionSpacing && now - note.UpdatedAtUtc < RevisionSpacing) return;
        db.NoteRevisions.Add(new NoteRevision { NoteId = note.Id, Title = note.Title, Content = note.Content, SavedAtUtc = note.UpdatedAtUtc });
        var surplus = await db.NoteRevisions.Where(r => r.NoteId == note.Id).OrderByDescending(r => r.SavedAtUtc).Skip(MaxRevisions - 1).Select(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        if (surplus.Count > 0) await db.NoteRevisions.Where(r => surplus.Contains(r.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NoteRevision>> GetRevisionsAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.NoteRevisions.AsNoTracking().Where(r => r.NoteId == id).OrderByDescending(r => r.SavedAtUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SetPinnedAsync(int id, bool pinned, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, s => s.SetProperty(n => n.IsPinned, pinned), cancellationToken);

    public Task SetFavoriteAsync(int id, bool favorite, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, s => s.SetProperty(n => n.IsFavorite, favorite), cancellationToken);

    public Task SetAppearanceAsync(int id, string? accent, string? icon, CancellationToken cancellationToken = default)
    {
        if (accent != null && !Palette.Accents.Contains(accent)) throw new ArgumentException("Unknown accent.");
        if (icon?.Length > 32) throw new ArgumentException("Unknown icon.");
        return UpdateAsync(id, s => s.SetProperty(n => n.Accent, accent).SetProperty(n => n.Icon, icon), cancellationToken);
    }

    private async Task UpdateAsync(int id, Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Note>> set, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var n = await db.Notes.Where(x => x.Id == id).ExecuteUpdateAsync(set, ct).ConfigureAwait(false);
        if (n == 0) throw new KeyNotFoundException("The note no longer exists.");
    }

    public async Task SetContextAsync(int id, EntityRef? context, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The note no longer exists.");
        var oldProject = note.ProjectId;
        await ApplyContextAsync(db, note, context, cancellationToken).ConfigureAwait(false);
        if (oldProject != note.ProjectId) ProjectStore.Log(db, note.ProjectId, ProjectActivityKind.NoteAdded, note.Title);
        note.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexNoteAsync(db, note, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id, cancellationToken).ConfigureAwait(false);
        if (note == null) return;
        db.Notes.Remove(note);
        ProjectStore.Log(db, note.ProjectId, ProjectActivityKind.NoteDeleted, note.Title);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.DeleteAsync(db, SearchResultKind.Note, id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ───────────────────────── queries ─────────────────────────

    public async Task<IReadOnlyList<NoteRow>> QueryAsync(NoteQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var q = db.Notes.AsNoTracking();
        q = query.View switch
        {
            NoteView.Pinned => q.Where(n => n.IsPinned),
            NoteView.Favorites => q.Where(n => n.IsFavorite),
            NoteView.Recent => q.Where(n => n.UpdatedAtUtc >= Now.AddDays(-30)),
            _ => q,
        };
        if (query.GeneralOnly) q = q.Where(n => n.ProjectId == null && n.ContextKind == null);
        else if (query.Module is { } module)
            q = module == EntityKind.Project ? q.Where(n => n.ProjectId != null) : q.Where(n => n.ContextKind == module);
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim();
            q = q.Where(n => n.Tags == tag || EF.Functions.Like(n.Tags, tag + ",%") || EF.Functions.Like(n.Tags, "%, " + tag) || EF.Functions.Like(n.Tags, "%, " + tag + ",%"));
        }
        List<int>? ranked = null;
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            if (FtsQuery.Build(query.Text) is not { } fts) return [];
            ranked = await SearchIndex.MatchIdsAsync(db, fts, SearchResultKind.Note, 5000, cancellationToken).ConfigureAwait(false);
            q = q.Where(n => ranked.Contains(n.Id));
        }
        q = query.Sort switch
        {
            NoteSort.Title => q.OrderBy(n => n.Title),
            NoteSort.Created => q.OrderByDescending(n => n.CreatedAtUtc),
            _ => q.OrderByDescending(n => n.IsPinned && query.View == NoteView.All).ThenByDescending(n => n.UpdatedAtUtc),
        };
        var rows = await q.Skip(Math.Max(0, query.Skip)).Take(Math.Clamp(query.Take, 1, 5000))
            .Select(n => new
            {
                n.Id, n.Title, Head = n.Content.Substring(0, ExcerptChars + 60), n.IsPinned, n.IsFavorite, n.Tags, n.ProjectId, n.ContextKind, n.ContextId, n.Accent, n.Icon,
                n.CreatedAtUtc, n.UpdatedAtUtc, n.Content.Length,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var list = rows.Select(r => new NoteRow(r.Id, r.Title, TravelStore.Excerpt(r.Head, ExcerptChars), r.IsPinned, r.IsFavorite, r.Tags,
            r.ProjectId is { } p ? new EntityRef(EntityKind.Project, p) : r.ContextKind is { } k && r.ContextId is { } cid ? new EntityRef(k, cid) : null,
            r.Accent, r.Icon, r.CreatedAtUtc, r.UpdatedAtUtc, r.Length)).ToList();
        // text search keeps relevance order unless the user chose one
        if (ranked != null && query.Sort == NoteSort.Updated)
        {
            var order = ranked.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            list = list.OrderBy(n => order.GetValueOrDefault(n.Id, int.MaxValue)).ToList();
        }
        return list;
    }

    public async Task<IReadOnlyList<TagCount>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var all = await db.Notes.AsNoTracking().Where(n => n.Tags != "").Select(n => n.Tags).ToListAsync(cancellationToken).ConfigureAwait(false);
        return all.SelectMany(TagText.Split).GroupBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new TagCount(g.First(), g.Count())).OrderByDescending(t => t.Count).ThenBy(t => t.Tag, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ───────────────────────── helpers ─────────────────────────

    private static async Task ApplyContextAsync(ArcadiaDbContext db, Note note, EntityRef? context, CancellationToken ct)
    {
        note.ProjectId = null;
        note.ContextKind = null;
        note.ContextId = null;
        if (context is not { } c) return;
        if (c.Kind == EntityKind.Project)
        {
            if (!await db.Projects.AnyAsync(p => p.Id == c.Id, ct).ConfigureAwait(false)) throw new ArgumentException("The project no longer exists.");
            note.ProjectId = c.Id;
        }
        else if (c.Kind == EntityKind.Note) throw new ArgumentException("A note cannot belong to another note; link to it in the text instead.");
        else
        {
            note.ContextKind = c.Kind;
            note.ContextId = c.Id;
        }
    }

    private static string TitleOf(string? title) => string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();

    private static void Validate(string? title, string content)
    {
        if (title?.Trim().Length > 300) throw new ArgumentException("The title is too long (300 characters at most).");
        if (content.Length > MaxContentChars) throw new ArgumentException("The note is too long.");
    }
}

/// <summary>Arcadia's accent tones: the only colours users can pick (categories, notes, projects).</summary>
public static class Palette
{
    public static readonly IReadOnlySet<string> Accents = new HashSet<string>(StringComparer.Ordinal) { "Blue", "Cyan", "Green", "Amber", "Purple", "Red", "Gold", "Steel" };
}
