using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Arcadia.App.Services;
using Arcadia.Core.Travel;

namespace Arcadia.App.Views.Travel;

public enum MapMarkerKind { Wanted, Planned, Visited, Itinerary, Photo, Search }

/// <summary>A point drawn on the map. <see cref="Key"/> identifies it for selection and clicks.</summary>
public sealed record MapMarker(object Key, double Latitude, double Longitude, MapMarkerKind Kind, string Label, bool IsFavorite = false, int? Number = null, Color? Tone = null);

/// <summary>A route line through points, e.g. one itinerary day.</summary>
public sealed record MapRoute(IReadOnlyList<(double Lat, double Lon)> Points, Color Color, bool Dashed = true);

public enum CountryShade { None, Wanted, Visited }

/// <summary>A clicked or right-clicked position.</summary>
public sealed record MapPoint(double Latitude, double Longitude, string? CountryCode);

/// <summary>
/// Arcadia's world map: the bundled Natural Earth outlines drawn as a dark vector chart (works offline), optional
/// online detail tiles faded in when zooming towards street level, markers, routes and labels. Web Mercator,
/// pan by dragging, zoom with the wheel/double-click/keys, horizontally wrapping. Always left-to-right.
/// </summary>
public sealed class WorldMap : FrameworkElement
{
    private const double TileSize = 256;
    public const double MinZoomLevel = 1, MaxZoomLevel = 18;

