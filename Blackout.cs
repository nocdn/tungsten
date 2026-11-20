using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Tungsten;

/// <summary>A transient, explicitly requested session. No configuration or startup state is saved.</summary>
public sealed class BlackoutService
{
    readonly Dispatcher _ui;
    Process? _child;
    EventWaitHandle? _stop;
    public bool IsActive => _child != null;
    public event Action? Ended;
    public BlackoutService(Dispatcher ui) => _ui = ui;

    public async Task<bool> Start()
    {
        if (IsActive) return false;
        string token = Guid.NewGuid().ToString("N");
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, BlackoutSession.EventName(token, "stop"));
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, BlackoutSession.EventName(token, "ready"));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--blackout-session");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(token);
        var child = new Process { StartInfo = start, EnableRaisingEvents = true };
        _stop = stop; _child = child;
        child.Exited += (_, _) => _ui.BeginInvoke(new Action(() =>
        {
            if (_child != child) return;
            Program.Log("Blackout ended: " + child.ExitCode);
            _child = null; _stop = null;
            stop.Dispose(); child.Dispose();
            Ended?.Invoke();
        }));
        try
        {
            if (!child.Start()) throw new InvalidOperationException("Helper did not start");
            bool shown = await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(4)));
            if (shown && _child == child && !child.HasExited) { Program.Log("Blackout started"); return true; }
            Stop();
            return false;
        }
        catch (Exception e)
        {
            Program.Log("Blackout start: " + e.Message);
            if (_child == child)
            {
                stop.Set();
                _child = null; _stop = null;
                stop.Dispose(); child.Dispose();
                Ended?.Invoke();
            }
            return false;
        }
    }

    public void Stop() { try { _stop?.Set(); } catch (ObjectDisposedException) { } }
}

/// <summary>
/// Owns the opaque windows in a separate process. Its safety thread can terminate them even
/// if either UI thread hangs. A fresh parent-owned event pair is required before any window exists.
/// </summary>
internal static class BlackoutSession
{
    internal const ulong MaximumLifetimeMs = 8 * 60 * 60 * 1000;
    internal const int ExitStopped = 0, ExitTaskManager = 10, ExitDesktop = 11, ExitExpired = 12, ExitParent = 13, ExitError = 14;
    internal static string EventName(string token, string kind) => @"Local\Tungsten.Blackout." + token + "." + kind;
    static readonly List<BlackForm> Windows = new();
    static readonly WinEventProc DesktopSwitched = (_, _, _, _, _, _, _) => Environment.Exit(ExitDesktop);
    static readonly KeyboardProc Keyboard = FilterKeyboard;

    internal static int Run(string[] args)
    {
        // This entry point cannot black out a sign-in session by itself: an already running bar
        // must have just created both events and must remain alive for the whole session.
        if (args.Length != 2 || !int.TryParse(args[0], out int parentId) ||
            !Guid.TryParseExact(args[1], "N", out _) || parentId == Environment.ProcessId) return ExitError;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (parent.HasExited || parent.SessionId != Process.GetCurrentProcess().SessionId ||
                !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return ExitError;
            using var stop = EventWaitHandle.OpenExisting(EventName(args[1], "stop"));
            using var ready = EventWaitHandle.OpenExisting(EventName(args[1], "ready"));
            return RunWindows(parent, stop, ready, MaximumLifetimeMs);
        }
        catch (Exception e) { Program.Log("Blackout helper: " + e.Message); return ExitError; }
    }

