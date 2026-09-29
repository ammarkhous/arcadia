using Arcadia.Core.Text;
using Arcadia.Core.Travel;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

public sealed partial class TravelStore
{
    // ───────────────────────── countries ─────────────────────────

    public async Task<IReadOnlyList<CountryProgress>> GetCountryProgressAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        return await CountryProgressAsync(db, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<CountryProgress>> CountryProgressAsync(ArcadiaDbContext db, CancellationToken ct)
    {
        var places = await db.TravelPlaces.AsNoTracking().Where(p => p.CountryCode != null)
            .Select(p => new { p.Id, Code = p.CountryCode!, p.Status }).ToListAsync(ct).ConfigureAwait(false);
        var visits = await db.PlaceVisits.AsNoTracking().Where(v => v.Place!.CountryCode != null)
            .Select(v => new { Code = v.Place!.CountryCode!, v.VisitedOn }).ToListAsync(ct).ConfigureAwait(false);
        var trips = await db.TripPlaces.AsNoTracking().Where(tp => tp.Place!.CountryCode != null && tp.Trip!.Status != TripStatus.Cancelled)
            .Select(tp => new { Code = tp.Place!.CountryCode!, tp.TripId }).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var marks = await db.TravelCountries.AsNoTracking().ToDictionaryAsync(c => c.Code, StringComparer.OrdinalIgnoreCase, ct).ConfigureAwait(false);

        var codes = places.Select(p => p.Code).Concat(marks.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        return codes.Select(code =>
        {
            var ps = places.Where(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)).ToList();
            var vs = visits.Where(v => string.Equals(v.Code, code, StringComparison.OrdinalIgnoreCase)).ToList();
            var mark = marks.GetValueOrDefault(code);
            var visitedPlaces = ps.Count(p => p.Status == PlaceStatus.Visited);
            var visited = (mark?.MarkedVisited ?? false) || visitedPlaces > 0;
            return new CountryProgress(code.ToUpperInvariant(), visited, mark?.MarkedVisited ?? false,
                (mark?.WantToVisit ?? false) || (!visited && ps.Count > 0), mark?.IsFavorite ?? false,
                ps.Count, visitedPlaces, vs.Count, vs.Max(v => v.VisitedOn),
                trips.Count(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase)), mark?.Notes);
        }).OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
    }

