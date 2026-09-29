using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Text;
using Arcadia.Core.Travel;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data;

/// <summary>
/// SQLite persistence for Travel: places and visits, trips with their places, days, itinerary and documents, the
/// journal, photos (library documents plus EXIF data), the bucket list and per-country notes. Every change keeps the
/// global search index in step, inside the same transaction.
/// </summary>
public sealed partial class TravelStore : ITravelStore
{
    private static readonly string[] Themes =
        ["mountains-dusk", "lake-sunset", "desert-sunset", "mountains-snow", "forest-night", "golden-realm", "lake-storm", "castle-violet"];

    private readonly IDbContextFactory<ArcadiaDbContext> _factory;
    private readonly TimeProvider _time;
    private readonly ICountryCatalog? _countries;

    public TravelStore(IDbContextFactory<ArcadiaDbContext> factory, TimeProvider time, ICountryCatalog? countries = null)
    {
        _factory = factory;
        _time = time;
        _countries = countries;
        if (countries != null) CountryNames = code => countries.Find(code) is { } c ? $"{c.NameEn} {c.NameDe} {c.NameAr}" : null;
    }

    /// <summary>Country names for the search index ("Schweiz", "سويسرا" find places in CH). Set by the first store created.</summary>
    internal static Func<string?, string?> CountryNames { get; private set; } = _ => null;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private Task<ArcadiaDbContext> Db(CancellationToken ct) => _factory.CreateDbContextAsync(ct);

    // ───────────────────────── validation helpers ─────────────────────────

    private static string RequireName(string? name, int max)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A name is required.");
        return ProjectStore.Clip(name, max);
    }

    private static string? Text(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    private static string? Line(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : ProjectStore.Clip(value, max);

    private static (double?, double?) Position(double? lat, double? lon)
    {
        if (lat is null && lon is null) return (null, null);
        if (lat is not { } a || lon is not { } o) throw new ArgumentException("Latitude and longitude must be given together.");
        if (!GeoMath.IsValid(a, o)) throw new ArgumentException("Latitude must be between -90 and 90 and longitude between -180 and 180.");
        return (Math.Round(a, 7), Math.Round(o, 7));
    }

    private static int? Rating(int? rating) =>
        TravelRules.IsValidRating(rating) ? rating : throw new ArgumentException("A rating is 1 to 5 stars.");

    private static string? CountryCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant() is { Length: <= 8 } c ? c : throw new ArgumentException("Invalid country code.");

    private static string? Url(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return LinkRules.TryNormalize(url, out var normalized) ? normalized : throw new ArgumentException("Enter a complete web address (https://…).");
    }

    // ───────────────────────── search index ─────────────────────────

    private static Task IndexPlaceAsync(ArcadiaDbContext db, TravelPlace p, CancellationToken ct) =>
        SearchIndex.IndexPlaceAsync(db, p, string.Join(' ', CountryNames(p.CountryCode), p.Type.ToString()), ct);

    private static async Task IndexTripAsync(ArcadiaDbContext db, int tripId, CancellationToken ct)
    {
        var trip = await db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tripId, ct).ConfigureAwait(false);
        if (trip == null) return;
        var places = await db.TripPlaces.Where(tp => tp.TripId == tripId).Select(tp => new { tp.Place!.Name, tp.Place.CountryCode, tp.Place.City }).ToListAsync(ct).ConfigureAwait(false);
        var extra = string.Join(' ', places.Select(p => p.Name).Concat(places.Select(p => p.City ?? string.Empty))
            .Concat(places.Select(p => p.CountryCode).Distinct().Select(CountryNames).Where(n => n != null)!));
        await SearchIndex.IndexTripAsync(db, trip, extra, ct).ConfigureAwait(false);
    }

    private static async Task IndexJournalAsync(ArcadiaDbContext db, TravelJournalEntry j, CancellationToken ct)
    {
        var trip = j.TripId is { } t ? await db.Trips.Where(x => x.Id == t).Select(x => x.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false) : null;
        var place = j.PlaceId is { } p ? await db.TravelPlaces.Where(x => x.Id == p).Select(x => x.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false) : null;
        await SearchIndex.IndexJournalAsync(db, j, string.Join(' ', trip, place), ct).ConfigureAwait(false);
    }

    /// <summary>Re-adds every trip, place and journal entry (used by the full index rebuild).</summary>
    internal static async Task ReindexAllAsync(ArcadiaDbContext db, CancellationToken ct)
    {
        foreach (var p in await db.TravelPlaces.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)) await IndexPlaceAsync(db, p, ct).ConfigureAwait(false);
        foreach (var id in await db.Trips.Select(t => t.Id).ToListAsync(ct).ConfigureAwait(false)) await IndexTripAsync(db, id, ct).ConfigureAwait(false);
        foreach (var j in await db.TravelJournalEntries.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)) await IndexJournalAsync(db, j, ct).ConfigureAwait(false);
    }

    /// <summary>Trips mention their places, so renaming/moving a place refreshes the trips that contain it.</summary>
    private static async Task ReindexTripsOfPlaceAsync(ArcadiaDbContext db, int placeId, CancellationToken ct)
    {
        foreach (var tripId in await db.TripPlaces.Where(tp => tp.PlaceId == placeId).Select(tp => tp.TripId).ToListAsync(ct).ConfigureAwait(false))
            await IndexTripAsync(db, tripId, ct).ConfigureAwait(false);
    }

    private static bool Matches(string? text, string folded) => text != null && TextFold.Fold(text).Contains(folded, StringComparison.Ordinal);
}
