using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arcadia.UI.Artwork;

namespace Arcadia.App.Diagnostics;

/// <summary>Visual QA: renders every procedural scene theme into one labelled PNG.</summary>
internal static class ArtSheet
{
    public static void Render(string path)
    {
        const int cellW = 480, cellH = 200, cols = 3, labelH = 22;
        var names = SceneThemes.Names.OrderBy(n => n).ToList();
        int rows = (names.Count + cols - 1) / cols;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, cols * cellW, rows * (cellH + labelH)));
            for (int i = 0; i < names.Count; i++)
            {
                double x = i % cols * cellW, y = i / cols * (cellH + labelH);
                dc.PushTransform(new TranslateTransform(x, y));
                ScenePainter.Paint(dc, new Size(cellW - 4, cellH), SceneThemes.Get(names[i]), 7, 0.3, 0);
                dc.Pop();
                var label = new FormattedText(names[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, 1);
                dc.DrawText(label, new Point(x + 6, y + cellH + 3));
            }
        }
        var bmp = new RenderTargetBitmap(cols * cellW, rows * (cellH + labelH), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
