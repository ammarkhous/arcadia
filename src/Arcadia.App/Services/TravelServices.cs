using System.IO;
using Arcadia.Core.Documents;
using Arcadia.Core.Media;
using Arcadia.Core.Travel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>Travel data changed (trips, places, journal, photos…). Pages reload lazily when they next show.</summary>
public sealed record TravelChangedMessage(int? TripId = null);

public sealed record PhotoAddResult(int Added, int Skipped, IReadOnlyList<int> PhotoIds, int WithGps);

/// <summary>
/// Adds travel photos. Files are referenced through the Documents library (never copied or moved); EXIF metadata is
/// read locally on a background thread; GPS positions stay on this computer.
/// </summary>
public sealed class TravelPhotoService(IDocumentStore documents, ITravelStore travel, IFileFactsService files, IPhotoMetadataReader metadata,
    IMessenger messenger, ILogger<TravelPhotoService> logger)
{
    public static bool IsImage(string path) =>
        IMediaStore.SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) || Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase);

    public async Task<PhotoAddResult> AddAsync(IReadOnlyList<string> paths, PhotoLink link, CancellationToken ct = default)
    {
        var images = paths.Where(IsImage).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var skipped = paths.Count - images.Count;
        if (images.Count == 0) return new PhotoAddResult(0, skipped, [], 0);

        var facts = new List<FileFacts>();
        foreach (var p in images) facts.Add(await files.GetFactsAsync(p, readContent: false, ct).ConfigureAwait(false));
        var present = facts.Where(f => f.Exists && !f.IsFolder).ToList();
        skipped += facts.Count - present.Count;
        var result = await documents.AddAsync(present, null, null, ct).ConfigureAwait(false);
        var pathById = (await documents.GetPathsAsync(result.DocumentIds, ct).ConfigureAwait(false)).ToList();
        var metas = await Task.Run(() => pathById.Select(d => (d.Id, Meta: metadata.Read(d.Path))).ToList(), ct).ConfigureAwait(false);
        var ids = await travel.AttachPhotosAsync(metas.Select(m => (m.Id, m.Meta)).ToList(), link, ct).ConfigureAwait(false);
        logger.LogInformation("Added {Count} travel photos ({Gps} with GPS)", ids.Count, metas.Count(m => m.Meta.Latitude != null));
        messenger.Send(new DocumentsChangedMessage());
        messenger.Send(new TravelChangedMessage(link.TripId));
        return new PhotoAddResult(ids.Count, skipped, ids, metas.Count(m => m.Meta.Latitude != null));
    }

    /// <summary>
    /// Links photos that carry GPS positions to the nearest saved place within <paramref name="maxKm"/> (all local).
    /// Returns how many were linked.
    /// </summary>
    public async Task<int> MatchToPlacesAsync(IReadOnlyList<PhotoRow> photos, IReadOnlyList<PlaceSummary> places, CancellationToken ct = default)
    {
        var candidates = places.Where(p => p.Latitude != null && p.Longitude != null).ToList();
        var byPlace = new Dictionary<int, List<int>>();
        foreach (var photo in photos.Where(p => p.PlaceId == null && p.Latitude != null && p.Longitude != null))
        {
            var best = TravelRules.NearestForPhoto(photo.Latitude!.Value, photo.Longitude!.Value, candidates, p => (p.Latitude!.Value, p.Longitude!.Value, p.Type));
            if (best == null) continue;
            if (!byPlace.TryGetValue(best.Id, out var list)) byPlace[best.Id] = list = [];
            list.Add(photo.Id);
        }
        foreach (var (placeId, ids) in byPlace) await travel.SetPhotosPlaceAsync(ids, placeId, ct).ConfigureAwait(false);
        if (byPlace.Count > 0) messenger.Send(new TravelChangedMessage());
        return byPlace.Values.Sum(l => l.Count);
    }
}
