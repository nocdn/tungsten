using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Tungsten;

public sealed class ResolvedGame
{
    public GameEntry Entry = null!;
    public string? ExePath;
    public string? MatchDir;
    public int SteamAppId;
    public BitmapSource? Icon;
    public BitmapSource? Art;
    public Color Accent = Color.FromRgb(0x2B, 0x2B, 0x2B);

    public bool Matches(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        if (ExePath != null && string.Equals(exe, ExePath, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var x in Entry.Exes)
            if (string.Equals(exe, x, StringComparison.OrdinalIgnoreCase)) return true;
        if (MatchDir != null && exe.StartsWith(MatchDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public static class GameResolver
{
    public static GameEntry CreateEntry(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            name = ExeDisplayName(path) ?? name;
        return new GameEntry { Name = name, Path = path };
    }

    public static string? ExeDisplayName(string exe)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exe);
            foreach (var cand in new[] { vi.ProductName, vi.FileDescription })
            {
                var c = cand?.Trim();
                if (string.IsNullOrWhiteSpace(c) || c.Length > 48) continue;
                if (c.Contains("Operating System", StringComparison.OrdinalIgnoreCase)) continue;
                if (c.Equals("BootstrapPackagedGame", StringComparison.OrdinalIgnoreCase)) continue;
                return c;
            }
        }
        catch { }
        return null;
    }

    public static ResolvedGame Resolve(GameEntry e)
    {
        var r = new ResolvedGame { Entry = e };
        try
        {
            string path = e.Path;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string? target = null, args = e.Arguments, url = null;

            if (ext == ".lnk")
            {
                var (t, a) = ShortcutTarget(path);
                target = t;
                args = ((args ?? "") + " " + (a ?? "")).Trim();
                if (t != null && t.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) url = ReadUrl(t);
            }
            else if (ext == ".url") url = ReadUrl(path);
            else target = path;

            if (target != null && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) r.ExePath = target;

            var m = Regex.Match(url ?? args ?? "", @"steam://(?:rungameid|run|launch)/(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success && r.ExePath != null && Path.GetFileName(r.ExePath).Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
                m = Regex.Match(args ?? "", @"-applaunch\s+(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int id)) r.SteamAppId = id;

            if (r.SteamAppId > 0)
            {
                if (r.ExePath != null && Path.GetFileName(r.ExePath).Equals("steam.exe", StringComparison.OrdinalIgnoreCase)) r.ExePath = null;
                r.MatchDir = Steam.InstallDir(r.SteamAppId);
                r.Art = Steam.Art(r.SteamAppId);
            }
            else if ((r.ExePath ?? target) is string file && !file.Contains("://"))
            {
                // Works for .exe and also launcher scripts (.bat/.cmd): the game usually lives next to them.
                var dir = Path.GetDirectoryName(file);
                if (!IsGenericDir(dir)) r.MatchDir = dir;
            }

            // Steam's desktop shortcuts point at a .ico: read its largest layer directly (the shell would
            // pad a 48px icon into a 256px canvas).
            r.Icon = (ext == ".url" ? IconTools.LoadIco(ReadIni(path, "IconFile")) : null)
                     ?? ShellIcon.Get(path, 256) ?? (r.ExePath != null ? ShellIcon.Get(r.ExePath, 256) : null);
            // Launcher scripts (.bat etc.) have a generic icon: borrow the main game exe's icon instead.
            if (r.SteamAppId == 0 && r.MatchDir != null && (r.ExePath == null || r.Icon == null) && MainExe(r.MatchDir) is string main)
                r.Icon = ShellIcon.Get(main, 256) ?? r.Icon;
            r.Accent = ColorTools.Accent(r.Icon ?? r.Art);
        }
        catch (Exception ex) { Program.Log($"Resolve {e.Path}: {ex}"); }
        return r;
    }

    /// <summary>The biggest .exe in a game folder is almost always the game itself.</summary>
    static string? MainExe(string dir)
    {
        try
        {
            string[] skip = { "unins", "crash", "setup", "redist", "report", "helper", "dxsetup", "vc_" };
            return new DirectoryInfo(dir).EnumerateFiles("*.exe", SearchOption.TopDirectoryOnly)
                .Where(f => !skip.Any(s => f.Name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => f.Length).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    static bool IsGenericDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return true;
        dir = dir.TrimEnd('\\');
        if (string.Equals(Path.GetPathRoot(dir)?.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase)) return true;
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (dir.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.UserProfile })
            if (string.Equals(Environment.GetFolderPath(f).TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string? ReadIni(string file, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
                if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return line[(key.Length + 1)..].Trim();
        }
        catch { }
        return null;
    }

    static string? ReadUrl(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line.Substring(4).Trim();
        }
        catch { }
        return null;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLinkCo { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    }

    public static (string? target, string? args) ShortcutTarget(string lnk)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkCo();
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var ab = new StringBuilder(2048);
            link.GetArguments(ab, ab.Capacity);
            Marshal.ReleaseComObject(link);
            return (sb.Length > 0 ? Environment.ExpandEnvironmentVariables(sb.ToString()) : null, ab.ToString());
        }
        catch (Exception e) { Program.Log("lnk: " + e.Message); return (null, null); }
    }
}

public static class Steam
{
    static string? _root;
    static List<string>? _libs;

    public static string? Root => _root ??= (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)?.Replace('/', '\\');

    static List<string> Libraries()
    {
        if (_libs != null) return _libs;
        var l = new List<string>();
        if (Root != null)
        {
            l.Add(Root);
            var vdf = Path.Combine(Root, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        l.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch { }
        }
        return _libs = l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? InstallDir(int appId)
    {
        foreach (var lib in Libraries())
        {
            var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(acf)) continue;
            try
            {
                var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                if (m.Success) return Path.Combine(lib, "steamapps", "common", m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch { }
        }
        return null;
    }

    /// <summary>Portrait library capsule art from Steam's local cache.</summary>
    public static BitmapSource? Art(int appId)
    {
        if (Root == null) return null;
        var dir = Path.Combine(Root, "appcache", "librarycache", appId.ToString());
        if (!Directory.Exists(dir)) return null;
        foreach (var name in new[] { "library_600x900.jpg", "library_capsule.jpg", "library_600x900_2x.jpg" })
        {
            try
            {
                var f = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                if (f != null) return ColorTools.LoadImage(f, 240);
            }
            catch { }
        }
        return null;
    }
}

public static class ShellIcon
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] struct DIBSECTION { public BITMAP dsBm; public BITMAPINFOHEADER dsBmih; public uint f0, f1, f2; public IntPtr dshSection; public uint dsOffset; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out DIBSECTION ds);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    public static BitmapSource? Get(string path, int size)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var f) != 0 || f == null) return null;
            try
            {
                if (f.GetImage(new SIZE { cx = size, cy = size }, 0x4 /*ICONONLY*/ | 0x1 /*BIGGERSIZEOK*/, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
                try { return FromHBitmap(hbm); } finally { DeleteObject(hbm); }
            }
            finally { Marshal.ReleaseComObject(f); }
        }
        catch (Exception e) { Program.Log($"icon {path}: {e.Message}"); return null; }
    }

    static BitmapSource FromHBitmap(IntPtr hbm)
    {
        GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds);
        var bm = ds.dsBm;
        if (bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32)
        {
            var s = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            s.Freeze();
            return s;
        }
        int w = bm.bmWidth, h = bm.bmHeight, stride = bm.bmWidthBytes;
        var buf = new byte[stride * h];
        Marshal.Copy(bm.bmBits, buf, 0, buf.Length);
        if (ds.dsBmih.biHeight > 0)
        {
            var row = new byte[stride];
            for (int y = 0; y < h / 2; y++)
            {
                Buffer.BlockCopy(buf, y * stride, row, 0, stride);
                Buffer.BlockCopy(buf, (h - 1 - y) * stride, buf, y * stride, stride);
                Buffer.BlockCopy(row, 0, buf, (h - 1 - y) * stride, stride);
            }
        }
        bool anyAlpha = false;
        for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { anyAlpha = true; break; }
        var src = BitmapSource.Create(w, h, 96, 96, anyAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, buf, stride);
        src.Freeze();
        return src;
    }
}

public static class ColorTools
{
    public static BitmapSource? LoadImage(string file, int decodeWidth)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(file);
            bi.DecodePixelWidth = decodeWidth;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception e) { Program.Log($"image {file}: {e.Message}"); return null; }
    }

    /// <summary>A dark, saturated tile colour picked from the icon's dominant hue.</summary>
    public static Color Accent(BitmapSource? src)
    {
        var fallback = Color.FromRgb(0x2B, 0x2B, 0x2B);
        if (src == null) return fallback;
        try
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            double r = 0, g = 0, b = 0, wt = 0;
            for (int i = 0; i < px.Length; i += 16)
            {
                double a = px[i + 3] / 255.0;
                if (a < 0.5) continue;
                int B = px[i], G = px[i + 1], R = px[i + 2];
                int max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                double sat = max == 0 ? 0 : (max - min) / (double)max;
                double weight = a * (0.1 + sat * sat * 3);
                r += R * weight; g += G * weight; b += B * weight; wt += weight;
            }
            if (wt <= 0) return fallback;
            r /= wt; g /= wt; b /= wt;
            // to HSV, then force into a dark, rich range
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            double v = mx / 255.0, s = mx == 0 ? 0 : (mx - mn) / mx;
            double hue = 0;
            if (mx != mn)
            {
                if (mx == r) hue = 60 * (((g - b) / (mx - mn)) % 6);
                else if (mx == g) hue = 60 * (((b - r) / (mx - mn)) + 2);
                else hue = 60 * (((r - g) / (mx - mn)) + 4);
            }
            if (hue < 0) hue += 360;
            s = Math.Min(s, 0.7);
            v = 0.38;
            return FromHsv(hue, s, v);
        }
        catch { return fallback; }
    }

