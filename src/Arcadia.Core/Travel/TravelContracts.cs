using Arcadia.Core.Documents;

namespace Arcadia.Core.Travel;

// ───────────── drafts (what editors hand to the store) ─────────────

public sealed record PlaceDraft(
    string Name,
    PlaceType Type,
    PlaceStatus Status,
    double? Latitude,
    double? Longitude,
    string? CountryCode = null,
    string? Region = null,
    string? City = null,
    string? Description = null,
    string? Notes = null,
    int? Rating = null,
    bool IsFavorite = false,
    string? Tags = null,
    string? Source = null);

public sealed record TripDraft(
    string Name,
    TripStatus Status = TripStatus.Planning,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    string? Description = null,
    string? Tags = null,
    string Accent = "Cyan",
    string ArtworkTheme = "mountains-dusk",
    int? PrimaryPlaceId = null,
    int? ProjectId = null,
    int? Rating = null);

public sealed record ItineraryItemDraft(
    string Title,
    TimeSlot Slot = TimeSlot.AnyTime,
    TimeOnly? Time = null,
    ItineraryCategory Category = ItineraryCategory.Activity,
    int? PlaceId = null,
    string? Description = null,
    string? Notes = null,
    string? Url = null);

public sealed record JournalDraft(
    string Title,
    DateOnly EntryDate,
    string Text,
    int? TripId = null,
    int? PlaceId = null,
    JournalMood Mood = JournalMood.None,
    string? Tags = null,
    bool IsHighlight = false);

public sealed record BucketDraft(
    string Title,
    BucketKind Kind = BucketKind.Experience,
    BucketPriority Priority = BucketPriority.Normal,
    int? TargetYear = null,
    string? CountryCode = null,
    int? PlaceId = null,
    int? TripId = null,
    string? Notes = null,
    string? Tags = null);

// ───────────── read models ─────────────

public sealed record PlaceSummary(
    int Id,
    string Name,
    PlaceType Type,
    PlaceStatus Status,
    bool IsFavorite,
    double? Latitude,
    double? Longitude,
    string? CountryCode,
    string? Region,
    string? City,
    int? Rating,
    string Tags,
    int VisitCount,
    DateOnly? FirstVisit,
    DateOnly? LastVisit,
    int TripCount,
    int PhotoCount,
    string? CoverPath,
    DateTime CreatedAtUtc);

public sealed record PlaceQuery(
    PlaceStatus? Status = null,
    PlaceType? Type = null,
    string? CountryCode = null,
    bool FavoritesOnly = false,
    int? TripId = null,
    string? Text = null);

public sealed record TripSummary(
    int Id,
    string Name,
    string? Description,
    DateOnly? StartDate,
    DateOnly? EndDate,
    TripStatus Status,
    string Accent,
    string ArtworkTheme,
    int ArtworkSeed,
    string? CoverPath,
    int PlaceCount,
    int DayCount,
    int ItemCount,
    int ItemsDone,
    int PhotoCount,
    int DocumentCount,
    int JournalCount,
    IReadOnlyList<string> CountryCodes,
    IReadOnlyList<string> Cities,
    string? PrimaryPlaceName,
    bool IsFavorite,
    int? Rating,
    int? ProjectId,
    string Tags,
    DateTime UpdatedAtUtc,
    DateTime? LastOpenedAtUtc);

public sealed record JournalSummary(
    int Id,
    string Title,
    DateOnly EntryDate,
    string Excerpt,
    int? TripId,
    string? TripName,
    int? PlaceId,
    string? PlaceName,
    JournalMood Mood,
    string Tags,
    bool IsHighlight,
    int PhotoCount,
    DateTime UpdatedAtUtc);

public sealed record JournalQuery(int? TripId = null, int? PlaceId = null, string? Text = null);

/// <summary>A photo row: the referenced file plus its Travel metadata and links.</summary>
public sealed record PhotoRow(
    int Id,
    int DocumentId,
    string Path,
    string FileName,
    bool IsMissing,
    long? SizeBytes,
    DateTime? FileModifiedUtc,
    DateTime? TakenAt,
    double? Latitude,
    double? Longitude,
    string? Camera,
    int Orientation,
    int? PixelWidth,
    int? PixelHeight,
    int? TripId,
    string? TripName,
    int? PlaceId,
    string? PlaceName,
    int? JournalEntryId,
    string? Caption,
    bool IsHighlight,
    DateTime AddedAtUtc)
{
    /// <summary>The best date for ordering: when it was taken, else the file date, else when it was added.</summary>
    public DateTime SortDate => TakenAt ?? FileModifiedUtc?.ToLocalTime() ?? AddedAtUtc.ToLocalTime();
}

public sealed record PhotoQuery(int? TripId = null, int? PlaceId = null, int? JournalEntryId = null, bool UnassignedWithGpsOnly = false, int Take = 2000);

/// <summary>Where newly added photos should belong.</summary>
public sealed record PhotoLink(int? TripId = null, int? PlaceId = null, int? JournalEntryId = null);

