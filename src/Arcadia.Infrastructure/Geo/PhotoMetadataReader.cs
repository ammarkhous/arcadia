using System.Globalization;
using Arcadia.Core.Travel;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Microsoft.Extensions.Logging;

namespace Arcadia.Infrastructure.Geo;

/// <summary>
/// Reads date taken, camera, orientation, size and GPS position from a photo, entirely locally (MetadataExtractor,
/// Apache-2.0). The file is opened read-only and never modified; nothing is uploaded.
/// </summary>
public sealed class PhotoMetadataReader(ILogger<PhotoMetadataReader> logger) : IPhotoMetadataReader
{
    public PhotoMetadata Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var dirs = ImageMetadataReader.ReadMetadata(stream);
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();

            DateTime? taken = null;
            if (sub != null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt)) taken = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            else if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt0)) taken = DateTime.SpecifyKind(dt0, DateTimeKind.Unspecified);

            var make = ifd0?.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
            var model = ifd0?.GetDescription(ExifDirectoryBase.TagModel)?.Trim();
            string? camera = model == null ? make : make != null && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? $"{make} {model}" : model;

            int orientation = 1;
            if (ifd0 != null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) && o is >= 1 and <= 8) orientation = o;

            int? width = null, height = null;
            foreach (var d in dirs)
            {
                foreach (var tag in d.Tags)
                {
                    if (width == null && tag.Name is "Image Width" or "Exif Image Width" && int.TryParse(Digits(tag.Description), out var w)) width = w;
                    if (height == null && tag.Name is "Image Height" or "Exif Image Height" && int.TryParse(Digits(tag.Description), out var h)) height = h;
                }
            }

            double? lat = null, lon = null;
            var location = gps?.GetGeoLocation();
            if (location != null && GeoMath.IsValid(location.Value.Latitude, location.Value.Longitude) && !(location.Value.Latitude == 0 && location.Value.Longitude == 0))
            {
                lat = location.Value.Latitude;
                lon = location.Value.Longitude;
            }
            return new PhotoMetadata(taken, lat, lon, string.IsNullOrWhiteSpace(camera) ? null : camera, orientation, width, height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageProcessingException or NotSupportedException or ArgumentException or FormatException)
        {
            logger.LogDebug("No metadata for {File}: {Message}", Path.GetFileName(path), ex.Message);
            return PhotoMetadata.Empty;
        }
    }

    private static string Digits(string? s) => new((s ?? string.Empty).TakeWhile(char.IsDigit).ToArray());
}
