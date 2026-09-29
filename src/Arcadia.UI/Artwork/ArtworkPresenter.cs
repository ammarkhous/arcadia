using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Arcadia.UI.Artwork;

/// <summary>
/// Shows real artwork when <see cref="ImagePath"/> points to an image, otherwise the procedural scene.
/// Images are decoded off the UI thread at roughly display size and faded in.
/// </summary>
[TemplatePart(Name = "PART_Image", Type = typeof(Image))]
public class ArtworkPresenter : Control
{
    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(
        nameof(ImagePath), typeof(string), typeof(ArtworkPresenter), new PropertyMetadata(null, (d, _) => ((ArtworkPresenter)d).LoadImage()));

    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(string), typeof(ArtworkPresenter), new PropertyMetadata("mountains-dusk"));

    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(
        nameof(Seed), typeof(int), typeof(ArtworkPresenter), new PropertyMetadata(1));

    public static readonly DependencyProperty VignetteProperty = DependencyProperty.Register(
        nameof(Vignette), typeof(double), typeof(ArtworkPresenter), new PropertyMetadata(0.45));

    public static readonly DependencyProperty BottomShadeProperty = DependencyProperty.Register(
        nameof(BottomShade), typeof(double), typeof(ArtworkPresenter), new PropertyMetadata(0.0));

    /// <summary>When false, no procedural scene is drawn behind the image (e.g. for transparent logos).</summary>
    public static readonly DependencyProperty ShowSceneProperty = DependencyProperty.Register(
        nameof(ShowScene), typeof(bool), typeof(ArtworkPresenter), new PropertyMetadata(true, (d, _) => ((ArtworkPresenter)d).UpdateScene()));

    public bool ShowScene { get => (bool)GetValue(ShowSceneProperty); set => SetValue(ShowSceneProperty, value); }

    /// <summary>UniformToFill for artwork (default); Uniform for logos that must not be cropped.</summary>
    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch), typeof(System.Windows.Media.Stretch), typeof(ArtworkPresenter), new PropertyMetadata(System.Windows.Media.Stretch.UniformToFill));

    public System.Windows.Media.Stretch Stretch { get => (System.Windows.Media.Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }

    /// <summary>
    /// Shows the whole image (Uniform) over a blurred, darkened copy of itself that fills the frame —
    /// used where a wide banner must be seen in full in a frame of a different shape.
    /// </summary>
    public static readonly DependencyProperty AmbientFillProperty = DependencyProperty.Register(
        nameof(AmbientFill), typeof(bool), typeof(ArtworkPresenter), new PropertyMetadata(false));

    public bool AmbientFill { get => (bool)GetValue(AmbientFillProperty); set => SetValue(AmbientFillProperty, value); }

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(ArtworkPresenter), new PropertyMetadata(3.0, (d, _) => ((ArtworkPresenter)d).UpdateClip()));

    public string? ImagePath { get => (string?)GetValue(ImagePathProperty); set => SetValue(ImagePathProperty, value); }
    public string Theme { get => (string)GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
    public int Seed { get => (int)GetValue(SeedProperty); set => SetValue(SeedProperty, value); }
    public double Vignette { get => (double)GetValue(VignetteProperty); set => SetValue(VignetteProperty, value); }
    public double BottomShade { get => (double)GetValue(BottomShadeProperty); set => SetValue(BottomShadeProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    private Image? _image;
    private FrameworkElement? _scene;
    private bool _expectingImage;
    private Image? _backdrop;
    private FrameworkElement? _ambient;
    private int _loadVersion;

    static ArtworkPresenter()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ArtworkPresenter), new FrameworkPropertyMetadata(typeof(ArtworkPresenter)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _image = GetTemplateChild("PART_Image") as Image;
        _scene = GetTemplateChild("PART_Scene") as FrameworkElement;
        _backdrop = GetTemplateChild("PART_Backdrop") as Image;
        _ambient = GetTemplateChild("PART_Ambient") as FrameworkElement;
        LoadImage();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateClip();
        OnSizeForImage();
    }

    /// <summary>
    /// The procedural scene is only painted when there is no real artwork to show (it is the placeholder);
    /// while a real image loads the frame stays dark, which keeps scrolling large libraries cheap.
    /// </summary>
    private void UpdateScene()
    {
        if (_scene != null) _scene.Visibility = ShowScene && !_expectingImage ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateClip()
    {
        var r = CornerRadius;
        Clip = r <= 0 ? null : new RectangleGeometry(new Rect(RenderSize), r, r);
    }

    private string? _loadedPath;
    private Size _decodedFor;
    private System.Windows.Threading.DispatcherTimer? _resizeTimer;

    /// <summary>
    /// Loads the image at exactly the pixel size this frame needs (layout size × DPI, accounting for cropping),
    /// waiting for layout first. Re-decodes sharper if the frame later grows (e.g. the window is maximized).
    /// </summary>
    private async void LoadImage()
    {
        if (_image == null) return;
        var version = ++_loadVersion;
        var path = ImagePath;
        bool samePicture = path == _loadedPath && _image.Source != null;
        if (!samePicture)
        {
            if (_backdrop != null) _backdrop.Source = null;
            if (_ambient != null) _ambient.Visibility = Visibility.Collapsed; // only behind a real image
            _image.Source = null;
            _image.Opacity = 0;
            _loadedPath = null;
        }

        _expectingImage = !string.IsNullOrWhiteSpace(path) && (ImageCache.IsWebUrl(path) || File.Exists(path));
        UpdateScene();
        if (!_expectingImage) return;
        // Wait for layout, except for auto-sized frames (e.g. a logo with a fixed height and auto width):
        // those only get their size from the image, so size the decode from the known dimension instead.
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 && h < 1) return; // OnRenderSizeChanged calls back
        if (w < 1) w = double.IsInfinity(MaxWidth) ? h * 8 : MaxWidth;
        if (h < 1) h = double.IsInfinity(MaxHeight) ? w : MaxHeight;

        var dpi = VisualTreeHelper.GetDpi(this);
        var box = new Size(w * dpi.DpiScaleX, h * dpi.DpiScaleY);
        var stretch = AmbientFill ? System.Windows.Media.Stretch.Uniform : Stretch;
        // Already decoded at this size (e.g. scrolling back through the library): show it immediately.
        if (!samePicture && ImageCache.TryGetDecoded(path!, box, stretch) is { } ready)
        {
            Show(ready, path!, box, fade: false);
            return;
        }
        BitmapSource? bitmap;
        try
        {
            bitmap = await ImageCache.LoadAsync(path!, box, stretch);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException
                                       or HttpRequestException or TaskCanceledException or ArgumentException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning($"Arcadia: artwork '{path}' could not be loaded: {ex.Message}");
            if (version == _loadVersion)
            {
                _expectingImage = false; // fall back to the procedural placeholder
                UpdateScene();
            }
            return;
        }

        if (version != _loadVersion || _image == null) return;
        // A sharper re-decode of the same picture swaps without a fade.
        Show(bitmap, path!, box, fade: !samePicture);
    }

    private void Show(BitmapSource bitmap, string path, Size box, bool fade)
    {
        if (_image == null) return;
        _image.Source = bitmap;
        if (_backdrop != null && AmbientFill)
        {
            _backdrop.Source = bitmap;
            if (_ambient != null) _ambient.Visibility = Visibility.Visible;
        }
        _loadedPath = path;
        _decodedFor = box;
        if (fade && Motion.AnimationsEnabled)
            _image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.EaseOut });
        else
        {
            _image.BeginAnimation(OpacityProperty, null);
            _image.Opacity = 1;
        }
    }

    private void OnSizeForImage()
    {
        if (_image == null || string.IsNullOrWhiteSpace(ImagePath)) return;
        if (_loadedPath == null)
        {
            LoadImage(); // first real layout
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        bool grewNoticeably = ActualWidth * dpi.DpiScaleX > _decodedFor.Width * 1.15 || ActualHeight * dpi.DpiScaleY > _decodedFor.Height * 1.15;
        if (!grewNoticeably) return;
        _resizeTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { _resizeTimer!.Stop(); LoadImage(); }, Dispatcher);
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }
}

