using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arcadia.Core.Media;
using Arcadia.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>
/// Copies imported images into %LocalAppData%\Arcadia\Media and creates JPEG thumbnails.
/// Stored paths are relative to the media root so a relocated data folder keeps working.
/// All decoding/encoding runs on a worker thread (frozen WPF bitmaps are thread-safe).
/// </summary>
public sealed class MediaStore(IAppPaths paths, ILogger<MediaStore> logger) : IMediaStore
{
    private const int ThumbnailWidth = 480;
    private const int MaxCoverWidth = 2560;

    public string Root { get; } = Path.Combine(paths.DataDirectory, "Media");

    public Task<ImportedMedia> ImportAsync(string sourcePath, MediaKind kind, CancellationToken cancellationToken = default) =>
        Task.Run(() => Import(sourcePath, kind, cancellationToken), cancellationToken);

    private ImportedMedia Import(string sourcePath, MediaKind kind, CancellationToken ct)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!IMediaStore.SupportedExtensions.Contains(ext)) throw new NotSupportedException($"'{ext}' is not a supported image type.");
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Image not found.", sourcePath);

        var folder = kind switch
        {
            MediaKind.Cover => "covers",
            MediaKind.Hero => "heroes",
            MediaKind.BoxArt => "boxart",
            MediaKind.Logo => "logos",
            _ => "screenshots",
        };
        Directory.CreateDirectory(Path.Combine(Root, folder));
        var id = Guid.NewGuid().ToString("N");

        // Decode first: this validates the file really is an image before anything is written.
        var source = Decode(sourcePath, decodeWidth: null);
        ct.ThrowIfCancellationRequested();

        string relative;
        if (kind == MediaKind.Logo)
        {
            // Logos keep their transparency: store the original file, no JPEG thumbnail.
            relative = Path.Combine(folder, id + ext);
            File.Copy(sourcePath, Path.Combine(Root, relative));
            logger.LogInformation("Imported {Kind} {Source} as {Path}", kind, sourcePath, relative);
            return new ImportedMedia(relative, null, source.PixelWidth, source.PixelHeight);
        }
        if (kind != MediaKind.Screenshot && source.PixelWidth > MaxCoverWidth)
        {
            // Covers/heroes are display art — keep a sensible size rather than a 8K original.
            relative = Path.Combine(folder, id + ".jpg");
            SaveJpeg(Scale(source, MaxCoverWidth), Path.Combine(Root, relative), 92);
        }
        else
        {
            relative = Path.Combine(folder, id + ext);
            File.Copy(sourcePath, Path.Combine(Root, relative));
        }

        string? thumb = null;
        try
        {
            var thumbRelative = Path.Combine(folder, id + ".thumb.jpg");
            SaveJpeg(Scale(source, ThumbnailWidth), Path.Combine(Root, thumbRelative), 85);
            thumb = thumbRelative;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Thumbnail generation failed for {Source}", sourcePath);
        }

        logger.LogInformation("Imported {Kind} {Source} as {Path}", kind, sourcePath, relative);
        return new ImportedMedia(relative, thumb, source.PixelWidth, source.PixelHeight);
    }

    public string? Resolve(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return null;
        return Path.IsPathFullyQualified(storedPath) ? storedPath : Path.Combine(Root, storedPath);
    }

    public void Delete(IEnumerable<string?> storedPaths)
    {
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        foreach (var stored in storedPaths)
        {
            var full = Resolve(stored);
            if (full == null) continue;
            full = Path.GetFullPath(full);
            // Never delete anything outside Arcadia's own media folder.
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete media file {Path}", full);
            }
        }
    }

    private static BitmapSource Decode(string path, int? decodeWidth)
    {
        var bmp = new BitmapImage();
        using (var stream = File.OpenRead(path))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth is { } w) bmp.DecodePixelWidth = w;
            bmp.StreamSource = stream;
            bmp.EndInit();
        }
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource Scale(BitmapSource source, int maxWidth)
    {
        if (source.PixelWidth <= maxWidth) return source;
        double s = (double)maxWidth / source.PixelWidth;
        var scaled = new TransformedBitmap(source, new ScaleTransform(s, s));
        scaled.Freeze();
        return scaled;
    }

    private static void SaveJpeg(BitmapSource bitmap, string path, int quality)
    {
        // JPEG has no alpha channel; converting drops it (transparent areas become black, which suits the dark UI).
        BitmapSource frame = bitmap;
        if (bitmap.Format.Masks.Count == 4 || bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Pbgra32)
            frame = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        var temp = path + ".tmp";
        using (var fs = File.Create(temp)) encoder.Save(fs);
        File.Move(temp, path, overwrite: true);
    }
}