public sealed record BucketRow(
    int Id,
    string Title,
    BucketKind Kind,
    BucketPriority Priority,
    int? TargetYear,
    string? CountryCode,
    int? PlaceId,
    string? PlaceName,
    int? TripId,
    string? TripName,
    string? Notes,
    string Tags,
    bool IsCompleted,
    DateOnly? CompletedOn,
    int SortOrder);

/// <summary>Everything Arcadia knows about the user's relationship with one country.</summary>
public sealed record CountryProgress(
    string Code,
    bool Visited,
    bool MarkedVisited,
    bool WantToVisit,
    bool IsFavorite,
    int PlaceCount,
    int VisitedPlaceCount,
    int VisitCount,
    DateOnly? LastVisit,
    int TripCount,
    string? Notes);

public sealed record TravelStats(
    int CountriesVisited,
    int UnCountriesVisited,
    int ContinentsVisited,
    int CitiesVisited,
    int PlacesSaved,
    int PlacesVisited,
    int PlacesWanted,
    int TripsCompleted,
    int TripsUpcoming,
    int TripsTotal,
    int DaysTraveling,
    int Photos,
    int JournalEntries,
    int BucketTotal,
    int BucketCompleted,
    /// <summary>A country the user marked as favourite (null if none).</summary>
    string? FavoriteCountryCode,
    /// <summary>The country with the most recorded visits.</summary>
    string? MostVisitedCountryCode,
    string? MostVisitedCity,
    int MostVisitedCityCount);

public enum MemoryKind { Photo, Journal, Visit }

/// <summary>One moment in the Memories story.</summary>
public sealed record MemoryItem(
    MemoryKind Kind,
    int Id,
    DateTime When,
    string Title,
    string? Text,
    string? PhotoPath,
    int Orientation,
    int? PlaceId,
    string? PlaceName,
    bool IsHighlight);

/// <summary>A chapter of memories: a trip, or a month of moments outside trips.</summary>
public sealed record MemoryChapter(
    string Key,
    int? TripId,
    string Title,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<string> CountryCodes,
    IReadOnlyList<MemoryItem> Items);

public sealed record TripDocumentRow(DocumentRow Document, bool IsImportant);

// ───────────── store ─────────────