/// <summary>Small in-memory cache of decoded, frozen bitmaps keyed by path and decode width.</summary>
public static class ImageCache
{
    /// <summary>Decoded-bitmap memory budget (BGRA bytes); least recently used pictures are dropped first.</summary>
    private const long CapacityBytes = 256L * 1024 * 1024;
    private static readonly Dictionary<string, BitmapSource> Cache = new();
    private static readonly Dictionary<string, LinkedListNode<string>> Nodes = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Width, int Height, long Stamp)> Dimensions = new();
    private static long _bytes;
    private static readonly LinkedList<string> Order = new();
    private static readonly Lock Gate = new();

    /// <summary>Folder for downloaded web images (set at startup). Without it web images are not loaded.</summary>
    public static string? WebCacheDirectory { get; set; }

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Arcadia/0.1 (+desktop)");
        return client;
    });

    public static bool IsWebUrl(string path) =>
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    // Downloads are asynchronous (no thread-pool blocking), limited in parallel and de-duplicated per URL.
    private static readonly SemaphoreSlim DownloadSlots = new(6);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> InFlight = new();

    /// <summary>
    /// Loads a local file or web image (downloaded once into the disk cache) and decodes it off the UI thread,
    /// at the resolution needed to fill <paramref name="boxPixels"/> with the given stretch (never upscaled).
    /// </summary>
    public static async Task<BitmapSource> LoadAsync(string path, Size boxPixels, System.Windows.Media.Stretch stretch)
    {
        if (IsWebUrl(path)) path = await DownloadToCacheAsync(path).ConfigureAwait(false);
        return await Task.Run(() => Load(path, DecodeWidthFor(path, boxPixels, stretch))).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the bitmap synchronously when it has already been decoded for this size (no disk access
    /// beyond a cached timestamp), otherwise null.
    /// </summary>
    public static BitmapSource? TryGetDecoded(string path, Size boxPixels, System.Windows.Media.Stretch stretch)
    {
        if (IsWebUrl(path)) return null;
        if (!Dimensions.TryGetValue(path, out var d)) return null;
        var key = Key(path, DecodeWidthFor(d.Width, d.Height, boxPixels, stretch), d.Stamp);
        lock (Gate)
        {
            if (!Cache.TryGetValue(key, out var hit)) return null;
            Touch(key);
            return hit;
        }
    }

    private static string Key(string path, int decodeWidth, long stamp) => $"{path}|{decodeWidth}|{stamp}";

    /// <summary>Drops every decoded image (memory pressure). Returns the approximate bytes released; images reload on demand.</summary>
    public static long Trim()
    {
        lock (Gate)
        {
            var freed = _bytes;
            Cache.Clear();
            Nodes.Clear();
            Order.Clear();
            _bytes = 0;
            return freed;
        }
    }

    private static void Touch(string key)
    {
        if (!Nodes.TryGetValue(key, out var node)) return;
        Order.Remove(node);
        Order.AddFirst(node);
    }

    /// <summary>Pixel width to decode at: enough to cover the box (or fit it), rounded up to 128 px, 0 = full size.</summary>
    internal static int DecodeWidthFor(string path, Size box, System.Windows.Media.Stretch stretch)
    {
        var stamp = File.GetLastWriteTimeUtc(path).Ticks;
        if (!Dimensions.TryGetValue(path, out var d) || d.Stamp != stamp)
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            d = (frame.PixelWidth, frame.PixelHeight, stamp);
            Dimensions[path] = d;
        }
        return DecodeWidthFor(d.Width, d.Height, box, stretch);
    }

    internal static int DecodeWidthFor(int iw, int ih, Size box, System.Windows.Media.Stretch stretch)
    {
        if (iw <= 0 || ih <= 0) return 0;
        double scale = stretch switch
        {
            System.Windows.Media.Stretch.UniformToFill => Math.Max(box.Width / iw, box.Height / ih),
            System.Windows.Media.Stretch.Uniform => Math.Min(box.Width / iw, box.Height / ih),
            _ => box.Width / iw,
        };
        int needed = (int)Math.Ceiling(iw * scale / 128.0) * 128;
        return needed >= iw * 0.9 ? 0 : Math.Max(128, needed);
    }

    /// <summary>Downloads a web image once into the disk cache and returns the local file path.</summary>
    public static Task<string> DownloadToCacheAsync(string url)
    {
        if (WebCacheDirectory == null) throw new InvalidOperationException("Web image cache is not configured.");
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        if (ext.Length is 0 or > 5) ext = ".img";
        var file = Path.Combine(WebCacheDirectory, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ext);
        if (File.Exists(file) && new FileInfo(file).Length > 0) return Task.FromResult(file);

        var task = InFlight.GetOrAdd(url, _ => DownloadAsync(url, file));
        _ = task.ContinueWith(_ => InFlight.TryRemove(url, out Task<string>? _), TaskScheduler.Default);
        return task;
    }

    private static async Task<string> DownloadAsync(string url, string file)
    {
        await DownloadSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var bytes = await Http.Value.GetByteArrayAsync(url).ConfigureAwait(false);
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
            return file;
        }
        finally
        {
            DownloadSlots.Release();
        }
    }

    private static long SizeOf(BitmapSource b) => (long)b.PixelWidth * b.PixelHeight * 4;

    public static BitmapSource Load(string path, int decodeWidth)
    {
        var key = Key(path, decodeWidth, Dimensions.TryGetValue(path, out var d) ? d.Stamp : File.GetLastWriteTimeUtc(path).Ticks);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit)) { Touch(key); return hit; }
        }

        var bmp = new BitmapImage();
        using (var stream = File.OpenRead(path))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = stream;
            bmp.EndInit();
        }
        bmp.Freeze();

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var raced)) return raced;
            Cache[key] = bmp;
            Nodes[key] = Order.AddFirst(key);
            _bytes += SizeOf(bmp);
            while (_bytes > CapacityBytes && Order.Count > 1)
            {
                var oldest = Order.Last!.Value;
                Order.RemoveLast();
                Nodes.Remove(oldest);
                if (Cache.Remove(oldest, out var dropped)) _bytes -= SizeOf(dropped);
            }
        }
        return bmp;
    }
}
