using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Tungsten;

/// <summary>Live view of every controller input (buttons, sticks, triggers, battery).</summary>
public sealed class TestWindow : Window
{
    readonly Hub _hub;
    readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly Dictionary<ushort, Border> _buttons = new();
    readonly Ellipse _ls = Dot(), _rs = Dot();
    readonly Rectangle _lt = Bar(), _rt = Bar();
    readonly TextBlock _raw = Ui.Text("", 13, Ui.Fg2);
    readonly Canvas _c = new() { Width = 720, Height = 330 };
    DateTime? _bHeld;

    static readonly Brush Off = Ui.B(0xFF2C2C2C), On = Ui.B(0xFF3FA535);

    public TestWindow(Hub hub)
    {
        _hub = hub;
        Title = "Tungsten - Controller test";
        Icon = AppIcon.WindowIcon();
        Width = 780; Height = 470;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = Ui.B(0xFF181818);
        FontFamily = Ui.Font;

        var root = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
        root.Children.Add(Ui.Text("Press anything on your controller", 20, Ui.Fg, FontWeights.SemiBold));
        root.Children.Add(Ui.Text("Hold B for one second or press Esc to close.", 13, Ui.Fg3));
        root.Children.Add(_c);
        root.Children.Add(_raw);
        Content = root;

        Pill("LB", Gamepad.LShoulder, 60, 20, 80); Pill("RB", Gamepad.RShoulder, 580, 20, 80);
        Place(_lt, 60, 4); Place(_rt, 580, 4);
        Pill("View", Gamepad.Back, 270, 110, 56); Pill("Home", Gamepad.Guide, 335, 100, 50, 50); Pill("Menu", Gamepad.StartBtn, 394, 110, 56);
        Pill("Y", Gamepad.BtnY, 560, 80, 40, 40); Pill("X", Gamepad.BtnX, 515, 125, 40, 40);
        Pill("B", Gamepad.BtnB, 605, 125, 40, 40); Pill("A", Gamepad.BtnA, 560, 170, 40, 40);
        Pill("↑", Gamepad.DUp, 210, 190, 36, 36); Pill("↓", Gamepad.DDown, 210, 262, 36, 36);
        Pill("←", Gamepad.DLeft, 174, 226, 36, 36); Pill("→", Gamepad.DRight, 246, 226, 36, 36);
        Stick(80, 100, _ls); Stick(430, 200, _rs);
        Pill("LS", Gamepad.LThumb, 108, 216, 44); Pill("RS", Gamepad.RThumb, 458, 316 - 0, 44);

        _t.Tick += (_, _) => Tick();
        Loaded += (_, _) => _t.Start();
        Closed += (_, _) => _t.Stop();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    static Ellipse Dot() => new() { Width = 16, Height = 16, Fill = Brushes.White };
    static Rectangle Bar() => new() { Width = 0, Height = 8, Fill = On, RadiusX = 4, RadiusY = 4 };

    void Place(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); _c.Children.Add(e); }

    void Pill(string label, ushort bit, double x, double y, double w, double h = 30)
    {
        var b = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(Math.Min(w, h) / 2), Background = Off,
            Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _buttons[bit] = b;
        Place(b, x, y);
    }

    void Stick(double x, double y, Ellipse dot)
    {
        Place(new Ellipse { Width = 100, Height = 100, Stroke = Ui.Stroke, StrokeThickness = 2, Fill = Ui.B(0xFF222222) }, x, y);
        dot.Tag = new Point(x + 50, y + 50);
        Place(dot, x + 42, y + 42);
    }

    void MoveDot(Ellipse dot, short sx, short sy)
    {
        var c = (Point)dot.Tag;
        Canvas.SetLeft(dot, c.X + sx / 32768.0 * 42 - 8);
        Canvas.SetTop(dot, c.Y - sy / 32768.0 * 42 - 8);
    }

    void Tick()
    {
        if (!_hub.Pad.TryGetState(out int slot, out var s))
        {
            _raw.Text = "No XInput controller detected - plug it in or switch it to X-input mode.";
            return;
        }
        var p = s.Pad;
        foreach (var (bit, b) in _buttons) b.Background = (p.Buttons & bit) != 0 ? On : Off;
        MoveDot(_ls, p.LX, p.LY);
        MoveDot(_rs, p.RX, p.RY);
        _lt.Width = p.LT / 255.0 * 80;
        _rt.Width = p.RT / 255.0 * 80;
        var bat = _hub.Pad.Battery();
        string batText = bat == null ? "?" : bat.Value.type == 1 ? "wired" : $"level {bat.Value.level}/3";
        _raw.Text = $"Slot {slot}  ·  packet {s.Packet}  ·  buttons 0x{p.Buttons:X4}  ·  LX {p.LX}  LY {p.LY}  RX {p.RX}  RY {p.RY}  ·  LT {p.LT}  RT {p.RT}  ·  battery {batText}";

        if ((p.Buttons & Gamepad.BtnB) != 0)
        {
            _bHeld ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _bHeld.Value).TotalMilliseconds > 1000) Close();
        }
        else _bHeld = null;
    }
}
