using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Tungsten;

public static class Program
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tungsten");
    const string ToggleName = @"Local\Tungsten.Toggle", QuitName = @"Local\Tungsten.Quit", PressName = @"Local\Tungsten.Press";
    static readonly object LogLock = new();

    public static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(DataDir);
                var p = Path.Combine(DataDir, "log.txt");
                if (File.Exists(p) && new FileInfo(p).Length > 1_000_000) File.Delete(p);
                File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--diag")) { Diag.Run(); return 0; }
        int mk = Array.IndexOf(args, "--make-ico");
        if (mk >= 0 && mk + 1 < args.Length)
        {
            File.WriteAllBytes(args[mk + 1], AppIcon.BuildIco());
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(AppIcon.Render(256)));
            using var fs = File.Create(Path.ChangeExtension(args[mk + 1], ".png"));
            png.Save(fs);
            return 0;
        }

        if (args.Contains("--install-admin")) return InstallAdmin(args.Contains("--show"));
        if (args.Contains("--uninstall-admin")) return UninstallAdmin();

        const string MutexName = @"Local\Tungsten.Instance";
        if (args.Contains("--replace"))
            for (int i = 0; i < 50 && Elevation.MutexExists(MutexName); i++) Thread.Sleep(100);
        var mutex = Elevation.CreateMutex(MutexName, out bool exists);
        bool first = !exists;
        int pr = Array.IndexOf(args, "--press");
        if (pr >= 0 && pr + 1 < args.Length)
        {
            // Virtual controller presses for scripting/testing: --press Right,Right,A
            if (first) return 1;
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "press.txt"), args[pr + 1]);
            Signal(PressName);
            return 0;
        }
        if (!first)
        {
            // Already running: "--quit" stops it, launching it again opens the overlay.
            string? name = args.Contains("--quit") ? QuitName : args.Contains("--background") ? null : ToggleName;
            if (name != null) Signal(name);
            return 0;
        }
        if (args.Contains("--quit")) return 0;

        // Admin mode installed: hand over to the elevated scheduled task (no UAC prompt).
        if (!Elevation.IsElevated && Elevation.TaskInstalled())
        {
            mutex.Dispose();
            if (Elevation.RunTask())
            {
                if (args.Contains("--show")) SignalWhenReady(ToggleName);
                return 0;
            }
            mutex = Elevation.CreateMutex(MutexName, out _);
        }
        GC.KeepAlive(mutex);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Log("UI exception: " + e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("FATAL: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("Task exception: " + e.Exception); e.SetObserved(); };

        var toggle = Elevation.CreateEvent(ToggleName);
        var quit = Elevation.CreateEvent(QuitName);
        var press = Elevation.CreateEvent(PressName);
        var hub = new Hub(app);
        app.Startup += (_, _) => hub.Start(args.Contains("--show"));
        new Thread(() =>
        {
            while (true)
            {
                int i = WaitHandle.WaitAny(new WaitHandle[] { toggle, quit, press });
                if (i == 0) app.Dispatcher.BeginInvoke(new Action(hub.ToggleOverlay));
                else if (i == 2)
                {
                    string seq = "";
                    try { seq = File.ReadAllText(Path.Combine(DataDir, "press.txt")); } catch { }
                    foreach (var name in seq.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (Enum.TryParse<PadButton>(name, true, out var b))
                            app.Dispatcher.Invoke(() => hub.Overlay.OnPad(b));
                        Thread.Sleep(250);
                    }
                }
                else { app.Dispatcher.BeginInvoke(new Action(hub.Quit)); return; }
            }
        }) { IsBackground = true }.Start();
        app.Run();
        GC.KeepAlive(mutex);
        return 0;
    }

    static bool Signal(string name)
    {
        try { using var ev = EventWaitHandle.OpenExisting(name); ev.Set(); return true; } catch { return false; }
    }

    static void SignalWhenReady(string name)
    {
        for (int i = 0; i < 80; i++) { Thread.Sleep(100); if (Signal(name)) return; }
    }

    /// <summary>Runs elevated (UAC): registers the admin task, then restarts Tungsten through it.</summary>
    static int InstallAdmin(bool show)
    {
        if (!Elevation.IsElevated) return Elevation.RunElevated("--install-admin" + (show ? " --show" : "")) ? 0 : 1;
        Signal(QuitName);
        Legacy.QuitOldInstance();
        for (int i = 0; i < 50 && Elevation.MutexExists(@"Local\Tungsten.Instance"); i++) Thread.Sleep(100);
        Thread.Sleep(300);
        if (!Elevation.CopyToProgramFiles() || !Elevation.Install()) { Log("Admin mode: install failed"); return 1; }
        Legacy.RemoveOldTask();
        StartupReg.Apply(ConfigStore.Load().StartWithWindows, Elevation.InstalledExe);
        Log("Admin mode installed to " + Elevation.InstallDir);
        Elevation.RunTask();
        if (show) SignalWhenReady(ToggleName);
        return 0;
    }

    /// <summary>Runs elevated: removes the admin task and restarts Tungsten normally.</summary>
    static int UninstallAdmin()
    {
        if (!Elevation.IsElevated) return Elevation.RunElevated("--uninstall-admin") ? 0 : 1;
        Elevation.Uninstall();
        Log("Admin mode removed");
        Signal(QuitName);
        for (int i = 0; i < 50 && Elevation.MutexExists(@"Local\Tungsten.Instance"); i++) Thread.Sleep(100);
        try { Elevation.Shell(Environment.ProcessPath!, "--background"); } catch { }
        return 0;
    }
}

