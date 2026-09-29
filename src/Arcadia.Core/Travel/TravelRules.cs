namespace Arcadia.Core.Travel;

/// <summary>Where a trip is in time, from its dates (independent of the status the user chose).</summary>
public enum TripPhase { Undated, Future, Current, Past }

/// <summary>Date and consistency rules for trips, itineraries, visits and ratings (pure, testable).</summary>
public static class TravelRules
{
    public const int MaxTripDays = 366;

    /// <summary>Inclusive length in days ("3 July – 5 July" = 3 days); null without both dates or if they are reversed.</summary>
    public static int? DurationDays(DateOnly? start, DateOnly? end) =>
        start is { } s && end is { } e && e >= s ? e.DayNumber - s.DayNumber + 1 : null;

    public static bool DatesAreValid(DateOnly? start, DateOnly? end) =>
        start is not { } s || end is not { } e || (e >= s && e.DayNumber - s.DayNumber < MaxTripDays * 3);

    public static TripPhase Phase(DateOnly? start, DateOnly? end, DateOnly today)
    {
        if (start is null && end is null) return TripPhase.Undated;
        var s = start ?? end!.Value;
        var e = end ?? start!.Value; // open-ended trips count as one day until an end is set
        if (today < s) return TripPhase.Future;
        return today > e ? TripPhase.Past : TripPhase.Current;
    }

    /// <summary>Days until the trip starts (0 = today); null if it has no start or already started.</summary>
    public static int? DaysUntil(DateOnly? start, DateOnly today) =>
        start is { } s && s >= today ? s.DayNumber - today.DayNumber : null;

    /// <summary>For a trip underway: which day it is (1-based) and the fraction completed.</summary>
    public static (int Day, int Of, double Fraction)? Progress(DateOnly? start, DateOnly? end, DateOnly today)
    {
        if (DurationDays(start, end) is not { } total || today < start!.Value || today > end!.Value) return null;
        var day = today.DayNumber - start.Value.DayNumber + 1;
        return (day, total, (double)day / total);
    }

    /// <summary>The date of itinerary day <paramref name="index"/> (0-based), when the trip has a start date.</summary>
    public static DateOnly? DayDate(DateOnly? start, int index) => start?.AddDays(index);

    /// <summary>
    /// Suggests a status from the dates, for trips whose status still follows the calendar (Upcoming → Active →
    /// Completed). Ideas, cancelled trips and trips without dates keep the status the user chose.
    /// </summary>
    public static TripStatus SuggestStatus(TripStatus current, DateOnly? start, DateOnly? end, DateOnly today)
    {
        if (current is TripStatus.Idea or TripStatus.Cancelled or TripStatus.Completed) return current;
        return Phase(start, end, today) switch
        {
            TripPhase.Future when current == TripStatus.Active => TripStatus.Upcoming,
            TripPhase.Current when current == TripStatus.Upcoming => TripStatus.Active,
            _ => current,
        };
    }

    public static bool IsValidRating(int? rating) => rating is null or (>= 1 and <= 5);

    /// <summary>Visit count, first and last visit (dated visits only for first/last).</summary>
    public static (int Count, DateOnly? First, DateOnly? Last) Summarize(IEnumerable<PlaceVisit> visits)
    {
        int count = 0;
        DateOnly? first = null, last = null;
        foreach (var v in visits)
        {
            count++;
            if (v.VisitedOn is not { } d) continue;
            if (first is null || d < first) first = d;
            if (last is null || d > last) last = d;
        }
        return (count, first, last);
    }

    /// <summary>Minutes used to order itinerary items when the user asks to sort a day by time.</summary>
    public static int SortMinutes(TimeSlot slot, TimeOnly? time) => slot switch
    {
        TimeSlot.Exact when time is { } t => t.Hour * 60 + t.Minute,
        TimeSlot.Morning => 8 * 60,
        TimeSlot.Afternoon => 14 * 60,
        TimeSlot.Evening => 19 * 60,
        _ => 24 * 60, // "any time" goes last
    };

    /// <summary>The part of day an exact time belongs to (used to label and group items).</summary>
    public static TimeSlot SlotOf(TimeOnly time) => time.Hour switch
    {
        < 12 => TimeSlot.Morning,
        < 17 => TimeSlot.Afternoon,
        _ => TimeSlot.Evening,
    };

    /// <summary>A reasonable default icon/category for a place type.</summary>
    public static ItineraryCategory CategoryFor(PlaceType type) => type switch
    {
        PlaceType.Hotel => ItineraryCategory.Accommodation,
        PlaceType.Restaurant => ItineraryCategory.Food,
        PlaceType.Museum or PlaceType.Landmark or PlaceType.Viewpoint => ItineraryCategory.Sight,
        _ => ItineraryCategory.Activity,
    };

    /// <summary>How far from a saved place a GPS-tagged photo may be to belong to it: a city covers its
    /// outskirts, a restaurant only its street. Countries and regions are never matched this way.</summary>
    public static double PhotoMatchRadiusKm(PlaceType type) => type switch
    {
        PlaceType.Country => 0,
        PlaceType.City => 15,
        PlaceType.Town => 6,
        PlaceType.Village or PlaceType.NationalPark or PlaceType.Mountain or PlaceType.Lake => 4,
        PlaceType.Restaurant or PlaceType.Hotel or PlaceType.Museum => 0.4,
        _ => 1.5,
    };

    /// <summary>The saved place a photo taken at (lat, lon) belongs to, or null. Distances are compared relative to each
    /// place's radius, so a temple 300 m away wins over the centre of the city it is in.</summary>
    public static T? NearestForPhoto<T>(double lat, double lon, IEnumerable<T> places, Func<T, (double Lat, double Lon, PlaceType Type)?> position) where T : class
    {
        T? best = null;
        var bestScore = double.MaxValue;
        foreach (var place in places)
        {
            if (position(place) is not { } p) continue;
            var radius = PhotoMatchRadiusKm(p.Type);
            if (radius <= 0) continue;
            var score = GeoMath.DistanceKm(lat, lon, p.Lat, p.Lon) / radius;
            if (score <= 1 && score < bestScore) { best = place; bestScore = score; }
        }
        return best;
    }
}
