using Arcadia.Core.Documents;
using Arcadia.Core.Projects;

namespace Arcadia.Core.Travel;

/// <summary>What kind of place it is. Stored by name, so new kinds can be added without a migration.</summary>
public enum PlaceType
{
    Country, City, Town, Village, Mountain, Beach, Lake, NationalPark, Viewpoint, Landmark, Restaurant, Hotel, Museum, Other,
}

/// <summary>
/// Where a place stands for the user. "Favourite" is deliberately not a status: a place can be visited <i>and</i> a
/// favourite, so it is a separate flag (<see cref="TravelPlace.IsFavorite"/>) drawn as a ring around the marker.
/// </summary>
public enum PlaceStatus { WantToVisit, Planned, Visited }

/// <summary>A saved place: a country, city, mountain, café… anything the user wants to remember or reach.</summary>
public sealed class TravelPlace
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>ISO 3166-1 alpha-2 code ("CH"), or Arcadia's "X-…" code for territories without one; null if unknown.</summary>
    public string? CountryCode { get; set; }
    public string? Region { get; set; }
    public string? City { get; set; }
    /// <summary>WGS84 degrees. Both null when the place has no position yet (it is then listed but not drawn).</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public PlaceType Type { get; set; } = PlaceType.Other;
    public PlaceStatus Status { get; set; } = PlaceStatus.WantToVisit;
    public string? Description { get; set; }
    /// <summary>Personal notes (plain text or light Markdown).</summary>
    public string? Notes { get; set; }
    /// <summary>1–5 stars; null when not rated.</summary>
    public int? Rating { get; set; }
    public bool IsFavorite { get; set; }
    /// <summary>Comma separated (see <see cref="TagText"/>).</summary>
    public string Tags { get; set; } = string.Empty;
    public int? CoverPhotoId { get; set; }
    /// <summary>Where the position came from ("manual", "map", "offline", "osm:relation/1685406") — used to avoid duplicates.</summary>
    public string? Source { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public bool IsSample { get; set; }

    public List<PlaceVisit> Visits { get; set; } = [];
    public List<TripPlace> Trips { get; set; } = [];

    public bool HasPosition => Latitude.HasValue && Longitude.HasValue;
    public IReadOnlyList<string> TagList => TagText.Split(Tags);
}

