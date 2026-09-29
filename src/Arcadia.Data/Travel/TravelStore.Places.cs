using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Text;
using Arcadia.Core.Travel;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

public sealed partial class TravelStore
{
    public async Task<IReadOnlyList<PlaceSummary>> ListPlacesAsync(PlaceQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        IQueryable<TravelPlace> q = db.TravelPlaces.AsNoTracking();
        if (query.Status is { } s) q = q.Where(p => p.Status == s);
        if (query.Type is { } t) q = q.Where(p => p.Type == t);
        if (CountryCode(query.CountryCode) is { } cc) q = q.Where(p => p.CountryCode == cc);
        if (query.FavoritesOnly) q = q.Where(p => p.IsFavorite);
        if (query.TripId is { } trip) q = q.Where(p => p.Trips.Any(tp => tp.TripId == trip));
        var places = await q.ToListAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var folded = TextFold.Fold(query.Text);
            var countryHits = _countries?.Search(query.Text, 5).Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            places = places.Where(p => Matches(p.Name, folded) || Matches(p.City, folded) || Matches(p.Region, folded) || Matches(p.Tags, folded)
                                       || Matches(p.Description, folded) || (p.CountryCode != null && countryHits.Contains(p.CountryCode))).ToList();
        }
        return await SummarizeAsync(db, places, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PlaceSummary>> SummarizeAsync(ArcadiaDbContext db, List<TravelPlace> places, CancellationToken ct)
    {
        if (places.Count == 0) return [];
        var ids = places.Select(p => p.Id).ToList();
        var visits = (await db.PlaceVisits.AsNoTracking().Where(v => ids.Contains(v.PlaceId)).Select(v => new { v.PlaceId, v.VisitedOn }).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(v => v.PlaceId).ToDictionary(g => g.Key, g => (Count: g.Count(), First: g.Min(x => x.VisitedOn), Last: g.Max(x => x.VisitedOn)));
        var trips = await db.TripPlaces.Where(tp => ids.Contains(tp.PlaceId)).GroupBy(tp => tp.PlaceId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        var photos = (await db.TravelPhotos.AsNoTracking().Where(ph => ph.PlaceId != null && ids.Contains(ph.PlaceId.Value))
                .Select(ph => new { PlaceId = ph.PlaceId!.Value, ph.Id, ph.Document!.Path, ph.IsHighlight, ph.TakenAt }).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(ph => ph.PlaceId).ToDictionary(g => g.Key, g => g.ToList());
        var coverIds = places.Where(p => p.CoverPhotoId != null).Select(p => p.CoverPhotoId!.Value).ToList();
        var covers = await db.TravelPhotos.Where(ph => coverIds.Contains(ph.Id)).Select(ph => new { ph.Id, ph.Document!.Path }).ToDictionaryAsync(x => x.Id, x => x.Path, ct).ConfigureAwait(false);

        return places.Select(p =>
        {
            var v = visits.GetValueOrDefault(p.Id);
            var own = photos.GetValueOrDefault(p.Id);
            var cover = p.CoverPhotoId is { } c && covers.TryGetValue(c, out var path) ? path
                : own?.OrderByDescending(x => x.IsHighlight).ThenByDescending(x => x.TakenAt).FirstOrDefault()?.Path;
            return new PlaceSummary(p.Id, p.Name, p.Type, p.Status, p.IsFavorite, p.Latitude, p.Longitude, p.CountryCode, p.Region, p.City, p.Rating, p.Tags,
                v.Count, v.First, v.Last, trips.GetValueOrDefault(p.Id), own?.Count ?? 0, cover, p.CreatedAtUtc);
        }).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<TravelPlace?> GetPlaceAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var place = await db.TravelPlaces.AsNoTracking().Include(p => p.Visits).AsSplitQuery().FirstOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
        if (place != null) place.Visits = place.Visits.OrderByDescending(v => v.VisitedOn).ThenByDescending(v => v.CreatedAtUtc).ToList();
        return place;
    }

    public async Task<TravelPlace?> FindPlaceBySourceAsync(string source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        return await db.TravelPlaces.AsNoTracking().FirstOrDefaultAsync(p => p.Source == source, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TravelPlace> CreatePlaceAsync(PlaceDraft draft, CancellationToken cancellationToken = default)
    {
        var (lat, lon) = Position(draft.Latitude, draft.Longitude);
        var place = new TravelPlace
        {
            Name = RequireName(draft.Name, 200),
            Type = draft.Type,
            Status = draft.Status,
            Latitude = lat,
            Longitude = lon,
            CountryCode = CountryCode(draft.CountryCode) ?? (lat is { } a && lon is { } o ? _countries?.Near(a, o)?.Code : null),
            Region = Line(draft.Region, 200),
            City = Line(draft.City, 200),
            Description = Text(draft.Description, 8000),
            Notes = Text(draft.Notes, 50000),
            Rating = Rating(draft.Rating),
            IsFavorite = draft.IsFavorite,
            Tags = TagText.Normalize(draft.Tags),
            Source = Line(draft.Source, 200),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.TravelPlaces.Add(place);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexPlaceAsync(db, place, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return place;
    }

    public async Task UpdatePlaceAsync(int id, PlaceDraft draft, CancellationToken cancellationToken = default)
    {
        var (lat, lon) = Position(draft.Latitude, draft.Longitude);
        await MutatePlaceAsync(id, p =>
        {
            p.Name = RequireName(draft.Name, 200);
            p.Type = draft.Type;
            p.Status = draft.Status;
            p.Latitude = lat;
            p.Longitude = lon;
            p.CountryCode = CountryCode(draft.CountryCode) ?? (lat is { } a && lon is { } o ? _countries?.Near(a, o)?.Code : null);
            p.Region = Line(draft.Region, 200);
            p.City = Line(draft.City, 200);
            p.Description = Text(draft.Description, 8000);
            p.Notes = Text(draft.Notes, 50000);
            p.Rating = Rating(draft.Rating);
            p.IsFavorite = draft.IsFavorite;
            p.Tags = TagText.Normalize(draft.Tags);
            if (draft.Source != null) p.Source = Line(draft.Source, 200);
        }, reindexTrips: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task MutatePlaceAsync(int id, Action<TravelPlace> change, bool reindexTrips, CancellationToken ct)
    {
        await using var db = await Db(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var place = await db.TravelPlaces.FindAsync([id], ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("The place no longer exists.");
        change(place);
        place.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await IndexPlaceAsync(db, place, ct).ConfigureAwait(false);
        if (reindexTrips) await ReindexTripsOfPlaceAsync(db, id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task SetPlaceStatusAsync(int id, PlaceStatus status, CancellationToken cancellationToken = default) =>
        MutatePlaceAsync(id, p => p.Status = status, false, cancellationToken);

    public Task SetPlaceFavoriteAsync(int id, bool favorite, CancellationToken cancellationToken = default) =>
        MutatePlaceAsync(id, p => p.IsFavorite = favorite, false, cancellationToken);

    public Task SetPlaceNotesAsync(int id, string? notes, CancellationToken cancellationToken = default) =>
        MutatePlaceAsync(id, p => p.Notes = Text(notes, 50000), false, cancellationToken);

    public async Task DeletePlaceAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var trips = await db.TripPlaces.Where(tp => tp.PlaceId == id).Select(tp => tp.TripId).ToListAsync(cancellationToken).ConfigureAwait(false);
        // photos, journal entries, itinerary items and bucket entries stay; they only lose the link (ON DELETE SET NULL)
        await db.TravelPlaces.Where(p => p.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.DeleteAsync(db, SearchResultKind.Place, id, cancellationToken).ConfigureAwait(false);
        foreach (var t in trips) await IndexTripAsync(db, t, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaceVisit> AddVisitAsync(int placeId, DateOnly? visitedOn, int? tripId, string? note, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var place = await db.TravelPlaces.FindAsync([placeId], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The place no longer exists.");
        if (tripId is { } t && !await db.Trips.AnyAsync(x => x.Id == t, cancellationToken).ConfigureAwait(false)) tripId = null;
        var visit = new PlaceVisit { PlaceId = placeId, TripId = tripId, VisitedOn = visitedOn, Note = Text(note, 2000), CreatedAtUtc = Now };
        db.PlaceVisits.Add(visit);
        place.Status = PlaceStatus.Visited;
        place.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return visit;
    }

    public async Task DeleteVisitAsync(int visitId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await db.PlaceVisits.Where(v => v.Id == visitId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(int TripId, string Name, DateOnly? Start)>> GetPlaceTripsAsync(int placeId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var rows = await db.TripPlaces.Where(tp => tp.PlaceId == placeId)
            .Select(tp => new { tp.TripId, tp.Trip!.Name, tp.Trip.StartDate }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.OrderByDescending(r => r.StartDate).Select(r => (r.TripId, r.Name, r.StartDate)).ToList();
    }
}
