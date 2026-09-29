using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Arcadia.IconForge;

/// <summary>
/// Generates Arcadia's vector icon set from geometric primitives on a 24×24 grid.
/// Every icon has a primary shape and an optional accent shape (rendered in a second tone),
/// which gives the set its duotone, "machined" desktop-software character.
///
/// Usage: dotnet run --project tools/Arcadia.IconForge -- &lt;repoRoot&gt; [previewPng]
/// Outputs: src/Arcadia.UI/Themes/Icons.xaml and src/Arcadia.App/Assets/arcadia.ico
/// </summary>
internal static class Program
{
    private sealed record Icon(string Name, Geometry Primary, Geometry? Accent = null);

    [STAThread]
    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        var icons = BuildIcons();

        var xamlPath = Path.Combine(root, "src", "Arcadia.UI", "Themes", "Icons.xaml");
        File.WriteAllText(xamlPath, ToXaml(icons), new UTF8Encoding(false));
        Console.WriteLine($"Wrote {icons.Count} icons to {xamlPath}");

        var icoPath = Path.Combine(root, "src", "Arcadia.App", "Assets", "arcadia.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
        WriteAppIcon(icons.Single(i => i.Name == "Logo"), icoPath);
        Console.WriteLine($"Wrote {icoPath}");

        if (args.Length > 1)
        {
            WritePreview(icons, args[1]);
            Console.WriteLine($"Wrote preview {args[1]}");
        }
        return 0;
    }

    // ───────────────────────────── primitives ─────────────────────────────

    private static Geometry R(double x, double y, double w, double h, double r = 0) => new RectangleGeometry(new Rect(x, y, w, h), r, r);
    private static Geometry C(double cx, double cy, double r) => new EllipseGeometry(new Point(cx, cy), r, r);

    private static Geometry E(double cx, double cy, double rx, double ry, double rotation = 0)
    {
        var g = new EllipseGeometry(new Point(cx, cy), rx, ry);
        if (rotation != 0) g.Transform = new RotateTransform(rotation, cx, cy);
        return g;
    }

    private static Geometry P(params double[] xy)
    {
        var fig = new PathFigure { StartPoint = new Point(xy[0], xy[1]), IsClosed = true, IsFilled = true };
        for (int i = 2; i < xy.Length; i += 2) fig.Segments.Add(new LineSegment(new Point(xy[i], xy[i + 1]), true));
        return new PathGeometry([fig]);
    }

    private static Geometry S(string data) => Geometry.Parse(data);

    private static Pen RoundPen(double width) => new(Brushes.Black, width)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    private static Geometry Line(double x1, double y1, double x2, double y2, double width) =>
        new LineGeometry(new Point(x1, y1), new Point(x2, y2)).GetWidenedPathGeometry(RoundPen(width));

    private static Geometry Polyline(double width, params double[] xy)
    {
        var fig = new PathFigure { StartPoint = new Point(xy[0], xy[1]), IsClosed = false, IsFilled = false };
        for (int i = 2; i < xy.Length; i += 2) fig.Segments.Add(new LineSegment(new Point(xy[i], xy[i + 1]), true));
        return new PathGeometry([fig]).GetWidenedPathGeometry(RoundPen(width));
    }

    private static Geometry Stroke(Geometry g, double width) => g.GetWidenedPathGeometry(RoundPen(width));

    /// <summary>Grows a shape outward by width/2 with round corners (used for gaps and soft corners).</summary>
    private static Geometry Grow(Geometry g, double width) => U(g, Stroke(g, width));

    private static Geometry U(params Geometry[] parts)
    {
        Geometry acc = Geometry.Empty;
        foreach (var p in parts) acc = Geometry.Combine(acc, p, GeometryCombineMode.Union, null);
        return acc;
    }

    private static Geometry X(Geometry a, params Geometry[] cut) => Geometry.Combine(a, U(cut), GeometryCombineMode.Exclude, null);

    private static Geometry Rot(Geometry g, double angle, double cx = 12, double cy = 12) =>
        Geometry.Combine(Geometry.Empty, g, GeometryCombineMode.Union, new RotateTransform(angle, cx, cy));

    // ───────────────────────────── the set ─────────────────────────────