    // The shorter lifetime parameter lets the exact same expiry code be tested without an eight-hour wait.
    internal static int RunWindows(Process parent, EventWaitHandle stop, EventWaitHandle ready, ulong lifetimeMs)
    {
        if (lifetimeMs == 0 || lifetimeMs > MaximumLifetimeMs) return ExitError;
        string desktop = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
        if (desktop.Length == 0 || !OnDesktop(desktop)) return ExitDesktop;
        if (TaskManagerRunning()) return ExitTaskManager;
        ulong began = GetTickCount64();
        var guard = new Thread(() =>
        {
            try
            {
                int tick = 0;
                while (true)
                {
                    if (stop.WaitOne(200)) Environment.Exit(ExitStopped);
                    if (parent.HasExited) Environment.Exit(ExitParent);
                    if (GetTickCount64() - began >= lifetimeMs) Environment.Exit(ExitExpired);
                    if (!OnDesktop(desktop)) Environment.Exit(ExitDesktop);
                    if (++tick % 3 == 0 && TaskManagerRunning()) Environment.Exit(ExitTaskManager);
                }
            }
            catch { Environment.Exit(ExitError); } // Fail open: never strand a black window after a guard error.
        }) { IsBackground = true, Name = "Tungsten.BlackoutSafety" };
        guard.Start();

        IntPtr hook = SetWinEventHook(0x20 /*EVENT_SYSTEM_DESKTOPSWITCH*/, 0x20, IntPtr.Zero, DesktopSwitched, 0, 0, 0);
        if (hook == IntPtr.Zero) return ExitError;
        IntPtr keys = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, Keyboard, GetModuleHandle(null), 0);
        if (keys == IntPtr.Zero) { UnhookWinEvent(hook); return ExitError; }
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            var context = new ApplicationContext();
            CoverScreens();
            var dispatcher = Windows[0];
            void DisplaysChanged(object? sender, EventArgs e)
            {
                try { dispatcher.BeginInvoke(new Action(CoverScreens)); }
                catch { Environment.Exit(ExitError); }
            }
            SystemEvents.DisplaySettingsChanged += DisplaysChanged;
            EventHandler? firstIdle = null;
            firstIdle = (_, _) => { Application.Idle -= firstIdle; ready.Set(); };
            Application.Idle += firstIdle;
            try { Application.Run(context); }
            finally { SystemEvents.DisplaySettingsChanged -= DisplaysChanged; }
            return ExitStopped;
        }
        catch (Exception e) { Program.Log("Blackout windows: " + e.Message); return ExitError; }
        finally
        {
            UnhookWinEvent(hook);
            UnhookWindowsHookEx(keys);
            foreach (var window in Windows) window.Dispose();
            Windows.Clear();
        }
    }

    static void CoverScreens()
    {
        try
        {
            // Update existing windows in place so display changes do not replace the UI dispatcher.
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                if (i == Windows.Count) Windows.Add(new BlackForm());
                var form = Windows[i];
                form.Bounds = screens[i].Bounds;
                if (!form.Visible) form.Show();
                var r = screens[i].Bounds;
                Native.SetWindowPos(form.Handle, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Native.SWP_SHOWWINDOW);
            }
            if (Windows.Count == 0) throw new InvalidOperationException("No displays");
            // Keep the first window/message dispatcher alive if a display is disconnected.
            for (int i = screens.Length; i < Windows.Count; i++) Windows[i].Hide();
            Native.ForceForeground(Windows[0].Handle);
        }
        catch { Environment.Exit(ExitError); }
    }

    static bool TaskManagerRunning()
    {
        var processes = Process.GetProcessesByName("Taskmgr");
        bool found = processes.Length != 0;
        foreach (var process in processes) process.Dispose();
        return found;
    }

    static IntPtr FilterKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, message, data);
        int key = Marshal.ReadInt32(data);
        // Forward modifiers and the Windows recovery chords, never implementing or suppressing SAS.
        bool modifier = key is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5;
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool recovery = ctrl && ((key == 0x1B && (GetAsyncKeyState(0x10) & 0x8000) != 0) ||
                                (key == 0x2E && (GetAsyncKeyState(0x12) & 0x8000) != 0));
        if (modifier || recovery) return CallNextHookEx(IntPtr.Zero, code, message, data);
        // Keeps Escape, Enter, Alt+F4, Windows shortcuts and old blackout hotkeys from acting behind the cover.
        return (IntPtr)1;
    }

    static bool OnDesktop(string expected)
    {
        IntPtr desktop = OpenInputDesktop(0, false, 1 /*DESKTOP_READOBJECTS*/);
        if (desktop == IntPtr.Zero) return false;
        try { return string.Equals(DesktopName(desktop), expected, StringComparison.Ordinal); }
        finally { CloseDesktop(desktop); }
    }
    static string DesktopName(IntPtr desktop)
    {
        var text = new StringBuilder(256);
        return desktop != IntPtr.Zero && GetUserObjectInformation(desktop, 2 /*UOI_NAME*/, text, text.Capacity * 2, out _) ? text.ToString() : "";
    }

    sealed class BlackForm : Form
    {
        public BlackForm()
        {
            Text = "Tungsten Blackout";
            BackColor = Color.Black;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
        }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /*WS_EX_TOOLWINDOW*/; return cp; }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Alt+F4 and ordinary input are not dismissal shortcuts. OS logoff/shutdown remains available.
            if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            base.OnFormClosing(e);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x20 /*WM_SETCURSOR*/) { SetCursor(IntPtr.Zero); m.Result = (IntPtr)1; return; }
            base.WndProc(ref m);
        }
    }

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
    delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowsHookExW")]
    static extern IntPtr SetWindowsHookEx(int kind, KeyboardProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW")]
    static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder text, int size, out int needed);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
}