    public static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public static Color Scale(Color c, double f) =>
        Color.FromRgb((byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));
}

/// <summary>Prepares shortcut icons for the tiles: trims transparent padding and tells whether the
/// icon is a solid square (like Apex's red tile) that can fill the whole tile.</summary>
public static class IconTools
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, Tuple<BitmapSource, bool>> Cache = new();

    /// <summary>Largest layer of a .ico file.</summary>
    public static BitmapSource? LoadIco(string? file)
    {
        try
        {
            if (string.IsNullOrEmpty(file) || !file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return null;
            using var fs = File.OpenRead(file);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource best = dec.Frames.OrderByDescending(f => f.PixelWidth).ThenByDescending(f => f.Format.BitsPerPixel).First();
            var conv = new FormatConvertedBitmap(best, PixelFormats.Pbgra32, null, 0);
            conv.Freeze();
            return conv;
        }
        catch (Exception e) { Program.Log($"ico {file}: {e.Message}"); return null; }
    }

    public static (BitmapSource icon, bool fullBleed) Prepare(BitmapSource src)
    {
        if (Cache.TryGetValue(src, out var hit)) return (hit.Item1, hit.Item2);
        var r = Compute(src);
        Cache.AddOrUpdate(src, Tuple.Create(r.Item1, r.Item2));
        return r;
    }

    static (BitmapSource, bool) Compute(BitmapSource src)
    {
        try
        {
            var bmp = src.Format == PixelFormats.Pbgra32 || src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
            int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bmp.CopyPixels(px, stride, 0);
            bool opaqueFormat = src.Format == PixelFormats.Bgr32;
            byte A(int x, int y) => opaqueFormat ? (byte)255 : px[y * stride + x * 4 + 3];

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (A(x, y) > 16) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
            if (maxX < 0) return (src, false);
            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            BitmapSource cropped = new CroppedBitmap(bmp, new Int32Rect(minX, minY, cw, ch));
            cropped.Freeze();

            // Solid square: nearly square and opaque along the middle of every edge (corners may be rounded).
            bool square = Math.Abs(cw - ch) <= Math.Max(cw, ch) * 0.06;
            int solid = 0, total = 0, inset = Math.Max(1, cw / 64);
            for (int i = cw / 5; i < cw * 4 / 5; i++)
            {
                total += 2;
                if (A(minX + i, minY + inset) > 200) solid++;
                if (A(minX + i, maxY - inset) > 200) solid++;
            }
            for (int i = ch / 5; i < ch * 4 / 5; i++)
            {
                total += 2;
                if (A(minX + inset, minY + i) > 200) solid++;
                if (A(maxX - inset, minY + i) > 200) solid++;
            }
            bool full = square && total > 0 && solid >= total * 0.9;
            return (cropped, full);
        }
        catch { return (src, false); }
    }
}