    private static List<Icon> BuildIcons()
    {
        var list = new List<Icon>();

        // Home — house with a roof band and a lit doorway.
        list.Add(new("Home",
            X(P(12, 2.6, 22.4, 11.6, 20.2, 11.6, 20.2, 21, 3.8, 21, 3.8, 11.6, 1.6, 11.6),
              R(9.8, 13.6, 4.4, 8, 1.2),
              Polyline(1.1, 4.6, 12.1, 12, 5.6, 19.4, 12.1)),
            R(10.7, 14.5, 2.6, 6.5, 0.6)));

        // Games — gamepad with cut-out d-pad; face buttons glow.
        var padBody = U(R(4, 6.8, 16, 9.2, 4.6), E(6.3, 15.1, 3.2, 4.6, 26), E(17.7, 15.1, 3.2, 4.6, -26));
        var dpad = U(R(5.4, 10.4, 4.6, 1.7, 0.4), R(6.85, 8.95, 1.7, 4.6, 0.4));
        list.Add(new("Games",
            X(padBody, dpad, C(16, 9.9, 1.15), C(18.2, 12.1, 1.15), R(10.9, 9.1, 2.2, 1.1, 0.55)),
            U(C(16, 9.9, 0.75), C(18.2, 12.1, 0.75))));

        // IT Tools — crossed wrench and screwdriver.
        var wrench = Rot(X(U(C(12, 6, 4.6), R(10.5, 8, 3, 13.6, 1.5)), R(10.6, -1, 2.8, 7.4, 0.3), C(12, 19.3, 0.85)), 45);
        var shaft = Rot(U(R(11.3, 2.4, 1.4, 11.4, 0.5), P(11.3, 2.6, 12.7, 2.6, 12, 1.2)), -45);
        var grip = Rot(R(10, 13.2, 4, 8.8, 1.8), -45);
        list.Add(new("ITTools", X(U(wrench, shaft), Grow(grip, 1.4)), grip));

        // Projects — clipboard with checklist.
        var board = R(4.4, 3.8, 15.2, 18.4, 2.2);
        var clip = R(8.3, 1.6, 7.4, 4.2, 1.4);
        var rows = new List<Geometry>();
        foreach (var (y, len) in new[] { (9.2, 6.4), (13.4, 6.4), (17.6, 4.4) })
        {
            rows.Add(R(7, y, 2.3, 2.3, 0.5));
            rows.Add(R(10.9, y + 0.45, len, 1.4, 0.7));
        }
        list.Add(new("Projects", X(board, [.. rows, Grow(clip, 1.3)]), clip));

        // Documents — folder with a sheet of paper and a lighter front flap.
        var back = U(R(2.4, 4.2, 8.2, 4.4, 1.2), R(2.4, 6.2, 19.2, 14.6, 1.8));
        var paper = R(5.2, 7.6, 12.6, 6, 0.6);
        var flap = Grow(P(4.9, 10.6, 22, 10.6, 20.3, 20.4, 2.9, 20.4), 0.8);
        list.Add(new("Documents", X(U(X(back, Grow(paper, 1.2)), paper), Grow(flap, 1.4)), flap));

        // Travel — globe with graticule.
        var graticule = U(Stroke(E(12, 12, 4.3, 9.6), 1.25), Line(1, 12, 23, 12, 1.25), Line(12, 1, 12, 23, 1.25),
            Line(2, 7.3, 22, 7.3, 1.05), Line(2, 16.7, 22, 16.7, 1.05));
        list.Add(new("Travel", X(C(12, 12, 9.8), graticule)));

        // ── Travel set ──
        // Compass — a thin ring around a four-point rose; the north point glows.
        var rose = P(12, 2.6, 14.1, 9.9, 21.4, 12, 14.1, 14.1, 12, 21.4, 9.9, 14.1, 2.6, 12, 9.9, 9.9);
        var north = P(12, 2.6, 14.1, 9.9, 12, 12, 9.9, 9.9);
        list.Add(new("Compass", U(X(C(12, 12, 10.6), C(12, 12, 9.2)), X(rose, Grow(north, 0.9), C(12, 12, 1.1))), north));
        // Map — three folded panels with a route.
        var panels = U(P(2, 5, 8, 3, 8, 19, 2, 21), P(8.8, 3, 15.2, 5, 15.2, 21, 8.8, 19), P(16, 5, 22, 3, 22, 19, 16, 21));
        list.Add(new("Map", X(panels, Polyline(1.3, 4.5, 16, 7, 11, 11.5, 14.5, 13.5, 9, 19.5, 11.5)), C(19.5, 11.5, 1.6)));
        // Mountain — two peaks with a snow cap.
        list.Add(new("Mountain", X(U(P(1.5, 20, 9.5, 5, 17.5, 20), P(10, 20, 16, 10, 22.5, 20)), P(9.5, 5, 7.4, 9, 8.6, 8.2, 9.5, 9.4, 10.5, 8.2, 11.6, 9)),
            P(9.5, 5, 7.4, 9, 8.6, 8.2, 9.5, 9.4, 10.5, 8.2, 11.6, 9)));
        // Suitcase — a trip.
        var caseBody = R(3, 7, 18, 13, 2.2);
        list.Add(new("Suitcase", U(X(caseBody, R(7.6, 7, 1.3, 13), R(15.1, 7, 1.3, 13)), X(R(8.4, 3.2, 7.2, 5, 1.6), R(10, 4.8, 4, 3))),
            U(R(7.6, 7.6, 1.3, 11.8, 0.4), R(15.1, 7.6, 1.3, 11.8, 0.4))));
        list.Add(new("Minus", R(4, 10.9, 16, 2.2, 1.1)));
        // Layers — stacked map sheets.
        list.Add(new("Layers", U(X(P(12, 3, 22, 8.5, 12, 14, 2, 8.5), P(12, 5.4, 17.6, 8.5, 12, 11.6, 6.4, 8.5)), Polyline(1.6, 2.8, 12.4, 12, 17.4, 21.2, 12.4),
            Polyline(1.6, 2.8, 16.2, 12, 21.2, 21.2, 16.2)), P(12, 5.4, 17.6, 8.5, 12, 11.6, 6.4, 8.5)));
        // Crosshair — locate / centre.
        list.Add(new("Crosshair", U(X(C(12, 12, 8), C(12, 12, 6.4)), R(11.2, 1.5, 1.6, 5, 0.8), R(11.2, 17.5, 1.6, 5, 0.8), R(1.5, 11.2, 5, 1.6, 0.8), R(17.5, 11.2, 5, 1.6, 0.8)),
            C(12, 12, 2.4)));
        // Bed — accommodation.
        list.Add(new("Bed", U(R(2, 5, 2, 15, 0.8), R(2, 13, 20, 3.4, 0.8), R(20, 11, 2, 9, 0.8), R(9, 9, 11, 4.4, 1.6)), C(6.4, 10.6, 2)));
        // Food — fork and knife.
        list.Add(new("Food", U(R(6.2, 11, 1.8, 11, 0.9), P(3.8, 2.5, 4.9, 2.5, 5.2, 7, 6.2, 7, 6.2, 2.5, 8, 2.5, 8, 7, 9, 7, 9.3, 2.5, 10.4, 2.5, 10.4, 8.4, 8.6, 11.2, 5.6, 11.2, 3.8, 8.4),
            R(15.6, 12, 1.8, 10, 0.9)), P(15.6, 12.6, 15.6, 2.5, 19.6, 6.5, 19.6, 12.6)));
        // Landmark — classical façade (museums, monuments).
        list.Add(new("Landmark", U(P(12, 2, 22, 7.2, 2, 7.2), R(2.5, 18.6, 19, 2.6, 0.5), R(4.2, 9, 2.4, 8.4), R(9, 9, 2.4, 8.4), R(13.6, 9, 2.4, 8.4), R(17.4, 9, 2.4, 8.4)),
            C(12, 5.3, 1.2)));
        // Wave — water, lakes and beaches.
        list.Add(new("Wave", U(Stroke(S("M2,9 C5,6 7,6 10,9 C13,12 15,12 18,9 C19.5,7.5 21,7 22,7"), 1.9), Stroke(S("M2,15 C5,12 7,12 10,15 C13,18 15,18 18,15 C19.5,13.5 21,13 22,13"), 1.9)),
            C(19.5, 4, 2)));
        // Tree — parks and nature.
        list.Add(new("Tree", U(P(12, 1.5, 19, 11, 16, 11, 21, 17, 3, 17, 8, 11, 5, 11), R(10.8, 16, 2.4, 6, 0.6)), P(12, 1.5, 15.2, 5.8, 8.8, 5.8)));
        // Binoculars — viewpoints.
        list.Add(new("Binoculars", U(C(6.6, 16, 4.6), C(17.4, 16, 4.6), R(4.4, 5, 4.6, 10, 1.4), R(15, 5, 4.6, 10, 1.4), R(9, 8, 6, 4.4, 1)), U(C(6.6, 16, 2.4), C(17.4, 16, 2.4))));
        // City — skyline.
        list.Add(new("City", X(U(R(2, 10, 6, 12, 0.6), R(8.6, 3, 7, 19, 0.6), R(16.2, 7, 6, 15, 0.6)), R(10.6, 6, 1.4, 1.6), R(12.8, 6, 1.4, 1.6), R(10.6, 9.6, 1.4, 1.6), R(12.8, 9.6, 1.4, 1.6),
            R(3.8, 13, 1.4, 1.6), R(18, 10, 1.4, 1.6), R(18, 13.6, 1.4, 1.6)), R(10.6, 16, 3.6, 6, 0.4)));
        // Timeline — a line of moments.
        list.Add(new("Timeline", U(R(5.2, 2, 1.6, 20, 0.8), R(9.4, 4.4, 11, 2.4, 1.2), R(9.4, 10.8, 8, 2.4, 1.2), R(9.4, 17.2, 10, 2.4, 1.2)),
            U(C(6, 5.6, 2.3), C(6, 12, 2.3), C(6, 18.4, 2.3))));

        // ── Phase 6: organizer ──
        // Repeat — a ring broken top and bottom with two arrowheads (recurring events).
        list.Add(new("Repeat", X(X(C(12, 12, 8.6), C(12, 12, 6.6)), U(R(9.4, 1, 5.2, 5, 0), R(9.4, 18, 5.2, 5, 0))),
            U(P(13.2, 1.2, 13.2, 8.2, 18.2, 4.7), P(10.8, 15.8, 10.8, 22.8, 5.8, 19.3))));
        // Automation — a closed control loop (two arrow heads) around a bolt: trigger → action, locally.
        list.Add(new("Automation",
            U(X(X(C(12, 12, 10), C(12, 12, 8)), U(R(0, 10.2, 5, 3.6, 0), R(19, 10.2, 5, 3.6, 0))),
              P(1.6, 9.6, 5.8, 13.4, 9.6, 9.6), P(14.4, 14.4, 18.2, 10.6, 22.4, 14.4)),
            P(13.2, 4.8, 7.8, 13, 11.4, 13, 10.4, 19.2, 16.2, 10.6, 12.6, 10.6)));
        // Link — two interlocked chain links.
        list.Add(new("Link", U(Rot(X(R(1.8, 8.6, 11.6, 6.8, 3.4), R(3.8, 10.4, 7.6, 3.2, 1.6)), -45, 12, 12), Rot(X(R(10.6, 8.6, 11.6, 6.8, 3.4), R(12.6, 10.4, 7.6, 3.2, 1.6)), -45, 12, 12))));
        // Bolt — quick capture.
        list.Add(new("Bolt", P(14, 1.8, 4.6, 13.8, 10.8, 13.8, 9.4, 22.2, 19.4, 9.6, 13, 9.6, 15.6, 1.8)));
        // Eye — preview.
        list.Add(new("Eye", X(S("M1.6,12 C5.2,5.4 18.8,5.4 22.4,12 C18.8,18.6 5.2,18.6 1.6,12 Z"), C(12, 12, 4.9)), C(12, 12, 2.9)));
        // Oracle — a faceted diamond lens with an open eye: sees across Arcadia (deterministic, not a robot).
        list.Add(new("Oracle",
            U(X(Rot(R(3.4, 3.4, 17.2, 17.2, 1.8), 45), Rot(R(5.5, 5.5, 13, 13, 1), 45)),
              X(S("M6.9,12 C9,9.1 15,9.1 17.1,12 C15,14.9 9,14.9 6.9,12 Z"), C(12, 12, 2.5))),
            C(12, 12, 1.5)));
        // Eye off — values hidden (Finance privacy mode).
        list.Add(new("EyeOff", X(X(S("M1.6,12 C5.2,5.4 18.8,5.4 22.4,12 C18.8,18.6 5.2,18.6 1.6,12 Z"), C(12, 12, 4.9)), Line(3.2, 3.2, 20.8, 20.8, 4.6)), Line(3.6, 3.6, 20.4, 20.4, 1.9)));
        // Wallet — body with a clasp pocket.
        list.Add(new("Wallet", U(X(R(2.4, 6.2, 19.2, 14, 2.6), R(14.6, 10.4, 8.2, 5.6, 1.8)), R(4.4, 3.2, 12.4, 2.2, 1.1)), C(17.8, 13.2, 1.4)));
        // Bank — pediment, columns, base.
        list.Add(new("Bank", U(P(12, 2, 22, 7.8, 2, 7.8), R(2, 19, 20, 2.6, 0.6)), U(R(4, 9.8, 2.6, 7.6, 0.4), R(8.5, 9.8, 2.6, 7.6, 0.4), R(12.9, 9.8, 2.6, 7.6, 0.4), R(17.4, 9.8, 2.6, 7.6, 0.4))));
        // Card — payment card with the magnetic stripe cut out.
        list.Add(new("Card", X(R(2, 5, 20, 14, 2.4), R(2, 8.4, 20, 2.8)), R(5, 14.2, 5.6, 2, 0.6)));
        // Cash — banknote with a medallion.
        list.Add(new("Cash", X(R(1.8, 6, 20.4, 12, 1.6), C(12, 12, 3.4)), U(C(12, 12, 2), R(4, 8.2, 2.2, 1.6, 0.6), R(17.8, 14.2, 2.2, 1.6, 0.6))));
        // Receipt — paper with a torn edge and printed lines.
        list.Add(new("Receipt", X(P(5, 2, 19, 2, 19, 21.6, 16.7, 19.8, 14.3, 21.6, 12, 19.8, 9.7, 21.6, 7.3, 19.8, 5, 21.6), R(8, 6.2, 8, 1.8, 0.9), R(8, 9.9, 8, 1.8, 0.9), R(8, 13.6, 5, 1.8, 0.9)),
            U(R(8.4, 6.6, 7.2, 1, 0.5), R(8.4, 10.3, 7.2, 1, 0.5), R(8.4, 14, 4.2, 1, 0.5))));
        // Target — rings with a bullseye (savings goals).
        list.Add(new("Target", U(X(C(12, 12, 9.8), C(12, 12, 7.6)), X(C(12, 12, 5.4), C(12, 12, 3.4))), C(12, 12, 2)));
        // Chart — three bars, the last one lit.
        list.Add(new("Chart", U(R(3, 13, 4.2, 8, 0.9), R(9.9, 8, 4.2, 13, 0.9)), R(16.8, 3.4, 4.2, 17.6, 0.9)));
        // Tag — price tag with an eyelet.
        list.Add(new("Tag", X(P(2.8, 2.8, 12.4, 2.8, 21.2, 11.6, 11.6, 21.2, 2.8, 12.4), C(7.6, 7.6, 2.1)), C(7.6, 7.6, 1.1)));
        // Transfer — money moving both ways between accounts.
        list.Add(new("Transfer", U(R(3, 6, 12.6, 2.4, 1.2), P(14.6, 2.8, 21, 7.2, 14.6, 11.6)), U(R(8.4, 15.6, 12.6, 2.4, 1.2), P(9.4, 12.4, 3, 16.8, 9.4, 21.2))));
        // Income — arrow down into a tray.
        list.Add(new("Income", U(R(3, 18, 18, 2.6, 1.3), R(3, 13.4, 2.6, 7.2, 1.3), R(18.4, 13.4, 2.6, 7.2, 1.3)), U(R(10.7, 2.4, 2.6, 8.6, 1.3), P(6.8, 9.6, 17.2, 9.6, 12, 15.2))));
        // Expense — arrow up out of a tray.
        list.Add(new("Expense", U(R(3, 18, 18, 2.6, 1.3), R(3, 13.4, 2.6, 7.2, 1.3), R(18.4, 13.4, 2.6, 7.2, 1.3)), U(R(10.7, 7, 2.6, 8.6, 1.3), P(6.8, 8.4, 17.2, 8.4, 12, 2.6))));        // Split view — editor and preview side by side.
        list.Add(new("SplitView", X(X(R(2.4, 4, 19.2, 16, 2), R(4.2, 5.8, 6.9, 12.4, 0.6)), R(12.9, 5.8, 6.9, 12.4, 0.6)), U(R(5.6, 8, 4, 1.4, 0.7), R(5.6, 10.8, 3, 1.4, 0.7))));
        // Agenda — date blocks with lines.
        list.Add(new("Agenda", U(R(10.4, 4.4, 11.2, 2, 1), R(10.4, 7.6, 7.4, 2, 1), R(10.4, 14.6, 11.2, 2, 1), R(10.4, 17.8, 7.4, 2, 1)),
            U(R(2.4, 3.6, 6.4, 6.8, 1.4), R(2.4, 13.8, 6.4, 6.8, 1.4))));

        // AI Assistant — friendly robot head with glowing eyes.
        var head = R(4.2, 7.4, 15.6, 12.4, 4.2);
        list.Add(new("AIAssistant",
            U(X(head, C(9.2, 13.4, 1.9), C(14.8, 13.4, 1.9), R(9.6, 16.6, 4.8, 1.1, 0.55)),
              R(11.3, 3.6, 1.4, 4.4, 0.5), C(12, 3.2, 1.6), R(1.5, 11.2, 1.9, 5.2, 0.9), R(20.6, 11.2, 1.9, 5.2, 0.9)),
            U(C(9.2, 13.4, 1.15), C(14.8, 13.4, 1.15))));

        // Calendar — binder rings, day grid, one highlighted day.
        var rings = U(R(7, 2.2, 2, 4.8, 1), R(15, 2.2, 2, 4.8, 1));
        var days = new List<Geometry>();
        foreach (var y in new[] { 11.0, 15.3 })
            foreach (var x in new[] { 6.2, 10.5, 14.8 })
                if (!(x == 14.8 && y == 15.3)) days.Add(R(x, y, 3, 3, 0.5));
        list.Add(new("Calendar",
            U(X(R(3, 4.6, 18, 16.8, 2.2), R(4.8, 9.6, 14.4, 10, 1), Grow(rings, 1.2)), rings, U([.. days])),
            R(14.8, 15.3, 3, 3, 0.5)));

        // Tasks — checkbox with an escaping check mark.
        var check = Polyline(2.6, 7.6, 12.2, 10.9, 15.4, 19.8, 6.2);
        list.Add(new("Tasks", X(X(R(3.2, 3.2, 17.6, 17.6, 3.4), R(5.4, 5.4, 13.2, 13.2, 1.8)), Grow(check, 1.4)), check));

        // Notes — page with folded corner.
        var page = Grow(P(5, 2.6, 14.6, 2.6, 19.6, 7.6, 19.6, 21.4, 5, 21.4), 0.8);
        var fold = P(14.6, 2.6, 14.6, 7.6, 19.6, 7.6);
        list.Add(new("Notes",
            X(page, R(7.8, 10.6, 9, 1.4, 0.7), R(7.8, 13.8, 9, 1.4, 0.7), R(7.8, 17, 5.8, 1.4, 0.7), Grow(fold, 1)),
            Grow(P(15.5, 4, 15.5, 6.7, 18.2, 6.7), 0.6)));

        // Finance — stacked coins.
        static Geometry Coin(double cx, double cy) => U(E(cx, cy, 6.6, 2.5), R(cx - 6.6, cy, 13.2, 2.6), E(cx, cy + 2.6, 6.6, 2.5));
        var coins = new[] { Coin(13, 5.8), Coin(11.4, 10.2), Coin(12.6, 14.6) };
        var coinParts = new List<Geometry>();
        for (int i = 0; i < coins.Length; i++)
        {
            var above = coins.Take(i).Select(c => Grow(c, 1.1)).ToArray();
            coinParts.Add(above.Length == 0 ? coins[i] : X(coins[i], above));
        }
        list.Add(new("Finance", U([.. coinParts]), E(13, 5.8, 4.4, 1.4)));

        // Settings — eight-tooth gear with an axle.
        var teeth = Enumerable.Range(0, 8).Select(k => Rot(R(10.4, 1.4, 3.2, 4.6, 0.8), k * 45)).ToArray();
        list.Add(new("Settings", X(U([C(12, 12, 7.4), .. teeth]), C(12, 12, 3.2)), C(12, 12, 1.7)));

        // Header utilities
        list.Add(new("Search", U(X(C(10.4, 10.4, 7.2), C(10.4, 10.4, 5)), Line(15.9, 15.9, 20.8, 20.8, 3))));
        var bell = S("M12,3.2 C8.3,3.2 6.2,6.1 6.2,9.6 L6.2,13.8 L4.2,16.6 C3.9,17.1 4.2,17.6 4.8,17.6 L19.2,17.6 C19.8,17.6 20.1,17.1 19.8,16.6 L17.8,13.8 L17.8,9.6 C17.8,6.1 15.7,3.2 12,3.2 Z");
        list.Add(new("Bell", U(bell, C(12, 2.8, 1.3), X(C(12, 19.4, 2.1), Grow(bell, 1.1)))));
        var cloud = U(C(11.2, 15.4, 3.4), C(15.4, 13.2, 4.3), C(19.4, 16.2, 2.9), R(7.8, 14.8, 14.2, 4.8, 2.4));
        var sun = U([C(8.6, 9, 3.4), .. Enumerable.Range(0, 8).Select(k => Rot(Line(8.6, 2.2, 8.6, 3.6, 1.3), k * 45, 8.6, 9))]);
        list.Add(new("Weather", cloud, X(sun, Grow(cloud, 1.4))));
        list.Add(new("MapPin", X(S("M12,2.5 C8.1,2.5 5,5.5 5,9.4 C5,14.4 12,21.5 12,21.5 C12,21.5 19,14.4 19,9.4 C19,5.5 15.9,2.5 12,2.5 Z"), C(12, 9.4, 2.7))));

        // Window chrome glyphs (drawn thin; they sit in 46×32 buttons)
        list.Add(new("WindowMinimize", R(6.5, 11.4, 11, 1.3, 0.3)));
        list.Add(new("WindowMaximize", X(R(6.5, 6.5, 11, 11, 1), R(7.8, 7.8, 8.4, 8.4, 0.4))));
        var front = X(R(6, 8.5, 9.5, 9.5, 1), R(7.3, 9.8, 6.9, 6.9, 0.4));
        var backSq = X(X(R(8.5, 6, 9.5, 9.5, 1), R(9.8, 7.3, 6.9, 6.9, 0.4)), R(5, 7.4, 11.6, 11.6));
        list.Add(new("WindowRestore", U(front, backSq)));
        list.Add(new("WindowClose", U(Line(7, 7, 17, 17, 1.4), Line(17, 7, 7, 17, 1.4))));

        // Glyphs
        list.Add(new("Play", Grow(P(7.4, 4.8, 19.4, 12, 7.4, 19.2), 1.6)));
        list.Add(new("Check", Polyline(2.8, 5.4, 12.6, 10, 17, 18.8, 7.2)));
        list.Add(new("Close", U(Line(6.5, 6.5, 17.5, 17.5, 2.6), Line(17.5, 6.5, 6.5, 17.5, 2.6))));
        list.Add(new("Hourglass",
            U(R(5.4, 2.6, 13.2, 2.2, 1.1), R(5.4, 19.2, 13.2, 2.2, 1.1),
              X(S("M7,4.6 L17,4.6 C17,8.8 13.7,10.5 13.3,12 C13.7,13.5 17,15.2 17,19.4 L7,19.4 C7,15.2 10.3,13.5 10.7,12 C10.3,10.5 7,8.8 7,4.6 Z"),
                S("M8.7,6.1 L15.3,6.1 C15,8.6 12.6,10 12,11.2 C11.4,10 9,8.6 8.7,6.1 Z M12,12.8 C12.6,14.2 15,15.4 15.3,17.9 L8.7,17.9 C9,15.4 11.4,14.2 12,12.8 Z"))),
            U(S("M9.4,17.9 L14.6,17.9 C14.2,16.4 13,15.7 12,15.1 C11,15.7 9.8,16.4 9.4,17.9 Z"), S("M9.9,7.4 L14.1,7.4 C13.6,8.6 12.6,9.3 12,9.9 C11.4,9.3 10.4,8.6 9.9,7.4 Z"))));
        list.Add(new("Plus", U(R(11, 4.5, 2, 15, 1), R(4.5, 11, 15, 2, 1))));
        list.Add(new("ChevronDown", Polyline(2.2, 6.5, 9.5, 12, 15, 17.5, 9.5)));
        list.Add(new("ChevronRight", Polyline(2.2, 9.5, 6.5, 15, 12, 9.5, 17.5)));
        list.Add(new("ArrowRight", U(Line(4.5, 12, 18.5, 12, 2), Polyline(2, 13, 6.5, 18.5, 12, 13, 17.5))));

        var cup = S("M6.5,3.5 L17.5,3.5 L17.5,9 C17.5,12.4 15.2,14.6 12,14.6 C8.8,14.6 6.5,12.4 6.5,9 Z");
        list.Add(new("Trophy",
            X(U(cup, X(C(6.4, 7.6, 3.2), C(6.4, 7.6, 1.8)), X(C(17.6, 7.6, 3.2), C(17.6, 7.6, 1.8)), R(11, 13.8, 2, 4, 0.3), R(7.6, 17.6, 8.8, 2.1, 0.8), R(6.4, 19.5, 11.2, 1.8, 0.6)), Grow(R(9, 5.2, 1.4, 5.6, 0.7), 1)),
            R(9, 5.2, 1.4, 5.6, 0.7)));
        list.Add(new("Clock", U(X(C(12, 12, 9.6), C(12, 12, 7.8)), Line(12, 12, 12, 6.8, 1.8), Line(12, 12, 15.6, 14.1, 1.8)), C(12, 12, 1.3)));
        var camBody = U(R(2.6, 7, 18.8, 13, 2.4), Grow(P(8.2, 7.4, 9.8, 4.8, 14.2, 4.8, 15.8, 7.4), 1));
        list.Add(new("Camera", X(camBody, C(12, 13.4, 5), R(17.2, 9, 2.2, 1.4, 0.5)), C(12, 13.4, 3.4)));
        var frame = X(R(2.6, 4.4, 18.8, 15.2, 2), R(4.4, 6.2, 15.2, 11.6, 0.8));
        list.Add(new("Image", U(frame, P(4.4, 17.8, 9.2, 11.4, 12.4, 15.2, 14.6, 12.8, 19.6, 17.8)), C(15.6, 9.4, 1.6)));
        var pencilBody = U(R(10.6, 5.4, 2.8, 11, 0.4), P(10.6, 16.4, 13.4, 16.4, 12, 20.4));
        var eraser = R(10.6, 2.4, 2.8, 2.4, 0.6);
        list.Add(new("Journal", Rot(X(pencilBody, P(11.5, 18.9, 12.5, 18.9, 12, 20.6)), 45), Rot(U(eraser, P(11.5, 18.9, 12.5, 18.9, 12, 20.6)), 45)));
        list.Add(new("Flag", R(5, 3, 1.8, 18.6, 0.9), S("M8,4 L19.2,4 L16.6,8.2 L19.2,12.4 L8,12.4 Z")));
        var star = Star(12, 12.4, 10, 4.2);
        list.Add(new("Star", Grow(star, 1)));
        list.Add(new("Pin", Rot(U(R(8.6, 2.8, 6.8, 2.2, 0.9), R(10, 4.6, 4, 6.2, 0.4), Grow(P(7.4, 11.4, 16.6, 11.4, 15.2, 13.4, 8.8, 13.4), 0.6), R(11.4, 13.2, 1.2, 8, 0.6)), 35)));
        list.Add(new("Warning", X(Grow(P(12, 3.4, 21.4, 20, 2.6, 20), 1.8), R(11, 8.6, 2, 6.6, 1), C(12, 17.2, 1.2))));
        list.Add(new("Library", U(R(3, 4, 3.6, 16.4, 0.8), R(7.8, 4, 3.6, 16.4, 0.8), Rot(R(13.6, 4.6, 3.6, 16, 0.8), -14, 15.4, 20.4)), R(3, 8, 3.6, 1.2)));
        list.Add(new("Sparkle", Star4(12, 12, 9.5, 2.2), Star4(18.6, 5.2, 3.2, 0.9)));

        // Library / detail tooling
        list.Add(new("ViewGrid", U(R(3.5, 3.5, 7.4, 7.4, 1.4), R(13.1, 3.5, 7.4, 7.4, 1.4), R(3.5, 13.1, 7.4, 7.4, 1.4)), R(13.1, 13.1, 7.4, 7.4, 1.4)));
        list.Add(new("ViewList", U(R(8.4, 4.6, 12.6, 2.6, 1.3), R(8.4, 10.7, 12.6, 2.6, 1.3), R(8.4, 16.8, 12.6, 2.6, 1.3)), U(R(3, 4.4, 3, 3, 0.8), R(3, 10.5, 3, 3, 0.8), R(3, 16.6, 3, 3, 0.8))));
        var bin = X(Grow(P(5.6, 7.4, 18.4, 7.4, 17.2, 21, 6.8, 21), 0.8), R(9, 10, 1.5, 8.4, 0.75), R(13.5, 10, 1.5, 8.4, 0.75));
        list.Add(new("Trash", U(bin, R(3.6, 4.4, 16.8, 2, 1), R(9.4, 2.4, 5.2, 2.6, 1))));
        list.Add(new("ChevronLeft", Polyline(2.2, 14.5, 6.5, 9, 12, 14.5, 17.5)));
        list.Add(new("Import", U(Polyline(2.4, 4, 14, 4, 20, 20, 20, 20, 14), Line(12, 3, 12, 14, 2.4)), Polyline(2.4, 7.5, 10, 12, 14.5, 16.5, 10)));
        list.Add(new("External", U(Polyline(2.2, 18, 13.5, 18, 20, 4, 20, 4, 6, 10.5, 6), Line(11, 13, 19.5, 4.5, 2.2)), Polyline(2.2, 13.5, 4, 20, 4, 20, 10.5)));
        list.Add(new("Sort", U(Line(7.5, 4, 7.5, 19, 2), Polyline(2, 3.8, 15.4, 7.5, 19.2, 11.2, 15.4)), U(Line(16.5, 5, 16.5, 20, 2), Polyline(2, 12.8, 8.6, 16.5, 4.8, 20.2, 8.6))));

        // ── IT toolbox ──────────────────────────────────────────────
        // CPU — die with pins on every side, lit core.
        var pins = new List<Geometry>();
        foreach (var p in new[] { 8.2, 12, 15.8 })
        {
            pins.Add(R(p - 0.7, 1.8, 1.4, 3.4, 0.5)); pins.Add(R(p - 0.7, 18.8, 1.4, 3.4, 0.5));
            pins.Add(R(1.8, p - 0.7, 3.4, 1.4, 0.5)); pins.Add(R(18.8, p - 0.7, 3.4, 1.4, 0.5));
        }
        list.Add(new("Cpu", U(X(R(5, 5, 14, 14, 2), R(8.6, 8.6, 6.8, 6.8, 1)), U([.. pins])), R(9.8, 9.8, 4.4, 4.4, 0.6)));

        // Memory — a RAM module with chips and a notched edge connector.
        var chips = U(R(5.2, 7.6, 3, 5, 0.4), R(10.5, 7.6, 3, 5, 0.4), R(15.8, 7.6, 3, 5, 0.4));
        list.Add(new("Memory", X(R(2, 5.4, 20, 11.6, 1.2), chips, R(11.2, 14.6, 1.6, 2.6)),
            U(R(3.6, 17.8, 3, 2.6, 0.4), R(7.8, 17.8, 3, 2.6, 0.4), R(13.2, 17.8, 3, 2.6, 0.4), R(17.4, 17.8, 3, 2.6, 0.4))));

        // Network — three linked nodes (a small topology).
        list.Add(new("Network", U(X(R(8.4, 2.4, 7.2, 5.6, 1.2), R(9.8, 3.8, 4.4, 2.8, 0.4)), Line(12, 8, 12, 12.6, 1.6), Line(5.2, 12.6, 18.8, 12.6, 1.6),
                Line(5.2, 12.6, 5.2, 15.6, 1.6), Line(18.8, 12.6, 18.8, 15.6, 1.6), X(R(1.6, 15.6, 7.2, 5.6, 1.2), R(3, 17, 4.4, 2.8, 0.4)), X(R(15.2, 15.6, 7.2, 5.6, 1.2), R(16.6, 17, 4.4, 2.8, 0.4))),
            U(R(9.8, 3.8, 4.4, 2.8, 0.4), R(3, 17, 4.4, 2.8, 0.4), R(16.6, 17, 4.4, 2.8, 0.4))));

        // Ping — a source dot with outgoing signal arcs.
        static Geometry Arc(double r, double w) => X(X(C(5, 19, r + w / 2), C(5, 19, r - w / 2)), P(5, 19, 5 + 30, 19, 5 + 30, 19 + 30, -30, 19 + 30, -30, -30, 5, -30), P(5, 19, -30, 19, -30, -30));
        list.Add(new("Ping", U(Arc(8, 2), Arc(14, 2)), C(5, 19, 2.6)));

        // Route — hops joined by a winding path.
        list.Add(new("Route", Polyline(1.8, 4.5, 19.5, 10, 14, 7, 8, 14, 7.5, 19.5, 4.5), U(C(4.5, 19.5, 2.4), C(10, 14, 1.8), C(14, 7.5, 1.8), C(19.5, 4.5, 2.4))));

        // DNS — globe with a name tag.
        var dnsGlobe = X(C(10.5, 10.5, 8.4), U(Stroke(E(10.5, 10.5, 3.6, 8.2), 1.1), Line(2, 10.5, 19, 10.5, 1.1), Line(10.5, 2, 10.5, 19, 1.1)));
        var tag = R(11.8, 14.6, 10.6, 7, 1.4);
        list.Add(new("Dns", X(dnsGlobe, Grow(tag, 1.4)), X(tag, R(13.6, 16.8, 7, 1.2, 0.6), R(13.6, 18.8, 4.6, 1.2, 0.6))));

        // Port — a network plug.
        var plugBody = U(R(6.4, 8.4, 11.2, 9.6, 1.6), R(10, 18, 4, 4, 0.6));
        list.Add(new("Port", X(plugBody, R(8.4, 10.4, 7.2, 3, 0.4)), U(R(8.4, 2.8, 2.2, 5.6, 0.8), R(13.4, 2.8, 2.2, 5.6, 0.8))));

        // Devices — a monitor and a phone side by side.
        var screenFrame = X(R(1.6, 3.6, 15, 11, 1.4), R(3.2, 5.2, 11.8, 7.8, 0.4));
        var phone = R(15.6, 9, 6.8, 12.4, 1.4);
        list.Add(new("Devices", U(X(U(screenFrame, R(7.6, 14.6, 3, 3), R(5, 17.6, 8.2, 1.8, 0.8)), Grow(phone, 1.2)), X(phone, R(16.9, 10.6, 4.2, 8, 0.4))),
            U(R(3.2, 5.2, 11.8, 7.8, 0.4), R(17.8, 19.2, 2.4, 0.9, 0.4))));

        // Processes — a window with an activity trace.
        var procWin = X(R(2.4, 3.6, 19.2, 16.8, 2), R(4.2, 7.4, 15.6, 11.2, 0.6));
        list.Add(new("Processes", U(procWin, C(5.4, 5.5, 0.8), C(8, 5.5, 0.8)), Polyline(1.8, 5.4, 13.4, 8.6, 13.4, 10.6, 9.4, 13.4, 16.6, 15.6, 11.8, 18.6, 11.8)));

        // Services — two meshed gears.
        static Geometry Gear(double cx, double cy, double r, int n, double hole)
        {
            var t = Enumerable.Range(0, n).Select(k => Rot(R(cx - r * 0.2, cy - r - r * 0.34, r * 0.4, r * 0.7, r * 0.1), k * 360.0 / n, cx, cy)).ToArray();
            return X(U([C(cx, cy, r), .. t]), C(cx, cy, hole));
        }
        list.Add(new("Services", U(Gear(8.6, 9, 5.6, 8, 2.2), X(Gear(16.6, 16.2, 4.2, 7, 1.6), C(8.6, 9, 7.2))), C(8.6, 9, 1.1)));

        // Drive — a storage unit with an activity light.
        var drive = R(2.4, 6, 19.2, 12, 2.2);
        list.Add(new("Drive", X(drive, R(4.4, 8, 15.2, 5, 0.8), R(4.4, 14.6, 8, 1.4, 0.7)), U(C(17.6, 15.3, 1.3), R(4.4, 8, 15.2, 5, 0.8))));

        // FileSearch — a document with a magnifier over it.
        var doc = Grow(P(3.6, 2.4, 12.4, 2.4, 16.4, 6.4, 16.4, 11, 3.6, 21.6), 0.2);
        var lens = U(X(C(14.6, 14.6, 5), C(14.6, 14.6, 3.4)), Line(18.2, 18.2, 21.4, 21.4, 2.4));
        var sheet = X(Grow(P(3.6, 2.4, 12.4, 2.4, 16.4, 6.4, 16.4, 9, 11, 9, 8.6, 12.6, 8.6, 21.6, 3.6, 21.6), 0.8), R(5.8, 7, 5, 1.2, 0.6), R(5.8, 10.2, 3.4, 1.2, 0.6));
        list.Add(new("FileSearch", U(X(sheet, Grow(lens, 1.2)), lens), C(14.6, 14.6, 2.6)));

        // Hash — the number sign, slanted like a stamped mark.
        list.Add(new("Hash", U(Line(9.6, 3, 7.6, 21, 2.2), Line(16.4, 3, 14.4, 21, 2.2), Line(4, 8.8, 20.4, 8.8, 2.2), Line(3.4, 15.2, 19.8, 15.2, 2.2))));

        // Subnet — a calculator.
        var calcKeys = new List<Geometry>();
        foreach (var ky in new[] { 11.0, 14.4, 17.8 })
            foreach (var kx in new[] { 6.4, 10.4, 14.4 })
                calcKeys.Add(R(kx, ky, 3, 2.4, 0.5));
        list.Add(new("Subnet", X(R(3.8, 2, 16.4, 20, 2.2), [R(6, 4.4, 12, 4.6, 0.8), .. calcKeys]), U(R(6.9, 5.3, 10.2, 2.8, 0.4))));

        // Terminal — a console window with a prompt.
        var term = X(R(2, 3.6, 20, 16.8, 2), R(3.8, 7, 16.4, 11.6, 0.6));
        list.Add(new("Terminal", U(term, Polyline(1.9, 6.4, 10, 9.4, 12.8, 6.4, 15.6)), R(11.2, 14.6, 6.4, 1.9, 0.9)));

        // EventLog — a ruled list with a warning marker.
        var logRows = U(R(9.6, 5, 11.6, 2, 1), R(9.6, 11, 11.6, 2, 1), R(9.6, 17, 11.6, 2, 1));
        list.Add(new("EventLog", U(logRows, C(4.6, 6, 1.8), C(4.6, 18, 1.8)), Grow(P(4.6, 9.8, 7.2, 14.4, 2, 14.4), 0.6)));

        // Diagnostic — a clipboard with a heartbeat.
        var dBoard = R(4.4, 3.8, 15.2, 18.4, 2.2);
        var dClip = R(8.3, 1.6, 7.4, 4.2, 1.4);
        var beat = Polyline(1.8, 6.4, 14, 9.2, 14, 10.8, 10, 13.2, 18, 15, 12.8, 17.6, 12.8);
        list.Add(new("Diagnostic", X(X(dBoard, R(6.2, 7.4, 11.6, 13, 0.8)), Grow(dClip, 1.3)), U(dClip, beat)));

        // Copy — two stacked sheets.
        var front2 = R(8.4, 7.6, 12, 14, 1.6);
        list.Add(new("Copy", U(X(X(R(3.6, 2.4, 12, 14, 1.6), R(5.4, 4.2, 8.4, 10.4, 0.4)), Grow(front2, 1.4)), X(front2, R(10.2, 9.4, 8.4, 10.4, 0.4)))));

        // Export — an arrow leaving a tray.
        list.Add(new("Export", U(Polyline(2.4, 4, 14, 4, 20, 20, 20, 20, 14), Line(12, 3.4, 12, 14, 2.4)), Polyline(2.4, 7.5, 7.6, 12, 3.2, 16.5, 7.6)));

        // Refresh — two chasing arrows.
        var ring1 = X(X(C(12, 12, 8.4), C(12, 12, 6.2)), P(12, 12, 24, 0, 24, 12), P(12, 12, 0, 24, 0, 12));
        list.Add(new("Refresh", U(ring1, P(15.2, 1.6, 22.2, 4.4, 16.4, 9.2), P(8.8, 22.4, 1.8, 19.6, 7.6, 14.8))));

        list.Add(new("Stop", R(5.5, 5.5, 13, 13, 2)));
        list.Add(new("Info", X(C(12, 12, 9.8), C(12, 7.2, 1.5), R(10.8, 10.2, 2.4, 8, 1.2))));

        // Shield — Windows elevation / protected operations.
        var shield = S("M12,1.8 L20.4,5 L20.4,11.2 C20.4,16.2 16.8,20.2 12,22.2 C7.2,20.2 3.6,16.2 3.6,11.2 L3.6,5 Z");
        list.Add(new("Shield", X(shield, S("M12,4.2 L18.2,6.6 L18.2,11.2 C18.2,15 15.6,18.2 12,19.8 Z")), S("M12,4.2 L18.2,6.6 L18.2,11.2 C18.2,15 15.6,18.2 12,19.8 Z")));

        // Monitor — a display on a stand (system information).
        list.Add(new("Monitor", U(X(R(1.8, 3, 20.4, 13.6, 1.6), R(3.6, 4.8, 16.8, 10, 0.4)), R(10.6, 16.6, 2.8, 3), R(6.6, 19.4, 10.8, 1.8, 0.9)), R(3.6, 4.8, 16.8, 10, 0.4)));

        // Windows — four panes in perspective-free form (the platform category).
        list.Add(new("WindowsLogo", U(R(3, 3, 8.4, 8.4, 0.8), R(12.6, 3, 8.4, 8.4, 0.8), R(3, 12.6, 8.4, 8.4, 0.8)), R(12.6, 12.6, 8.4, 8.4, 0.8)));

        // Arcadia emblem — an "A" formed by a mountain pass, a gem at the summit, a blue peak within.
        var aShape = P(12, 1.4, 22.8, 21.8, 17.4, 21.8, 12, 11.8, 6.6, 21.8, 1.2, 21.8);
        var gem = P(12, 5.4, 13.35, 7.6, 12, 9.8, 10.65, 7.6);
        list.Add(new("Logo", X(aShape, Grow(gem, 1.1)), U(gem, P(12, 14.4, 16, 21.8, 8, 21.8))));

        return list;
    }

