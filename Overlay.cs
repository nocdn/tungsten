using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Devices.WiFi;

namespace Tungsten;

enum ItemKind { Minimize, Blackout, Settings, Game, Add, Bluetooth, Volume, Wifi }

sealed class GameModel
{
    public ResolvedGame? Pinned;
    public WinInfo? Window;
    public string Name = "";
    public string? Exe;
    public BitmapSource? Icon, Art;
    public Color Accent;
    public string Key => Pinned != null ? "g:" + Pinned.Entry.Id : "w:" + (Exe ?? Window!.Hwnd.ToString());
}

sealed class BarItem
{
    public ItemKind Kind;
    public string Key = "";
    public int Group;
    public GameModel? Game;
    public Grid Root = null!;
    public Border Face = null!;
    public Border? Ring;
    public Border? Dot;
    public FrameworkElement? Glyph;
    public readonly ScaleTransform Scale = new(1, 1);
}

sealed class MItem
{
    public string Key = "", Glyph = "", Label = "", Detail = "";
    public Brush? DetailBrush;
    public bool Enabled = true, Header, KeepOpen;
    public Action? A, Y;
    public Action<int>? Adjust;
    public Func<FrameworkElement>? Control;
    public bool Focusable => Enabled && !Header;
}

sealed class DropMenu
{
    public string Kind = "", Title = "", AnchorKey = "";
    public double Width = 320;
    public double MaxListHeight = 46 * 9;
    public Func<List<MItem>> Build = () => new();
    public Func<(bool busy, string? status)>? Status;
    public Action? Closed;
    public List<MItem> Items = new();
}

sealed class PadKey
{
    public int Row, Col, Span = 1;
    public string Cmd = "";
    public string? Ch;
    public Border View = null!;
    public TextBlock Text = null!;
}

sealed class PadState
{
    public string Title = "", Error = "";
    public readonly StringBuilder Text = new();
    public bool Reveal, Shift;
    public int Layer, Row = 1, Col;
    public Action<string> Done = _ => { };
    public Border View = null!;
    public TextBlock Field = null!;
    public Grid Keys = null!;
    public readonly List<PadKey> KeyList = new();
}

/// <summary>The Xbox-style guide bar. One full-monitor, transparent, topmost window, reused between opens.</summary>
public sealed class Overlay : Window
{
    const double Tile = 72;

    readonly Hub _hub;
    IntPtr _hwnd;

