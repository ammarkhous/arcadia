using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Arcadia.App.Services;

/// <summary>
/// Windows' own file-type icons and type names ("PDF Document", "Microsoft Excel Worksheet"), looked up by
/// extension only (SHGFI_USEFILEATTRIBUTES: the file is never opened, so missing files still get their icon).
/// Results are frozen and cached per extension.
/// </summary>
public static class ShellFileInfo
{
    private const uint ShgfiIcon = 0x100, ShgfiSmallIcon = 0x1, ShgfiUseFileAttributes = 0x10, ShgfiTypeName = 0x400;
    private const uint FileAttributeNormal = 0x80, FileAttributeDirectory = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    private static readonly ConcurrentDictionary<(string, bool), ImageSource?> Icons = new();
    private static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shell icon for an extension (".pdf") or a folder; null if Windows has none.</summary>
    public static ImageSource? Icon(string extension, bool isFolder, bool large) =>
        Icons.GetOrAdd((isFolder ? "<folder>" : extension.ToLowerInvariant(), large), key => Load(key.Item1, isFolder, key.Item2));

    private static ImageSource? Load(string extension, bool isFolder, bool large)
    {
        var info = new ShFileInfo();
        var flags = ShgfiIcon | ShgfiUseFileAttributes | (large ? 0 : ShgfiSmallIcon);
        var name = isFolder ? "folder" : "file" + extension;
        if (SHGetFileInfo(name, isFolder ? FileAttributeDirectory : FileAttributeNormal, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags) == 0 || info.hIcon == 0)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>Windows' localized type description, e.g. "PDF Document"; falls back to the extension.</summary>
    public static string TypeName(string extension, bool isFolder)
    {
        var key = isFolder ? "<folder>" : extension;
        return TypeNames.GetOrAdd(key, _ =>
        {
            var info = new ShFileInfo();
            var name = isFolder ? "folder" : "file" + extension;
            SHGetFileInfo(name, isFolder ? FileAttributeDirectory : FileAttributeNormal, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiTypeName | ShgfiUseFileAttributes);
            return string.IsNullOrWhiteSpace(info.szTypeName) ? extension.TrimStart('.').ToUpperInvariant() : info.szTypeName;
        });
    }
}

/// <summary>
/// Small JPEG thumbnails for images, cached on disk under Cache\Thumbnails. The cache key includes the file's size
/// and modification time, so an edited image gets a fresh thumbnail automatically. Images are decoded directly at
/// thumbnail size (never at full resolution).
/// </summary>
public sealed class ThumbnailService(string cacheDirectory, Microsoft.Extensions.Logging.ILogger<ThumbnailService> logger)
{
    private static readonly SemaphoreSlim Slots = new(2);
    public string CacheDirectory { get; } = cacheDirectory;

    /// <summary>Gaming Mode / memory pressure: existing thumbnails are served, new ones are not generated (Phase 9).</summary>
    public static bool Paused { get; set; }

    /// <summary>
    /// A cached JPEG thumbnail (made once, on a background thread). <paramref name="orientation"/> is the EXIF
    /// orientation (1–8); the thumbnail is turned upright, the original file is never changed.
    /// </summary>
    public async Task<string?> GetAsync(string path, int width, CancellationToken ct = default, int orientation = 1)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(
                $"{path.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{width}" + (orientation > 1 ? $"|o{orientation}" : string.Empty))));
            var target = Path.Combine(CacheDirectory, key[..2], key + ".jpg");
            if (File.Exists(target)) return target;
            if (Paused) return null;

            await Slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    var bmp = new BitmapImage();
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        bmp.DecodePixelWidth = width;
                        bmp.StreamSource = stream;
                        bmp.EndInit();
                    }
                    bmp.Freeze();
                    BitmapSource upright = orientation > 1 ? Orient(bmp, orientation) : bmp;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temp = target + ".tmp";
                    var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                    encoder.Frames.Add(BitmapFrame.Create(upright));
                    using (var fs = File.Create(temp)) encoder.Save(fs);
                    File.Move(temp, target, overwrite: true);
                    return target;
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                Slots.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(logger, ex, "No thumbnail for {Path}", path);
            return null;
        }
    }

    /// <summary>Applies an EXIF orientation (rotation and/or mirroring) to a bitmap.</summary>
    public static BitmapSource Orient(BitmapSource source, int orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        if (t == null) return source;
        var result = new TransformedBitmap(source, t);
        result.Freeze();
        return result;
    }
}