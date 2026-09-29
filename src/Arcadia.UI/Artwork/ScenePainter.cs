using System.Windows;
using System.Windows.Media;

namespace Arcadia.UI.Artwork;

/// <summary>
/// Deterministic landscape painter. Given a theme, a seed and a size it always produces the same picture,
/// so placeholder artwork is stable between launches and across layout changes.
/// </summary>
public static class ScenePainter
{
    public static void Paint(DrawingContext dc, Size size, SceneTheme t, int seed, double vignette, double bottomShade)
    {
        double w = size.Width, h = size.Height;
        if (w < 2 || h < 2) return;

        var rng = new Random(unchecked(seed * 7919 + 104729));
        dc.PushClip(new RectangleGeometry(new Rect(size)));

        double hy = h * t.HorizonY;
        PaintSky(dc, w, h, hy, t);
        if (t.Stars > 0) PaintStars(dc, w, hy, t, rng);
        if (t.Features.HasFlag(SceneFeature.Planet)) PaintNebulaAndPlanet(dc, w, h, t, rng);
        PaintSun(dc, w, h, t);
        if (t.Cloudiness > 0) PaintClouds(dc, w, hy, t, rng);

        double waterY = t.Water ? hy + (h - hy) * 0.12 : double.NaN;
        PaintRidges(dc, w, h, hy, waterY, t, rng);

        if (t.Features.HasFlag(SceneFeature.City)) PaintCity(dc, w, h, hy, t, rng);
        if (t.Water) PaintWater(dc, w, h, waterY, t, rng);
        if (t.Features.HasFlag(SceneFeature.Town)) PaintTownLights(dc, w, h, waterY, t, rng);
        if (t.Features.HasFlag(SceneFeature.Wanderer)) PaintWanderer(dc, w, h, t);

        PaintVignette(dc, w, h, vignette, bottomShade);
        dc.Pop();
    }

    // ───────────────────────────── helpers ─────────────────────────────

    internal static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    internal static Color Alpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static Brush Solid(Color c) => Frozen(new SolidColorBrush(c));