/// <summary>One visit to a place (the visit count and first/last visit are derived from these rows).</summary>
public sealed class PlaceVisit
{
    public int Id { get; set; }
    public int PlaceId { get; set; }
    public TravelPlace? Place { get; set; }
    public int? TripId { get; set; }
    public Trip? Trip { get; set; }
    /// <summary>Calendar date of the visit; null if the user does not remember it.</summary>
    public DateOnly? VisitedOn { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public enum TripStatus { Idea, Planning, Upcoming, Active, Completed, Cancelled }

/// <summary>A journey — past, current or future — with its places, days, notes, photos and documents.</summary>
public sealed class Trip
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Calendar dates (no time zone): a trip "is" on the dates the traveller lives them.</summary>
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public TripStatus Status { get; set; } = TripStatus.Planning;
    public int? CoverPhotoId { get; set; }
    /// <summary>Procedural scene used when there is no cover photo.</summary>
    public string ArtworkTheme { get; set; } = "mountains-dusk";
    public int ArtworkSeed { get; set; }
    public string Accent { get; set; } = "Cyan";
    public int? PrimaryPlaceId { get; set; }
    public TravelPlace? PrimaryPlace { get; set; }
    /// <summary>1–5 stars, for completed trips.</summary>
    public int? Rating { get; set; }
    public string Tags { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    /// <summary>Optional link to an Arcadia project (e.g. "Japan 2027 planning").</summary>
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? LastOpenedAtUtc { get; set; }
    public bool IsSample { get; set; }

    public List<TripPlace> Places { get; set; } = [];
    public List<TripDay> Days { get; set; } = [];
    public List<TripDocument> Documents { get; set; } = [];

    public IReadOnlyList<string> TagList => TagText.Split(Tags);
}

/// <summary>A place that belongs to a trip, in the order the user arranged them.</summary>
public sealed class TripPlace
{
    public int TripId { get; set; }
    public Trip? Trip { get; set; }
    public int PlaceId { get; set; }
    public TravelPlace? Place { get; set; }
    public int SortOrder { get; set; }
    public DateTime AddedAtUtc { get; set; }
}

/// <summary>
/// One day of the itinerary. Its date is derived from the trip start and <see cref="Index"/>, so moving a trip moves
/// its days with it; trips without dates simply show "Day 1, Day 2…".
/// </summary>
public sealed class TripDay
{
    public int Id { get; set; }
    public int TripId { get; set; }
    public Trip? Trip { get; set; }
    /// <summary>0-based position (Day 1 = 0).</summary>
    public int Index { get; set; }
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public List<ItineraryItem> Items { get; set; } = [];
}

/// <summary>When an itinerary item happens: an exact time or just a part of the day.</summary>
public enum TimeSlot { AnyTime, Morning, Afternoon, Evening, Exact }

public enum ItineraryCategory { Activity, Sight, Transport, Accommodation, Food, Other }

public sealed class ItineraryItem
{
    public int Id { get; set; }
    public int DayId { get; set; }
    public TripDay? Day { get; set; }
    public int SortOrder { get; set; }
    public TimeSlot Slot { get; set; } = TimeSlot.AnyTime;
    /// <summary>Local wall-clock time; only meaningful with <see cref="TimeSlot.Exact"/>.</summary>
    public TimeOnly? Time { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? PlaceId { get; set; }
    public TravelPlace? Place { get; set; }
    public ItineraryCategory Category { get; set; } = ItineraryCategory.Activity;
    public string? Notes { get; set; }
    public bool IsDone { get; set; }
    /// <summary>Optional http(s) link (ticket page, trail description…). Stored only, never fetched.</summary>
    public string? Url { get; set; }
}

/// <summary>A Phase 4 library document (ticket, booking, insurance…) associated with a trip. The file is only referenced.</summary>
public sealed class TripDocument
{
    public int TripId { get; set; }
    public Trip? Trip { get; set; }
    public int DocumentId { get; set; }
    public Document? Document { get; set; }
    public bool IsImportant { get; set; }
    public DateTime AddedAtUtc { get; set; }
}

public enum JournalMood { None, Joyful, Calm, Excited, Tired, Reflective, Adventurous }

/// <summary>A travel journal entry; may belong to a trip, a place, both or neither.</summary>
public sealed class TravelJournalEntry
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateOnly EntryDate { get; set; }
    /// <summary>Plain text or light Markdown, stored exactly as typed.</summary>
    public string Text { get; set; } = string.Empty;
    public int? TripId { get; set; }
    public Trip? Trip { get; set; }
    public int? PlaceId { get; set; }
    public TravelPlace? Place { get; set; }
    public JournalMood Mood { get; set; }
    public string Tags { get; set; } = string.Empty;
    /// <summary>Shown prominently in Memories.</summary>
    public bool IsHighlight { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public bool IsSample { get; set; }
}

/// <summary>
/// A travel photo: a library <see cref="Document"/> (the file is referenced, never copied) plus what Travel knows
/// about it — EXIF data read locally, and the trip/place/journal entry it belongs to.
/// </summary>
public sealed class TravelPhoto
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public Document? Document { get; set; }
    public int? TripId { get; set; }
    public Trip? Trip { get; set; }
    public int? PlaceId { get; set; }
    public TravelPlace? Place { get; set; }
    public int? JournalEntryId { get; set; }
    public TravelJournalEntry? JournalEntry { get; set; }
    /// <summary>Camera wall-clock time from EXIF (no time zone information exists in most photos).</summary>
    public DateTime? TakenAt { get; set; }
    /// <summary>GPS position found in the photo (kept locally, never sent anywhere).</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Camera { get; set; }
    /// <summary>EXIF orientation 1–8 (1 = upright). Applied when displaying; the file is never modified.</summary>
    public int Orientation { get; set; } = 1;
    public int? PixelWidth { get; set; }
    public int? PixelHeight { get; set; }
    public string? Caption { get; set; }
    public bool IsHighlight { get; set; }
    public DateTime AddedAtUtc { get; set; }

    public bool HasGps => Latitude.HasValue && Longitude.HasValue;
}

public enum BucketKind { Experience, Place, Country }

public enum BucketPriority { Someday, Normal, High, Dream }

public sealed class BucketListItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public BucketKind Kind { get; set; } = BucketKind.Experience;
    public string? CountryCode { get; set; }
    public int? PlaceId { get; set; }
    public TravelPlace? Place { get; set; }
    public int? TripId { get; set; }
    public Trip? Trip { get; set; }
    public BucketPriority Priority { get; set; } = BucketPriority.Normal;
    public int? TargetYear { get; set; }
    public string? Notes { get; set; }
    public string Tags { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateOnly? CompletedOn { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsSample { get; set; }
}

/// <summary>
/// What the user said about a country directly (favourite, "I want to go", "I have been there" without saving a
/// place). Whether a country counts as visited is derived: marked visited, or at least one visited place in it.
/// </summary>
public sealed class TravelCountry
{
    /// <summary>ISO alpha-2 (or Arcadia "X-…" code).</summary>
    public string Code { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public bool WantToVisit { get; set; }
    public bool MarkedVisited { get; set; }
    public string? Notes { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