    public async Task SetCountryAsync(string code, bool? favorite = null, bool? wantToVisit = null, bool? markedVisited = null, string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var c = CountryCode(code) ?? throw new ArgumentException("A country code is required.");
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var row = await db.TravelCountries.FindAsync([c], cancellationToken).ConfigureAwait(false);
        if (row == null) db.TravelCountries.Add(row = new TravelCountry { Code = c });
        if (favorite is { } f) row.IsFavorite = f;
        if (wantToVisit is { } w) row.WantToVisit = w;
        if (markedVisited is { } m) row.MarkedVisited = m;
        if (notes != null) row.Notes = Text(notes, 4000);
        row.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ───────────────────────── statistics ─────────────────────────

    public async Task<TravelStats> GetStatsAsync(DateOnly today, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var countries = await CountryProgressAsync(db, cancellationToken).ConfigureAwait(false);
        var visitedCodes = countries.Where(c => c.Visited).Select(c => c.Code).ToList();
        var un = _countries == null ? visitedCodes.Count : visitedCodes.Count(c => _countries.Find(c)?.IsUnState == true);
        var continents = _countries == null ? 0 : visitedCodes.Select(c => _countries.Find(c)?.Continent).Where(c => !string.IsNullOrEmpty(c) && c != "Seven seas (open ocean)").Distinct().Count();

        var places = await db.TravelPlaces.AsNoTracking().Select(p => new { p.Id, p.Name, p.City, p.Type, p.Status, p.CountryCode }).ToListAsync(cancellationToken).ConfigureAwait(false);
        // a city counts once however many of its places were visited ("Rome" + "Colosseum" in Rome = 1 city)
        var visitedCities = places.Where(p => p.Status == PlaceStatus.Visited)
            .Select(p => p.Type is PlaceType.City or PlaceType.Town or PlaceType.Village ? p.Name : p.City)
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => TextFold.Fold(n)).Distinct().Count();

        var visitCounts = await db.PlaceVisits.AsNoTracking().Select(v => new { v.PlaceId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byPlace = visitCounts.GroupBy(v => v.PlaceId).ToDictionary(g => g.Key, g => g.Count());
        var cityVisits = places.Where(p => byPlace.ContainsKey(p.Id))
            .GroupBy(p => p.Type is PlaceType.City or PlaceType.Town or PlaceType.Village ? p.Name : p.City ?? string.Empty)
            .Where(g => g.Key.Length > 0).Select(g => (City: g.Key, Count: g.Sum(p => byPlace[p.Id]))).OrderByDescending(x => x.Count).FirstOrDefault();

        var trips = await db.Trips.AsNoTracking().Select(t => new { t.Status, t.StartDate, t.EndDate }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var completed = trips.Count(t => t.Status == TripStatus.Completed);
        var upcoming = trips.Count(t => t.Status is not (TripStatus.Cancelled or TripStatus.Completed or TripStatus.Idea) && TravelRules.Phase(t.StartDate, t.EndDate, today) == TripPhase.Future);
        // days on the road: completed and current trips, overlapping trips counted once; an active trip counts up to today,
        // a trip the user marked completed counts in full
        var days = new HashSet<int>();
        foreach (var t in trips.Where(t => t.Status is TripStatus.Completed or TripStatus.Active && t.StartDate != null))
        {
            var end = t.EndDate ?? t.StartDate!.Value;
            if (t.Status == TripStatus.Active && end > today) end = today;
            for (var d = t.StartDate!.Value; d <= end && days.Count < 100_000; d = d.AddDays(1)) days.Add(d.DayNumber);
        }

        var photos = await db.TravelPhotos.CountAsync(cancellationToken).ConfigureAwait(false);
        var journal = await db.TravelJournalEntries.CountAsync(cancellationToken).ConfigureAwait(false);
        var bucket = await db.BucketListItems.GroupBy(b => b.IsCompleted).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var favoriteCountry = countries.Where(c => c.IsFavorite).OrderByDescending(c => c.VisitCount).FirstOrDefault()?.Code;
        var mostVisited = countries.Where(c => c.VisitCount > 0).OrderByDescending(c => c.VisitCount).ThenByDescending(c => c.LastVisit).FirstOrDefault()?.Code;

        return new TravelStats(visitedCodes.Count, un, continents, visitedCities, places.Count,
            places.Count(p => p.Status == PlaceStatus.Visited), places.Count(p => p.Status != PlaceStatus.Visited),
            completed, upcoming, trips.Count(t => t.Status != TripStatus.Cancelled), days.Count, photos, journal,
            bucket.Sum(b => b.Count), bucket.FirstOrDefault(b => b.Key)?.Count ?? 0,
            favoriteCountry, mostVisited, cityVisits.City, cityVisits.Count);
    }

    // ───────────────────────── memories ─────────────────────────

    public async Task<IReadOnlyList<MemoryChapter>> GetMemoriesAsync(int? tripId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await Db(cancellationToken).ConfigureAwait(false);
        var photoQuery = db.TravelPhotos.AsNoTracking();
        var journalQuery = db.TravelJournalEntries.AsNoTracking();
        var visitQuery = db.PlaceVisits.AsNoTracking();
        if (tripId is { } t)
        {
            photoQuery = photoQuery.Where(p => p.TripId == t);
            journalQuery = journalQuery.Where(j => j.TripId == t);
            visitQuery = visitQuery.Where(v => v.TripId == t);
        }
        var photos = await photoQuery.Select(p => new
        {
            p.Id, p.TripId, p.PlaceId, Place = p.Place!.Name, p.Document!.Path, p.Document.DisplayName, p.Document.IsMissing, p.Document.FileModifiedUtc,
            p.TakenAt, p.Caption, p.Orientation, p.IsHighlight, p.AddedAtUtc,
        }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var journal = await journalQuery.Select(j => new { j.Id, j.TripId, j.PlaceId, Place = j.Place!.Name, j.Title, j.Text, j.EntryDate, j.IsHighlight })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var visits = await visitQuery.Where(v => v.VisitedOn != null).Select(v => new { v.Id, v.TripId, v.PlaceId, Place = v.Place!.Name, v.VisitedOn, v.Note, v.Place.IsFavorite })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var items = new List<(int? Trip, MemoryItem Item)>();
        foreach (var p in photos.Where(p => !p.IsMissing))
        {
            var when = p.TakenAt ?? p.FileModifiedUtc?.ToLocalTime() ?? p.AddedAtUtc.ToLocalTime();
            items.Add((p.TripId, new MemoryItem(MemoryKind.Photo, p.Id, when, p.Caption ?? Path.GetFileNameWithoutExtension(p.DisplayName), p.Caption, p.Path, p.Orientation, p.PlaceId, p.Place, p.IsHighlight)));
        }
        foreach (var j in journal)
            items.Add((j.TripId, new MemoryItem(MemoryKind.Journal, j.Id, j.EntryDate.ToDateTime(new TimeOnly(20, 0)), j.Title, Excerpt(j.Text), null, 1, j.PlaceId, j.Place, j.IsHighlight)));
        foreach (var v in visits)
            items.Add((v.TripId, new MemoryItem(MemoryKind.Visit, v.Id, v.VisitedOn!.Value.ToDateTime(new TimeOnly(12, 0)), v.Place, v.Note, null, 1, v.PlaceId, v.Place, v.IsFavorite)));

        var tripIds = items.Where(i => i.Trip != null).Select(i => i.Trip!.Value).Distinct().ToList();
        var tripInfo = await db.Trips.AsNoTracking().Where(x => tripIds.Contains(x.Id)).Select(x => new { x.Id, x.Name, x.StartDate, x.EndDate }).ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);
        var tripCountries = (await db.TripPlaces.AsNoTracking().Where(tp => tripIds.Contains(tp.TripId) && tp.Place!.CountryCode != null)
                .Select(tp => new { tp.TripId, Code = tp.Place!.CountryCode! }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(x => x.TripId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Code).Distinct().ToList());
        var placeCountries = await db.TravelPlaces.AsNoTracking().Where(p => p.CountryCode != null).ToDictionaryAsync(p => p.Id, p => p.CountryCode!, cancellationToken).ConfigureAwait(false);

        var chapters = new List<MemoryChapter>();
        foreach (var group in items.GroupBy(i => i.Trip is { } id && tripInfo.ContainsKey(id) ? "trip:" + id : "month:" + i.Item.When.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)))
        {
            var list = group.Select(g => g.Item).OrderBy(i => i.When).ToList();
            if (group.Key.StartsWith("trip:", StringComparison.Ordinal))
            {
                var info = tripInfo[group.First().Trip!.Value];
                chapters.Add(new MemoryChapter(group.Key, info.Id, info.Name, info.StartDate ?? DateOnly.FromDateTime(list[0].When), info.EndDate ?? DateOnly.FromDateTime(list[^1].When),
                    tripCountries.GetValueOrDefault(info.Id) ?? [], list));
            }
            else
            {
                var codes = list.Where(i => i.PlaceId is { } pid && placeCountries.ContainsKey(pid)).Select(i => placeCountries[i.PlaceId!.Value]).Distinct().ToList();
                var first = DateOnly.FromDateTime(list[0].When);
                chapters.Add(new MemoryChapter(group.Key, null, string.Empty, new DateOnly(first.Year, first.Month, 1),
                    new DateOnly(first.Year, first.Month, 1).AddMonths(1).AddDays(-1), codes, list));
            }
        }
        return chapters.OrderByDescending(c => c.To ?? c.From ?? DateOnly.MinValue).ToList();
    }
}