    // ── palette (dark chart) ──
    private static readonly Brush Ocean = Frozen(new LinearGradientBrush(Color.FromRgb(0x05, 0x09, 0x10), Color.FromRgb(0x09, 0x11, 0x1D), 90));
    private static readonly Brush Land = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x1E, 0x2C)));
    private static readonly Brush LandHover = Frozen(new SolidColorBrush(Color.FromRgb(0x1B, 0x29, 0x3C)));
    private static readonly Brush VisitedFill = Frozen(new LinearGradientBrush(Color.FromRgb(0x17, 0x5F, 0x74), Color.FromRgb(0x0F, 0x44, 0x57), 90));
    private static readonly Brush WantedFill = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x30, 0x17)));
    private static readonly Color Cyan = Color.FromRgb(0x45, 0xD2, 0xFF);
    private static readonly Color Blue = Color.FromRgb(0x4F, 0x8D, 0xFF);
    private static readonly Color Amber = Color.FromRgb(0xF2, 0xB1, 0x3C);
    private static readonly Color Gold = Color.FromRgb(0xFF, 0xD2, 0x6A);
    private static readonly Pen Border = FrozenPen(Color.FromRgb(0x2B, 0x40, 0x5C), 0.8);
    private static readonly Pen VisitedBorder = FrozenPen(Color.FromArgb(0xE0, 0x5F, 0xDA, 0xFF), 1.2);
    private static readonly Pen SelectedBorder = FrozenPen(Color.FromRgb(0x9F, 0xEB, 0xFF), 1.8);
    private static readonly Pen Graticule = FrozenPen(Color.FromArgb(0x55, 0x1A, 0x2C, 0x44), 1);
    private static readonly Pen Equator = FrozenPen(Color.FromArgb(0x70, 0x24, 0x3A, 0x58), 1, dashed: true);
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6F, 0x86, 0xA6)));
    private static readonly Brush CoordBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0x4E, 0x66, 0x86)));
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface MonoFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static Pen FrozenPen(Color c, double w, bool dashed = false)
    {
        var p = new Pen(new SolidColorBrush(c), w) { LineJoin = PenLineJoin.Round };
        if (dashed) p.DashStyle = new DashStyle([4, 4], 0);
        return Frozen(p);
    }

    private readonly Dictionary<(string Code, bool Detail), Geometry> _geometry = [];
    private readonly Dictionary<string, FormattedText> _labels = [];
    private readonly DispatcherTimer _zoomTimer;
    private double _zoomTarget, _zoomFrom, _zoomPivotX, _zoomPivotY;
    private DateTime _zoomStart;
    private Point? _dragStart;
    private (double X, double Y) _dragCenter;
    private bool _dragged;
    private string? _hoverCountry;
    private object? _hoverMarker;
    private List<(Rect Hit, MapMarker? Marker, List<MapMarker>? Cluster)> _hitTargets = [];
    private MapTileImages? _subscribedTiles;

    public WorldMap()
    {
        FlowDirection = FlowDirection.LeftToRight; // geography never mirrors
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        AutomationProperties.SetName(this, "World map");
        _zoomTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _zoomTimer.Tick += OnZoomTick;
        Loaded += (_, _) => Subscribe(Tiles);
        Unloaded += (_, _) => { Subscribe(null); _zoomTimer.Stop(); };
    }

    // ───────────────────────── properties ─────────────────────────

    public static readonly DependencyProperty CountriesProperty = DependencyProperty.Register(nameof(Countries), typeof(ICountryCatalog), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public ICountryCatalog? Countries { get => (ICountryCatalog?)GetValue(CountriesProperty); set => SetValue(CountriesProperty, value); }

    public static readonly DependencyProperty TilesProperty = DependencyProperty.Register(nameof(Tiles), typeof(MapTileImages), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((WorldMap)d).Subscribe((MapTileImages?)e.NewValue)));
    public MapTileImages? Tiles { get => (MapTileImages?)GetValue(TilesProperty); set => SetValue(TilesProperty, value); }

    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(nameof(Markers), typeof(IEnumerable), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCollectionChanged));
    public IEnumerable? Markers { get => (IEnumerable?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }

    public static readonly DependencyProperty RoutesProperty = DependencyProperty.Register(nameof(Routes), typeof(IEnumerable), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCollectionChanged));
    public IEnumerable? Routes { get => (IEnumerable?)GetValue(RoutesProperty); set => SetValue(RoutesProperty, value); }

    public static readonly DependencyProperty CountryShadesProperty = DependencyProperty.Register(nameof(CountryShades), typeof(IReadOnlyDictionary<string, CountryShade>), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyDictionary<string, CountryShade>? CountryShades { get => (IReadOnlyDictionary<string, CountryShade>?)GetValue(CountryShadesProperty); set => SetValue(CountryShadesProperty, value); }

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(nameof(SelectedKey), typeof(object), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((WorldMap)d).StartPulse()));
    public object? SelectedKey { get => GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }

    public static readonly DependencyProperty SelectedCountryProperty = DependencyProperty.Register(nameof(SelectedCountry), typeof(string), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public string? SelectedCountry { get => (string?)GetValue(SelectedCountryProperty); set => SetValue(SelectedCountryProperty, value); }

    public static readonly DependencyProperty LatitudeProperty = DependencyProperty.Register(nameof(Latitude), typeof(double), typeof(WorldMap),
        new FrameworkPropertyMetadata(25.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public double Latitude { get => (double)GetValue(LatitudeProperty); set => SetValue(LatitudeProperty, value); }

    public static readonly DependencyProperty LongitudeProperty = DependencyProperty.Register(nameof(Longitude), typeof(double), typeof(WorldMap),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public double Longitude { get => (double)GetValue(LongitudeProperty); set => SetValue(LongitudeProperty, value); }

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(WorldMap),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, null,
            (_, v) => Math.Clamp((double)v, MinZoomLevel, MaxZoomLevel)));
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    public static readonly DependencyProperty LanguageCodeProperty = DependencyProperty.Register(nameof(LanguageCode), typeof(string), typeof(WorldMap),
        new FrameworkPropertyMetadata("en", FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((WorldMap)d)._labels.Clear()));
    public string LanguageCode { get => (string)GetValue(LanguageCodeProperty); set => SetValue(LanguageCodeProperty, value); }

    /// <summary>Compact maps (dashboard, trip overview) hide graticule labels and country names.</summary>
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(WorldMap),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    public static readonly DependencyProperty MarkerCommandProperty = DependencyProperty.Register(nameof(MarkerCommand), typeof(ICommand), typeof(WorldMap));
    public ICommand? MarkerCommand { get => (ICommand?)GetValue(MarkerCommandProperty); set => SetValue(MarkerCommandProperty, value); }

    public static readonly DependencyProperty CountryCommandProperty = DependencyProperty.Register(nameof(CountryCommand), typeof(ICommand), typeof(WorldMap));
    public ICommand? CountryCommand { get => (ICommand?)GetValue(CountryCommandProperty); set => SetValue(CountryCommandProperty, value); }

    /// <summary>Invoked on a click on empty land or sea with a <see cref="MapPoint"/>.</summary>
    public static readonly DependencyProperty PointCommandProperty = DependencyProperty.Register(nameof(PointCommand), typeof(ICommand), typeof(WorldMap));
    public ICommand? PointCommand { get => (ICommand?)GetValue(PointCommandProperty); set => SetValue(PointCommandProperty, value); }

    /// <summary>The position last right-clicked (for the context menu's "Add place here").</summary>
    public static readonly DependencyProperty ContextPointProperty = DependencyProperty.Register(nameof(ContextPoint), typeof(MapPoint), typeof(WorldMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public MapPoint? ContextPoint { get => (MapPoint?)GetValue(ContextPointProperty); set => SetValue(ContextPointProperty, value); }

    private static readonly DependencyPropertyKey CursorTextKey = DependencyProperty.RegisterReadOnly(nameof(CursorText), typeof(string), typeof(WorldMap), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty CursorTextProperty = CursorTextKey.DependencyProperty;
    /// <summary>Coordinates and country under the mouse ("46.0212° N, 7.7493° E · Switzerland").</summary>
    public string CursorText => (string)GetValue(CursorTextProperty);

    private static readonly DependencyPropertyKey ShowsTilesKey = DependencyProperty.RegisterReadOnly(nameof(ShowsTiles), typeof(bool), typeof(WorldMap), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowsTilesProperty = ShowsTilesKey.DependencyProperty;
    /// <summary>True while online tiles are visible (the attribution must then be shown).</summary>
    public bool ShowsTiles => (bool)GetValue(ShowsTilesProperty);

    public static readonly DependencyProperty PulseProperty = DependencyProperty.Register(nameof(Pulse), typeof(double), typeof(WorldMap),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Pulse { get => (double)GetValue(PulseProperty); set => SetValue(PulseProperty, value); }

    private static void OnCollectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (WorldMap)d;
        if (e.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= map.OnItemsChanged;
        if (e.NewValue is INotifyCollectionChanged n) n.CollectionChanged += map.OnItemsChanged;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private void Subscribe(MapTileImages? tiles)
    {
        if (_subscribedTiles != null) _subscribedTiles.Changed -= OnTilesChanged;
        _subscribedTiles = IsLoaded ? tiles : null;
        if (_subscribedTiles != null) _subscribedTiles.Changed += OnTilesChanged;
    }

    private void OnTilesChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void StartPulse()
    {
        if (SelectedKey == null || !Arcadia.UI.Motion.AnimationsEnabled) { BeginAnimation(PulseProperty, null); Pulse = 0; return; }
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1600)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(anim, 30);
        BeginAnimation(PulseProperty, anim);
    }

    // ───────────────────────── geometry ─────────────────────────

    private double WorldPixels => TileSize * Math.Pow(2, Zoom);

    private (double X, double Y) Center => GeoMath.ToWorld(Latitude, Longitude);

    /// <summary>Screen position of a world point, choosing the horizontal copy nearest the view centre.</summary>
    private Point ToScreen(double wx, double wy)
    {
        var (cx, cy) = Center;
        var dx = wx - cx;
        dx -= Math.Round(dx); // wrap around the date line
        var w = WorldPixels;
        return new Point(ActualWidth / 2 + dx * w, ActualHeight / 2 + (wy - cy) * w);
    }

    private Point ToScreen(double lat, double lon, bool _) { var (x, y) = GeoMath.ToWorld(lat, lon); return ToScreen(x, y); }

    private (double Lat, double Lon) ToGeo(Point p)
    {
        var (cx, cy) = Center;
        var w = WorldPixels;
        var wx = cx + (p.X - ActualWidth / 2) / w;
        var wy = Math.Clamp(cy + (p.Y - ActualHeight / 2) / w, 0, 1);
        var (lat, lon) = GeoMath.FromWorld(wx, wy);
        return (lat, GeoMath.NormalizeLongitude(lon));
    }

    private Geometry CountryGeometry(CountryInfo c, bool detail)
    {
        if (_geometry.TryGetValue((c.Code, detail), out var g)) return g;
        var sg = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var ctx = sg.Open())
        {
            foreach (var polygon in detail ? c.Detail : c.Coarse)
                foreach (var ring in polygon)
                {
                    if (ring.Length < 6) continue;
                    var pts = new List<Point>(ring.Length / 2);
                    for (int i = 0; i < ring.Length; i += 2)
                    {
                        var (x, y) = GeoMath.ToWorld(ring[i + 1], ring[i]);
                        pts.Add(new Point(x * TileSize, y * TileSize));
                    }
                    ctx.BeginFigure(pts[0], true, true);
                    ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
                }
        }
        sg.Freeze();
        _geometry[(c.Code, detail)] = sg;
        return sg;
    }

    // ───────────────────────── rendering ─────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Rect(0, 0, ActualWidth, ActualHeight);
        if (size.Width < 2 || size.Height < 2) return;
        dc.DrawRectangle(Ocean, null, size);
        var w = WorldPixels;
        var (cx, cy) = Center;
        var tileAlpha = TileOpacity();
        SetValue(ShowsTilesKey, tileAlpha > 0.02);

        DrawGraticule(dc, size);
        if (tileAlpha > 0.02) DrawTiles(dc, size, tileAlpha);
        DrawCountries(dc, size, w, cx, cy, tileAlpha);
        DrawRoutes(dc);
        DrawMarkers(dc, size);
    }

    /// <summary>Online tiles fade in from zoom 3 (country scale) to 4.5; the vector chart carries the world view.</summary>
    private double TileOpacity() => Tiles?.Source == null ? 0 : Math.Clamp((Zoom - 3) / 1.5, 0, 1);

    private void DrawGraticule(DrawingContext dc, Rect size)
    {
        var step = Zoom < 2.5 ? 30 : Zoom < 4 ? 15 : Zoom < 6 ? 5 : Zoom < 8 ? 1 : 0;
        if (step == 0) return;
        var (topLat, leftLon) = ToGeo(new Point(0, 0));
        var (bottomLat, rightLon) = ToGeo(new Point(size.Width, size.Height));
        var span = (size.Width / WorldPixels) * 360;
        for (double lat = -80; lat <= 80; lat += step)
        {
            if (lat > topLat || lat < bottomLat) continue;
            var y = ToScreen(lat, 0, true).Y;
            dc.DrawLine(lat == 0 ? Equator : Graticule, new Point(0, y), new Point(size.Width, y));
            if (!IsCompact) DrawCoord(dc, FormatLat(lat), new Point(6, y - 13));
        }
        var startLon = Math.Floor((Longitude - span / 2) / step) * step;
        for (double lon = startLon; lon <= Longitude + span / 2 + step; lon += step)
        {
            var x = ToScreen(0, GeoMath.NormalizeLongitude(lon), true).X;
            if (x < 0 || x > size.Width) continue;
            dc.DrawLine(Graticule, new Point(x, 0), new Point(x, size.Height));
            if (!IsCompact) DrawCoord(dc, FormatLon(GeoMath.NormalizeLongitude(lon)), new Point(x + 4, size.Height - 16));
        }
    }

    private static string FormatLat(double lat) => lat == 0 ? "0°" : $"{Math.Abs(lat).ToString("0.#", CultureInfo.InvariantCulture)}°{(lat > 0 ? 'N' : 'S')}";
    private static string FormatLon(double lon) => Math.Abs(lon) < 1e-9 || Math.Abs(Math.Abs(lon) - 180) < 1e-9 ? $"{Math.Abs(lon):0}°" : $"{Math.Abs(lon).ToString("0.#", CultureInfo.InvariantCulture)}°{(lon > 0 ? 'E' : 'W')}";

    private void DrawCoord(DrawingContext dc, string text, Point at)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, 9.5, CoordBrush, 1.25);
        dc.DrawText(ft, at);
    }

    private void DrawTiles(DrawingContext dc, Rect size, double alpha)
    {
        if (Tiles is not { } tiles || tiles.Source is not { } source) return;
        var z = (int)Math.Clamp(Math.Floor(Zoom + 0.35), source.MinZoom, source.MaxZoom);
        var scale = Math.Pow(2, Zoom - z) * TileSize;
        var n = 1 << z;
        var (cx, cy) = Center;
        var originX = ActualWidth / 2 - cx * n * scale;
        var originY = ActualHeight / 2 - cy * n * scale;
        int x0 = (int)Math.Floor(-originX / scale), x1 = (int)Math.Floor((size.Width - originX) / scale);
        int y0 = Math.Max(0, (int)Math.Floor(-originY / scale)), y1 = Math.Min(n - 1, (int)Math.Floor((size.Height - originY) / scale));
        if (x1 - x0 > 24 || y1 - y0 > 24) return; // never request an unreasonable number of tiles
        dc.PushOpacity(alpha);
        for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++)
            {
                var rect = new Rect(originX + tx * scale, originY + ty * scale, scale + 0.5, scale + 0.5);
                var wx = ((tx % n) + n) % n;
                var img = tiles.Get(z, wx, ty, load: true);
                if (img != null) { dc.DrawImage(img, rect); continue; }
                // while loading: show a cached ancestor tile, magnified
                for (int up = 1; up <= 4 && z - up >= 0; up++)
                {
                    int pz = z - up, px = wx >> up, py = ty >> up, f = 1 << up;
                    var parent = tiles.Get(pz, px, py, load: false);
                    if (parent == null) continue;
                    dc.PushClip(new RectangleGeometry(rect));
                    var full = scale * f;
                    dc.DrawImage(parent, new Rect(rect.X - (wx - px * f) * scale, rect.Y - (ty - py * f) * scale, full, full));
                    dc.Pop();
                    break;
                }
            }
        dc.Pop();
    }

    private void DrawCountries(DrawingContext dc, Rect size, double w, double cx, double cy, double tileAlpha)
    {
        if (Countries is not { } catalog || Zoom > 12) return;
        var detail = Zoom >= 3.4;
        var shades = CountryShades;
        var k = w / TileSize; // geometry units → pixels
        var viewWorld = new Rect(cx - size.Width / 2 / w, cy - size.Height / 2 / w, size.Width / w, size.Height / w);
        var landOpacity = 1 - tileAlpha * 0.92;
        var shadedOpacity = 1 - tileAlpha * 0.45;
        // pens are drawn inside the scale transform, so their widths are divided by it
        var border = ScaledPen(Border, 1 / k);
        var visitedBorder = ScaledPen(VisitedBorder, 1 / k);
        var selectedBorder = ScaledPen(SelectedBorder, 1 / k);
        var labels = new List<(CountryInfo Country, Point At)>();

        foreach (var copy in new[] { 0, -1, 1 })
        {
            var view = viewWorld;
            view.Offset(-copy, 0); // the part of the base world visible through this copy
            if (view.Right < 0 || view.Left > 1) continue;
            dc.PushTransform(new MatrixTransform(k, 0, 0, k, ActualWidth / 2 + (copy - cx) * w, ActualHeight / 2 - cy * w));
            foreach (var c in catalog.All)
            {
                if (c.Detail.Count == 0) continue;
                var (minLon, minLat, maxLon, maxLat) = c.Bounds;
                var (bx0, by1) = GeoMath.ToWorld(minLat, minLon);
                var (bx1, by0) = GeoMath.ToWorld(maxLat, maxLon);
                if (!new Rect(bx0, by0, Math.Max(0, bx1 - bx0), Math.Max(0, by1 - by0)).IntersectsWith(view)) continue;
                var shade = shades != null && shades.TryGetValue(c.Code, out var s) ? s : CountryShade.None;
                var selected = string.Equals(SelectedCountry, c.Code, StringComparison.OrdinalIgnoreCase);
                var hover = string.Equals(_hoverCountry, c.Code, StringComparison.OrdinalIgnoreCase);
                Brush fill = shade switch { CountryShade.Visited => VisitedFill, CountryShade.Wanted => WantedFill, _ => hover ? LandHover : Land };
                var pen = selected ? selectedBorder : shade == CountryShade.Visited ? visitedBorder : border;
                dc.PushOpacity(shade == CountryShade.None ? landOpacity : shadedOpacity);
                dc.DrawGeometry(fill, pen, CountryGeometry(c, detail));
                dc.Pop();
                if (copy == 0 && !IsCompact && c.Population > 0) labels.Add((c, ToScreen(c.LabelLatitude, c.LabelLongitude, true)));
            }
            dc.Pop();
        }
        if (Zoom >= 2.6 && !IsCompact) DrawCountryLabels(dc, labels, size);
    }

    private static Pen ScaledPen(Pen basePen, double unit)
    {
        var p = new Pen(basePen.Brush, basePen.Thickness * unit) { LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }
    private void DrawCountryLabels(DrawingContext dc, List<(CountryInfo Country, Point At)> labels, Rect size)
    {
        var taken = new List<Rect>();
        var fontSize = Math.Clamp(9 + (Zoom - 2.6) * 1.6, 9.5, 14);
        // bigger countries first, so small ones never hide them
        foreach (var (c, at) in labels.OrderByDescending(l => (l.Country.Bounds.MaxLon - l.Country.Bounds.MinLon) * (l.Country.Bounds.MaxLat - l.Country.Bounds.MinLat)))
        {
            var widthPx = (c.Bounds.MaxLon - c.Bounds.MinLon) / 360 * WorldPixels;
            if (widthPx < 46) continue;
            var ft = Label(c.Name(LanguageCode).ToUpper(CultureInfo.CurrentCulture), fontSize);
            if (ft.Width > widthPx * 1.4) continue;
            var rect = new Rect(at.X - ft.Width / 2, at.Y - ft.Height / 2, ft.Width, ft.Height);
            if (!size.IntersectsWith(rect) || taken.Any(t => t.IntersectsWith(rect))) continue;
            taken.Add(rect);
            dc.DrawText(ft, rect.TopLeft);
        }
    }

    private FormattedText Label(string text, double size)
    {
        var key = $"{text}|{size:0.0}";
        if (_labels.TryGetValue(key, out var ft)) return ft;
        ft = new FormattedText(text, CultureInfo.CurrentCulture, IsArabic(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, LabelFace, size, LabelBrush, 1.25);
        if (!IsArabic(text)) ft.SetFontStretch(FontStretches.Normal);
        if (_labels.Count > 2000) _labels.Clear();
        _labels[key] = ft;
        return ft;
    }

    private static bool IsArabic(string s) => s.Any(ch => ch is >= '؀' and <= 'ۿ');

    private void DrawRoutes(DrawingContext dc)
    {
        if (Routes == null) return;
        foreach (var route in Routes.OfType<MapRoute>())
        {
            if (route.Points.Count < 2) continue;
            var pen = new Pen(new SolidColorBrush(route.Color), 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (route.Dashed) pen.DashStyle = new DashStyle([3, 2.5], 0);
            pen.Freeze();
            var glow = new Pen(new SolidColorBrush(Color.FromArgb(0x38, route.Color.R, route.Color.G, route.Color.B)), 6) { LineJoin = PenLineJoin.Round };
            glow.Freeze();
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                var pts = route.Points.Select(p => ToScreen(p.Lat, p.Lon, true)).ToList();
                // keep consecutive points on the same side of the date line
                for (int i = 1; i < pts.Count; i++)
                {
                    var dx = pts[i].X - pts[i - 1].X;
                    if (Math.Abs(dx) > WorldPixels / 2) pts[i] = new Point(pts[i].X - Math.Sign(dx) * WorldPixels, pts[i].Y);
                }
                ctx.BeginFigure(pts[0], false, false);
                ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
            }
            sg.Freeze();
            dc.DrawGeometry(null, glow, sg);
            dc.DrawGeometry(null, pen, sg);
        }
    }

    private void DrawMarkers(DrawingContext dc, Rect size)
    {
        _hitTargets = [];
        if (Markers == null) return;
        var all = Markers.OfType<MapMarker>().Where(m => GeoMath.IsValid(m.Latitude, m.Longitude)).ToList();
        var placed = all.Select(m => (Marker: m, At: ToScreen(m.Latitude, m.Longitude, true))).Where(x => size.Contains(x.At) || Inflate(size, 30).Contains(x.At)).ToList();

        // cluster crowded markers at world scale (never the selected one or itinerary numbers)
        var cell = Zoom < 6 ? 26.0 : 0;
        var clusters = new Dictionary<(int, int), List<(MapMarker Marker, Point At)>>();
        var singles = new List<(MapMarker Marker, Point At)>();
        foreach (var p in placed)
        {
            if (cell == 0 || Equals(p.Marker.Key, SelectedKey) || p.Marker.Kind is MapMarkerKind.Itinerary or MapMarkerKind.Search) { singles.Add(p); continue; }
            var key = ((int)Math.Floor(p.At.X / cell), (int)Math.Floor(p.At.Y / cell));
            if (!clusters.TryGetValue(key, out var list)) clusters[key] = list = [];
            list.Add(p);
        }
        foreach (var group in clusters.Values)
        {
            if (group.Count == 1) { singles.Add(group[0]); continue; }
            var at = new Point(group.Average(g => g.At.X), group.Average(g => g.At.Y));
            DrawCluster(dc, at, group);
            _hitTargets.Add((new Rect(at.X - 13, at.Y - 13, 26, 26), null, group.Select(g => g.Marker).ToList()));
        }
        var showLabels = Zoom >= 5.5 && !IsCompact;
        var ordered = singles.OrderBy(s => Equals(s.Marker.Key, SelectedKey)).ThenBy(s => s.Marker.Kind == MapMarkerKind.Search).ToList();
        foreach (var (m, at) in ordered)
        {
            DrawMarker(dc, m, at, Equals(m.Key, SelectedKey), Equals(m.Key, _hoverMarker));
            _hitTargets.Add((new Rect(at.X - 11, at.Y - 11, 22, 22), m, null));
        }

        // labels go on top of every marker; the selected and hovered ones claim their space first
        var dots = ordered.Select(s => new Rect(s.At.X - 8, s.At.Y - 8, 16, 16)).ToList();
        var taken = new List<Rect>();
        foreach (var (m, at) in Enumerable.Reverse(ordered).OrderByDescending(s => Equals(s.Marker.Key, SelectedKey) || Equals(s.Marker.Key, _hoverMarker)))
        {
            var selected = Equals(m.Key, SelectedKey);
            var hover = Equals(m.Key, _hoverMarker);
            if (!(showLabels || selected || hover) || m.Label.Length == 0) continue;
            var ft = new FormattedText(m.Label, CultureInfo.CurrentCulture, IsArabic(m.Label) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                LabelFace, selected ? 12.5 : 11.5, selected || hover ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xC8, 0xD6, 0xE8)), 1.25);
            // right, left, below or above the marker, whichever is free
            Rect[] spots =
            [
                new(at.X + 11, at.Y - ft.Height / 2, ft.Width, ft.Height),
                new(at.X - 11 - ft.Width, at.Y - ft.Height / 2, ft.Width, ft.Height),
                new(at.X - ft.Width / 2, at.Y + 10, ft.Width, ft.Height),
                new(at.X - ft.Width / 2, at.Y - 10 - ft.Height, ft.Width, ft.Height),
            ];
            Rect? spot = spots.Cast<Rect?>().FirstOrDefault(s => !taken.Concat(dots).Any(t => t.IntersectsWith(s!.Value)));
            if (spot == null && (selected || hover)) spot = spots[0];
            if (spot is not { } rect) continue;
            taken.Add(rect);
            var halo = ft.BuildGeometry(rect.TopLeft);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0x05, 0x09, 0x10)), 3.2) { LineJoin = PenLineJoin.Round }, halo);
            dc.DrawText(ft, rect.TopLeft);
        }
    }

    private static Rect Inflate(Rect r, double by) { r.Inflate(by, by); return r; }

    private void DrawCluster(DrawingContext dc, Point at, List<(MapMarker Marker, Point At)> group)
    {
        var visited = group.Count(g => g.Marker.Kind == MapMarkerKind.Visited);
        var tone = visited * 2 >= group.Count ? Cyan : Blue;
        var r = Math.Min(15, 9 + Math.Log2(group.Count) * 1.6);
        dc.DrawEllipse(BrushOf(Color.FromArgb(0x38, tone.R, tone.G, tone.B)), null, at, r + 4, r + 4);
        dc.DrawEllipse(ClusterFill, PenOf(tone, 1.4), at, r, r);
        if (!_counts.TryGetValue(group.Count, out var ft))
            _counts[group.Count] = ft = new FormattedText(group.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 11, Brushes.White, 1.25);
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }

    private void DrawMarker(DrawingContext dc, MapMarker m, Point at, bool selected, bool hover)
    {
        var tone = m.Tone ?? m.Kind switch
        {
            MapMarkerKind.Visited => Cyan,
            MapMarkerKind.Planned => Blue,
            MapMarkerKind.Wanted => Amber,
            MapMarkerKind.Search => Colors.White,
            MapMarkerKind.Photo => Color.FromRgb(0xB9, 0xC8, 0xDC),
            _ => Cyan,
        };
        var brush = BrushOf(tone);
        var r = selected ? 7.5 : hover ? 6.5 : 5.5;
        if (selected)
        {
            var p = Pulse;
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(180 * (1 - p)), tone.R, tone.G, tone.B)), 1.6), at, r + 4 + p * 14, r + 4 + p * 14);
        }
        dc.DrawEllipse(BrushOf(Color.FromArgb(0x40, tone.R, tone.G, tone.B)), null, at, r + 5, r + 5);
        var dark = MarkerDark;
        switch (m.Kind)
        {
            case MapMarkerKind.Wanted:
                // an open ring: "somewhere I want to go"
                dc.DrawEllipse(dark, PenOf(tone, 2.2), at, r, r);
                break;
            case MapMarkerKind.Itinerary when m.Number is { } n:
                dc.DrawEllipse(brush, new Pen(dark, 1.4), at, r + 3.5, r + 3.5);
                var ft = new FormattedText(n.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10.5, dark, 1.25);
                dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
                break;
            case MapMarkerKind.Photo:
                dc.DrawRectangle(dark, PenOf(tone, 1.6), new Rect(at.X - r + 1, at.Y - r + 2, (r - 1) * 2, (r - 2) * 2));
                break;
            case MapMarkerKind.Search:
                dc.DrawLine(new Pen(brush, 1.4), new Point(at.X - 13, at.Y), new Point(at.X - 6, at.Y));
                dc.DrawLine(new Pen(brush, 1.4), new Point(at.X + 6, at.Y), new Point(at.X + 13, at.Y));
                dc.DrawLine(new Pen(brush, 1.4), new Point(at.X, at.Y - 13), new Point(at.X, at.Y - 6));
                dc.DrawLine(new Pen(brush, 1.4), new Point(at.X, at.Y + 6), new Point(at.X, at.Y + 13));
                dc.DrawEllipse(null, new Pen(brush, 1.6), at, r, r);
                break;
            default:
                dc.DrawEllipse(brush, MarkerDarkPen, at, r, r);
                if (m.Kind == MapMarkerKind.Planned) dc.DrawEllipse(dark, null, at, r * 0.38, r * 0.38);
                break;
        }
        if (m.IsFavorite) dc.DrawEllipse(null, PenOf(Gold, 1.4), at, r + 3, r + 3);
    }

    // Frozen brushes and pens shared by every marker (hundreds per frame on a large library), and the cluster counts' text.
    private static readonly Dictionary<Color, SolidColorBrush> Brushes_ = [];
    private static readonly Dictionary<(Color, double), Pen> Pens = [];
    private static readonly SolidColorBrush ClusterFill = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x14, 0x22)));
    private static readonly SolidColorBrush MarkerDark = Frozen(new SolidColorBrush(Color.FromRgb(0x06, 0x0B, 0x13)));
    private static readonly Pen MarkerDarkPen = Frozen(new Pen(MarkerDark, 1.4));
    private readonly Dictionary<int, FormattedText> _counts = [];

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }


    private static SolidColorBrush BrushOf(Color c)
    {
        if (!Brushes_.TryGetValue(c, out var b)) Brushes_[c] = b = Frozen(new SolidColorBrush(c));
        return b;
    }

    private static Pen PenOf(Color c, double width)
    {
        if (!Pens.TryGetValue((c, width), out var p)) Pens[(c, width)] = p = Frozen(new Pen(BrushOf(c), width));
        return p;
    }

    // ───────────────────────── navigation ─────────────────────────

    /// <summary>Animates to a position (and zoom).</summary>
    public void FlyTo(double lat, double lon, double? zoom = null)
    {
        if (!GeoMath.IsValid(lat, lon)) return;
        var targetZoom = zoom ?? Zoom;
        if (!Arcadia.UI.Motion.AnimationsEnabled) { Latitude = lat; Longitude = GeoMath.NormalizeLongitude(lon); Zoom = targetZoom; return; }
        var fromLat = Latitude;
        var fromLon = Longitude;
        var dLon = GeoMath.NormalizeLongitude(lon - fromLon);
        var fromZoom = Zoom;
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / 320);
            var e = 1 - Math.Pow(1 - t, 3);
            Latitude = fromLat + (lat - fromLat) * e;
            Longitude = GeoMath.NormalizeLongitude(fromLon + dLon * e);
            Zoom = fromZoom + (targetZoom - fromZoom) * e;
            if (t >= 1) timer.Stop();
        };
        timer.Start();
    }

    /// <summary>Frames all given points (or does nothing when there are none).</summary>
    public void Fit(IReadOnlyCollection<(double Lat, double Lon)> points, double maxZoom = 11)
    {
        var valid = points.Where(p => GeoMath.IsValid(p.Lat, p.Lon)).ToList();
        if (valid.Count == 0 || ActualWidth < 10) return;
        if (valid.Count == 1) { FlyTo(valid[0].Lat, valid[0].Lon, Math.Min(maxZoom, 9)); return; }
        var world = valid.Select(p => GeoMath.ToWorld(p.Lat, p.Lon)).ToList();
        double minX = world.Min(p => p.X), maxX = world.Max(p => p.X), minY = world.Min(p => p.Y), maxY = world.Max(p => p.Y);
        var spanX = Math.Max(maxX - minX, 1e-6);
        var spanY = Math.Max(maxY - minY, 1e-6);
        var zoom = Math.Log2(Math.Min((ActualWidth - 120) / (spanX * TileSize), (ActualHeight - 120) / (spanY * TileSize)));
        var (lat, lon) = GeoMath.FromWorld((minX + maxX) / 2, (minY + maxY) / 2);
        FlyTo(lat, lon, Math.Clamp(zoom, MinZoomLevel, maxZoom));
    }

    public void ZoomBy(double delta) => AnimateZoom(Zoom + delta, new Point(ActualWidth / 2, ActualHeight / 2));

    private void AnimateZoom(double target, Point pivot)
    {
        _zoomFrom = Zoom;
        _zoomTarget = Math.Clamp(target, MinZoomLevel, MaxZoomLevel);
        _zoomPivotX = pivot.X;
        _zoomPivotY = pivot.Y;
        _zoomStart = DateTime.UtcNow;
        _zoomTimer.Start();
    }

    private void OnZoomTick(object? sender, EventArgs e)
    {
        var t = Math.Min(1, (DateTime.UtcNow - _zoomStart).TotalMilliseconds / 160);
        var eased = 1 - Math.Pow(1 - t, 2);
        SetZoomAround(_zoomFrom + (_zoomTarget - _zoomFrom) * eased, new Point(_zoomPivotX, _zoomPivotY));
        if (t >= 1) _zoomTimer.Stop();
    }

    /// <summary>Changes the zoom keeping the geographic point under <paramref name="pivot"/> in place.</summary>
    private void SetZoomAround(double zoom, Point pivot)
    {
        var (lat, lon) = ToGeo(pivot);
        var (px, py) = GeoMath.ToWorld(lat, lon);
        Zoom = zoom;
        var w = WorldPixels;
        var cx = px - (pivot.X - ActualWidth / 2) / w;
        var cy = Math.Clamp(py - (pivot.Y - ActualHeight / 2) / w, 0, 1);
        var (clat, clon) = GeoMath.FromWorld(cx, cy);
        Latitude = clat;
        Longitude = GeoMath.NormalizeLongitude(clon);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var target = (_zoomTimer.IsEnabled ? _zoomTarget : Zoom) + e.Delta / 120.0 * 0.6;
        AnimateZoom(target, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2)
        {
            AnimateZoom(Zoom + 1, e.GetPosition(this));
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragCenter = Center;
        _dragged = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragStart is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            var d = p - start;
            if (!_dragged && Math.Abs(d.X) + Math.Abs(d.Y) < 4) return;
            _dragged = true;
            Cursor = Cursors.SizeAll;
            var w = WorldPixels;
            var cx = _dragCenter.X - d.X / w;
            var cy = Math.Clamp(_dragCenter.Y - d.Y / w, 0.02, 0.98);
            var (lat, lon) = GeoMath.FromWorld(cx, cy);
            Latitude = lat;
            Longitude = GeoMath.NormalizeLongitude(lon);
            return;
        }
        UpdateHover(p);
    }

    private void UpdateHover(Point p)
    {
        var (lat, lon) = ToGeo(p);
        var marker = HitMarker(p);
        var country = Countries?.At(lat, lon);
        SetValue(CursorTextKey, GeoMath.Format(lat, lon, Zoom < 6 ? 2 : 4) + (country != null ? "  ·  " + country.Name(LanguageCode) : string.Empty));
        var markerKey = marker.Marker?.Key;
        if (!Equals(markerKey, _hoverMarker) || _hoverCountry != country?.Code)
        {
            _hoverMarker = markerKey;
            _hoverCountry = country?.Code;
            ToolTip = marker.Cluster != null ? string.Join("\n", marker.Cluster.Take(8).Select(m => m.Label)) + (marker.Cluster.Count > 8 ? "\n…" : string.Empty) : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverCountry = null;
        _hoverMarker = null;
        SetValue(CursorTextKey, string.Empty);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var wasDrag = _dragged;
        _dragStart = null;
        _dragged = false;
        Cursor = Cursors.Hand;
        ReleaseMouseCapture();
        if (wasDrag) return;
        var hit = HitMarker(p);
        if (hit.Marker is { } m)
        {
            SelectedKey = m.Key;
            if (MarkerCommand?.CanExecute(m) == true) MarkerCommand.Execute(m);
            return;
        }
        if (hit.Cluster is { } cluster)
        {
            Fit(cluster.Select(c => (c.Latitude, c.Longitude)).ToList(), Math.Max(Zoom + 2, 7));
            return;
        }
        var (lat, lon) = ToGeo(p);
        var country = Countries?.At(lat, lon);
        var point = new MapPoint(lat, lon, country?.Code);
        if (PointCommand?.CanExecute(point) == true) PointCommand.Execute(point);
        if (country != null)
        {
            SelectedCountry = country.Code;
            if (CountryCommand?.CanExecute(country.Code) == true) CountryCommand.Execute(country.Code);
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var (lat, lon) = ToGeo(e.GetPosition(this));
        ContextPoint = new MapPoint(lat, lon, Countries?.At(lat, lon)?.Code);
        base.OnMouseRightButtonDown(e);
    }

    private (MapMarker? Marker, List<MapMarker>? Cluster) HitMarker(Point p)
    {
        for (int i = _hitTargets.Count - 1; i >= 0; i--)
            if (_hitTargets[i].Hit.Contains(p)) return (_hitTargets[i].Marker, _hitTargets[i].Cluster);
        return (null, null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var w = WorldPixels;
        var (cx, cy) = Center;
        void Pan(double dx, double dy)
        {
            var (lat, lon) = GeoMath.FromWorld(cx + dx / w, Math.Clamp(cy + dy / w, 0.02, 0.98));
            Latitude = lat;
            Longitude = GeoMath.NormalizeLongitude(lon);
        }
        switch (e.Key)
        {
            case Key.Left: Pan(-80, 0); break;
            case Key.Right: Pan(80, 0); break;
            case Key.Up: Pan(0, -80); break;
            case Key.Down: Pan(0, 80); break;
            case Key.OemPlus or Key.Add: ZoomBy(1); break;
            case Key.OemMinus or Key.Subtract: ZoomBy(-1); break;
            default: return;
        }
        e.Handled = true;
    }
}
