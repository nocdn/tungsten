using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Tungsten;

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

public sealed class WinInfo
{
    public IntPtr Hwnd;
    public uint Pid;
    public string Title = "";
    public string? Exe;
    public bool Minimized;
}

public static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    /// <summary>3 = exclusive Direct3D fullscreen, 2 = busy/fullscreen app, 5 = normal.</summary>
    public static int NotificationState() => SHQueryUserNotificationState(out int s) == 0 ? s : -1;
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

    public const int SW_MINIMIZE = 6, SW_RESTORE = 9, SW_FORCEMINIMIZE = 11;
    public const uint WM_SYSCOMMAND = 0x0112, WM_CLOSE = 0x0010;
    public static readonly IntPtr SC_MINIMIZE = (IntPtr)0xF020;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "NotifyIconOverflowWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
        "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd", "Windows.UI.Input.InputSite.WindowClass",
    };

    public static string GetTitle(IntPtr h)
    {
        int n = GetWindowTextLength(h);
        if (n <= 0) return "";
        var sb = new StringBuilder(n + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClass(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string? ProcessPath(uint pid)
    {
        IntPtr hp = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int n = sb.Capacity;
            return QueryFullProcessImageName(hp, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(hp); }
    }

    /// <summary>A real, user-facing top-level app window (not shell, tool, owned or cloaked).</summary>
    public static bool IsCandidate(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h) || !IsWindowVisible(h)) return false;
        if (GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return false;
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out int cloaked, 4) == 0 && cloaked != 0) return false;
        if (ShellClasses.Contains(GetClass(h))) return false;
        if (GetWindowTextLength(h) == 0) return false;
        return true;
    }

    public static WinInfo? Describe(IntPtr h)
    {
        if (!IsWindow(h)) return null;
        GetWindowThreadProcessId(h, out uint pid);
        return new WinInfo { Hwnd = h, Pid = pid, Title = GetTitle(h), Exe = ProcessPath(pid), Minimized = IsIconic(h) };
    }

    public static List<WinInfo> AppWindows()
    {
        var list = new List<WinInfo>();
        uint me = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsCandidate(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me) return true;
            list.Add(new WinInfo { Hwnd = h, Pid = pid, Title = GetTitle(h), Exe = ProcessPath(pid), Minimized = IsIconic(h) });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static RECT MonitorRect(IntPtr h)
    {
        IntPtr mon;
        if (h != IntPtr.Zero) mon = MonitorFromWindow(h, 2 /*NEAREST*/);
        else { GetCursorPos(out var p); mon = MonitorFromPoint(p, 2); }
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        return mi.rcMonitor;
    }

    public static bool IsFullscreen(IntPtr h)
    {
        if (IsIconic(h) || !GetWindowRect(h, out var r)) return false;
        var m = MonitorRect(h);
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    public static void MakeTopmost(IntPtr h) =>
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>
    /// Bring a window to the foreground even when Windows' foreground lock would normally refuse
    /// (e.g. while a fullscreen game owns focus).
    /// </summary>
    public static void ForceForeground(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        if (GetForegroundWindow() == h) return;
        if (SetForegroundWindow(h) && GetForegroundWindow() == h) return;

        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }

        if (GetForegroundWindow() != h)
        {
            // Last resort: a synthetic Alt tap makes us the "last input" process, which lifts the lock.
            keybd_event(0x12, 0, 0x1, UIntPtr.Zero);
            keybd_event(0x12, 0, 0x1 | 0x2, UIntPtr.Zero);
            SetForegroundWindow(h);
        }
    }
}