    private static Geometry Star(double cx, double cy, double outer, double inner)
    {
        var pts = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? outer : inner;
            var a = -Math.PI / 2 + i * Math.PI / 5;
            pts.Add(cx + r * Math.Cos(a));
            pts.Add(cy + r * Math.Sin(a));
        }
        return P([.. pts]);
    }

    private static Geometry Star4(double cx, double cy, double outer, double inner)
    {
        var pts = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            var r = i % 2 == 0 ? outer : inner;
            var a = -Math.PI / 2 + i * Math.PI / 4;
            pts.Add(cx + r * Math.Cos(a));
            pts.Add(cy + r * Math.Sin(a));
        }
        return P([.. pts]);
    }

    // ───────────────────────────── output ─────────────────────────────

    private static string Serialize(Geometry g)
    {
        var outlined = g.GetOutlinedPathGeometry(0.01, ToleranceType.Absolute);
        var text = outlined.ToString(CultureInfo.InvariantCulture);
        // Round coordinates to 2 decimals to keep the resource file readable.
        return Regex.Replace(text, @"-?\d+\.\d+(E-?\d+)?", m =>
            Math.Round(double.Parse(m.Value, CultureInfo.InvariantCulture), 2).ToString("0.##", CultureInfo.InvariantCulture));
    }

    private static string ToXaml(List<Icon> icons)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!-- GENERATED by tools/Arcadia.IconForge. Edit Program.cs there and re-run; do not hand-edit. -->");
        sb.AppendLine("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
        sb.AppendLine("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
        sb.AppendLine("                    xmlns:po=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation/options\"");
        sb.AppendLine("                    xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"");
        sb.AppendLine("                    mc:Ignorable=\"po\">");
        foreach (var icon in icons)
        {
            sb.AppendLine($"    <StreamGeometry x:Key=\"Icon.{icon.Name}\" po:Freeze=\"True\">{Serialize(icon.Primary)}</StreamGeometry>");
            if (icon.Accent != null)
                sb.AppendLine($"    <StreamGeometry x:Key=\"Icon.{icon.Name}.Accent\" po:Freeze=\"True\">{Serialize(icon.Accent)}</StreamGeometry>");
        }
        sb.AppendLine("</ResourceDictionary>");
        return sb.ToString();
    }

    private static readonly Brush Metal = new LinearGradientBrush(
        [new GradientStop(Color.FromRgb(0xF2, 0xF6, 0xFB), 0), new GradientStop(Color.FromRgb(0x9A, 0xAC, 0xC6), 1)], 90);
    private static readonly Brush Accent = new LinearGradientBrush(
        [new GradientStop(Color.FromRgb(0x7C, 0xE0, 0xFF), 0), new GradientStop(Color.FromRgb(0x2F, 0x7B, 0xFF), 1)], 90);
    private static readonly Brush Gold = new LinearGradientBrush(
        [new GradientStop(Color.FromRgb(0xFF, 0xE7, 0xA6), 0), new GradientStop(Color.FromRgb(0xE0, 0xA8, 0x3C), 0.55), new GradientStop(Color.FromRgb(0x9C, 0x68, 0x1E), 1)], 90);

    private static void DrawIcon(DrawingContext dc, Icon icon, Rect target, Brush primary, Brush accent)
    {
        dc.PushTransform(new TranslateTransform(target.X, target.Y));
        dc.PushTransform(new ScaleTransform(target.Width / 24, target.Height / 24));
        dc.DrawGeometry(primary, null, icon.Primary);
        if (icon.Accent != null) dc.DrawGeometry(accent, null, icon.Accent);
        dc.Pop();
        dc.Pop();
    }

    private static void WritePreview(List<Icon> icons, string path)
    {
        const int cell = 96, cols = 8;
        int rowsCount = (icons.Count + cols - 1) / cols;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x16)), null, new Rect(0, 0, cols * cell, rowsCount * cell));
            var face = new Typeface("Segoe UI");
            for (int i = 0; i < icons.Count; i++)
            {
                double x = i % cols * cell, y = i / cols * cell;
                DrawIcon(dc, icons[i], new Rect(x + 24, y + 8, 48, 48), Metal, Accent);
                DrawIcon(dc, icons[i], new Rect(x + 6, y + 62, 20, 20), Metal, Accent);
                DrawIcon(dc, icons[i], new Rect(x + 30, y + 66, 16, 16), Metal, Accent);
                var ft = new FormattedText(icons[i].Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 9, Brushes.Gray, 1);
                dc.DrawText(ft, new Point(x + 50, y + 70));
            }
        }
        var rtb = new RenderTargetBitmap(cols * cell, rowsCount * cell, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        SavePng(rtb, path);
    }

    private static BitmapSource RenderAppIcon(Icon logo, int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double pad = size * 0.06, radius = size * 0.2;
            var tile = new Rect(pad, pad, size - 2 * pad, size - 2 * pad);
            var tileBrush = new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x44), Color.FromRgb(0x07, 0x0B, 0x13), 90);
            var border = new Pen(new LinearGradientBrush(Color.FromRgb(0x5A, 0x9C, 0xFF), Color.FromRgb(0x14, 0x2A, 0x55), 90), Math.Max(1, size / 32.0));
            dc.DrawRoundedRectangle(tileBrush, border, tile, radius, radius);
            var inset = size * 0.2;
            DrawIcon(dc, logo, new Rect(inset, inset * 0.95, size - 2 * inset, size - 2 * inset), Gold, Accent);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private static void WriteAppIcon(Icon logo, string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(s =>
        {
            using var ms = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(RenderAppIcon(logo, s)));
            enc.Save(ms);
            return (size: s, data: ms.ToArray());
        }).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) w.Write(data);
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(fs);
    }
}