public sealed class Hub
{
    readonly Application _app;
    public AppConfig Config = null!;
    public Gamepad Pad = null!;
    public Overlay Overlay = null!;
    public readonly BluetoothService Bt = new();
    public readonly WifiService Wifi = new();
    System.Windows.Forms.NotifyIcon? _tray;
    System.Windows.Forms.ToolStripMenuItem? _startItem;
    DispatcherTimer? _guard;
    TestWindow? _test;

    public Hub(Application app) { _app = app; }

    public void Start(bool show)
    {
        Legacy.Migrate();
        Config = ConfigStore.Load();
        if (Config.BlockGameBar) GameBarGuard.Enforce();
        StartupReg.Apply(Config.StartWithWindows);

        Overlay = new Overlay(this);
        Pad = new Gamepad(_app.Dispatcher);
        Pad.Pressed += b => Overlay.OnPad(b);
        Pad.Start();

        _guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _guard.Tick += (_, _) => { if (Config.BlockGameBar) GameBarGuard.Enforce(); };
        _guard.Start();

        CreateTray();
        Log($"Started {Environment.ProcessPath} elevated={Elevation.IsElevated}");
        if (show) Overlay.Open();
    }

    static void Log(string s) => Program.Log(s);

    public void ToggleOverlay()
    {
        if (Overlay.IsOpen) Overlay.Dismiss(true); else Overlay.Open();
    }

    public bool TestActive => _test != null && _test.IsActive;

    public void ShowTest()
    {
        if (_test == null)
        {
            _test = new TestWindow(this);
            _test.Closed += (_, _) => _test = null;
            _test.Show();
        }
        _test.Activate();
    }

    public void SaveConfig() => ConfigStore.Save(Config);

    public void SetStartup(bool on)
    {
        Config.StartWithWindows = on;
        StartupReg.Apply(on);
        SaveConfig();
        if (_startItem != null) _startItem.Checked = on;
    }

    public void SetBlockGameBar(bool on)
    {
        Config.BlockGameBar = on;
        if (on) GameBarGuard.Enforce(); else GameBarGuard.Release();
        SaveConfig();
    }

    /// <summary>Turning admin mode on shows a UAC prompt once; Tungsten then restarts elevated.</summary>
    public void SetAdminMode(bool on)
    {
        if (on == Elevation.IsElevated) return;
        if (!Elevation.RunElevated(on ? "--install-admin --show" : "--uninstall-admin"))
            Overlay.ShowToast("Admin mode needs you to accept the Windows prompt");
    }

    public void Quit()
    {
        try { Pad?.Dispose(); Bt.Stop(); Wifi.Stop(); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _app.Shutdown();
    }

    void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Tungsten", null, (_, _) => Overlay.Open());
        menu.Items.Add("Controller test", null, (_, _) => ShowTest());
        _startItem = new System.Windows.Forms.ToolStripMenuItem("Start with Windows") { Checked = Config.StartWithWindows, CheckOnClick = true };
        _startItem.Click += (_, _) => SetStartup(_startItem.Checked);
        menu.Items.Add(_startItem);
        var admin = new System.Windows.Forms.ToolStripMenuItem("Admin mode (show over games run as administrator)") { Checked = Elevation.IsElevated };
        admin.Click += (_, _) => SetAdminMode(!Elevation.IsElevated);
        menu.Items.Add(admin);
        menu.Items.Add("Open data folder", null, (_, _) => { try { Elevation.Shell(Program.DataDir); } catch { } });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit Tungsten", null, (_, _) => Quit());
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "Tungsten - press Home on your controller",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Overlay.Open(); };
    }
}

