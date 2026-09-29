using Arcadia.Core.Documents;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Travel;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

public sealed partial class TravelStore
{
    public async Task<IReadOnlyList<TripSummary>> ListTripsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var trips = await db.Trips.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return await SummarizeTripsAsync(db, trips, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TripSummary?> GetTripSummaryAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var trips = await db.Trips.AsNoTracking().Where(t => t.Id == id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return (await SummarizeTripsAsync(db, trips, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    }

    private static async Task<IReadOnlyList<TripSummary>> SummarizeTripsAsync(ArcadiaDbContext db, List<Trip> trips, CancellationToken ct)
    {
        if (trips.Count == 0) return [];
        var ids = trips.Select(t => t.Id).ToList();
        var places = (await db.TripPlaces.AsNoTracking().Where(tp => ids.Contains(tp.TripId))
                .Select(tp => new { tp.TripId, tp.PlaceId, tp.Place!.Name, tp.Place.CountryCode, tp.Place.City, tp.Place.Type, tp.SortOrder }).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(x => x.TripId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.SortOrder).ToList());
        var days = await db.TripDays.Where(d => ids.Contains(d.TripId)).GroupBy(d => d.TripId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        var items = (await db.ItineraryItems.Where(i => ids.Contains(i.Day!.TripId)).Select(i => new { i.Day!.TripId, i.IsDone }).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(i => i.TripId).ToDictionary(g => g.Key, g => (All: g.Count(), Done: g.Count(x => x.IsDone)));
        var photos = (await db.TravelPhotos.AsNoTracking().Where(p => p.TripId != null && ids.Contains(p.TripId.Value))
                .Select(p => new { TripId = p.TripId!.Value, p.Id, p.Document!.Path, p.IsHighlight, p.TakenAt }).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(p => p.TripId).ToDictionary(g => g.Key, g => g.ToList());
        var docs = await db.TripDocuments.Where(d => ids.Contains(d.TripId)).GroupBy(d => d.TripId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        var journal = await db.TravelJournalEntries.Where(j => j.TripId != null && ids.Contains(j.TripId.Value)).GroupBy(j => j.TripId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        var coverIds = trips.Where(t => t.CoverPhotoId != null).Select(t => t.CoverPhotoId!.Value).ToList();
        var covers = await db.TravelPhotos.Where(p => coverIds.Contains(p.Id)).Select(p => new { p.Id, p.Document!.Path }).ToDictionaryAsync(x => x.Id, x => x.Path, ct).ConfigureAwait(false);
        var primaryIds = trips.Where(t => t.PrimaryPlaceId != null).Select(t => t.PrimaryPlaceId!.Value).ToList();
        var primaries = await db.TravelPlaces.Where(p => primaryIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct).ConfigureAwait(false);

        return trips.Select(t =>
        {
            var tp = places.GetValueOrDefault(t.Id) ?? [];
            var own = photos.GetValueOrDefault(t.Id);
            var cover = t.CoverPhotoId is { } c && covers.TryGetValue(c, out var path) ? path
                : own?.OrderByDescending(x => x.IsHighlight).ThenBy(x => x.TakenAt).FirstOrDefault()?.Path;
            var countries = tp.Where(p => p.CountryCode != null).Select(p => p.CountryCode!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var cities = tp.Select(p => p.City ?? (p.Type is PlaceType.City or PlaceType.Town or PlaceType.Village ? p.Name : null))
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            var it = items.GetValueOrDefault(t.Id);
            return new TripSummary(t.Id, t.Name, t.Description, t.StartDate, t.EndDate, t.Status, t.Accent, t.ArtworkTheme, t.ArtworkSeed, cover,
                tp.Count, days.GetValueOrDefault(t.Id), it.All, it.Done, own?.Count ?? 0, docs.GetValueOrDefault(t.Id), journal.GetValueOrDefault(t.Id),
                countries, cities, t.PrimaryPlaceId is { } pp ? primaries.GetValueOrDefault(pp) : tp.FirstOrDefault()?.Name,
                t.IsFavorite, t.Rating, t.ProjectId, t.Tags, t.UpdatedAtUtc, t.LastOpenedAtUtc);
        })
        // newest journeys first; undated ideas last
        .OrderByDescending(t => t.StartDate ?? DateOnly.MinValue).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Trip?> GetTripAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var trip = await db.Trips.AsNoTracking()
            .Include(t => t.Places).ThenInclude(tp => tp.Place)
            .Include(t => t.Days).ThenInclude(d => d.Items).ThenInclude(i => i.Place)
            .Include(t => t.Project)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        if (trip == null) return null;
        trip.Places = trip.Places.OrderBy(p => p.SortOrder).ToList();
        trip.Days = trip.Days.OrderBy(d => d.Index).ToList();
        foreach (var d in trip.Days) d.Items = d.Items.OrderBy(i => i.SortOrder).ToList();
        return trip;
    }

    private async Task<int?> ValidProjectAsync(ArcadiaDbContext db, int? projectId, CancellationToken ct) =>
        projectId is { } p && await db.Projects.AnyAsync(x => x.Id == p, ct).ConfigureAwait(false) ? p : null;

    public async Task<Trip> CreateTripAsync(TripDraft draft, IReadOnlyList<int>? placeIds = null, CancellationToken cancellationToken = default)
    {
        if (!TravelRules.DatesAreValid(draft.StartDate, draft.EndDate)) throw new ArgumentException("The trip cannot end before it starts.");
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var seed = Random.Shared.Next(1, 10_000);
        var trip = new Trip
        {
            Name = RequireName(draft.Name, 200),
            Status = draft.Status,
            StartDate = draft.StartDate,
            EndDate = draft.EndDate,
            Description = Text(draft.Description, 20000),
            Tags = TagText.Normalize(draft.Tags),
            Accent = string.IsNullOrWhiteSpace(draft.Accent) ? "Cyan" : draft.Accent,
            ArtworkTheme = string.IsNullOrWhiteSpace(draft.ArtworkTheme) ? Themes[seed % Themes.Length] : draft.ArtworkTheme,
            ArtworkSeed = seed,
            ProjectId = await ValidProjectAsync(db, draft.ProjectId, cancellationToken).ConfigureAwait(false),
            Rating = Rating(draft.Rating),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.Trips.Add(trip);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var order = 0;
        foreach (var pid in (placeIds ?? []).Append(draft.PrimaryPlaceId ?? 0).Where(p => p > 0).Distinct())
        {
            if (!await db.TravelPlaces.AnyAsync(p => p.Id == pid, cancellationToken).ConfigureAwait(false)) continue;
            db.TripPlaces.Add(new TripPlace { TripId = trip.Id, PlaceId = pid, SortOrder = order++, AddedAtUtc = Now });
        }
        if (draft.PrimaryPlaceId is { } primary && await db.TravelPlaces.AnyAsync(p => p.Id == primary, cancellationToken).ConfigureAwait(false))
            trip.PrimaryPlaceId = primary;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexTripAsync(db, trip.Id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return trip;
    }

    public async Task UpdateTripAsync(int id, TripDraft draft, CancellationToken cancellationToken = default)
    {
        if (!TravelRules.DatesAreValid(draft.StartDate, draft.EndDate)) throw new ArgumentException("The trip cannot end before it starts.");
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var projectId = await ValidProjectAsync(db, draft.ProjectId, cancellationToken).ConfigureAwait(false);
        int? primary = draft.PrimaryPlaceId is { } pp && await db.TravelPlaces.AnyAsync(p => p.Id == pp, cancellationToken).ConfigureAwait(false) ? pp : null;
        await MutateTripAsync(db, id, t =>
        {
            t.Name = RequireName(draft.Name, 200);
            t.Status = draft.Status;
            t.StartDate = draft.StartDate;
            t.EndDate = draft.EndDate;
            t.Description = Text(draft.Description, 20000);
            t.Tags = TagText.Normalize(draft.Tags);
            if (!string.IsNullOrWhiteSpace(draft.Accent)) t.Accent = draft.Accent;
            if (!string.IsNullOrWhiteSpace(draft.ArtworkTheme)) t.ArtworkTheme = draft.ArtworkTheme;
            t.ProjectId = projectId;
            t.PrimaryPlaceId = primary;
            t.Rating = Rating(draft.Rating);
        }, cancellationToken).ConfigureAwait(false);
        if (primary is { } p2 && !await db.TripPlaces.AnyAsync(tp => tp.TripId == id && tp.PlaceId == p2, cancellationToken).ConfigureAwait(false))
            await AddPlaceToTripAsync(id, p2, cancellationToken).ConfigureAwait(false);
    }

    private async Task MutateTripAsync(ArcadiaDbContext db, int id, Action<Trip> change, CancellationToken ct, bool reindex = true)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var trip = await db.Trips.FindAsync([id], ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("The trip no longer exists.");
        change(trip);
        trip.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (reindex) await IndexTripAsync(db, id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task MutateTripAsync(int id, Action<Trip> change, CancellationToken ct, bool reindex = false)
    {
        await using var db = await Db(ct).ConfigureAwait(false);
        await MutateTripAsync(db, id, change, ct, reindex).ConfigureAwait(false);
    }

    public Task SetTripStatusAsync(int id, TripStatus status, CancellationToken cancellationToken = default) =>
        MutateTripAsync(id, t => t.Status = status, cancellationToken);

    public Task SetTripFavoriteAsync(int id, bool favorite, CancellationToken cancellationToken = default) =>
        MutateTripAsync(id, t => t.IsFavorite = favorite, cancellationToken);

    public async Task MarkTripOpenedAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await db.Trips.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastOpenedAtUtc, Now), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTripAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var docIds = await db.TripDocuments.Where(d => d.TripId == id).Select(d => d.DocumentId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var journalIds = await db.TravelJournalEntries.Where(j => j.TripId == id).Select(j => j.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        await db.Trips.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.DeleteAsync(db, SearchResultKind.Trip, id, cancellationToken).ConfigureAwait(false);
        foreach (var d in docIds) await SearchIndex.IndexDocumentAsync(db, d, null, cancellationToken).ConfigureAwait(false);
        foreach (var j in await db.TravelJournalEntries.AsNoTracking().Where(j => journalIds.Contains(j.Id)).ToListAsync(cancellationToken).ConfigureAwait(false))
            await IndexJournalAsync(db, j, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddPlaceToTripAsync(int tripId, int placeId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        if (await db.TripPlaces.AnyAsync(tp => tp.TripId == tripId && tp.PlaceId == placeId, cancellationToken).ConfigureAwait(false)) return;
        if (!await db.Trips.AnyAsync(t => t.Id == tripId, cancellationToken).ConfigureAwait(false) ||
            !await db.TravelPlaces.AnyAsync(p => p.Id == placeId, cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException("The trip or place no longer exists.");
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var next = await db.TripPlaces.Where(tp => tp.TripId == tripId).Select(tp => (int?)tp.SortOrder).MaxAsync(cancellationToken).ConfigureAwait(false) ?? -1;
        db.TripPlaces.Add(new TripPlace { TripId = tripId, PlaceId = placeId, SortOrder = next + 1, AddedAtUtc = Now });
        var place = await db.TravelPlaces.FindAsync([placeId], cancellationToken).ConfigureAwait(false);
        if (place is { Status: PlaceStatus.WantToVisit }) place.Status = PlaceStatus.Planned; // it is part of a plan now
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await TouchTripAsync(db, tripId, cancellationToken).ConfigureAwait(false);
        await IndexTripAsync(db, tripId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePlaceFromTripAsync(int tripId, int placeId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.TripPlaces.Where(tp => tp.TripId == tripId && tp.PlaceId == placeId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Trips.Where(t => t.Id == tripId && t.PrimaryPlaceId == placeId).ExecuteUpdateAsync(s => s.SetProperty(t => t.PrimaryPlaceId, (int?)null), cancellationToken).ConfigureAwait(false);
        await TouchTripAsync(db, tripId, cancellationToken).ConfigureAwait(false);
        await IndexTripAsync(db, tripId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReorderTripPlacesAsync(int tripId, IReadOnlyList<int> placeIds, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var rows = await db.TripPlaces.Where(tp => tp.TripId == tripId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var order = placeIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        foreach (var r in rows) r.SortOrder = order.TryGetValue(r.PlaceId, out var i) ? i : placeIds.Count + r.SortOrder;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task TouchTripAsync(ArcadiaDbContext db, int tripId, CancellationToken ct) =>
        db.Trips.Where(t => t.Id == tripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAtUtc, Now), ct);

    // ───────────────────────── itinerary ─────────────────────────

    public async Task<TripDay> AddDayAsync(int tripId, string? title, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var trip = await db.Trips.FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The trip no longer exists.");
        var count = await db.TripDays.CountAsync(d => d.TripId == tripId, cancellationToken).ConfigureAwait(false);
        if (count >= TravelRules.MaxTripDays) throw new InvalidOperationException("A trip can have at most 366 days.");
        var day = new TripDay { TripId = tripId, Index = count, Title = Line(title, 200) };
        db.TripDays.Add(day);
        // a day added past the last date lengthens a dated trip, so day N always has a real date
        if (TravelRules.DayDate(trip.StartDate, count) is { } date && trip.EndDate is { } end && date > end) trip.EndDate = date;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await TouchTripAsync(db, tripId, cancellationToken).ConfigureAwait(false);
        return day;
    }

    public async Task<int> EnsureDaysForDatesAsync(int tripId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var trip = await db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        if (TravelRules.DurationDays(trip.StartDate, trip.EndDate) is not { } total) return 0;
        total = Math.Min(total, TravelRules.MaxTripDays);
        var count = await db.TripDays.CountAsync(d => d.TripId == tripId, cancellationToken).ConfigureAwait(false);
        for (int i = count; i < total; i++) db.TripDays.Add(new TripDay { TripId = tripId, Index = i });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Math.Max(0, total - count);
    }

    public async Task UpdateDayAsync(int dayId, string? title, string? notes, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var day = await db.TripDays.FindAsync([dayId], cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The day no longer exists.");
        day.Title = Line(title, 200);
        day.Notes = Text(notes, 8000);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDayAsync(int dayId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var day = await db.TripDays.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dayId, cancellationToken).ConfigureAwait(false);
        if (day == null) return;
        await db.TripDays.Where(d => d.Id == dayId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.TripDays.Where(d => d.TripId == day.TripId && d.Index > day.Index)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Index, d => d.Index - 1), cancellationToken).ConfigureAwait(false);
        await TouchTripAsync(db, day.TripId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ItineraryItem Apply(ItineraryItem item, ItineraryItemDraft d)
    {
        item.Title = RequireName(d.Title, 300);
        item.Slot = d.Slot;
        item.Time = d.Slot == TimeSlot.Exact ? d.Time ?? throw new ArgumentException("Choose a time, or pick a part of the day instead.") : null;
        item.Category = d.Category;
        item.PlaceId = d.PlaceId;
        item.Description = Text(d.Description, 4000);
        item.Notes = Text(d.Notes, 8000);
        item.Url = Url(d.Url);
        return item;
    }

    public async Task<ItineraryItem> AddItemAsync(int dayId, ItineraryItemDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var day = await db.TripDays.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dayId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("The day no longer exists.");
        if (draft.PlaceId is { } p && !await db.TravelPlaces.AnyAsync(x => x.Id == p, cancellationToken).ConfigureAwait(false)) draft = draft with { PlaceId = null };
        var next = await db.ItineraryItems.Where(i => i.DayId == dayId).Select(i => (int?)i.SortOrder).MaxAsync(cancellationToken).ConfigureAwait(false) ?? -1;
        var item = Apply(new ItineraryItem { DayId = dayId, SortOrder = next + 1 }, draft);
        db.ItineraryItems.Add(item);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // an itinerary place is part of the trip
        if (item.PlaceId is { } placeId && !await db.TripPlaces.AnyAsync(tp => tp.TripId == day.TripId && tp.PlaceId == placeId, cancellationToken).ConfigureAwait(false))
            await AddPlaceToTripAsync(day.TripId, placeId, cancellationToken).ConfigureAwait(false);
        else await TouchTripAsync(db, day.TripId, cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async Task UpdateItemAsync(int itemId, ItineraryItemDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var item = await db.ItineraryItems.Include(i => i.Day).FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken).ConfigureAwait(false)
                   ?? throw new KeyNotFoundException("The itinerary item no longer exists.");
        if (draft.PlaceId is { } p && !await db.TravelPlaces.AnyAsync(x => x.Id == p, cancellationToken).ConfigureAwait(false)) draft = draft with { PlaceId = null };
        Apply(item, draft);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (item.PlaceId is { } placeId && !await db.TripPlaces.AnyAsync(tp => tp.TripId == item.Day!.TripId && tp.PlaceId == placeId, cancellationToken).ConfigureAwait(false))
            await AddPlaceToTripAsync(item.Day!.TripId, placeId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteItemAsync(int itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await db.ItineraryItems.Where(i => i.Id == itemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetItemDoneAsync(int itemId, bool done, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await db.ItineraryItems.Where(i => i.Id == itemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsDone, done), cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveItemAsync(int itemId, int targetDayId, int targetIndex, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var item = await db.ItineraryItems.Include(i => i.Day).FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken).ConfigureAwait(false)
                   ?? throw new KeyNotFoundException("The itinerary item no longer exists.");
        var target = await db.TripDays.FirstOrDefaultAsync(d => d.Id == targetDayId, cancellationToken).ConfigureAwait(false)
                     ?? throw new KeyNotFoundException("The day no longer exists.");
        if (target.TripId != item.Day!.TripId) throw new InvalidOperationException("Items can only move between days of the same trip.");
        var sourceDayId = item.DayId;
        var targetItems = await db.ItineraryItems.Where(i => i.DayId == targetDayId && i.Id != itemId).OrderBy(i => i.SortOrder).ToListAsync(cancellationToken).ConfigureAwait(false);
        targetIndex = Math.Clamp(targetIndex, 0, targetItems.Count);
        targetItems.Insert(targetIndex, item);
        item.DayId = targetDayId;
        for (int i = 0; i < targetItems.Count; i++) targetItems[i].SortOrder = i;
        if (sourceDayId != targetDayId)
        {
            var rest = await db.ItineraryItems.Where(i => i.DayId == sourceDayId && i.Id != itemId).OrderBy(i => i.SortOrder).ToListAsync(cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < rest.Count; i++) rest[i].SortOrder = i;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SortDayByTimeAsync(int dayId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var items = await db.ItineraryItems.Where(i => i.DayId == dayId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ordered = items.OrderBy(i => TravelRules.SortMinutes(i.Slot, i.Time)).ThenBy(i => i.SortOrder).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].SortOrder = i;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ───────────────────────── trip documents ─────────────────────────

    public async Task<IReadOnlyList<TripDocumentRow>> ListTripDocumentsAsync(int tripId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var links = await db.TripDocuments.AsNoTracking().Where(d => d.TripId == tripId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = links.Select(l => l.DocumentId).ToList();
        var docs = await db.Documents.AsNoTracking().Where(d => ids.Contains(d.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var rows = await DocumentStore.ToRowsAsync(db, docs, cancellationToken).ConfigureAwait(false);
        var important = links.ToDictionary(l => l.DocumentId, l => l.IsImportant);
        return rows.Select(r => new TripDocumentRow(r, important.GetValueOrDefault(r.Id)))
            .OrderByDescending(r => r.IsImportant).ThenBy(r => r.Document.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task AssociateDocumentAsync(int tripId, int documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        if (await db.TripDocuments.AnyAsync(d => d.TripId == tripId && d.DocumentId == documentId, cancellationToken).ConfigureAwait(false)) return;
        if (!await db.Trips.AnyAsync(t => t.Id == tripId, cancellationToken).ConfigureAwait(false) ||
            !await db.Documents.AnyAsync(d => d.Id == documentId, cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException("The trip or document no longer exists.");
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.TripDocuments.Add(new TripDocument { TripId = tripId, DocumentId = documentId, AddedAtUtc = Now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexDocumentAsync(db, documentId, null, cancellationToken).ConfigureAwait(false);
        await TouchTripAsync(db, tripId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DissociateDocumentAsync(int tripId, int documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.TripDocuments.Where(d => d.TripId == tripId && d.DocumentId == documentId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await SearchIndex.IndexDocumentAsync(db, documentId, null, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetDocumentImportantAsync(int tripId, int documentId, bool important, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        await db.TripDocuments.Where(d => d.TripId == tripId && d.DocumentId == documentId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsImportant, important), cancellationToken).ConfigureAwait(false);
    }
}