    private static Brush Vertical(double y0, double y1, params (double offset, Color color)[] stops)
    {
        var b = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, y0), EndPoint = new Point(0, y1) };
        foreach (var (o, c) in stops) b.GradientStops.Add(new GradientStop(c, o));
        return Frozen(b);
    }

    private static Brush Radial(Point center, double rx, double ry, params (double offset, Color color)[] stops)
    {
        var b = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = center, GradientOrigin = center, RadiusX = rx, RadiusY = ry };
        foreach (var (o, c) in stops) b.GradientStops.Add(new GradientStop(c, o));
        return Frozen(b);
    }

    /// <summary>1D fractal value noise in [0,1].</summary>
    private sealed class Noise1D
    {
        private readonly double[] _v = new double[256];
        private readonly double _offset;

        public Noise1D(Random rng)
        {
            for (int i = 0; i < _v.Length; i++) _v[i] = rng.NextDouble();
            _offset = rng.NextDouble() * 100;
        }

        private double Sample(double x)
        {
            x += _offset;
            int i = (int)Math.Floor(x);
            double f = x - i;
            f = f * f * (3 - 2 * f);
            return _v[i & 255] * (1 - f) + _v[(i + 1) & 255] * f;
        }

        public double Fractal(double x, int octaves, double persistence)
        {
            double sum = 0, amp = 1, norm = 0, freq = 1;
            for (int o = 0; o < octaves; o++)
            {
                sum += Sample(x * freq) * amp;
                norm += amp;
                amp *= persistence;
                freq *= 2.03;
            }
            return sum / norm;
        }
    }

    // ───────────────────────────── layers ─────────────────────────────

    private static void PaintSky(DrawingContext dc, double w, double h, double hy, SceneTheme t)
    {
        var sky = Vertical(0, h,
            (0, t.SkyTop),
            (hy / h * 0.55, t.SkyMid),
            (hy / h, t.Horizon),
            (1, Lerp(t.Horizon, t.RidgeNear, 0.6)));
        dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
    }

    private static void PaintStars(DrawingContext dc, double w, double hy, SceneTheme t, Random rng)
    {
        int count = (int)(w * hy / 700 * t.Stars);
        var groups = new[] { new GeometryGroup(), new GeometryGroup(), new GeometryGroup() };
        for (int i = 0; i < count; i++)
        {
            double y = Math.Pow(rng.NextDouble(), 1.6) * hy * 0.85;
            double x = rng.NextDouble() * w;
            double r = 0.35 + rng.NextDouble() * (rng.NextDouble() < 0.06 ? 1.1 : 0.55);
            groups[rng.Next(3)].Children.Add(new EllipseGeometry(new Point(x, y), r, r));
        }
        double[] alpha = [0.35, 0.6, 0.9];
        for (int i = 0; i < 3; i++) dc.DrawGeometry(Solid(Alpha(Colors.White, alpha[i])), null, groups[i]);
    }

    private static void PaintNebulaAndPlanet(DrawingContext dc, double w, double h, SceneTheme t, Random rng)
    {
        for (int i = 0; i < 5; i++)
        {
            var c = new Point(w * (0.1 + rng.NextDouble() * 0.8), h * (0.15 + rng.NextDouble() * 0.5));
            var color = i % 2 == 0 ? t.Haze : t.Accent;
            dc.DrawRectangle(Radial(c, w * (0.25 + rng.NextDouble() * 0.2), h * (0.15 + rng.NextDouble() * 0.2),
                (0, Alpha(color, 0.28)), (1, Alpha(color, 0))), null, new Rect(0, 0, w, h));
        }

        double r = Math.Min(w, h) * 0.34;
        var center = new Point(w * 0.68, h * 0.46);
        // atmosphere glow
        dc.DrawEllipse(Radial(center, r * 1.35, r * 1.35, (0.72, Alpha(t.Accent, 0.35)), (1, Alpha(t.Accent, 0))), null, center, r * 1.35, r * 1.35);
        var lit = new Point(center.X - r * 0.45, center.Y - r * 0.5);
        var body = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, Center = lit, GradientOrigin = lit, RadiusX = r * 1.6, RadiusY = r * 1.6,
            GradientStops =
            {
                new GradientStop(Lerp(t.Accent, Colors.White, 0.35), 0),
                new GradientStop(Lerp(t.Accent, t.SkyMid, 0.35), 0.35),
                new GradientStop(Lerp(t.SkyMid, Colors.Black, 0.55), 0.75),
                new GradientStop(Color.FromRgb(4, 5, 10), 1),
            },
        };
        body.Freeze();
        dc.DrawEllipse(body, null, center, r, r);

        // bands
        var clip = new EllipseGeometry(center, r, r);
        dc.PushClip(clip);
        for (int i = 0; i < 7; i++)
        {
            double y = center.Y - r + (i + 0.5) * (2 * r / 7) + (rng.NextDouble() - 0.5) * 6;
            dc.DrawRectangle(Solid(Alpha(Colors.Black, 0.08 + rng.NextDouble() * 0.08)), null, new Rect(center.X - r, y, 2 * r, r * 0.07 + rng.NextDouble() * r * 0.08));
        }
        dc.Pop();

        // ring
        var ring = new EllipseGeometry(center, r * 1.65, r * 0.32) { Transform = new RotateTransform(-14, center.X, center.Y) };
        var ringPen = new Pen(Solid(Alpha(Lerp(t.Accent, Colors.White, 0.5), 0.55)), Math.Max(1.2, r * 0.05));
        dc.DrawGeometry(null, ringPen, ring);
    }

    private static void PaintSun(DrawingContext dc, double w, double h, SceneTheme t)
    {
        var c = new Point(w * t.SunX, h * t.SunY);
        double glow = Math.Max(w, h) * 0.55;
        dc.DrawRectangle(Radial(c, glow, glow * 0.6,
            (0, Alpha(t.Sun, 0.5)), (0.22, Alpha(t.Sun, 0.18)), (1, Alpha(t.Sun, 0))), null, new Rect(0, 0, w, h));
        if (t.SunSize > 0)
        {
            double r = Math.Max(1.5, w * t.SunSize * 0.35);
            dc.DrawEllipse(Radial(c, r * 2.2, r * 2.2, (0, Alpha(Colors.White, 0.95)), (0.4, Alpha(t.Sun, 0.9)), (1, Alpha(t.Sun, 0))), null, c, r * 2.2, r * 2.2);
        }
    }

    private static void PaintClouds(DrawingContext dc, double w, double hy, SceneTheme t, Random rng)
    {
        int n = 3 + (int)(t.Cloudiness * 7);
        for (int i = 0; i < n; i++)
        {
            double y = hy * (0.15 + rng.NextDouble() * 0.7);
            double x = rng.NextDouble() * w;
            double rx = w * (0.12 + rng.NextDouble() * 0.25);
            double ry = hy * (0.025 + rng.NextDouble() * 0.05);
            double nearHorizon = y / hy;
            var tint = Lerp(t.SkyMid, t.Sun, nearHorizon * 0.8);
            dc.DrawEllipse(Radial(new Point(x, y), rx, ry, (0, Alpha(tint, 0.34 * t.Cloudiness + 0.1)), (1, Alpha(tint, 0))), null, new Point(x, y), rx, ry);
        }
    }

    private static void PaintRidges(DrawingContext dc, double w, double h, double hy, double waterY, SceneTheme t, Random rng)
    {
        int layers = Math.Max(1, t.Layers);
        bool blocks = t.Features.HasFlag(SceneFeature.Blocks);
        bool mesa = t.Features.HasFlag(SceneFeature.Mesa);
        bool water = !double.IsNaN(waterY);
        int castleLayer = layers >= 3 ? layers - 2 : 0;

        for (int i = 0; i < layers; i++)
        {
            double f = layers == 1 ? 1 : i / (double)(layers - 1);
            var noise = new Noise1D(rng);

            // With water, all ridges sit on the far shore; otherwise they step down towards the viewer.
            double baseY = water ? waterY + 1 : hy + f * (h - hy) * 0.55;
            double amp = h * (water ? (0.36 - f * 0.26) : (0.34 - f * 0.19)) * (t.Features.HasFlag(SceneFeature.Planet) ? 0.35 : 1);
            double freq = 1.6 + i * 0.9 + t.Roughness * 1.5;
            int octaves = 3 + (int)(t.Roughness * 3);
            double step = blocks ? Math.Max(4, w / 46) : Math.Max(1.5, w / 360);

            var pts = new List<Point>();
            for (double x = -step; x <= w + step; x += step)
            {
                // Sample in height units so mountains keep their proportions on very wide banners.
                double n = noise.Fractal(x / (h * 2.4) * freq, octaves, 0.5);
                if (i < layers - 1 && !mesa && !blocks) n = 0.35 * n + 0.65 * (1 - Math.Abs(2 * n - 1)); // sharper far peaks
                if (mesa) n = n > 0.52 ? 0.62 + (n - 0.52) * 0.15 : n * 0.55 + Math.Max(0, n - 0.46) * 3;
                double y = baseY - amp * n;
                if (blocks) y = Math.Round(y / step) * step;
                if (blocks && pts.Count > 0) pts.Add(new Point(x, pts[^1].Y));
                pts.Add(new Point(x, y));
            }

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(-step, h + 2), true, true);
                foreach (var p in pts) ctx.LineTo(p, true, false);
                ctx.LineTo(new Point(w + step, h + 2), true, false);
            }
            geo.Freeze();

            var col = Lerp(t.RidgeFar, t.RidgeNear, Math.Pow(f, 0.75));
            double top = baseY - amp;
            var fill = Vertical(top, baseY + (h - baseY) * 0.5,
                (0, Lerp(col, t.Haze, 0.16 * (1 - f))),
                (0.5, col),
                (1, Lerp(col, Colors.Black, 0.45)));
            dc.DrawGeometry(fill, null, geo);

            // Sun-facing slopes catch light: a horizontal wash centred on the sun.
            var lit = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(w, 0) };
            lit.GradientStops.Add(new GradientStop(Alpha(t.Sun, 0), Math.Max(0, t.SunX - 0.45)));
            lit.GradientStops.Add(new GradientStop(Alpha(t.Sun, 0.16 * (1 - f * 0.8)), t.SunX));
            lit.GradientStops.Add(new GradientStop(Alpha(t.Sun, 0), Math.Min(1, t.SunX + 0.45)));
            lit.Freeze();
            dc.DrawGeometry(lit, null, geo);

            // Rim light where the sun grazes the ridge line
            var rim = new StreamGeometry();
            using (var ctx = rim.Open())
            {
                ctx.BeginFigure(pts[0], false, false);
                for (int k = 1; k < pts.Count; k++) ctx.LineTo(pts[k], true, false);
            }
            rim.Freeze();
            var rimBrush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(w, 0) };
            rimBrush.GradientStops.Add(new GradientStop(Alpha(t.Sun, 0.05), 0));
            rimBrush.GradientStops.Add(new GradientStop(Alpha(t.Sun, 0.55 * (1 - f * 0.7)), t.SunX));
            rimBrush.GradientStops.Add(new GradientStop(Alpha(t.Sun, 0.05), 1));
            rimBrush.Freeze();
            dc.DrawGeometry(null, new Pen(rimBrush, blocks ? 1.5 : 1), rim);

            if (blocks) PaintBlockDetail(dc, pts, h, step, col, f, rng);

            // Valley haze between layers
            if (i < layers - 1 && !water)
            {
                double hazeTop = baseY - amp * 0.3;
                dc.DrawRectangle(Vertical(hazeTop, baseY + 2, (0, Alpha(t.Haze, 0)), (1, Alpha(t.Haze, 0.2 * (1 - f)))), null,
                    new Rect(0, hazeTop, w, Math.Max(0, baseY + 2 - hazeTop)));
            }

            if (t.Features.HasFlag(SceneFeature.Castle) && i == castleLayer) PaintCastle(dc, w, h, pts, t, col, rng);
            if (t.Features.HasFlag(SceneFeature.Forest) && i >= layers - 2) PaintForest(dc, pts, h, w, col, f, rng);
        }
    }

    private static void PaintBlockDetail(DrawingContext dc, List<Point> pts, double h, double step, Color col, double f, Random rng)
    {
        // Grass cap and stone grain for the voxel look.
        var cap = new GeometryGroup();
        var grain = new GeometryGroup();
        for (int k = 0; k < pts.Count - 1; k += 2)
        {
            var p = pts[k];
            cap.Children.Add(new RectangleGeometry(new Rect(p.X, p.Y, step, step * 0.35)));
            for (double y = p.Y + step; y < h; y += step)
                if (rng.NextDouble() < 0.12) grain.Children.Add(new RectangleGeometry(new Rect(p.X, y, step, step)));
        }
        dc.DrawGeometry(Solid(Alpha(Lerp(col, Color.FromRgb(0x7C, 0xD0, 0x5A), 0.6 - f * 0.2), 0.8)), null, cap);
        dc.DrawGeometry(Solid(Alpha(Colors.Black, 0.12)), null, grain);
    }

    private static void PaintCastle(DrawingContext dc, double w, double h, List<Point> ridge, SceneTheme t, Color layerColor, Random rng)
    {
        // Find the highest point near the sun side of the frame.
        double targetX = w * (t.SunX > 0.5 ? 0.62 : 0.38);
        var candidates = ridge.Where(p => Math.Abs(p.X - targetX) < w * 0.16).ToList();
        if (candidates.Count == 0) return;
        var peak = candidates.MinBy(p => p.Y);
        double s = Math.Min(h * 0.36, w * 0.2);
        double baseY = peak.Y + s * 0.18;
        double cx = peak.X;

        var body = new GeometryGroup { FillRule = FillRule.Nonzero };
        var windows = new GeometryGroup();
        void Tower(double x, double width, double height, bool spire)
        {
            body.Children.Add(new RectangleGeometry(new Rect(x - width / 2, baseY - height, width, height + s)));
            if (spire)
            {
                var roof = new StreamGeometry();
                using (var ctx = roof.Open())
                {
                    ctx.BeginFigure(new Point(x - width * 0.62, baseY - height), true, true);
                    ctx.LineTo(new Point(x, baseY - height - width * 1.7), true, false);
                    ctx.LineTo(new Point(x + width * 0.62, baseY - height), true, false);
                }
                body.Children.Add(roof);
            }
            else
            {
                double m = width / 5;
                for (int k = 0; k < 3; k++)
                    body.Children.Add(new RectangleGeometry(new Rect(x - width / 2 + k * 2 * m, baseY - height - m, m, m + 0.5)));
            }
            if (rng.NextDouble() < 0.8)
                windows.Children.Add(new RectangleGeometry(new Rect(x - width * 0.12, baseY - height * (0.45 + rng.NextDouble() * 0.3), Math.Max(1, width * 0.22), Math.Max(1.2, width * 0.35))));
        }

        body.Children.Add(new RectangleGeometry(new Rect(cx - s * 0.55, baseY - s * 0.28, s * 1.1, s)));
        Tower(cx, s * 0.2, s * 0.72, true);
        Tower(cx - s * 0.36, s * 0.14, s * 0.48, true);
        Tower(cx + s * 0.34, s * 0.16, s * 0.56, false);
        Tower(cx - s * 0.58, s * 0.12, s * 0.34, false);
        Tower(cx + s * 0.6, s * 0.11, s * 0.4, true);
        Tower(cx + s * 0.12, s * 0.1, s * 0.9, true);

        var castleColor = Lerp(layerColor, Colors.Black, 0.25);
        dc.DrawGeometry(Solid(castleColor), null, body);
        dc.DrawGeometry(Solid(Alpha(t.Accent, 0.9)), null, windows);
        dc.DrawGeometry(Solid(Alpha(t.Accent, 0.25)), new Pen(Solid(Alpha(t.Accent, 0.15)), 2), windows);
    }

    private static void PaintForest(DrawingContext dc, List<Point> ridge, double h, double w, Color layerColor, double f, Random rng)
    {
        var trees = new StreamGeometry();
        double scale = h * (0.05 + f * 0.07);
        using (var ctx = trees.Open())
        {
            double x = -scale;
            while (x < w + scale)
            {
                x += scale * (0.18 + rng.NextDouble() * 0.45);
                if (rng.NextDouble() < 0.18) { x += scale * rng.NextDouble() * 1.5; continue; }
                int idx = Math.Clamp((int)((x + 2) / (w + 4) * ridge.Count), 0, ridge.Count - 1);
                double gy = ridge[idx].Y + scale * 0.25;
                double th = scale * (0.6 + rng.NextDouble() * 0.8);
                double tw = th * 0.32;
                for (int tier = 0; tier < 3; tier++)
                {
                    double top = gy - th + tier * th * 0.26;
                    double bottom = gy - th * 0.35 + tier * th * 0.3;
                    double half = tw * (0.55 + tier * 0.3);
                    ctx.BeginFigure(new Point(x, top), true, true);
                    ctx.LineTo(new Point(x + half, bottom), true, false);
                    ctx.LineTo(new Point(x - half, bottom), true, false);
                }
            }
        }
        trees.Freeze();
        dc.DrawGeometry(Solid(Lerp(layerColor, Colors.Black, 0.2)), null, trees);
    }

    private static void PaintCity(DrawingContext dc, double w, double h, double hy, SceneTheme t, Random rng)
    {
        double baseY = hy + (h - hy) * 0.12;
        var near = new GeometryGroup();
        var far = new GeometryGroup();
        var lit = new[] { new GeometryGroup(), new GeometryGroup(), new GeometryGroup() };
        Color[] litColors = [t.Accent, Color.FromRgb(0xFF, 0x4F, 0xB0), Color.FromRgb(0xFC, 0xE9, 0x4F)];
        var neon = new List<(Rect rect, Color color)>();

        for (int pass = 0; pass < 2; pass++)
        {
            double x = -10;
            while (x < w)
            {
                double bw = w * (0.025 + rng.NextDouble() * 0.045);
                double bh = (h - (h - baseY)) * (pass == 0 ? 0.25 + rng.NextDouble() * 0.45 : 0.12 + rng.NextDouble() * 0.35);
                if (rng.NextDouble() < 0.08) bh *= 1.5;
                var r = new Rect(x, baseY - bh, bw, bh + 2);
                (pass == 0 ? far : near).Children.Add(new RectangleGeometry(r));
                if (pass == 1)
                {
                    double cell = Math.Max(2, bw / 6);
                    for (double wy = r.Top + cell; wy < baseY - cell; wy += cell * 1.8)
                        for (double wx = r.Left + cell * 0.6; wx < r.Right - cell; wx += cell * 1.6)
                            if (rng.NextDouble() < 0.28) lit[rng.Next(3)].Children.Add(new RectangleGeometry(new Rect(wx, wy, cell * 0.7, cell * 0.8)));
                    if (rng.NextDouble() < 0.25)
                        neon.Add((new Rect(r.Left + bw * 0.4, r.Top + bh * 0.15, Math.Max(1.5, bw * 0.12), bh * 0.35), litColors[rng.Next(2)]));
                }
                x += bw + (pass == 0 ? 0 : rng.NextDouble() * bw * 0.15);
            }
        }

        dc.DrawGeometry(Solid(Lerp(t.RidgeFar, t.Horizon, 0.18)), null, far);
        dc.DrawRectangle(Vertical(hy - h * 0.2, baseY, (0, Alpha(t.Horizon, 0)), (1, Alpha(t.Horizon, 0.35))), null, new Rect(0, hy - h * 0.2, w, baseY - hy + h * 0.2));
        dc.DrawGeometry(Solid(t.RidgeNear), null, near);
        for (int i = 0; i < 3; i++) dc.DrawGeometry(Solid(Alpha(litColors[i], 0.85)), null, lit[i]);
        foreach (var (rect, color) in neon)
        {
            dc.DrawRectangle(Solid(Alpha(color, 0.25)), null, Rect.Inflate(rect, 3, 3));
            dc.DrawRectangle(Solid(color), null, rect);
        }
    }

    private static void PaintWater(DrawingContext dc, double w, double h, double waterY, SceneTheme t, Random rng)
    {
        var water = Vertical(waterY, h,
            (0, Lerp(t.Horizon, t.SkyMid, 0.35)),
            (0.25, Lerp(t.SkyMid, t.RidgeNear, 0.4)),
            (1, Lerp(t.SkyTop, Colors.Black, 0.55)));
        dc.DrawRectangle(water, null, new Rect(0, waterY, w, h - waterY));

        // Glitter path beneath the sun
        double sx = w * t.SunX;
        var glitter = new GeometryGroup();
        for (int i = 0; i < 70; i++)
        {
            double d = rng.NextDouble();
            double y = waterY + 2 + d * d * (h - waterY);
            double spread = w * (0.03 + d * 0.16);
            double x = sx + (rng.NextDouble() - 0.5) * 2 * spread;
            double len = w * (0.01 + rng.NextDouble() * 0.04) * (1 - d * 0.5);
            glitter.Children.Add(new RectangleGeometry(new Rect(x - len / 2, y, len, 0.8 + d)));
        }
        dc.DrawGeometry(Solid(Alpha(t.Sun, 0.45)), null, glitter);

        // Subtle horizontal ripples
        var ripples = new GeometryGroup();
        for (int i = 0; i < 40; i++)
        {
            double y = waterY + rng.NextDouble() * (h - waterY);
            double x = rng.NextDouble() * w;
            ripples.Children.Add(new RectangleGeometry(new Rect(x, y, w * (0.05 + rng.NextDouble() * 0.15), 0.7)));
        }
        dc.DrawGeometry(Solid(Alpha(Colors.White, 0.05)), null, ripples);
        dc.DrawRectangle(Solid(Alpha(t.Sun, 0.25)), null, new Rect(0, waterY, w, 1));
    }

    private static void PaintTownLights(DrawingContext dc, double w, double h, double waterY, SceneTheme t, Random rng)
    {
        if (double.IsNaN(waterY)) waterY = h * t.HorizonY;
        var houses = new GeometryGroup();
        var lights = new GeometryGroup();
        double start = w * 0.36, end = w * 0.5;
        for (int i = 0; i < 38; i++)
        {
            double x = start + rng.NextDouble() * (end - start);
            double bh = h * (0.012 + rng.NextDouble() * 0.03);
            double bw = w * (0.004 + rng.NextDouble() * 0.008);
            houses.Children.Add(new RectangleGeometry(new Rect(x, waterY - bh, bw, bh + 1)));
            if (rng.NextDouble() < 0.7) lights.Children.Add(new EllipseGeometry(new Point(x + bw / 2, waterY - bh * 0.5), 0.9, 0.9));
        }
        // spire
        houses.Children.Add(new RectangleGeometry(new Rect(w * 0.42, waterY - h * 0.09, Math.Max(2, h * 0.02), h * 0.09)));
        dc.DrawGeometry(Solid(Lerp(t.RidgeNear, t.RidgeFar, 0.25)), null, houses);
        dc.DrawGeometry(Solid(Alpha(t.Accent, 0.3)), new Pen(Solid(Alpha(t.Accent, 0.2)), 3), lights);
        dc.DrawGeometry(Solid(t.Accent), null, lights);
    }

    private static void PaintWanderer(DrawingContext dc, double w, double h, SceneTheme t)
    {
        // A cliff in the foreground with a lone traveller resting and looking over the valley.
        double cx = w * 0.62, top = h * 0.6, u = h; // horizontal extents in height units
        var cliff = new StreamGeometry();
        using (var ctx = cliff.Open())
        {
            ctx.BeginFigure(new Point(cx - 0.95 * u, h + 2), true, true);
            ctx.BezierTo(new Point(cx - 0.7 * u, h * 0.86), new Point(cx - 0.45 * u, top + h * 0.05), new Point(cx - 0.18 * u, top + h * 0.01), true, true);
            ctx.LineTo(new Point(cx + 0.28 * u, top - h * 0.005), true, true);
            ctx.BezierTo(new Point(cx + 0.55 * u, top + h * 0.03), new Point(cx + 0.8 * u, h * 0.84), new Point(cx + 1.25 * u, h + 2), true, true);
        }
        cliff.Freeze();
        var cliffFill = Vertical(top, h, (0, Lerp(t.RidgeNear, t.RidgeFar, 0.18)), (1, t.RidgeNear));
        dc.DrawGeometry(cliffFill, null, cliff);
        dc.DrawGeometry(null, new Pen(Solid(Alpha(t.Sun, 0.35)), 1), cliff);

        double s = h * 0.42;
        double fx = cx + u * 0.02, fy = top - h * 0.002;
        var figure = new GeometryGroup { FillRule = FillRule.Nonzero };
        // backpack
        figure.Children.Add(new RectangleGeometry(new Rect(fx + s * 0.1, fy - s * 0.62, s * 0.26, s * 0.4), s * 0.06, s * 0.06));
        // torso (leaning forward slightly)
        var torso = new StreamGeometry();
        using (var ctx = torso.Open())
        {
            ctx.BeginFigure(new Point(fx - s * 0.12, fy - s * 0.2), true, true);
            ctx.LineTo(new Point(fx - s * 0.08, fy - s * 0.6), true, false);
            ctx.LineTo(new Point(fx + s * 0.14, fy - s * 0.64), true, false);
            ctx.LineTo(new Point(fx + s * 0.14, fy - s * 0.15), true, false);
        }
        figure.Children.Add(torso);
        // head + hood
        figure.Children.Add(new EllipseGeometry(new Point(fx - s * 0.02, fy - s * 0.73), s * 0.1, s * 0.11));
        // legs extended over the edge
        var legs = new StreamGeometry();
        using (var ctx = legs.Open())
        {
            ctx.BeginFigure(new Point(fx - s * 0.12, fy - s * 0.22), true, true);
            ctx.LineTo(new Point(fx - s * 0.46, fy - s * 0.12), true, false);
            ctx.LineTo(new Point(fx - s * 0.5, fy + s * 0.12), true, false);
            ctx.LineTo(new Point(fx - s * 0.42, fy + s * 0.12), true, false);
            ctx.LineTo(new Point(fx - s * 0.38, fy - s * 0.02), true, false);
            ctx.LineTo(new Point(fx + s * 0.1, fy - s * 0.02), true, false);
        }
        figure.Children.Add(legs);
        // walking staff
        figure.Children.Add(new RectangleGeometry(new Rect(fx - s * 0.3, fy - s * 0.95, s * 0.025, s * 0.95)) { Transform = new RotateTransform(-12, fx - s * 0.3, fy) });

        dc.DrawGeometry(Solid(Color.FromRgb(6, 8, 12)), new Pen(Solid(Alpha(t.Sun, 0.3)), 0.8), figure);
    }

    private static void PaintVignette(DrawingContext dc, double w, double h, double vignette, double bottomShade)
    {
        if (vignette > 0)
        {
            var center = new Point(w / 2, h * 0.45);
            dc.DrawRectangle(Radial(center, w * 0.75, h * 0.85,
                (0.55, Alpha(Colors.Black, 0)), (1, Alpha(Colors.Black, 0.75 * vignette))), null, new Rect(0, 0, w, h));
        }
        if (bottomShade > 0)
        {
            dc.DrawRectangle(Vertical(h * 0.45, h, (0, Alpha(Colors.Black, 0)), (1, Alpha(Colors.Black, 0.85 * bottomShade))), null, new Rect(0, h * 0.45, w, h * 0.55));
        }
    }
}