/// <summary>Tungsten used to be called GuideHub: carry its settings over and retire the old copy.</summary>
static class Legacy
{
    static readonly string OldDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GuideHub");

    public static void Migrate()
    {
        try
        {
            string oldCfg = Path.Combine(OldDataDir, "config.json"), newCfg = Path.Combine(Program.DataDir, "config.json");
            if (File.Exists(oldCfg) && !File.Exists(newCfg))
            {
                Directory.CreateDirectory(Program.DataDir);
                File.Copy(oldCfg, newCfg);
                Program.Log("Imported GuideHub settings");
            }
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            run?.DeleteValue("GuideHub", false);
        }
        catch (Exception e) { Program.Log("Migrate: " + e.Message); }
        QuitOldInstance();
    }

    public static void QuitOldInstance()
    {
        try { using var ev = EventWaitHandle.OpenExisting(@"Local\GuideHub.Quit"); ev.Set(); } catch { }
    }

    /// <summary>Needs elevation.</summary>
    public static void RemoveOldTask()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/delete /tn \"GuideHub\" /f") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(10000);
        }
        catch { }
    }
}

/// <summary>`Tungsten.exe --diag` writes a hardware/feature report to %APPDATA%\Tungsten\diag.txt.</summary>
static class Diag
{
    public static void Run()
    {
        var sb = new StringBuilder();
        void Try(string title, Action a)
        {
            sb.AppendLine("== " + title);
            try { a(); } catch (Exception e) { sb.AppendLine("  ERROR " + e.GetType().Name + ": " + e.Message); }
        }

        Try("XInput", () =>
        {
            var pad = new Gamepad(Dispatcher.CurrentDispatcher);
            sb.AppendLine("  (probe via GameBar guard value) UseNexusForGameBarEnabled=" +
                Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", "missing"));
        });
        Try("Bluetooth audio filters", () =>
        {
            foreach (var f in BtAudio.Filters()) sb.AppendLine("  " + f);
        });
        Try("Bluetooth paired AEP", () =>
        {
            var list = Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True",
                new[] { "System.Devices.Aep.IsConnected", "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.Bluetooth.Cod.Major" },
                Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint).AsTask().Result;
            foreach (var d in list)
                sb.AppendLine($"  {d.Name} connected={d.Properties["System.Devices.Aep.IsConnected"]} addr={d.Properties["System.Devices.Aep.DeviceAddress"]} major={d.Properties["System.Devices.Aep.Bluetooth.Cod.Major"]}");
        });
        Try("Radios", () =>
        {
            Windows.Devices.Radios.Radio.RequestAccessAsync().AsTask().Wait();
            foreach (var r in Windows.Devices.Radios.Radio.GetRadiosAsync().AsTask().Result) sb.AppendLine($"  {r.Kind} {r.Name} {r.State}");
        });
        Try("Wi-Fi", () =>
        {
            var acc = Windows.Devices.WiFi.WiFiAdapter.RequestAccessAsync().AsTask().Result;
            sb.AppendLine("  access=" + acc);
            var ads = Windows.Devices.WiFi.WiFiAdapter.FindAllAdaptersAsync().AsTask().Result;
            foreach (var a in ads)
            {
                var prof = a.NetworkAdapter.GetConnectedProfileAsync().AsTask().Result;
                sb.AppendLine($"  adapter {a.NetworkAdapter.NetworkAdapterId} connected={(prof?.WlanConnectionProfileDetails?.GetConnectedSsid() ?? "-")}");
                a.ScanAsync().AsTask().Wait();
                var nets = a.NetworkReport.AvailableNetworks;
                sb.AppendLine($"  networks={nets.Count}: " + string.Join(", ", nets.Select(n => $"{n.Ssid}({n.SignalBars},{n.SecuritySettings.NetworkAuthenticationType})").Distinct().Take(12)));
            }
        });
        Try("Windows", () =>
        {
            foreach (var w in Native.AppWindows())
                sb.AppendLine($"  {w.Hwnd} pid={w.Pid} min={w.Minimized} fs={Native.IsFullscreen(w.Hwnd)} '{w.Title}' {w.Exe}");
        });
        Try("Pinned games", () =>
        {
            foreach (var g in ConfigStore.Load().Games)
            {
                var r = GameResolver.Resolve(g);
                sb.AppendLine($"  {g.Name}: exe={r.ExePath} dir={r.MatchDir} steam={r.SteamAppId} icon={r.Icon?.PixelWidth} art={r.Art?.PixelWidth} accent={r.Accent}");
            }
        });
        Directory.CreateDirectory(Program.DataDir);
        File.WriteAllText(Path.Combine(Program.DataDir, "diag.txt"), sb.ToString());
    }
}
