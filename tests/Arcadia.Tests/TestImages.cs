using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Arcadia.Tests;

/// <summary>Builds real JPEG files with an EXIF block (date taken, camera, orientation, GPS) for photo tests.</summary>
public static class TestImages
{
    public static byte[] Jpeg(int width = 64, int height = 48, Color? color = null, double? lat = null, double? lon = null, int orientation = 1,
        DateTime? taken = null, string make = "Arcadia", string model = "TestCam")
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(color ?? Colors.SteelBlue), null, new Rect(0, 0, width, height));
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var jpeg = ms.ToArray();

        var tiff = Tiff(lat, lon, orientation, taken, make, model);
        var app1 = new List<byte> { 0xFF, 0xE1 };
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var len = payload.Length + 2;
        app1.Add((byte)(len >> 8));
        app1.Add((byte)(len & 0xFF));
        app1.AddRange(payload);
        // SOI, our APP1, then the encoder's segments (skipping its SOI)
        return new byte[] { 0xFF, 0xD8 }.Concat(app1).Concat(jpeg.Skip(2)).ToArray();
    }

    private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data);

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s + "\0");
    private static byte[] Short(ushort v) => BitConverter.GetBytes(v);
    private static byte[] Long(uint v) => BitConverter.GetBytes(v);

    private static byte[] Rationals(double degrees)
    {
        degrees = Math.Abs(degrees);
        var d = (uint)degrees;
        var mTotal = (degrees - d) * 60;
        var m = (uint)mTotal;
        var s = (uint)Math.Round((mTotal - m) * 60 * 1000);
        return new[] { d, 1u, m, 1u, s, 1000u }.SelectMany(BitConverter.GetBytes).ToArray();
    }

    private static byte[] Tiff(double? lat, double? lon, int orientation, DateTime? taken, string make, string model)
    {
        // Three IFDs laid out one after the other, each followed by its out-of-line values.
        var ifd0 = new List<Entry>
        {
            new(0x010F, 2, (uint)Ascii(make).Length, Ascii(make)),
            new(0x0110, 2, (uint)Ascii(model).Length, Ascii(model)),
            new(0x0112, 3, 1, Short((ushort)orientation)),
            new(0x8769, 4, 1, Long(0)), // Exif IFD pointer, patched below
        };
        var exif = new List<Entry>();
        if (taken is { } t) exif.Add(new(0x9003, 2, 20, Ascii(t.ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))));
        exif.Add(new(0xA002, 4, 1, Long(64)));
        exif.Add(new(0xA003, 4, 1, Long(48)));
        List<Entry>? gps = null;
        if (lat is { } la && lon is { } lo)
        {
            ifd0.Add(new(0x8825, 4, 1, Long(0))); // GPS IFD pointer, patched below
            gps =
            [
                new(0x0000, 1, 4, [2, 3, 0, 0]),
                new(0x0001, 2, 2, Ascii(la >= 0 ? "N" : "S")),
                new(0x0002, 5, 3, Rationals(la)),
                new(0x0003, 2, 2, Ascii(lo >= 0 ? "E" : "W")),
                new(0x0004, 5, 3, Rationals(lo)),
            ];
        }

        static int Size(List<Entry> ifd) => 2 + ifd.Count * 12 + 4 + ifd.Where(e => e.Data.Length > 4).Sum(e => e.Data.Length + (e.Data.Length & 1));
        uint exifOffset = (uint)(8 + Size(ifd0));
        uint gpsOffset = exifOffset + (uint)Size(exif);
        ifd0[3] = ifd0[3] with { Data = Long(exifOffset) };
        if (gps != null) ifd0[4] = ifd0[4] with { Data = Long(gpsOffset) };

        var output = new List<byte>();
        output.AddRange("II"u8.ToArray());
        output.AddRange(Short(42));
        output.AddRange(Long(8));
        Write(output, ifd0, 8);
        Write(output, exif, exifOffset);
        if (gps != null) Write(output, gps, gpsOffset);
        return output.ToArray();

        static void Write(List<byte> o, List<Entry> ifd, uint at)
        {
            var dataAt = at + 2 + (uint)ifd.Count * 12 + 4;
            var data = new List<byte>();
            o.AddRange(Short((ushort)ifd.Count));
            foreach (var e in ifd.OrderBy(e => e.Tag))
            {
                o.AddRange(Short(e.Tag));
                o.AddRange(Short(e.Type));
                o.AddRange(Long(e.Count));
                if (e.Data.Length <= 4) o.AddRange(e.Data.Concat(new byte[4 - e.Data.Length]));
                else
                {
                    o.AddRange(Long(dataAt + (uint)data.Count));
                    data.AddRange(e.Data);
                    if ((e.Data.Length & 1) == 1) data.Add(0);
                }
            }
            o.AddRange(Long(0)); // no next IFD
            o.AddRange(data);
        }
    }
}
