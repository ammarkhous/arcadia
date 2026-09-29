namespace Arcadia.Core.Media;

public enum MediaKind { Cover, Hero, Screenshot, BoxArt, Logo }

/// <param name="Path">Stored path of the imported file (relative to the media root).</param>
/// <param name="ThumbnailPath">Stored path of a small preview, when one was generated.</param>
public sealed record ImportedMedia(string Path, string? ThumbnailPath, int PixelWidth, int PixelHeight);

/// <summary>
/// Owns Arcadia's media folder. Imported files are copied (never referenced in place) so the library
/// survives the originals being moved, and thumbnails keep lists fast.
/// </summary>
public interface IMediaStore
{
    string Root { get; }

    Task<ImportedMedia> ImportAsync(string sourcePath, MediaKind kind, CancellationToken cancellationToken = default);

    /// <summary>Turns a stored path into an absolute path (absolute paths pass through). Null/empty → null.</summary>
    string? Resolve(string? storedPath);

    /// <summary>Best-effort removal of media files that belong to Arcadia's media folder.</summary>
    void Delete(IEnumerable<string?> storedPaths);

    static readonly IReadOnlyList<string> SupportedExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"];
}