/// <summary>Persistence for Travel. Every change also updates the global search index.</summary>
public interface ITravelStore
{
    // places
    Task<IReadOnlyList<PlaceSummary>> ListPlacesAsync(PlaceQuery query, CancellationToken cancellationToken = default);
    Task<TravelPlace?> GetPlaceAsync(int id, CancellationToken cancellationToken = default);
    Task<TravelPlace?> FindPlaceBySourceAsync(string source, CancellationToken cancellationToken = default);
    Task<TravelPlace> CreatePlaceAsync(PlaceDraft draft, CancellationToken cancellationToken = default);
    Task UpdatePlaceAsync(int id, PlaceDraft draft, CancellationToken cancellationToken = default);
    Task DeletePlaceAsync(int id, CancellationToken cancellationToken = default);
    Task SetPlaceStatusAsync(int id, PlaceStatus status, CancellationToken cancellationToken = default);
    Task SetPlaceFavoriteAsync(int id, bool favorite, CancellationToken cancellationToken = default);
    Task SetPlaceNotesAsync(int id, string? notes, CancellationToken cancellationToken = default);
    /// <summary>Records a visit and marks the place Visited.</summary>
    Task<PlaceVisit> AddVisitAsync(int placeId, DateOnly? visitedOn, int? tripId, string? note, CancellationToken cancellationToken = default);
    Task DeleteVisitAsync(int visitId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(int TripId, string Name, DateOnly? Start)>> GetPlaceTripsAsync(int placeId, CancellationToken cancellationToken = default);

    // trips
    Task<IReadOnlyList<TripSummary>> ListTripsAsync(CancellationToken cancellationToken = default);
    Task<TripSummary?> GetTripSummaryAsync(int id, CancellationToken cancellationToken = default);
    /// <summary>Trip with places (ordered), days and itinerary items.</summary>
    Task<Trip?> GetTripAsync(int id, CancellationToken cancellationToken = default);
    Task<Trip> CreateTripAsync(TripDraft draft, IReadOnlyList<int>? placeIds = null, CancellationToken cancellationToken = default);
    Task UpdateTripAsync(int id, TripDraft draft, CancellationToken cancellationToken = default);
    Task SetTripStatusAsync(int id, TripStatus status, CancellationToken cancellationToken = default);
    Task SetTripFavoriteAsync(int id, bool favorite, CancellationToken cancellationToken = default);
    Task MarkTripOpenedAsync(int id, CancellationToken cancellationToken = default);
    /// <summary>Deletes the trip, its days and itinerary. Places, journal entries, photos and documents are kept (unlinked).</summary>
    Task DeleteTripAsync(int id, CancellationToken cancellationToken = default);
    Task AddPlaceToTripAsync(int tripId, int placeId, CancellationToken cancellationToken = default);
    Task RemovePlaceFromTripAsync(int tripId, int placeId, CancellationToken cancellationToken = default);
    Task ReorderTripPlacesAsync(int tripId, IReadOnlyList<int> placeIds, CancellationToken cancellationToken = default);

    // itinerary
    Task<TripDay> AddDayAsync(int tripId, string? title, CancellationToken cancellationToken = default);
    /// <summary>Adds days until there is one per calendar day of the trip (never removes days).</summary>
    Task<int> EnsureDaysForDatesAsync(int tripId, CancellationToken cancellationToken = default);
    Task UpdateDayAsync(int dayId, string? title, string? notes, CancellationToken cancellationToken = default);
    /// <summary>Deletes a day and its items; later days move up.</summary>
    Task DeleteDayAsync(int dayId, CancellationToken cancellationToken = default);
    Task<ItineraryItem> AddItemAsync(int dayId, ItineraryItemDraft draft, CancellationToken cancellationToken = default);
    Task UpdateItemAsync(int itemId, ItineraryItemDraft draft, CancellationToken cancellationToken = default);
    Task DeleteItemAsync(int itemId, CancellationToken cancellationToken = default);
    Task SetItemDoneAsync(int itemId, bool done, CancellationToken cancellationToken = default);
    /// <summary>Moves an item to a position in a day (the same or another day of the same trip).</summary>
    Task MoveItemAsync(int itemId, int targetDayId, int targetIndex, CancellationToken cancellationToken = default);
    Task SortDayByTimeAsync(int dayId, CancellationToken cancellationToken = default);

    // trip documents (Phase 4 library)
    Task<IReadOnlyList<TripDocumentRow>> ListTripDocumentsAsync(int tripId, CancellationToken cancellationToken = default);
    Task AssociateDocumentAsync(int tripId, int documentId, CancellationToken cancellationToken = default);
    Task DissociateDocumentAsync(int tripId, int documentId, CancellationToken cancellationToken = default);
    Task SetDocumentImportantAsync(int tripId, int documentId, bool important, CancellationToken cancellationToken = default);

    // journal
    Task<IReadOnlyList<JournalSummary>> ListJournalAsync(JournalQuery query, CancellationToken cancellationToken = default);
    Task<TravelJournalEntry?> GetJournalAsync(int id, CancellationToken cancellationToken = default);
    Task<TravelJournalEntry> CreateJournalAsync(JournalDraft draft, CancellationToken cancellationToken = default);
    Task UpdateJournalAsync(int id, JournalDraft draft, CancellationToken cancellationToken = default);
    Task DeleteJournalAsync(int id, CancellationToken cancellationToken = default);

    // photos
    /// <summary>Creates or updates Travel photo records for library documents (one record per file).</summary>
    Task<IReadOnlyList<int>> AttachPhotosAsync(IReadOnlyList<(int DocumentId, PhotoMetadata Metadata)> photos, PhotoLink link, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PhotoRow>> ListPhotosAsync(PhotoQuery query, CancellationToken cancellationToken = default);
    Task<PhotoRow?> GetPhotoAsync(int id, CancellationToken cancellationToken = default);
    Task UpdatePhotoAsync(int id, string? caption, bool isHighlight, CancellationToken cancellationToken = default);
    Task SetPhotosTripAsync(IReadOnlyCollection<int> photoIds, int? tripId, CancellationToken cancellationToken = default);
    Task SetPhotosPlaceAsync(IReadOnlyCollection<int> photoIds, int? placeId, CancellationToken cancellationToken = default);
    Task SetPhotosJournalAsync(IReadOnlyCollection<int> photoIds, int? journalEntryId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Removes photos from Travel. The library reference is removed too when nothing else uses it
    /// (no project, trip, favourite or tags). The image files are never touched.
    /// </summary>
    Task RemovePhotosAsync(IReadOnlyCollection<int> photoIds, CancellationToken cancellationToken = default);
    Task SetTripCoverAsync(int tripId, int? photoId, CancellationToken cancellationToken = default);
    Task SetPlaceCoverAsync(int placeId, int? photoId, CancellationToken cancellationToken = default);

    // bucket list
    Task<IReadOnlyList<BucketRow>> ListBucketAsync(CancellationToken cancellationToken = default);
    Task<BucketListItem> CreateBucketAsync(BucketDraft draft, CancellationToken cancellationToken = default);
    Task UpdateBucketAsync(int id, BucketDraft draft, CancellationToken cancellationToken = default);
    Task SetBucketCompletedAsync(int id, bool completed, DateOnly? on, CancellationToken cancellationToken = default);
    Task DeleteBucketAsync(int id, CancellationToken cancellationToken = default);
    Task ReorderBucketAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken = default);

    // countries, statistics, timeline, memories
    Task<IReadOnlyList<CountryProgress>> GetCountryProgressAsync(CancellationToken cancellationToken = default);
    Task SetCountryAsync(string code, bool? favorite = null, bool? wantToVisit = null, bool? markedVisited = null, string? notes = null, CancellationToken cancellationToken = default);
    Task<TravelStats> GetStatsAsync(DateOnly today, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryChapter>> GetMemoriesAsync(int? tripId = null, CancellationToken cancellationToken = default);
}
