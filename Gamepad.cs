using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace Tungsten;

public enum PadButton { Up, Down, Left, Right, A, B, X, Y, Menu, View, LB, RB, LS, RS, Guide }

[StructLayout(LayoutKind.Sequential)]
public struct XGamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

[StructLayout(LayoutKind.Sequential)]
public struct XState { public uint Packet; public XGamepad Pad; }

[StructLayout(LayoutKind.Sequential)]
public struct XBattery { public byte Type, Level; }

/// <summary>
/// Polls XInput on a background thread. Uses the hidden XInputGetStateEx (ordinal 100) so the
/// Guide/Home button is visible. Works regardless of which window has focus.
/// </summary>
public sealed class Gamepad : IDisposable
{
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern uint XInputGetStateEx(uint index, out XState state);
    [DllImport("xinput1_4.dll")] static extern uint XInputGetBatteryInformation(uint index, byte devType, out XBattery battery);

    public const ushort DUp = 0x1, DDown = 0x2, DLeft = 0x4, DRight = 0x8, StartBtn = 0x10, Back = 0x20, LThumb = 0x40, RThumb = 0x80,
        LShoulder = 0x100, RShoulder = 0x200, Guide = 0x400, BtnA = 0x1000, BtnB = 0x2000, BtnX = 0x4000, BtnY = 0x8000;

    readonly Dispatcher _ui;
    readonly Thread _thread;
    volatile bool _run = true;

    /// <summary>When false only the Guide button is dispatched (overlay hidden, game has the pad).</summary>
    public volatile bool Navigation;
    public volatile bool Blackout;
    public event Action<PadButton>? Pressed;

    public readonly XState[] Latest = new XState[4];
    public readonly bool[] Connected = new bool[4];

    public Gamepad(Dispatcher ui)
    {
        _ui = ui;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Tungsten.Gamepad", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();
    public void Dispose() => _run = false;

    public bool TryGetState(out int slot, out XState state)
    {
        for (int i = 0; i < 4; i++)
            if (Connected[i]) { slot = i; state = Latest[i]; return true; }
        slot = -1; state = default; return false;
    }

    /// <summary>Returns battery type (1 = wired, 2/3 = battery, 0xFF unknown) and level 0-3, or null.</summary>
    public (byte type, byte level)? Battery()
    {
        for (uint i = 0; i < 4; i++)
        {
            if (!Connected[i]) continue;
            try { if (XInputGetBatteryInformation(i, 0, out var b) == 0) return (b.Type, b.Level); } catch { }
        }
        return null;
    }

    void Loop()
    {
        const int n = 15;
        var nextProbe = new long[4];
        var wasDown = new bool[n];
        var heldSince = new long[n];
        var nextRepeat = new long[n];
        var down = new bool[n];
        // Stick hysteresis state: -1, 0, +1 per axis (LX, LY, RX, RY)
        var axis = new int[4];
        var sw = Stopwatch.StartNew();

        while (_run)
        {
            long now = sw.ElapsedMilliseconds;
            ushort buttons = 0;
            short lx = 0, ly = 0, rx = 0, ry = 0;
            for (uint i = 0; i < 4; i++)
            {
                if (!Connected[i] && now < nextProbe[i]) continue; // probing empty slots is slow; do it once a second
                uint rc;
                XState s;
                try { rc = XInputGetStateEx(i, out s); } catch { rc = 1167; s = default; }
                if (rc == 0)
                {
                    Connected[i] = true;
                    Latest[i] = s;
                    buttons |= s.Pad.Buttons;
                    if (Math.Abs((int)s.Pad.LX) > Math.Abs((int)lx)) lx = s.Pad.LX;
                    if (Math.Abs((int)s.Pad.LY) > Math.Abs((int)ly)) ly = s.Pad.LY;
                    if (Math.Abs((int)s.Pad.RX) > Math.Abs((int)rx)) rx = s.Pad.RX;
                    if (Math.Abs((int)s.Pad.RY) > Math.Abs((int)ry)) ry = s.Pad.RY;
                }
                else
                {
                    Connected[i] = false;
                    nextProbe[i] = now + 1000;
                }
            }

            short[] vals = { lx, ly, rx, ry };
            for (int a = 0; a < 4; a++)
            {
                int v = vals[a];
                if (axis[a] == 0) { if (v > 17000) axis[a] = 1; else if (v < -17000) axis[a] = -1; }
                else if (Math.Abs(v) < 9000 || Math.Sign(v) != axis[a]) axis[a] = 0;
            }

            Array.Clear(down);
            down[(int)PadButton.Up] = (buttons & DUp) != 0 || axis[1] == 1 || axis[3] == 1;
            down[(int)PadButton.Down] = (buttons & DDown) != 0 || axis[1] == -1 || axis[3] == -1;
            down[(int)PadButton.Left] = (buttons & DLeft) != 0 || axis[0] == -1 || axis[2] == -1;
            down[(int)PadButton.Right] = (buttons & DRight) != 0 || axis[0] == 1 || axis[2] == 1;
            down[(int)PadButton.A] = (buttons & BtnA) != 0;
            down[(int)PadButton.B] = (buttons & BtnB) != 0;
            down[(int)PadButton.X] = (buttons & BtnX) != 0;
            down[(int)PadButton.Y] = (buttons & BtnY) != 0;
            down[(int)PadButton.Menu] = (buttons & StartBtn) != 0;
            down[(int)PadButton.View] = (buttons & Back) != 0;
            down[(int)PadButton.LB] = (buttons & LShoulder) != 0;
            down[(int)PadButton.RB] = (buttons & RShoulder) != 0;
            down[(int)PadButton.LS] = (buttons & LThumb) != 0;
            down[(int)PadButton.RS] = (buttons & RThumb) != 0;
            down[(int)PadButton.Guide] = (buttons & Guide) != 0;

            for (int b = 0; b < n; b++)
            {
                bool isDir = b <= (int)PadButton.Right;
                if (down[b] && !wasDown[b])
                {
                    heldSince[b] = now;
                    nextRepeat[b] = now + 380;
                    Fire((PadButton)b);
                }
                else if (down[b] && isDir && now >= nextRepeat[b])
                {
                    nextRepeat[b] = now + (now - heldSince[b] > 1500 ? 55 : 95);
                    Fire((PadButton)b);
                }
                wasDown[b] = down[b];
            }

            Thread.Sleep(8);
        }
    }

    void Fire(PadButton b)
    {
        if (!Navigation && b != PadButton.Guide && !(Blackout && b == PadButton.Y)) return;
        _ui.BeginInvoke(new Action(() =>
        {
            try { Pressed?.Invoke(b); }
            catch (Exception e) { Program.Log("Pad handler: " + e); }
        }));
    }
}
