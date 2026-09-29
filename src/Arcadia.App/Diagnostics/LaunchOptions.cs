using System.Globalization;

namespace Arcadia.App.Diagnostics;

/// <summary>
/// Command-line switches. The capture switches exist for visual QA: they render the window to a PNG at a
/// chosen size and display scale, then exit.
///   --page settings          start on a section
///   --lang ar                override the language for this run
///   --data-dir PATH          use an isolated data folder
///   --capture out.png --size 1920x1080 --scale 1.5
///   --art-sheet out.png      render all procedural scene themes
///   --game 3                 open a game's detail page
///   --dialog edit            (with --capture) render a dialog: edit | log | shot | confirm
///   --page ittools --tab ping  open an IT tool (sysinfo, performance, network, ping, trace, dns, port, devices, …)
///   --perf-scroll grid|list  open the library, scroll it top to bottom and log frame times, then exit
///   --wait-pid N             wait for that process to exit first (Arcadia restarting itself, e.g. after a restore)
/// </summary>
public sealed class LaunchOptions
{
    public string? Page { get; private init; }
    public string? Language { get; private init; }
    public string? DataDirectory { get; private init; }
    public string? CapturePath { get; private init; }
    /// <summary>--art-sheet out.png renders every procedural scene theme to a contact sheet.</summary>
    public string? ArtSheetPath { get; private init; }
    /// <summary>--game ID opens a game's detail page (with --page games implied).</summary>
    public int? GameId { get; private init; }
    /// <summary>--dialog edit|log|shot|confirm: capture a dialog instead of the main window (QA).</summary>
    public string? Dialog { get; private init; }
    /// <summary>--tab overview|sessions|journal|screenshots (with --game).</summary>
    public string? Tab { get; private init; }
    /// <summary>--perf-scroll grid|list: scroll benchmark for the library (QA).</summary>
    public string? PerfScroll { get; private init; }
    /// <summary>--project ID opens a project workspace (QA); --tab selects the workspace tab.</summary>
    public int? ProjectId { get; private init; }
    /// <summary>--doc ID opens the Documents library on a document (QA).</summary>
    public int? DocumentId { get; private init; }
    /// <summary>--trip ID opens a trip dossier (QA); --tab selects its tab.</summary>
    public int? TripId { get; private init; }
    /// <summary>--place ID opens Travel on a place (QA).</summary>
    public int? PlaceId { get; private init; }
    /// <summary>--wait-pid N: the previous Arcadia that is restarting into this one.</summary>
    public int? WaitPid { get; private init; }
    public int CaptureWidth { get; private init; } = 1920;
    public int CaptureHeight { get; private init; } = 1080;
    public double CaptureScale { get; private init; } = 1.0;
    public bool IsCapture => CapturePath != null;

    private static string? PngOnly(string? path) =>
        path != null && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 ? path : null;

    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        string? page = null, lang = null, dataDir = null, capture = null, artSheet = null, dialog = null, tab = null, perfScroll = null;
        int? waitPid = null;
        int? gameId = null, projectId = null, documentId = null, tripId = null, placeId = null;
        int width = 1920, height = 1080;
        double scale = 1.0;

        for (int i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--page": page = Next(); break;
                case "--lang": lang = Next(); break;
                case "--data-dir": dataDir = Next(); break;
                // QA outputs are PNG files only: a crafted shortcut cannot make Arcadia overwrite other file types
                case "--capture": capture = PngOnly(Next()); break;
                case "--art-sheet": artSheet = PngOnly(Next()); break;
                case "--dialog": dialog = Next(); break;
                case "--tab": tab = Next(); break;
                case "--perf-scroll": perfScroll = Next() ?? "grid"; break;
                case "--wait-pid":
                    if (int.TryParse(Next(), out var wp)) waitPid = wp;
                    break;
                case "--doc":
                    if (int.TryParse(Next(), out var did)) documentId = did;
                    break;
                case "--trip":
                    if (int.TryParse(Next(), out var tid)) tripId = tid;
                    break;
                case "--place":
                    if (int.TryParse(Next(), out var plid)) placeId = plid;
                    break;
                case "--project":
                    if (int.TryParse(Next(), out var pid)) projectId = pid;
                    break;
                case "--game":
                    if (int.TryParse(Next(), out var id)) gameId = id;
                    break;
                case "--size":
                    var parts = Next()?.Split('x', 'X');
                    if (parts is { Length: 2 } && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)) { width = w; height = h; }
                    break;
                case "--scale":
                    if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s is >= 0.5 and <= 4) scale = s;
                    break;
            }
        }

        return new LaunchOptions
        {
            Page = page, Language = lang, DataDirectory = dataDir, CapturePath = capture, ArtSheetPath = artSheet, GameId = gameId, Dialog = dialog, Tab = tab, PerfScroll = perfScroll, ProjectId = projectId, DocumentId = documentId, TripId = tripId, PlaceId = placeId, WaitPid = waitPid,
            CaptureWidth = width, CaptureHeight = height, CaptureScale = scale,
        };
    }
}