    readonly Grid _root = new();
    readonly Border _shell = new();
    readonly TranslateTransform _shellShift = new();
    readonly StackPanel _left = H(), _games = H(), _right = H();
    readonly ScrollViewer _gamesScroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        MaxWidth = 8 * (Tile + 16) + 8, Focusable = false,
    };
    readonly Canvas _layer = new();
    readonly Border _toast = new();
    readonly TextBlock _toastText = Ui.Text("", 14, Ui.Fg);
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _menuRefresh = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };

    List<BarItem> _items = new();
    int _focus;
    string _sig = "";

    DropMenu? _menu;
    int _menuFocus = -1;
    bool _menuTouched;
    Border? _menuView;
    StackPanel? _menuList;
    TextBlock? _menuStatus;
    FrameworkElement? _menuSpinner;
    readonly List<(Border row, TextBlock g, TextBlock l, TextBlock d, MItem item)> _rows = new();
    Slider? _audioSlider;
    bool _updatingAudioSlider;

    PadState? _pad;

    IntPtr _prevFg;
    WinInfo? _current;
    DateTime _openedAt;
    bool _modal;
    int _closeToken;
    readonly HashSet<IntPtr> _hubMinimized = new();
    readonly Dictionary<string, ResolvedGame> _resolved = new();
    readonly Dictionary<string, (BitmapSource? icon, Color accent)> _exeCache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsOpen { get; private set; }

    static StackPanel H() => new() { Orientation = Orientation.Horizontal };

    public Overlay(Hub hub)
    {
        _hub = hub;
        Title = "Tungsten";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = true;
        Left = 0; Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        FontFamily = Ui.Font;
        Foreground = Ui.Fg;
        UseLayoutRounding = true;
        Content = _root;

        BuildChrome();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        };
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnText;
        Deactivated += OnDeactivated;
        _root.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == _root) Dismiss(true); };
        _tick.Tick += (_, _) => Tick();
        _menuRefresh.Tick += (_, _) => { _menuRefresh.Stop(); if (_menu != null) RenderMenuItems(true); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Ui.Anim(_toast, OpacityProperty, 0, 250); };
        _hub.Bt.Changed += () => Dispatcher.BeginInvoke(new Action(() => QueueMenuRefresh("bt")));
        _hub.Wifi.Changed += () => Dispatcher.BeginInvoke(new Action(() => QueueMenuRefresh("wifi")));
        _hub.Audio.Changed += () => Dispatcher.BeginInvoke(new Action(() => QueueMenuRefresh("audio")));

        new WindowInteropHelper(this).EnsureHandle();
    }

    // ───────────────────────────── chrome ─────────────────────────────

    void BuildChrome()
    {
        _root.Background = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0xB0, 0, 0, 0), 0),
            new(Color.FromArgb(0x60, 0, 0, 0), 0.35),
            new(Color.FromArgb(0x38, 0, 0, 0), 1),
        }, 90);

        // Just the row of tiles: actions | games | actions. Every slot is the same size.
        var row = H();
        row.VerticalAlignment = VerticalAlignment.Center;
        var slotMargin = new Thickness(4, 14, 4, 14); // room for the focus ring
        _left.Margin = slotMargin;
        _games.Margin = slotMargin;
        _right.Margin = slotMargin;
        row.Children.Add(_left);
        row.Children.Add(Sep());
        _gamesScroll.Content = _games;
        row.Children.Add(_gamesScroll);
        row.Children.Add(Sep());
        row.Children.Add(_right);

        _shell.Child = row;
        _shell.CornerRadius = new CornerRadius(22);
        _shell.Padding = new Thickness(8, 0, 8, 0);
        _shell.Background = Ui.B(0xF4202123);
        _shell.BorderBrush = Ui.Stroke;
        _shell.BorderThickness = new Thickness(1);
        _shell.Effect = new DropShadowEffect { BlurRadius = 48, ShadowDepth = 10, Opacity = 0.6, Color = Colors.Black, Direction = 270 };
        _shell.HorizontalAlignment = HorizontalAlignment.Center;
        _shell.VerticalAlignment = VerticalAlignment.Top;
        _shell.Margin = new Thickness(0, 52, 0, 0);
        _shell.LayoutTransform = new ScaleTransform(0.75, 0.75); // whole bar at 75%
        _shell.RenderTransform = _shellShift;
        _root.Children.Add(_shell);
        _root.Children.Add(_layer);

        _toast.Child = _toastText;
        _toast.CornerRadius = new CornerRadius(14);
        _toast.Background = Ui.B(0xF2303133);
        _toast.BorderBrush = Ui.Stroke;
        _toast.BorderThickness = new Thickness(1);
        _toast.Padding = new Thickness(20, 12, 20, 12);
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.VerticalAlignment = VerticalAlignment.Bottom;
        _toast.Margin = new Thickness(0, 0, 0, 110);
        _toast.Opacity = 0;
        _toast.IsHitTestVisible = false;
        _root.Children.Add(_toast);
    }

    static Border Sep() => new()
    {
        Width = 1, Height = 44, Background = Ui.B(0x26FFFFFF), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
    };

    public void ShowToast(string msg) => Toast(msg);

    void Toast(string msg)
    {
        _toastText.Text = msg;
        Ui.Anim(_toast, OpacityProperty, 1, 150);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ───────────────────────────── open / close ─────────────────────────────

    public void Open()
    {
        if (IsOpen) return;
        try
        {
            _prevFg = Native.GetForegroundWindow();
            if (_prevFg == _hwnd) _prevFg = IntPtr.Zero;
            _prevWasShown = _prevFg != IntPtr.Zero && !Native.IsIconic(_prevFg);
            _current = Native.IsCandidate(_prevFg) ? Native.Describe(_prevFg) : null;
            if (_current != null && _current.Pid == (uint)Environment.ProcessId) _current = null;

            _menu = null;
            _pad = null;
            _rows.Clear();
            _layer.Children.Clear();
            _menuView = null;

            Rebuild(initial: true);

            IsOpen = true;
            _modal = false;
            _openedAt = DateTime.UtcNow;
            _closeToken++;

            var m = Native.MonitorRect(_prevFg);
            _root.BeginAnimation(OpacityProperty, null);
            _root.Opacity = 0;
            Show();
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, m.Left, m.Top, m.Width, m.Height, Native.SWP_SHOWWINDOW);
            Native.ForceForeground(_hwnd);
            Activate();
            Focus();

            Ui.Anim(_root, OpacityProperty, 1, 140, 0);
            Ui.Anim(_shellShift, TranslateTransform.YProperty, 0, 260, -34);
            _hub.Pad.Navigation = true;
            _tick.Start();
            EnsureFocus(_closeToken);
            if (_current != null)
            {
                bool adminGame = !Elevation.IsElevated && Elevation.IsProcessElevated(_current.Pid);
                Program.Log($"Open over '{_current.Title}' fs={Native.IsFullscreen(_prevFg)} notif={Native.NotificationState()} adminGame={adminGame}");
                if (adminGame) Toast("This game runs as administrator - turn on Admin mode in Settings to show over it");
            }
        }
        catch (Exception e) { Program.Log("Open: " + e); }
    }

    /// <summary>Some games/apps grab focus back right after we appear; retry a few times.</summary>
    async void EnsureFocus(int token)
    {
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(180);
            if (!IsOpen || token != _closeToken || _modal) return;
            if (Native.GetForegroundWindow() == _hwnd) continue;
            Native.MakeTopmost(_hwnd);
            Native.ForceForeground(_hwnd);
        }
        if (!IsOpen || token != _closeToken || _modal || Native.GetForegroundWindow() == _hwnd) return;
        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg == _prevFg && Native.IsFullscreen(fg))
        {
            // A fullscreen game won't give up focus, so it would hide the guide. Minimise it instead
            // (closing the guide brings it back), then try once more.
            Program.Log("Overlay could not take focus from a fullscreen game - minimizing it so the guide is visible");
            Native.ShowWindowAsync(fg, Native.SW_MINIMIZE);
            await Task.Delay(250);
            Native.MakeTopmost(_hwnd);
            Native.ForceForeground(_hwnd);
        }
        else Program.Log("Overlay could not take focus (controller still works).");
    }

    public void Dismiss(bool restoreFocus)
    {
        if (!IsOpen) return;
        IsOpen = false;
        _hub.Pad.Navigation = false;
        _tick.Stop();
        _pad = null;
        CloseMenu(animate: false);
        _layer.Children.Clear();

        if (restoreFocus && _prevFg != IntPtr.Zero && Native.IsWindow(_prevFg))
        {
            if (!Native.IsIconic(_prevFg)) Native.ForceForeground(_prevFg);
            // Exclusive-fullscreen games minimise themselves when the guide takes focus: bring them back.
            else if (_prevWasShown) RestoreAndFocus(_prevFg);
        }

        int token = ++_closeToken;
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        a.Completed += (_, _) => { if (token == _closeToken && !IsOpen) Hide(); };
        _root.BeginAnimation(OpacityProperty, a);
        Ui.Anim(_shellShift, TranslateTransform.YProperty, -20, 110);
    }

    bool _prevWasShown;

    static async void RestoreAndFocus(IntPtr h)
    {
        Native.AllowSetForegroundWindow(-1);
        Native.ShowWindowAsync(h, Native.SW_RESTORE);
        for (int i = 0; i < 8; i++)
        {
            Native.ForceForeground(h);
            await Task.Delay(150);
            if (Native.GetForegroundWindow() == h && !Native.IsIconic(h)) return;
            if (Native.IsIconic(h)) Native.ShowWindowAsync(h, Native.SW_RESTORE);
        }
        Program.Log("Could not restore the game after closing the guide");
    }

    void OnDeactivated(object? sender, EventArgs e)
    {
        if (!IsOpen || _modal || (DateTime.UtcNow - _openedAt).TotalMilliseconds < 700) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var fg = Native.GetForegroundWindow();
            // The user switched to another app (alt-tab / click). Our controller input doesn't need focus,
            // so anything else (focus bouncing back to the game, taskbar) keeps the guide open...
            if (IsOpen && !_modal && fg != _hwnd && fg != _prevFg && Native.IsCandidate(fg)) { Program.Log("Closed: switched to another app"); Dismiss(false); }
            // ...except an exclusive-fullscreen game taking focus back: it draws over us, so the guide would
            // stay "open" invisibly and the next Home press would close it instead of opening it.
            else if (IsOpen && !_modal && fg == _prevFg && Native.NotificationState() == 3) { Program.Log("Closed: fullscreen game took focus back"); Dismiss(false); }
        }));
    }

    // ───────────────────────────── input ─────────────────────────────

    public void OnPad(PadButton b)
    {
        if (_hub.Blackout.IsActive)
        {
            if (b is PadButton.Guide or PadButton.Y) _hub.StopBlackout();
            return;
        }
        if (b == PadButton.Y && IsOpen && !_modal) { _hub.StartBlackout(); return; }
        if (b == PadButton.Guide)
        {
            if (_modal || _hub.TestActive) return;
            if (IsOpen) Dismiss(true); else Open();
            return;
        }
        Nav(b);
    }

    void Nav(PadButton b)
    {
        if (!IsOpen || _modal) return;
        if (_pad != null) { PadInput(b); return; }
        if (_menu != null) { MenuInput(b); return; }
        BarInput(b);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_pad != null)
        {
            switch (key)
            {
                case Key.Back: PadBackspace(); break;
                case Key.Enter: PadSubmit(); break;
                case Key.Escape: ClosePad(); break;
                case Key.Left: PadInput(PadButton.Left); break;
                case Key.Right: PadInput(PadButton.Right); break;
                case Key.Up: PadInput(PadButton.Up); break;
                case Key.Down: PadInput(PadButton.Down); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        PadButton? b = key switch
        {
            Key.Left => PadButton.Left, Key.Right => PadButton.Right, Key.Up => PadButton.Up, Key.Down => PadButton.Down,
            Key.Enter or Key.Space => PadButton.A, Key.Escape or Key.Back => PadButton.B,
            Key.Apps or Key.M => PadButton.Menu, Key.Delete or Key.Y => PadButton.Y,
            Key.PageUp => PadButton.LB, Key.PageDown => PadButton.RB,
            _ => null,
        };
        if (b != null) { e.Handled = true; Nav(b.Value); }
    }

    void OnText(object sender, TextCompositionEventArgs e)
    {
        if (_pad == null) return;
        foreach (char c in e.Text) if (!char.IsControl(c)) _pad.Text.Append(c);
        UpdatePadField();
        e.Handled = true;
    }

    // ───────────────────────────── models ─────────────────────────────

    ResolvedGame Resolved(GameEntry e)
    {
        if (!_resolved.TryGetValue(e.Id, out var r)) _resolved[e.Id] = r = GameResolver.Resolve(e);
        return r;
    }

    WinInfo? Pick(List<WinInfo> wins)
    {
        if (wins.Count == 0) return null;
        return wins.FirstOrDefault(w => _hubMinimized.Contains(w.Hwnd))
            ?? wins.FirstOrDefault(w => !w.Minimized && Native.IsFullscreen(w.Hwnd))
            ?? wins.FirstOrDefault(w => !w.Minimized)
            ?? wins[0];
    }

    (BitmapSource? icon, Color accent) ExeVisual(string? exe)
    {
        if (exe == null) return (null, Color.FromRgb(0x2B, 0x2B, 0x2B));
        if (!_exeCache.TryGetValue(exe, out var v))
        {
            var icon = ShellIcon.Get(exe, 256);
            _exeCache[exe] = v = (icon, ColorTools.Accent(icon));
        }
        return v;
    }

    string NameFor(WinInfo w)
    {
        var n = w.Exe != null ? GameResolver.ExeDisplayName(w.Exe) : null;
        if (string.IsNullOrWhiteSpace(n)) n = w.Title.Length is > 0 and <= 40 ? w.Title : Path.GetFileNameWithoutExtension(w.Exe ?? w.Title);
        return n!;
    }

    List<GameModel> BuildModels()
    {
        var wins = Native.AppWindows();
        _hubMinimized.RemoveWhere(h => !Native.IsWindow(h));
        var used = new HashSet<IntPtr>();
        var list = new List<GameModel>();
        foreach (var e in _hub.Config.Games)
        {
            var rg = Resolved(e);
            var matches = wins.Where(w => rg.Matches(w.Exe)).ToList();
            foreach (var mw in matches) used.Add(mw.Hwnd);
            list.Add(new GameModel { Pinned = rg, Window = Pick(matches), Name = e.Name, Exe = rg.ExePath, Icon = rg.Icon, Art = rg.Art, Accent = rg.Accent });
        }
        // Unpinned games still show up while they're running: anything Tungsten minimized, plus the
        // fullscreen app that was in front when the guide opened.
        var extra = wins.Where(w => !used.Contains(w.Hwnd) &&
            (_hubMinimized.Contains(w.Hwnd) || (_current != null && w.Hwnd == _current.Hwnd && Native.IsFullscreen(w.Hwnd))));
        foreach (var grp in extra.GroupBy(w => w.Exe ?? w.Hwnd.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            var w = Pick(grp.ToList())!;
            var (icon, accent) = ExeVisual(w.Exe);
            list.Add(new GameModel { Window = w, Name = NameFor(w), Exe = w.Exe, Icon = icon, Accent = accent });
        }
        return list;
    }

    string CurrentName()
    {
        if (_current == null) return "";
        foreach (var it in _items)
            if (it.Game?.Window?.Hwnd == _current.Hwnd) return it.Game.Name;
        return NameFor(_current);
    }

    // ───────────────────────────── bar ─────────────────────────────

    BarItem? Cur => _focus >= 0 && _focus < _items.Count ? _items[_focus] : null;

    void Rebuild(bool initial)
    {
        string? keep = initial ? null : Cur?.Key;
        var models = BuildModels();
        _sig = string.Join("|", models.Select(m => m.Key));

        _left.Children.Clear(); _games.Children.Clear(); _right.Children.Clear();
        _items = new List<BarItem>();

        Add(_left, MakeAction(ItemKind.Minimize, "act:min", "", 0));
        Add(_left, MakeAction(ItemKind.Blackout, "act:blackout", "\uE708", 0));
        Add(_left, MakeAction(ItemKind.Settings, "act:set", "", 0));
        foreach (var m in models) Add(_games, MakeGame(m));
        Add(_games, MakeAdd());
        Add(_right, MakeAction(ItemKind.Bluetooth, "act:bt", "", 2));
        Add(_right, MakeAction(ItemKind.Volume, "act:audio", "\uE767", 2));
        Add(_right, MakeAction(ItemKind.Wifi, "act:wifi", "", 2));

        int idx = -1;
        if (keep != null) idx = _items.FindIndex(i => i.Key == keep);
        if (idx < 0 && initial && _current != null)
            idx = _items.FindIndex(i => i.Game?.Window?.Hwnd == _current.Hwnd);
        if (idx < 0) idx = _items.FindIndex(i => i.Kind == ItemKind.Game);
        if (idx < 0) idx = _items.FindIndex(i => i.Kind == ItemKind.Add);
        _focus = -1;
        SetFocus(idx, animate: !initial);
        UpdateMinimizeState();
        if (_menu != null) PositionMenu();
    }

    void Add(Panel p, BarItem it)
    {
        _items.Add(it);
        p.Children.Add(it.Root);
        int index = _items.Count - 1;
        it.Root.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_modal) return; CloseMenu(); SetFocus(_items.IndexOf(it)); Activate(it); };
        it.Root.MouseRightButtonUp += (_, e) => { e.Handled = true; if (_modal) return; CloseMenu(); SetFocus(_items.IndexOf(it)); Options(it); };
    }

    /// <summary>
    /// Myna UI "minimize" arrows (24-unit viewBox). Drawn at the same ink size (~26px) and stroke
    /// (~1.75px) as the 28px Segoe Fluent glyphs used for the other actions, so they read as one set.
    /// </summary>
    /// <summary>Menu rows use this in place of a font glyph to get the minimize arrows.</summary>
    const string MinimizeGlyph = "minimize-arrows";

    /// <param name="box">Square the icon sits in.</param>
    /// <param name="ink">Size of the drawn arrows (the gear glyph's ink is ~93% of its font size).</param>
    /// <param name="stroke">Line width in pixels, matched to Segoe Fluent at that size.</param>
    static System.Windows.Shapes.Path MinimizeIcon(double box = 40, double ink = 26, double stroke = 1.75)
    {
        double scale = ink / 16; // the artwork spans 16 of its 24 units
        var geo = Geometry.Parse("M20 10h-6V4M4 14h6v6M20 4l-6 6m-4 4l-6 6").Clone();
        geo.Transform = new TransformGroup
        {
            Children = { new ScaleTransform(scale, scale), new TranslateTransform((box - 24 * scale) / 2, (box - 24 * scale) / 2) },
        };
        return new System.Windows.Shapes.Path
        {
            Data = geo, Stroke = Ui.Fg, StrokeThickness = stroke, Width = box, Height = box,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = false,
        };
    }

    BarItem MakeAction(ItemKind kind, string key, string glyph, int group)
    {
        var it = new BarItem { Kind = kind, Key = key, Group = group };
        // Same footprint as a game tile, grey face, slightly larger icon.
        it.Glyph = kind == ItemKind.Minimize ? MinimizeIcon() : Ui.Glyph(glyph, 28, Ui.Fg);
        it.Face = new Border
        {
            Width = Tile, Height = Tile, CornerRadius = new CornerRadius(10), Background = Ui.B(0xFF38393B), Child = it.Glyph,
            BorderBrush = Ui.B(0x14FFFFFF), BorderThickness = new Thickness(1),
        };
        it.Ring = FocusRing();
        it.Root = TileRoot(it);
        it.Root.Children.Add(it.Ring);
        it.Root.Children.Add(it.Face);
        return it;
    }

    BarItem MakeGame(GameModel m)
    {
        var it = new BarItem { Kind = ItemKind.Game, Key = m.Key, Group = 1, Game = m };
        it.Face = new Border { Width = Tile, Height = Tile, CornerRadius = new CornerRadius(10), BorderBrush = Ui.B(0x22FFFFFF), BorderThickness = new Thickness(1) };
        // Solid square shortcut icons (Apex, Outlast...) fill the whole tile, like on the desktop.
        var prepared = m.Icon != null ? IconTools.Prepare(m.Icon) : default;
        if (m.Icon != null && prepared.fullBleed)
        {
            var brush = new ImageBrush(prepared.icon) { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            it.Face.Background = brush;
        }
        else
        {
            var top = ColorTools.Scale(m.Accent, 1.35);
            var bottom = ColorTools.Scale(m.Accent, 0.6);
            it.Face.Background = new LinearGradientBrush(top, bottom, 60);
            if (m.Icon != null)
                it.Face.Child = new Image { Source = m.Icon, Width = 42, Height = 42, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            else
                it.Face.Child = Ui.Glyph("", 28, Ui.Fg);
            RenderOptions.SetBitmapScalingMode(it.Face.Child, BitmapScalingMode.HighQuality);
        }
        it.Ring = FocusRing();
        // Running indicator: glowing white dot in the tile's bottom-left corner.
        it.Dot = new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Brushes.White,
            BorderBrush = Ui.B(0x66000000), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(7, 0, 0, 7),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Color = Colors.White, Opacity = 1 },
            IsHitTestVisible = false,
            Visibility = m.Window != null ? Visibility.Visible : Visibility.Hidden,
        };
        it.Root = TileRoot(it);
        it.Root.Children.Add(it.Ring);
        it.Root.Children.Add(it.Face);
        it.Root.Children.Add(it.Dot);
        return it;
    }

    BarItem MakeAdd()
    {
        var it = new BarItem { Kind = ItemKind.Add, Key = "act:add", Group = 1 };
        var g = new Grid();
        g.Children.Add(new System.Windows.Shapes.Rectangle
        {
            RadiusX = 10, RadiusY = 10, Stroke = Ui.B(0x66FFFFFF), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 },
        });
        g.Children.Add(Ui.Glyph("", 24, Ui.Fg));
        it.Face = new Border { Width = Tile, Height = Tile, CornerRadius = new CornerRadius(10), Background = Ui.B(0xFF2E2F31), Child = g };
        it.Ring = FocusRing();
        it.Root = TileRoot(it);
        it.Root.Children.Add(it.Ring);
        it.Root.Children.Add(it.Face);
        return it;
    }

    static Border FocusRing() => new()
    {
        Margin = new Thickness(-6), CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(3), BorderBrush = Ui.Fg, Opacity = 0,
        Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 0, Color = Colors.White, Opacity = 0.45 },
    };

    static Grid TileRoot(BarItem it) => new()
    {
        Margin = new Thickness(8, 0, 8, 0), RenderTransform = it.Scale, RenderTransformOrigin = new Point(0.5, 0.5),
        Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
    };

    void Visual(BarItem it, bool on, bool animate = true)
    {
        int ms = animate ? 140 : 0;
        Ui.Anim(it.Scale, ScaleTransform.ScaleXProperty, on ? 1.05 : 1, ms);
        Ui.Anim(it.Scale, ScaleTransform.ScaleYProperty, on ? 1.05 : 1, ms);
        if (it.Ring != null) Ui.Anim(it.Ring, OpacityProperty, on ? 1 : 0, ms);
    }

    void SetFocus(int idx, bool animate = true)
    {
        if (_items.Count == 0) return;
        idx = Math.Clamp(idx, 0, _items.Count - 1);
        if (_focus >= 0 && _focus < _items.Count && _focus != idx) Visual(_items[_focus], false, animate);
        _focus = idx;
        Visual(_items[idx], true, animate);
        EnsureVisible(_items[idx]);
    }

    void FocusKey(string key)
    {
        int i = _items.FindIndex(x => x.Key == key);
        if (i >= 0) SetFocus(i);
    }

    void EnsureVisible(BarItem it)
    {
        if (it.Group != 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_games.IsAncestorOf(it.Root)) return;
            var p = it.Root.TransformToAncestor(_games).Transform(new Point(0, 0));
            double left = p.X + _games.Margin.Left - 14, right = p.X + _games.Margin.Left + it.Root.ActualWidth + 14;
            double off = _gamesScroll.HorizontalOffset, vw = _gamesScroll.ViewportWidth;
            if (left < off) _gamesScroll.ScrollToHorizontalOffset(left);
            else if (right > off + vw) _gamesScroll.ScrollToHorizontalOffset(right - vw);
        }));
    }

    void UpdateMinimizeState()
    {
        var it = _items.FirstOrDefault(i => i.Kind == ItemKind.Minimize);
        if (it?.Glyph != null) it.Root.Opacity = _current != null ? 1 : 0.45;
    }

    void Tick()
    {
        if (_current != null && !Native.IsWindow(_current.Hwnd)) { _current = null; UpdateMinimizeState(); }
        var models = BuildModels();
        var sig = string.Join("|", models.Select(m => m.Key));
        if (sig == _sig)
        {
            foreach (var m in models)
            {
                var it = _items.FirstOrDefault(i => i.Key == m.Key);
                if (it == null) continue;
                it.Game = m;
                if (it.Dot != null) it.Dot.Visibility = m.Window != null ? Visibility.Visible : Visibility.Hidden;
            }
        }
        else if (_menu == null && _pad == null) Rebuild(false);
    }

    void BarInput(PadButton b)
    {
        var it = Cur;
        switch (b)
        {
            case PadButton.Left: SetFocus(_focus - 1); break;
            case PadButton.Right: SetFocus(_focus + 1); break;
            case PadButton.LB:
            case PadButton.RB:
                if (it == null) break;
                int target = Math.Clamp(it.Group + (b == PadButton.LB ? -1 : 1), 0, 2);
                int i = _items.FindIndex(x => x.Group == target);
                if (i >= 0) SetFocus(i);
                break;
            case PadButton.A: if (it != null) Activate(it); break;
            case PadButton.B: Dismiss(true); break;
            case PadButton.Down:
            case PadButton.Menu:
            case PadButton.X:
                if (it != null) Options(it);
                break;
        }
    }

    void Activate(BarItem it)
    {
        switch (it.Kind)
        {
            case ItemKind.Minimize:
                if (_current == null) { Toast("Nothing in focus to minimize"); return; }
                MinimizeWindow(_current, CurrentName());
                break;
            case ItemKind.Settings: OpenMenu(SettingsMenu(it)); break;
            case ItemKind.Blackout: _hub.StartBlackout(); break;
            case ItemKind.Game: ActivateGame(it.Game!); break;
            case ItemKind.Add: AddGames(); break;
            case ItemKind.Bluetooth: OpenMenu(BtMenu(it)); break;
            case ItemKind.Volume: OpenMenu(AudioMenu(it)); break;
            case ItemKind.Wifi: OpenMenu(WifiMenu(it)); break;
        }
    }

    void Options(BarItem it)
    {
        switch (it.Kind)
        {
            case ItemKind.Game: OpenMenu(GameMenu(it)); break;
            case ItemKind.Settings: case ItemKind.Bluetooth: case ItemKind.Volume: case ItemKind.Wifi: Activate(it); break;
        }
    }

    // ───────────────────────────── game actions ─────────────────────────────

    async void MinimizeWindow(WinInfo w, string name)
    {
        var h = w.Hwnd;
        _hubMinimized.Add(h);
        if (_current?.Hwnd == h) _current = null;
        Native.ShowWindowAsync(h, Native.SW_MINIMIZE);
        await Task.Delay(300);
        if (Native.IsWindow(h) && !Native.IsIconic(h))
        {
            Native.PostMessage(h, Native.WM_SYSCOMMAND, Native.SC_MINIMIZE, IntPtr.Zero);
            await Task.Delay(350);
        }
        if (Native.IsWindow(h) && !Native.IsIconic(h))
        {
            Native.ShowWindowAsync(h, Native.SW_FORCEMINIMIZE);
            await Task.Delay(300);
        }
        bool ok = !Native.IsWindow(h) || Native.IsIconic(h) || !Native.IsWindowVisible(h);
        if (!IsOpen) return;
        Native.MakeTopmost(_hwnd);
        Native.ForceForeground(_hwnd);
        if (_menu == null && _pad == null) Rebuild(false);
        var tile = _items.FirstOrDefault(i => i.Game?.Window?.Hwnd == h);
        if (tile != null) SetFocus(_items.IndexOf(tile));
        Toast(ok ? $"{name} minimized. Select it to jump back in." : $"Couldn't minimize {name}. If it runs as administrator, Tungsten needs to as well.");
    }

    async void ActivateGame(GameModel m)
    {
        if (m.Window != null)
        {
            var h = m.Window.Hwnd;
            if (Native.IsIconic(h)) Native.ShowWindowAsync(h, Native.SW_RESTORE);
            Native.ForceForeground(h);
            _hubMinimized.Remove(h);
            Dismiss(false);
            // Restoring is asynchronous for fullscreen games; make sure it really ends up in front.
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(200);
                if (Native.GetForegroundWindow() == h) break;
                if (Native.IsIconic(h)) Native.ShowWindowAsync(h, Native.SW_RESTORE);
                Native.ForceForeground(h);
            }
        }
        else if (m.Pinned != null) Launch(m.Pinned.Entry);
    }

    void Launch(GameEntry e)
    {
        try
        {
            Native.AllowSetForegroundWindow(-1);
            string? dir = Path.GetExtension(e.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(e.Path) : null;
            Elevation.Shell(e.Path, string.IsNullOrWhiteSpace(e.Arguments) ? null : e.Arguments, dir);
            Program.Log("Launched " + e.Path);
            Dismiss(false);
        }
        catch (Exception ex)
        {
            Program.Log($"Launch {e.Path}: {ex.Message}");
            Toast($"Couldn't start {e.Name}: {ex.Message}");
        }
    }

    void CloseApp(WinInfo w, string name, bool force)
    {
        try
        {
            if (force) Process.GetProcessById((int)w.Pid).Kill();
            else Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Toast(force ? $"Force quit {name}" : $"Asked {name} to close");
            if (_current?.Hwnd == w.Hwnd) { _current = null; UpdateMinimizeState(); }
        }
        catch (Exception e) { Toast($"Couldn't close {name}: {e.Message}"); }
    }

    void AddGames()
    {
        _modal = true;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add games to Tungsten",
                Filter = "Games and shortcuts (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|All files (*.*)|*.*",
                DereferenceLinks = false,
                Multiselect = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            if (dlg.ShowDialog(this) != true) return;
            string? last = null;
            var added = new List<string>();
            foreach (var f in dlg.FileNames)
            {
                if (_hub.Config.Games.Any(g => string.Equals(g.Path, f, StringComparison.OrdinalIgnoreCase))) continue;
                var e = GameResolver.CreateEntry(f);
                _hub.Config.Games.Add(e);
                last = "g:" + e.Id;
                added.Add(e.Name);
            }
            _hub.SaveConfig();
            Rebuild(false);
            if (last != null) FocusKey(last);
            Toast(added.Count == 0 ? "Already in your bar" : $"Added {string.Join(", ", added)}");
        }
        catch (Exception e) { Program.Log("AddGames: " + e); Toast("Couldn't add that file"); }
        finally
        {
            _modal = false;
            if (IsOpen) { Native.MakeTopmost(_hwnd); Native.ForceForeground(_hwnd); Activate(); }
        }
    }

    void MoveGame(GameEntry e, int dir)
    {
        var list = _hub.Config.Games;
        int i = list.IndexOf(e), j = i + dir;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        _hub.SaveConfig();
        Rebuild(false);
    }

    void Unpin(GameEntry e)
    {
        _hub.Config.Games.Remove(e);
        _resolved.Remove(e.Id);
        _hub.SaveConfig();
        CloseMenu();
        int at = _focus;
        Rebuild(false);
        if (Cur?.Key == "g:" + e.Id || Cur == null) SetFocus(at);
        Toast($"Unpinned {e.Name}");
    }

    void Pin(GameModel m)
    {
        if (m.Exe == null) { Toast("Can't pin this one - pick it with + instead"); return; }
        var e = new GameEntry { Name = m.Name, Path = m.Exe };
        _hub.Config.Games.Add(e);
        _hub.SaveConfig();
        CloseMenu();
        Rebuild(false);
        FocusKey("g:" + e.Id);
        Toast($"Pinned {m.Name}");
    }

    static void Shell(string uri)
    {
        try { Elevation.Shell(uri); }
        catch (Exception e) { Program.Log("Shell " + uri + ": " + e.Message); }
    }

    // ───────────────────────────── dropdown menus ─────────────────────────────

    DropMenu SettingsMenu(BarItem anchor) => new()
    {
        Kind = "settings", Title = "Settings", Width = 360, AnchorKey = anchor.Key,
        Build = () =>
        {
            var cur = _current;
            string cn = cur != null ? CurrentName() : "";
            var c = _hub.Config;
            return new List<MItem>
            {
                new() { Key = "close", Glyph = "", Label = cur != null ? $"Close {cn}" : "Close game", Enabled = cur != null, A = () => CloseApp(cur!, cn, false) },
                new() { Key = "kill", Glyph = "", Label = cur != null ? $"Force quit {cn}" : "Force quit game", Enabled = cur != null, A = () => CloseApp(cur!, cn, true) },
                new() { Key = "h", Header = true, Label = "Tungsten" },
                new() { Key = "startup", Glyph = "", Label = "Start with Windows", Detail = c.StartWithWindows ? "On" : "Off", KeepOpen = true, A = () => _hub.SetStartup(!c.StartWithWindows) },
                new() { Key = "guard", Glyph = "", Label = "Block Windows controller bar", Detail = c.BlockGameBar ? "On" : "Off", KeepOpen = true, A = () => _hub.SetBlockGameBar(!c.BlockGameBar) },
                new() { Key = "admin", Glyph = "", Label = "Admin mode", Detail = Elevation.IsElevated ? "On" : "Off", A = () => { Dismiss(false); _hub.SetAdminMode(!Elevation.IsElevated); } },
                new() { Key = "test", Glyph = "", Label = "Controller test", A = () => { Dismiss(false); _hub.ShowTest(); } },
                new() { Key = "ws", Glyph = "", Label = "Windows Settings", A = () => { Dismiss(false); Shell("ms-settings:"); } },
                new() { Key = "quit", Glyph = "", Label = "Quit Tungsten", A = () => { Dismiss(true); _hub.Quit(); } },
            };
        },
    };

    DropMenu GameMenu(BarItem anchor)
    {
        string key = anchor.Key;
        return new DropMenu
        {
            Kind = "game", Title = anchor.Game!.Name, Width = 320, AnchorKey = key,
            Build = () =>
            {
                var g = _items.FirstOrDefault(i => i.Key == key)?.Game ?? anchor.Game!;
                var l = new List<MItem>();
                var w = g.Window;
                if (w != null)
                {
                    l.Add(new MItem { Key = "resume", Glyph = "", Label = w.Minimized ? "Resume" : "Switch to", A = () => ActivateGame(g) });
                    if (!w.Minimized) l.Add(new MItem { Key = "min", Glyph = MinimizeGlyph, Label = "Minimize", A = () => MinimizeWindow(w, g.Name) });
                }
                else if (g.Pinned != null) l.Add(new MItem { Key = "play", Glyph = "", Label = "Play", A = () => Launch(g.Pinned.Entry) });

                if (g.Pinned != null)
                {
                    var e = g.Pinned.Entry;
                    int idx = _hub.Config.Games.IndexOf(e);
                    l.Add(new MItem { Key = "left", Glyph = "", Label = "Move left", Enabled = idx > 0, KeepOpen = true, A = () => MoveGame(e, -1) });
                    l.Add(new MItem { Key = "right", Glyph = "", Label = "Move right", Enabled = idx < _hub.Config.Games.Count - 1, KeepOpen = true, A = () => MoveGame(e, 1) });
                    l.Add(new MItem { Key = "folder", Glyph = "", Label = "Show file location", A = () => { Dismiss(false); try { Elevation.Shell("explorer.exe", $"/select,\"{e.Path}\""); } catch { } } });
                    l.Add(new MItem { Key = "unpin", Glyph = "", Label = "Unpin from bar", A = () => Unpin(e) });
                }
                else l.Add(new MItem { Key = "pin", Glyph = "", Label = "Pin to bar", A = () => Pin(g) });

                if (w != null)
                {
                    l.Add(new MItem { Key = "close", Glyph = "", Label = "Close game", A = () => CloseApp(w, g.Name, false) });
                    l.Add(new MItem { Key = "kill", Glyph = "", Label = "Force quit", A = () => CloseApp(w, g.Name, true) });
                }
                return l;
            },
        };
    }

    DropMenu BtMenu(BarItem anchor)
    {
        _hub.Bt.Start();
        return new DropMenu
        {
            Kind = "bt", Title = "Bluetooth", Width = 410, AnchorKey = anchor.Key,
            Status = () => (_hub.Bt.Active && _hub.Bt.RadioOn != false, _hub.Bt.Message ?? (_hub.Bt.RadioOn == false ? "Off" : "Scanning")),
            Closed = () => _hub.Bt.Stop(),
            Build = BuildBt,
        };
    }

    static string BtGlyph(BtDevice d)
    {
        var n = d.Name.ToLowerInvariant();
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("xbox") || n.Contains("8bitdo") || n.Contains("dualsense") || n.Contains("dualshock")) return "";
        if (n.Contains("mouse") || n.Contains("viper") || n.Contains("mx ")) return "";
        if (n.Contains("keyboard") || n.Contains("keys")) return "";
        if (n.Contains("airpods") || n.Contains("buds") || n.Contains("headphone") || n.Contains("headset") || n.Contains("wh-") || n.Contains("wf-") || d.Major == 4) return "";
        if (d.Major == 2 || n.Contains("iphone") || n.Contains("phone") || n.Contains("galaxy") || n.Contains("pixel")) return "";
        if (d.Major == 1) return "";
        return "";
    }

    List<MItem> BuildBt()
    {
        var bt = _hub.Bt;
        var l = new List<MItem>
        {
            new() { Key = "radio", Glyph = "", Label = "Bluetooth", Detail = bt.RadioOn switch { true => "On", false => "Off", _ => "" }, Enabled = bt.HasRadio, KeepOpen = true, A = bt.ToggleRadio },
        };
        if (bt.RadioOn != false)
        {
            var devs = bt.Snapshot().Where(d => !string.IsNullOrWhiteSpace(d.Name)).ToList();
            devs = devs.Where(d => !(d.IsLe && !d.Paired && devs.Any(o => o != d && o.Name == d.Name && (o.Paired || !o.IsLe)))).ToList();
            var paired = devs.Where(d => d.Paired).GroupBy(d => d.Name).Select(g => g.OrderByDescending(d => d.Connected).ThenBy(d => d.IsLe).First())
                .OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
            var avail = devs.Where(d => !d.Paired).GroupBy(d => d.Name).Select(g => g.OrderByDescending(d => d.Rssi ?? -999).First())
                .OrderByDescending(d => d.Rssi ?? -999).Take(16).ToList();

            if (paired.Count > 0)
            {
                l.Add(new MItem { Key = "h1", Header = true, Label = "My devices" });
                l.AddRange(paired.Select(BtItem));
            }
            l.Add(new MItem { Key = "h2", Header = true, Label = "Nearby" });
            if (avail.Count == 0) l.Add(new MItem { Key = "none", Glyph = "", Label = "Looking for devices…", Enabled = false });
            else l.AddRange(avail.Select(BtItem));
        }
        l.Add(new MItem { Key = "more", Glyph = "", Label = "More Bluetooth settings", A = () => { Dismiss(false); Shell("ms-settings:bluetooth"); } });
        return l;
    }

    MItem BtItem(BtDevice d) => new()
    {
        Key = "d:" + d.Name + (d.Paired ? ":p" : ""),
        Glyph = BtGlyph(d),
        Label = d.Name,
        Detail = d.Status ?? (d.Connected ? "Connected" : d.Paired ? "Not connected" : "Pair"),
        DetailBrush = d.Status == null && d.Connected ? Ui.Green : null,
        KeepOpen = true,
        A = () => _hub.Bt.Toggle(d),
        Y = d.Paired ? () => _hub.Bt.Forget(d) : null,
    };

    DropMenu AudioMenu(BarItem anchor)
    {
        _hub.Audio.Start();
        return new DropMenu
        {
            Kind = "audio", Title = "Volume", Width = 430, MaxListHeight = 620, AnchorKey = anchor.Key,
            Status = () => (false, _hub.Audio.Message),
            Closed = () => _hub.Audio.Stop(),
            Build = BuildAudio,
        };
    }

    List<MItem> BuildAudio()
    {
        var audio = _hub.Audio;
        var items = new List<MItem>
        {
            new() { Key = "volume", Glyph = "\uE767", Label = "Volume", Detail = $"{audio.Volume}%", Enabled = audio.HasOutput,
                KeepOpen = true, Control = AudioSlider, Adjust = direction => { audio.ChangeVolume(direction * 5); UpdateAudioVolume(); } },
            new() { Key = "mute", Glyph = audio.Muted ? "\uE74F" : "\uE767", Label = "Mute", Detail = audio.Muted ? "On" : "Off",
                Enabled = audio.HasOutput, KeepOpen = true, A = audio.ToggleMute },
            new() { Key = "outputs", Header = true, Label = "Output device" },
        };
        foreach (var output in audio.Outputs)
        {
            string id = output.Id;
            items.Add(new MItem { Key = "output:" + id, Glyph = output.IsDefault ? "\uE73E" : "\uE767", Label = output.Name,
                Detail = output.IsDefault ? "Selected" : "", DetailBrush = output.IsDefault ? Ui.Green : null,
                KeepOpen = true, A = () => audio.Select(id) });
        }
        if (audio.Outputs.Count == 0) items.Add(new MItem { Key = "empty", Label = "No audio outputs connected", Enabled = false });
        items.Add(new MItem { Key = "more", Glyph = "\uE713", Label = "More sound settings", A = () => { Dismiss(false); Shell("ms-settings:sound"); } });
        return items;
    }

    FrameworkElement AudioSlider()
    {
        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = _hub.Audio.Volume, TickFrequency = 1, IsSnapToTickEnabled = true,
            SmallChange = 5, LargeChange = 10, IsMoveToPointEnabled = true, Focusable = false,
            IsEnabled = _hub.Audio.HasOutput, VerticalAlignment = VerticalAlignment.Center,
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type Slider}">
                  <Grid Height="24" Margin="0,0,8,0" Background="Transparent">
                    <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                        Value="{TemplateBinding Value}" Orientation="Horizontal">
                      <Track.DecreaseRepeatButton>
                        <RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False">
                          <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" CornerRadius="2" Background="#7CD86E"/></ControlTemplate></RepeatButton.Template>
                        </RepeatButton>
                      </Track.DecreaseRepeatButton>
                      <Track.Thumb>
                        <Thumb Width="16" Height="16" Focusable="False">
                          <Thumb.Template><ControlTemplate TargetType="Thumb"><Border CornerRadius="8" Background="#7CD86E" BorderBrush="#30572A" BorderThickness="1"/></ControlTemplate></Thumb.Template>
                        </Thumb>
                      </Track.Thumb>
                      <Track.IncreaseRepeatButton>
                        <RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False">
                          <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" CornerRadius="2" Background="#747576"/></ControlTemplate></RepeatButton.Template>
                        </RepeatButton>
                      </Track.IncreaseRepeatButton>
                    </Track>
                  </Grid>
                </ControlTemplate>
                """),
        };
        System.Windows.Automation.AutomationProperties.SetName(slider, "Output volume");
        slider.ValueChanged += (_, _) =>
        {
            if (_updatingAudioSlider) return;
            _menuTouched = true;
            int volumeRow = _menu?.Items.FindIndex(item => item.Key == "volume") ?? -1;
            if (volumeRow >= 0) SetMenuFocus(volumeRow);
            _hub.Audio.SetVolume((int)Math.Round(slider.Value));
            UpdateAudioVolume();
        };
        _audioSlider = slider;
        return slider;
    }

    void UpdateAudioVolume()
    {
        if (_menu?.Kind != "audio") return;
        foreach (var row in _rows.Where(r => r.item.Key == "volume"))
            row.d.Text = row.item.Detail = $"{_hub.Audio.Volume}%";
        if (_audioSlider == null) return;
        _updatingAudioSlider = true;
        try { _audioSlider.Value = _hub.Audio.Volume; }
        finally { _updatingAudioSlider = false; }
    }

    DropMenu WifiMenu(BarItem anchor)
    {
        _hub.Wifi.Start();
        return new DropMenu
        {
            Kind = "wifi", Title = "Wi-Fi", Width = 410, AnchorKey = anchor.Key,
            Status = () => (_hub.Wifi.Scanning, _hub.Wifi.Message ?? (_hub.Wifi.Scanning ? "Scanning" : "Live")),
            Closed = () => _hub.Wifi.Stop(),
            Build = BuildWifi,
        };
    }

    List<MItem> BuildWifi()
    {
        var w = _hub.Wifi;
        var l = new List<MItem>
        {
            new() { Key = "radio", Glyph = "", Label = "Wi-Fi", Detail = w.RadioOn switch { true => "On", false => "Off", _ => "" }, Enabled = w.HasRadio, KeepOpen = true, A = w.ToggleRadio },
        };
        if (w.RadioOn != false)
        {
            if (w.Networks.Count == 0)
                l.Add(new MItem { Key = "none", Glyph = "", Label = "Looking for networks…", Enabled = false });
            foreach (var n in w.Networks)
            {
                bool connected = n.Ssid == w.ConnectedSsid;
                var st = w.Status.GetValueOrDefault(n.Ssid);
                l.Add(new MItem
                {
                    Key = "n:" + n.Ssid,
                    Glyph = n.Bars switch { <= 1 => "", 2 => "", 3 => "", _ => "" },
                    Label = n.Ssid,
                    Detail = st ?? (connected ? "Connected" : n.Secured ? "Secured" : "Open"),
                    DetailBrush = st == null && connected ? Ui.Green : null,
                    KeepOpen = true,
                    A = () => WifiSelect(n),
                });
            }
        }
        l.Add(new MItem { Key = "more", Glyph = "", Label = "More Wi-Fi settings", A = () => { Dismiss(false); Shell("ms-settings:network-wifi"); } });
        return l;
    }

    async void WifiSelect(WifiNet n)
    {
        var w = _hub.Wifi;
        if (n.Ssid == w.ConnectedSsid) { w.Disconnect(); return; }
        if (n.Enterprise)
        {
            Toast("That network needs a username. Opening Windows Wi-Fi settings.");
            Dismiss(false);
            Shell("ms-settings:network-wifi");
            return;
        }
        if (!n.Secured || w.HasProfile(n.Ssid))
        {
            var r = await w.Connect(n, null);
            if (r == WiFiConnectionStatus.Success) { Toast($"Connected to {n.Ssid}"); return; }
            if (!n.Secured || r != WiFiConnectionStatus.InvalidCredential) return;
        }
        AskPassword(n, "");
    }

    void AskPassword(WifiNet n, string error)
    {
        ShowPad($"Password for {n.Ssid}", error, async pw =>
        {
            ClosePad();
            var r = await _hub.Wifi.Connect(n, pw);
            if (r == WiFiConnectionStatus.Success) Toast($"Connected to {n.Ssid}");
            else if (r == WiFiConnectionStatus.InvalidCredential && IsOpen && _menu?.Kind == "wifi") AskPassword(n, "That password didn't work. Try again.");
        });
    }

    void QueueMenuRefresh(string kind)
    {
        if (_menu?.Kind == kind && !_menuRefresh.IsEnabled) _menuRefresh.Start();
    }

    void OpenMenu(DropMenu m)
    {
        CloseMenu(animate: false);
        _menu = m;
        _menuFocus = -1;
        _menuTouched = false;

        var header = new Grid { Margin = new Thickness(10, 4, 10, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text(m.Title, 16, Ui.Fg, FontWeights.SemiBold);
        title.FontFamily = Ui.Display;
        header.Children.Add(title);
        var statusRow = H();
        statusRow.VerticalAlignment = VerticalAlignment.Center;
        var spin = Ui.Glyph("", 12, Ui.Fg2);
        var rot = new RotateTransform();
        spin.RenderTransform = rot;
        spin.RenderTransformOrigin = new Point(0.5, 0.5);
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        _menuSpinner = spin;
        _menuStatus = Ui.Text("", 12, Ui.Fg2);
        _menuStatus.Margin = new Thickness(6, 0, 0, 0);
        _menuStatus.MaxWidth = m.Width - 140;
        statusRow.Children.Add(spin);
        statusRow.Children.Add(_menuStatus);
        Grid.SetColumn(statusRow, 1);
        header.Children.Add(statusRow);

        _menuList = new StackPanel();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            MaxHeight = Math.Min(m.MaxListHeight, Math.Max(100, _root.ActualHeight - 240)), Content = _menuList, Focusable = false };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(new Border { Height = 1, Background = Ui.B(0x1FFFFFFF), Margin = new Thickness(4, 0, 4, 6) });
        stack.Children.Add(scroll);

        var shift = new TranslateTransform(0, -10);
        _menuView = new Border
        {
            Width = m.Width, CornerRadius = new CornerRadius(16), Padding = new Thickness(8, 8, 8, 10),
            Background = Ui.B(0xF7232426),
            BorderBrush = Ui.Stroke, BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black, Direction = 270 },
            Child = stack, RenderTransform = shift, Opacity = 0,
        };
        _menuView.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _layer.Children.Add(_menuView);
        RenderMenuItems(false);
        PositionMenu();
        Ui.Anim(_menuView, OpacityProperty, 1, 140);
        Ui.Anim(shift, TranslateTransform.YProperty, 0, 180);
    }

    void RenderMenuItems(bool preserve)
    {
        if (_menu == null || _menuList == null) return;
        // Do not replace the slider while the mouse is dragging it.
        if (_menu.Kind == "audio" && _audioSlider?.IsMouseCaptureWithin == true) { UpdateAudioVolume(); return; }
        // Until the user moves, keep focus on the first selectable row even while the list loads/refreshes.
        if (!_menuTouched) preserve = false;
        string? key = preserve && _menuFocus >= 0 && _menuFocus < _menu.Items.Count ? _menu.Items[_menuFocus].Key : null;
        int oldIdx = _menuFocus;
        _menu.Items = _menu.Build();
        _menuList.Children.Clear();
        _rows.Clear();
        foreach (var item in _menu.Items)
        {
            Border row;
            TextBlock g, l, d;
            if (item.Header)
            {
                l = Ui.Text(item.Label.ToUpperInvariant(), 11, Ui.Fg3, FontWeights.SemiBold);
                g = new TextBlock(); d = new TextBlock();
                row = new Border { Padding = new Thickness(12, 10, 12, 4), Child = l };
            }
            else
            {
                g = Ui.Glyph(item.Glyph, 17, Ui.Fg);
                l = Ui.Text(item.Label, 14.5, Ui.Fg);
                l.TextTrimming = TextTrimming.CharacterEllipsis;
                l.ToolTip = item.Label;
                d = Ui.Text(item.Detail, 12.5, item.DetailBrush ?? Ui.Fg2);
                d.Margin = new Thickness(10, 0, 0, 0);
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.HorizontalAlignment = HorizontalAlignment.Left;
                if (item.Glyph == MinimizeGlyph)
                {
                    // Same size/weight as the 17px menu glyphs; follows the glyph colour when the row is focused.
                    g.Text = "";
                    var icon = MinimizeIcon(box: 18, ink: 15.5, stroke: 1.15);
                    icon.HorizontalAlignment = HorizontalAlignment.Left;
                    icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(TextBlock.Foreground)) { Source = g });
                    grid.Children.Add(icon);
                }
                grid.Children.Add(g);
                Grid.SetColumn(l, 1); grid.Children.Add(l);
                Grid.SetColumn(d, 2); grid.Children.Add(d);
                if (item.Control != null)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                    var control = item.Control();
                    Grid.SetRow(control, 1); Grid.SetColumn(control, 1); Grid.SetColumnSpan(control, 2);
                    grid.Children.Add(control);
                }
                row = new Border
                {
                    Height = item.Control != null ? 76 : 44, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 1, 0, 1),
                    Background = Brushes.Transparent, Child = grid, Opacity = item.Enabled ? 1 : 0.4, Cursor = Cursors.Hand,
                };
                var captured = item;
                row.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    int i = _menu?.Items.IndexOf(captured) ?? -1;
                    if (i >= 0 && captured.Focusable) { _menuTouched = true; SetMenuFocus(i); MenuInput(PadButton.A); }
                };
            }
            _rows.Add((row, g, l, d, item));
            _menuList.Children.Add(row);
        }

        int idx = key != null ? _menu.Items.FindIndex(i => i.Key == key && i.Focusable) : -1;
        if (idx < 0 && preserve && oldIdx >= 0)
        {
            for (int dlt = 0; dlt < _menu.Items.Count && idx < 0; dlt++)
            {
                int a = oldIdx + dlt, b = oldIdx - dlt;
                if (a < _menu.Items.Count && _menu.Items[a].Focusable) idx = a;
                else if (b >= 0 && b < _menu.Items.Count && _menu.Items[b].Focusable) idx = b;
            }
        }
        if (idx < 0) idx = _menu.Items.FindIndex(i => i.Focusable);
        _menuFocus = -1;
        SetMenuFocus(idx);

        var (busy, st) = _menu.Status?.Invoke() ?? (false, null);
        if (_menuStatus != null) _menuStatus.Text = st ?? "";
        if (_menuSpinner != null) _menuSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetMenuFocus(int idx)
    {
        if (_menuFocus >= 0 && _menuFocus < _rows.Count)
        {
            var (row, g, l, d, item) = _rows[_menuFocus];
            row.Background = Brushes.Transparent;
            g.Foreground = Ui.Fg; l.Foreground = Ui.Fg; d.Foreground = item.DetailBrush ?? Ui.Fg2;
        }
        _menuFocus = idx;
        if (idx >= 0 && idx < _rows.Count)
        {
            var (row, g, l, d, item) = _rows[idx];
            row.Background = Ui.Fg;
            g.Foreground = Ui.Black; l.Foreground = Ui.Black;
            d.Foreground = item.DetailBrush == Ui.Green ? Ui.DarkGreen : Ui.Ink2;
            row.BringIntoView();
        }
    }

    void PositionMenu()
    {
        if (_menu == null || _menuView == null) return;
        var anchor = _items.FirstOrDefault(i => i.Key == _menu.AnchorKey) ?? Cur;
        if (anchor == null) return;
        _root.UpdateLayout();
        var pt = anchor.Root.TranslatePoint(new Point(anchor.Root.ActualWidth / 2, 0), _layer);
        double bottom = _shell.TranslatePoint(new Point(0, _shell.ActualHeight), _layer).Y;
        double left = Math.Clamp(pt.X - _menu.Width / 2, 16, Math.Max(16, _layer.ActualWidth - _menu.Width - 16));
        Canvas.SetLeft(_menuView, left);
        Canvas.SetTop(_menuView, bottom + 12);
    }

    void CloseMenu(bool animate = true)
    {
        if (_menu == null) return;
        var m = _menu;
        _menu = null;
        _audioSlider = null;
        _rows.Clear();
        _menuFocus = -1;
        var view = _menuView;
        _menuView = null;
        _menuList = null;
        if (view != null)
        {
            if (animate)
            {
                var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
                a.Completed += (_, _) => _layer.Children.Remove(view);
                view.BeginAnimation(OpacityProperty, a);
            }
            else _layer.Children.Remove(view);
        }
        try { m.Closed?.Invoke(); } catch (Exception e) { Program.Log("menu closed: " + e.Message); }
    }

    void MenuInput(PadButton b)
    {
        var m = _menu!;
        switch (b)
        {
            case PadButton.Up:
            case PadButton.Down:
                int dir = b == PadButton.Up ? -1 : 1;
                _menuTouched = true;
                for (int i = _menuFocus + dir; i >= 0 && i < m.Items.Count; i += dir)
                    if (m.Items[i].Focusable) { SetMenuFocus(i); break; }
                break;
            case PadButton.A:
            {
                if (_menuFocus < 0 || _menuFocus >= m.Items.Count) break;
                var it = m.Items[_menuFocus];
                if (!it.Focusable) break;
                it.A?.Invoke();
                if (_menu != m) break; // the action replaced or closed the menu
                if (it.KeepOpen) RenderMenuItems(true); else CloseMenu();
                break;
            }
            case PadButton.Y:
            {
                if (_menuFocus < 0 || _menuFocus >= m.Items.Count) break;
                var it = m.Items[_menuFocus];
                if (it.Y == null) break;
                it.Y();
                if (_menu == m) RenderMenuItems(true);
                break;
            }
            case PadButton.B:
            case PadButton.Menu:
                CloseMenu();
                break;
            case PadButton.Left:
            case PadButton.Right:
                if (_menuFocus >= 0 && _menuFocus < m.Items.Count && m.Items[_menuFocus].Adjust is { } adjust)
                {
                    _menuTouched = true;
                    adjust(b == PadButton.Left ? -1 : 1);
                    break;
                }
                CloseMenu();
                SetFocus(_focus + (b == PadButton.Left ? -1 : 1));
                break;
        }
    }

    // ───────────────────────────── on-screen keyboard ─────────────────────────────

    static readonly string[][] Layers =
    {
        new[] { "1234567890", "qwertyuiop", "asdfghjkl@", "zxcvbnm-_." },
        new[] { "!@#$%^&*()", "-_=+[]{}\\|", ";:'\",.<>/?", "`~€£¥§°•¡¿" },
    };

    void ShowPad(string title, string error, Action<string> done)
    {
        if (_pad != null) ClosePad();
        _pad = new PadState { Title = title, Error = error, Done = done };
        var p = _pad;

        var titleText = Ui.Text(title, 18, Ui.Fg, FontWeights.SemiBold);
        titleText.FontFamily = Ui.Display;
        var errText = Ui.Text(error, 13, Ui.Red);
        errText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
        errText.Margin = new Thickness(0, 4, 0, 0);

        p.Field = Ui.Text("", 18, Ui.Fg);
        var field = new Border
        {
            Height = 50, CornerRadius = new CornerRadius(10), Background = Ui.B(0x16FFFFFF), BorderBrush = Ui.Stroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 14, 0, 14), Child = p.Field,
        };

        p.Keys = new Grid();
        for (int c = 0; c < 10; c++) p.Keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        for (int r = 0; r < 5; r++) p.Keys.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });

        var stack = new StackPanel();
        stack.Children.Add(titleText);
        stack.Children.Add(errText);
        stack.Children.Add(field);
        stack.Children.Add(p.Keys);

        p.View = new Border
        {
            CornerRadius = new CornerRadius(18), Padding = new Thickness(22, 18, 22, 16),
            Background = Ui.B(0xFA232426),
            BorderBrush = Ui.Stroke, BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Opacity = 0.6, Color = Colors.Black, Direction = 270 },
            Child = stack, Opacity = 0,
        };
        p.View.MouseLeftButtonDown += (_, e) => e.Handled = true;
        if (_menuView != null) _menuView.Visibility = Visibility.Collapsed;
        _layer.Children.Add(p.View);
        BuildPadKeys();
        UpdatePadField();
        _root.UpdateLayout();
        double bottom = _shell.TranslatePoint(new Point(0, _shell.ActualHeight), _layer).Y;
        Canvas.SetLeft(p.View, Math.Max(16, (_layer.ActualWidth - p.View.ActualWidth) / 2));
        Canvas.SetTop(p.View, bottom + 14);
        Ui.Anim(p.View, OpacityProperty, 1, 150);
    }

    void BuildPadKeys()
    {
        var p = _pad!;
        p.Keys.Children.Clear();
        p.KeyList.Clear();
        var rows = Layers[p.Layer];
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 10; c++)
            {
                char ch = rows[r][c];
                string s = p.Shift && p.Layer == 0 ? char.ToUpperInvariant(ch).ToString() : ch.ToString();
                AddKey(new PadKey { Row = r, Col = c, Ch = s }, s);
            }
        AddKey(new PadKey { Row = 4, Col = 0, Span = 2, Cmd = "shift" }, p.Shift ? "⇧ SHIFT" : "⇧ Shift");
        AddKey(new PadKey { Row = 4, Col = 2, Span = 2, Cmd = "sym" }, p.Layer == 0 ? "&123" : "abc");
        AddKey(new PadKey { Row = 4, Col = 4, Span = 3, Cmd = "space" }, "Space");
        AddKey(new PadKey { Row = 4, Col = 7, Span = 1, Cmd = "back" }, "⌫");
        AddKey(new PadKey { Row = 4, Col = 8, Span = 2, Cmd = "done" }, "Connect");
        HighlightPadKey();
    }

    void AddKey(PadKey k, string label)
    {
        var p = _pad!;
        k.Text = new TextBlock
        {
            Text = label, FontSize = k.Cmd == "" ? 19 : 14, FontFamily = Ui.Font, Foreground = Ui.Fg, FontWeight = k.Cmd == "" ? FontWeights.Normal : FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        k.View = new Border
        {
            Margin = new Thickness(3), CornerRadius = new CornerRadius(8), Child = k.Text, Cursor = Cursors.Hand,
            Background = k.Cmd == "done" ? Ui.XboxGreen : k.Cmd == "" ? Ui.B(0x1AFFFFFF) : Ui.B(0x2AFFFFFF),
        };
        k.View.MouseLeftButtonUp += (_, e) => { e.Handled = true; p.Row = k.Row; p.Col = k.Col; HighlightPadKey(); PressKey(k); };
        Grid.SetRow(k.View, k.Row);
        Grid.SetColumn(k.View, k.Col);
        Grid.SetColumnSpan(k.View, k.Span);
        p.Keys.Children.Add(k.View);
        p.KeyList.Add(k);
    }

    PadKey KeyAt(int row, int col) => _pad!.KeyList.First(k => k.Row == row && col >= k.Col && col < k.Col + k.Span);

    void HighlightPadKey()
    {
        var p = _pad!;
        var cur = KeyAt(p.Row, p.Col);
        foreach (var k in p.KeyList)
        {
            bool on = k == cur;
            k.View.BorderBrush = on ? Ui.Fg : null;
            k.View.BorderThickness = new Thickness(on ? 2 : 0);
            if (on) { k.View.Background = Ui.Fg; k.Text.Foreground = Ui.Black; }
            else
            {
                k.View.Background = k.Cmd == "done" ? Ui.XboxGreen : k.Cmd == "" ? Ui.B(0x1AFFFFFF) : Ui.B(0x2AFFFFFF);
                k.Text.Foreground = Ui.Fg;
            }
        }
    }

    void UpdatePadField()
    {
        var p = _pad;
        if (p == null) return;
        string shown = p.Reveal ? p.Text.ToString() : new string('•', p.Text.Length);
        p.Field.Text = shown + "▏";
    }

    void PadInput(PadButton b)
    {
        var p = _pad!;
        switch (b)
        {
            case PadButton.Left:
            {
                var k = KeyAt(p.Row, p.Col);
                p.Col = k.Col - 1 < 0 ? 9 : k.Col - 1;
                p.Col = KeyAt(p.Row, p.Col).Col;
                break;
            }
            case PadButton.Right:
            {
                var k = KeyAt(p.Row, p.Col);
                p.Col = k.Col + k.Span > 9 ? 0 : k.Col + k.Span;
                break;
            }
            case PadButton.Up: p.Row = Math.Max(0, p.Row - 1); break;
            case PadButton.Down: p.Row = Math.Min(4, p.Row + 1); break;
            case PadButton.A: PressKey(KeyAt(p.Row, p.Col)); return;
            case PadButton.X: PadBackspace(); return;
            case PadButton.Y: p.Text.Append(' '); UpdatePadField(); return;
            case PadButton.LB: p.Shift = !p.Shift; BuildPadKeys(); return;
            case PadButton.RB: p.Layer = 1 - p.Layer; BuildPadKeys(); return;
            case PadButton.View: p.Reveal = !p.Reveal; UpdatePadField(); return;
            case PadButton.Menu: PadSubmit(); return;
            case PadButton.B: ClosePad(); return;
            default: return;
        }
        HighlightPadKey();
    }

    void PressKey(PadKey k)
    {
        var p = _pad!;
        switch (k.Cmd)
        {
            case "shift": p.Shift = !p.Shift; BuildPadKeys(); break;
            case "sym": p.Layer = 1 - p.Layer; BuildPadKeys(); break;
            case "space": p.Text.Append(' '); UpdatePadField(); break;
            case "back": PadBackspace(); break;
            case "done": PadSubmit(); break;
            default:
                p.Text.Append(k.Ch);
                if (p.Shift && p.Layer == 0 && char.IsLetter(k.Ch![0])) { p.Shift = false; BuildPadKeys(); }
                UpdatePadField();
                break;
        }
    }

    void PadBackspace()
    {
        var p = _pad;
        if (p == null || p.Text.Length == 0) return;
        p.Text.Length--;
        UpdatePadField();
    }

    void PadSubmit()
    {
        var p = _pad;
        if (p == null || p.Text.Length == 0) return;
        p.Done(p.Text.ToString());
    }

    void ClosePad()
    {
        var p = _pad;
        if (p == null) return;
        _pad = null;
        _layer.Children.Remove(p.View);
        if (_menuView != null) _menuView.Visibility = Visibility.Visible;
    }
}
