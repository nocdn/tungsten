using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tungsten;

/// <summary>
/// Renders the Tungsten logo (Assets/icon-tray.svg, Assets/icon-glyph.svg) at any size and
/// packs it into a multi-resolution .ico for the tray and the .exe.
/// </summary>
static class AppIcon
{
    static string[] PathData(string resource)
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("Tungsten.Assets." + resource)
            ?? throw new InvalidOperationException("missing resource " + resource);
        using var r = new StreamReader(s);
        return Regex.Matches(r.ReadToEnd(), "\\sd=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
    }

    static Drawing? _tray;
    static Geometry? _glyph;

    /// <summary>The bare logo mark (icon-glyph.svg), in a 44×45 coordinate space.</summary>
    public static Geometry Glyph
    {
        get
        {
            if (_glyph != null) return _glyph;
            var g = Geometry.Parse("F0 " + PathData("icon-glyph.svg")[0]);
            g.Freeze();
            return _glyph = g;
        }
    }

    /// <summary>Tray version (icon-tray.svg): dark rounded tile with the white mark, 46×45 space.</summary>
    static Drawing TrayDrawing()
    {
        if (_tray != null) return _tray;
        var p = PathData("icon-tray.svg");
        var tile = Geometry.Parse(p[0]);
        var mark = Geometry.Parse("F0 " + p[2]);
        var strokeBrush = new LinearGradientBrush(Color.FromRgb(0x2B, 0x2D, 0x2F), Color.FromRgb(0x13, 0x15, 0x17), new Point(23, 0.358398), new Point(23, 44.3584))
        { MappingMode = BrushMappingMode.Absolute };
        var dg = new DrawingGroup();
        using (var dc = dg.Open())
        {
            dc.PushClip(new RectangleGeometry(new Rect(1, 0.358398, 44, 44), 9.5, 9.5));
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x1F, 0x21, 0x23)), new Pen(strokeBrush, 1.83333), tile);
            dc.DrawGeometry(Brushes.White, null, mark);
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(153, 0, 0, 0)), 0.1375),
                new Rect(0.93125, 0.289648, 44.1375, 44.1375), 9.56875, 9.56875);
        }
        dg.Freeze();
        return _tray = dg;
    }

    public static BitmapSource Render(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double s = size / 46.0;
            dc.PushTransform(new ScaleTransform(s, s));
            dc.PushTransform(new TranslateTransform(0, 0.5)); // 46×45 art → centred in a square
            dc.DrawDrawing(TrayDrawing());
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    public static byte[] BuildIco()
    {
        var images = Sizes.Select(sz => (sz, data: sz >= 256 ? Png(Render(sz)) : Dib(Render(sz)))).ToList();
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (sz, data) in images)
        {
            bw.Write((byte)(sz >= 256 ? 0 : sz));
            bw.Write((byte)(sz >= 256 ? 0 : sz));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)1); bw.Write((short)32);
            bw.Write(data.Length); bw.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) bw.Write(data);
        bw.Flush();
        return ms.ToArray();
    }

    static byte[] Png(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Classic 32-bit icon DIB (straight alpha, bottom-up, empty AND mask).</summary>
    static byte[] Dib(BitmapSource src)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        int maskRow = (w + 31) / 32 * 4;
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(w); bw.Write(h * 2);
        bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(px.Length + maskRow * h);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = h - 1; y >= 0; y--) bw.Write(px, y * w * 4, w * 4);
        bw.Write(new byte[maskRow * h]);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Tray icon at the size Windows wants for the current DPI.</summary>
    public static System.Drawing.Icon Create()
    {
        int sz = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        using var ms = new MemoryStream(BuildIco());
        return new System.Drawing.Icon(ms, sz, sz);
    }

    public static ImageSource WindowIcon() => Render(64);
}
