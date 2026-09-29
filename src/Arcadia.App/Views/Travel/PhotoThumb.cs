using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Arcadia.App.Services;

namespace Arcadia.App.Views.Travel;

/// <summary>
/// A photo thumbnail that loads lazily when it becomes visible: the disk-cached JPEG from <see cref="ThumbnailService"/>
/// (made once, upright per EXIF orientation), decoded small, kept in a small in-memory LRU. Scrolling away cancels the
/// load, so hundreds of photos never sit in memory at full size.
/// </summary>
public sealed class PhotoThumb : Image
{
    /// <summary>Set once at startup.</summary>
    public static ThumbnailService? Thumbnails { get; set; }

    private static readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> Cache = [];
    private static readonly LinkedList<(string Key, ImageSource Image)> Lru = new();
    private const int CacheCapacity = 400;
    private CancellationTokenSource? _cts;

    public PhotoThumb()
    {
        Stretch = Stretch.UniformToFill;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => Load();
        Unloaded += (_, _) => { _cts?.Cancel(); _cts = null; };
    }

    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(nameof(Path), typeof(string), typeof(PhotoThumb),
        new PropertyMetadata(null, (d, _) => ((PhotoThumb)d).Load()));
    public string? Path { get => (string?)GetValue(PathProperty); set => SetValue(PathProperty, value); }

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(nameof(Orientation), typeof(int), typeof(PhotoThumb),
        new PropertyMetadata(1, (d, _) => ((PhotoThumb)d).Load()));
    public int Orientation { get => (int)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    /// <summary>Thumbnail width in pixels (320 for grids, 640 for large cards).</summary>
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.Register(nameof(DecodeWidth), typeof(int), typeof(PhotoThumb), new PropertyMetadata(320));
    public int DecodeWidth { get => (int)GetValue(DecodeWidthProperty); set => SetValue(DecodeWidthProperty, value); }

    private static readonly DependencyPropertyKey IsBrokenKey = DependencyProperty.RegisterReadOnly(nameof(IsBroken), typeof(bool), typeof(PhotoThumb), new PropertyMetadata(false));
    public static readonly DependencyProperty IsBrokenProperty = IsBrokenKey.DependencyProperty;
    /// <summary>True when the file is missing or cannot be decoded (the view shows a placeholder).</summary>
    public bool IsBroken => (bool)GetValue(IsBrokenProperty);

    private async void Load()
    {
        _cts?.Cancel();
        _cts = null;
        var path = Path;
        if (!IsLoaded || string.IsNullOrEmpty(path) || Thumbnails == null) { Source = null; return; }
        var key = $"{path}|{DecodeWidth}|{Orientation}";
        if (Cache.TryGetValue(key, out var node))
        {
            Lru.Remove(node);
            Lru.AddFirst(node);
            Source = node.Value.Image;
            SetValue(IsBrokenKey, false);
            return;
        }
        Source = null;
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var width = DecodeWidth;
            var file = await Thumbnails.GetAsync(path, width, cts.Token, Orientation);
            if (cts.IsCancellationRequested) return;
            if (file == null) { SetValue(IsBrokenKey, true); return; }
            var image = await Task.Run(() =>
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            Cache[key] = Lru.AddFirst((key, image));
            while (Lru.Count > CacheCapacity) { Cache.Remove(Lru.Last!.Value.Key); Lru.RemoveLast(); }
            Source = image;
            SetValue(IsBrokenKey, false);
            if (Arcadia.UI.Motion.AnimationsEnabled) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or FileFormatException)
        {
            SetValue(IsBrokenKey, true);
        }
    }
}
