using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Text;
using Arcadia.Core.Travel;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

public sealed partial class TravelStore
{
    // ───────────────────────── journal ─────────────────────────

    public async Task<IReadOnlyList<JournalSummary>> ListJournalAsync(JournalQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        IQueryable<TravelJournalEntry> q = db.TravelJournalEntries.AsNoTracking();
        if (query.TripId is { } t) q = q.Where(j => j.TripId == t);
        if (query.PlaceId is { } p) q = q.Where(j => j.PlaceId == p);
        var rows = await q.Select(j => new { j, Trip = j.Trip!.Name, Place = j.Place!.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var folded = TextFold.Fold(query.Text);
            rows = rows.Where(r => Matches(r.j.Title, folded) || Matches(r.j.Text, folded) || Matches(r.j.Tags, folded) || Matches(r.Trip, folded) || Matches(r.Place, folded)).ToList();
        }
        var ids = rows.Select(r => r.j.Id).ToList();
        var photos = await db.TravelPhotos.Where(ph => ph.JournalEntryId != null && ids.Contains(ph.JournalEntryId.Value)).GroupBy(ph => ph.JournalEntryId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new JournalSummary(r.j.Id, r.j.Title, r.j.EntryDate, Excerpt(r.j.Text), r.j.TripId, r.Trip, r.j.PlaceId, r.Place, r.j.Mood, r.j.Tags,
                r.j.IsHighlight, photos.GetValueOrDefault(r.j.Id), r.j.UpdatedAtUtc))
            .OrderByDescending(j => j.EntryDate).ThenByDescending(j => j.UpdatedAtUtc).ToList();
    }

    /// <summary>First ~180 characters of text, without Markdown markers.</summary>
    internal static string Excerpt(string text, int max = 180)
    {
        var plain = string.Join(' ', text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal))
            .Select(l => BlockMarkup.Replace(l, string.Empty))
            .Select(Inline).Where(l => l.Length > 0));
        return plain.Length <= max ? plain : plain[..max].TrimEnd() + "…";
    }

    // "# ", "> ", "- ", "* ", "1. " and "- [x] " at the start of a line
    private static readonly System.Text.RegularExpressions.Regex BlockMarkup = new(@"^(?:#{1,6}\s*|>\s*)*(?:(?:[-*+]|\d+[.)])\s+)?(?:\[[ xX]\]\s*)?", System.Text.RegularExpressions.RegexOptions.Compiled);
    // [text](url) and ![alt](url) → text; **bold**, *italic*, _italic_, ~~struck~~ and `code` → their words
    private static readonly System.Text.RegularExpressions.Regex LinkMarkup = new(@"!?\[([^\]]*)\]\([^)]*\)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex Emphasis = new(@"(\*{1,3}|~~)(?=\S)(.+?)(?<=\S)\1|(?<!\w)(_{1,3})(?=\S)(.+?)(?<=\S)\3(?!\w)|`([^`]+)`", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Inline(string line) =>
        Emphasis.Replace(LinkMarkup.Replace(line, "$1"), m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value).Trim();

    public async Task<TravelJournalEntry?> GetJournalAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        return await db.TravelJournalEntries.AsNoTracking().Include(j => j.Trip).Include(j => j.Place).FirstOrDefaultAsync(j => j.Id == id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int? Trip, int? Place)> ValidLinksAsync(ArcadiaDbContext db, int? tripId, int? placeId, CancellationToken ct)
    {
        int? t = tripId is { } a && await db.Trips.AnyAsync(x => x.Id == a, ct).ConfigureAwait(false) ? a : null;
        int? p = placeId is { } b && await db.TravelPlaces.AnyAsync(x => x.Id == b, ct).ConfigureAwait(false) ? b : null;
        return (t, p);
    }

    public async Task<TravelJournalEntry> CreateJournalAsync(JournalDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (trip, place) = await ValidLinksAsync(db, draft.TripId, draft.PlaceId, cancellationToken).ConfigureAwait(false);
        var entry = new TravelJournalEntry
        {
            Title = string.IsNullOrWhiteSpace(draft.Title) ? "…" : ProjectStore.Clip(draft.Title, 300),
            EntryDate = draft.EntryDate,
            Text = draft.Text ?? string.Empty,
            TripId = trip,
            PlaceId = place,
            Mood = draft.Mood,
            Tags = TagText.Normalize(draft.Tags),
            IsHighlight = draft.IsHighlight,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.TravelJournalEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexJournalAsync(db, entry, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    /// <summary>Saves an edit (used by autosave). The text is stored exactly as typed.</summary>
    public async Task UpdateJournalAsync(int id, JournalDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entry = await db.TravelJournalEntries.FindAsync([id], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The journal entry no longer exists.");
        var (trip, place) = await ValidLinksAsync(db, draft.TripId, draft.PlaceId, cancellationToken).ConfigureAwait(false);
        entry.Title = string.IsNullOrWhiteSpace(draft.Title) ? "…" : ProjectStore.Clip(draft.Title, 300);
        entry.EntryDate = draft.EntryDate;
        entry.Text = draft.Text ?? string.Empty;
        entry.TripId = trip;
        entry.PlaceId = place;
        entry.Mood = draft.Mood;
        entry.Tags = TagText.Normalize(draft.Tags);
        entry.IsHighlight = draft.IsHighlight;
        entry.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexJournalAsync(db, entry, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteJournalAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.TravelJournalEntries.Where(j => j.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.DeleteAsync(db, SearchResultKind.Journal, id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ───────────────────────── photos ─────────────────────────

    public async Task<IReadOnlyList<int>> AttachPhotosAsync(IReadOnlyList<(int DocumentId, PhotoMetadata Metadata)> photos, PhotoLink link, CancellationToken cancellationToken = default)
    {
        if (photos.Count == 0) return [];
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var (trip, place) = await ValidLinksAsync(db, link.TripId, link.PlaceId, cancellationToken).ConfigureAwait(false);
        int? journal = link.JournalEntryId is { } j && await db.TravelJournalEntries.AnyAsync(x => x.Id == j, cancellationToken).ConfigureAwait(false) ? j : null;
        var docIds = photos.Select(p => p.DocumentId).ToList();
        var existing = await db.TravelPhotos.Where(p => docIds.Contains(p.DocumentId)).ToDictionaryAsync(p => p.DocumentId, cancellationToken).ConfigureAwait(false);
        var validDocs = (await db.Documents.Where(d => docIds.Contains(d.Id)).Select(d => d.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var result = new List<TravelPhoto>();
        foreach (var (docId, meta) in photos)
        {
            if (!validDocs.Contains(docId)) continue;
            if (!existing.TryGetValue(docId, out var photo))
            {
                photo = new TravelPhoto { DocumentId = docId, AddedAtUtc = Now };
                db.TravelPhotos.Add(photo);
                existing[docId] = photo;
            }
            photo.TakenAt ??= meta.TakenAt;
            if (photo.Latitude == null && meta.Latitude is { } la && meta.Longitude is { } lo && GeoMath.IsValid(la, lo)) { photo.Latitude = la; photo.Longitude = lo; }
            photo.Camera ??= meta.Camera is { } cam ? ProjectStore.Clip(cam, 120) : null;
            if (meta.Orientation is >= 1 and <= 8) photo.Orientation = meta.Orientation;
            photo.PixelWidth ??= meta.PixelWidth;
            photo.PixelHeight ??= meta.PixelHeight;
            if (trip != null) photo.TripId = trip;
            if (place != null) photo.PlaceId = place;
            if (journal != null) photo.JournalEntryId = journal;
            result.Add(photo);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result.Select(p => p.Id).Distinct().ToList();
    }

    public async Task<IReadOnlyList<PhotoRow>> ListPhotosAsync(PhotoQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        IQueryable<TravelPhoto> q = db.TravelPhotos.AsNoTracking();
        if (query.TripId is { } t) q = q.Where(p => p.TripId == t);
        if (query.PlaceId is { } pl) q = q.Where(p => p.PlaceId == pl);
        if (query.JournalEntryId is { } j) q = q.Where(p => p.JournalEntryId == j);
        if (query.UnassignedWithGpsOnly) q = q.Where(p => p.PlaceId == null && p.Latitude != null);
        var rows = await ProjectPhotos(q).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.OrderBy(r => r.SortDate).Take(Math.Max(1, query.Take)).ToList();
    }

    private static IQueryable<PhotoRow> ProjectPhotos(IQueryable<TravelPhoto> q) =>
        q.Select(p => new PhotoRow(p.Id, p.DocumentId, p.Document!.Path, p.Document.DisplayName, p.Document.IsMissing, p.Document.SizeBytes, p.Document.FileModifiedUtc,
            p.TakenAt, p.Latitude, p.Longitude, p.Camera, p.Orientation, p.PixelWidth, p.PixelHeight, p.TripId, p.Trip!.Name, p.PlaceId, p.Place!.Name,
            p.JournalEntryId, p.Caption, p.IsHighlight, p.AddedAtUtc));

    public async Task<PhotoRow?> GetPhotoAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        return await ProjectPhotos(db.TravelPhotos.AsNoTracking().Where(p => p.Id == id)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePhotoAsync(int id, string? caption, bool isHighlight, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var c = Text(caption, 1000);
        await db.TravelPhotos.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Caption, c).SetProperty(p => p.IsHighlight, isHighlight), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPhotosTripAsync(IReadOnlyCollection<int> photoIds, int? tripId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var (trip, _) = await ValidLinksAsync(db, tripId, null, cancellationToken).ConfigureAwait(false);
        if (tripId != null && trip == null) throw new KeyNotFoundException("The trip no longer exists.");
        await db.TravelPhotos.Where(p => photoIds.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.TripId, trip), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPhotosPlaceAsync(IReadOnlyCollection<int> photoIds, int? placeId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var (_, place) = await ValidLinksAsync(db, null, placeId, cancellationToken).ConfigureAwait(false);
        if (placeId != null && place == null) throw new KeyNotFoundException("The place no longer exists.");
        await db.TravelPhotos.Where(p => photoIds.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.PlaceId, place), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPhotosJournalAsync(IReadOnlyCollection<int> photoIds, int? journalEntryId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        int? j = journalEntryId is { } x && await db.TravelJournalEntries.AnyAsync(e => e.Id == x, cancellationToken).ConfigureAwait(false) ? x : null;
        await db.TravelPhotos.Where(p => photoIds.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.JournalEntryId, j), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePhotosAsync(IReadOnlyCollection<int> photoIds, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var docIds = await db.TravelPhotos.Where(p => photoIds.Contains(p.Id)).Select(p => p.DocumentId).ToListAsync(cancellationToken).ConfigureAwait(false);
        await db.Trips.Where(t => t.CoverPhotoId != null && photoIds.Contains(t.CoverPhotoId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CoverPhotoId, (int?)null), cancellationToken).ConfigureAwait(false);
        await db.TravelPlaces.Where(p => p.CoverPhotoId != null && photoIds.Contains(p.CoverPhotoId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CoverPhotoId, (int?)null), cancellationToken).ConfigureAwait(false);
        await db.TravelPhotos.Where(p => photoIds.Contains(p.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        // Drop the library reference too when nothing else uses it. The image file itself is never touched.
        var unused = await db.Documents.Where(d => docIds.Contains(d.Id) && !d.IsFavorite && d.Description == null && !d.Tags.Any() && !d.Projects.Any()
                                                   && !db.TripDocuments.Any(td => td.DocumentId == d.Id))
            .Select(d => d.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        await db.Documents.Where(d => unused.Contains(d.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in unused) await SearchIndex.DeleteAsync(db, SearchResultKind.Document, id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetTripCoverAsync(int tripId, int? photoId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        if (photoId is { } p && !await db.TravelPhotos.AnyAsync(x => x.Id == p, cancellationToken).ConfigureAwait(false)) throw new KeyNotFoundException("The photo no longer exists.");
        await db.Trips.Where(t => t.Id == tripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.CoverPhotoId, photoId).SetProperty(t => t.UpdatedAtUtc, Now), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPlaceCoverAsync(int placeId, int? photoId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        if (photoId is { } p && !await db.TravelPhotos.AnyAsync(x => x.Id == p, cancellationToken).ConfigureAwait(false)) throw new KeyNotFoundException("The photo no longer exists.");
        await db.TravelPlaces.Where(t => t.Id == placeId).ExecuteUpdateAsync(s => s.SetProperty(t => t.CoverPhotoId, photoId), cancellationToken).ConfigureAwait(false);
    }

    // ───────────────────────── bucket list ─────────────────────────

    public async Task<IReadOnlyList<BucketRow>> ListBucketAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        return await db.BucketListItems.AsNoTracking()
            .OrderBy(b => b.IsCompleted).ThenBy(b => b.SortOrder).ThenBy(b => b.Id)
            .Select(b => new BucketRow(b.Id, b.Title, b.Kind, b.Priority, b.TargetYear, b.CountryCode, b.PlaceId, b.Place!.Name, b.TripId, b.Trip!.Name,
                b.Notes, b.Tags, b.IsCompleted, b.CompletedOn, b.SortOrder))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<BucketListItem> ApplyAsync(ArcadiaDbContext db, BucketListItem item, BucketDraft d, CancellationToken ct)
    {
        if (d.TargetYear is { } y && (y < 1900 || y > 2200)) throw new ArgumentException("Enter a target year such as 2027.");
        var (trip, place) = await ValidLinksAsync(db, d.TripId, d.PlaceId, ct).ConfigureAwait(false);
        item.Title = RequireName(d.Title, 300);
        item.Kind = d.Kind;
        item.Priority = d.Priority;
        item.TargetYear = d.TargetYear;
        item.CountryCode = CountryCode(d.CountryCode);
        item.PlaceId = place;
        item.TripId = trip;
        item.Notes = Text(d.Notes, 4000);
        item.Tags = TagText.Normalize(d.Tags);
        return item;
    }

    public async Task<BucketListItem> CreateBucketAsync(BucketDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var next = await db.BucketListItems.Select(b => (int?)b.SortOrder).MaxAsync(cancellationToken).ConfigureAwait(false) ?? -1;
        var item = await ApplyAsync(db, new BucketListItem { CreatedAtUtc = Now, SortOrder = next + 1 }, draft, cancellationToken).ConfigureAwait(false);
        db.BucketListItems.Add(item);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async Task UpdateBucketAsync(int id, BucketDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var item = await db.BucketListItems.FindAsync([id], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The bucket list entry no longer exists.");
        await ApplyAsync(db, item, draft, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetBucketCompletedAsync(int id, bool completed, DateOnly? on, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        DateOnly? date = completed ? on ?? Today : null;
        await db.BucketListItems.Where(b => b.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.IsCompleted, completed).SetProperty(b => b.CompletedOn, date), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBucketAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await db.BucketListItems.Where(b => b.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReorderBucketAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var items = await db.BucketListItems.Where(b => ids.Contains(b.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        foreach (var b in items) b.SortOrder = order[b.Id];
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
