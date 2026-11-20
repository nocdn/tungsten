using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Tungsten;

/// <summary>System-wide keyboard shortcut (default Win+Esc) that opens/closes the panel.</summary>
public sealed class Hotkey : IDisposable
{
    const int WM_HOTKEY = 0x0312, Id = 1;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    readonly HwndSource _src;
    readonly Action _pressed;
    public string Active { get; private set; } = "";

    public Hotkey(Action pressed)
    {
        _pressed = pressed;
        // Message-only window (HWND_MESSAGE) that just receives WM_HOTKEY.
        _src = new HwndSource(new HwndSourceParameters("TungstenHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _src.AddHook(Hook);
    }

    /// <summary>Registers e.g. "Win+Escape" or "Ctrl+Shift+Space"; falls back to the next candidate if taken.</summary>
    public bool Register(params string[] candidates)
    {
        UnregisterHotKey(_src.Handle, Id);
        Active = "";
        foreach (var c in candidates)
        {
            if (!TryParse(c, out uint mods, out uint vk)) { Program.Log($"Hotkey '{c}' not understood"); continue; }
            if (RegisterHotKey(_src.Handle, Id, mods | MOD_NOREPEAT, vk)) { Active = c; Program.Log("Hotkey: " + c); return true; }
            Program.Log($"Hotkey '{c}' is taken by another app (error {Marshal.GetLastWin32Error()})");
        }
        return false;
    }

    static bool TryParse(string s, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "windows": mods |= MOD_WIN; break;
                case "esc": vk = 0x1B; break;
                default:
                    if (!Enum.TryParse<Key>(raw, true, out var k)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                    break;
            }
        }
        return vk != 0;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_HOTKEY && w.ToInt32() == Id) { handled = true; _pressed(); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_src.Handle, Id);
        _src.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
}
