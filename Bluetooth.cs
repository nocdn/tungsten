using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Tungsten;

public sealed class BtDevice
{
    public string Id = "";
    public DeviceInformation Info = null!;
    public bool IsLe;
    public string Name = "";
    public bool Paired, Connected;
    public string Address = "";
    public int? Rssi;
    public int Major = -1;
    public string? Status;
    public bool Busy;
    public DateTime LastSeen = DateTime.UtcNow;

    public string AddressHex => Address.Replace(":", "").Replace("-", "").ToUpperInvariant();

    public void Refresh()
    {
        var p = Info.Properties;
        Name = Info.Name ?? "";
        Paired = p.TryGetValue("System.Devices.Aep.IsPaired", out var ip) && ip is bool bp ? bp : Info.Pairing.IsPaired;
        Connected = p.TryGetValue("System.Devices.Aep.IsConnected", out var ic) && ic is bool bc && bc;
        if (p.TryGetValue("System.Devices.Aep.DeviceAddress", out var a) && a is string s && s.Length > 0) Address = s;
        try { if (p.TryGetValue("System.Devices.Aep.SignalStrength", out var r) && r != null) Rssi = Convert.ToInt32(r); } catch { }
        try { if (p.TryGetValue("System.Devices.Aep.Bluetooth.Cod.Major", out var mj) && mj != null) Major = Convert.ToInt32(mj); } catch { }
    }
}

/// <summary>
/// Continuously scanning Bluetooth list (paired + nearby), with pair / connect / disconnect / forget.
/// </summary>
public sealed class BluetoothService
{
    const string ClassicAqs = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";
    const string LeAqs = "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";
    static readonly string[] Props =
    {
        "System.Devices.Aep.IsPaired", "System.Devices.Aep.IsConnected", "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.SignalStrength", "System.Devices.Aep.Bluetooth.Cod.Major",
    };

    readonly object _lock = new();
    readonly Dictionary<string, BtDevice> _devs = new();
    DeviceWatcher? _classic, _le;
    int _gen;
    Radio? _radio;
    bool _radioInit;

    public bool Active { get; private set; }
    public bool? RadioOn { get; private set; }
    public bool HasRadio => _radio != null;
    public string? Message { get; private set; }
    public event Action? Changed;

    void Raise() { try { Changed?.Invoke(); } catch (Exception e) { Program.Log("bt changed: " + e); } }

    public List<BtDevice> Snapshot() { lock (_lock) return _devs.Values.ToList(); }

    public async void Start()
    {
        if (Active) return;
        Active = true;
        Message = null;
        int gen = ++_gen;
        try
        {
            await InitRadio();
            if (!Active || gen != _gen) return;
            _classic = Create(ClassicAqs, false, gen);
            _le = Create(LeAqs, true, gen);
            _classic.Start();
            _le.Start();
        }
        catch (Exception e) { Message = "Bluetooth unavailable"; Program.Log("bt start: " + e); }
        Raise();
    }

    public void Stop()
    {
        Active = false;
        _gen++;
        foreach (var w in new[] { _classic, _le })
        {
            try
            {
                if (w != null && (w.Status == DeviceWatcherStatus.Started || w.Status == DeviceWatcherStatus.EnumerationCompleted)) w.Stop();
            }
            catch { }
        }
        _classic = _le = null;
    }

    DeviceWatcher Create(string aqs, bool le, int gen)
    {
        var w = DeviceInformation.CreateWatcher(aqs, Props, DeviceInformationKind.AssociationEndpoint);
        w.Added += (_, di) =>
        {
            if (gen != _gen) return;
            lock (_lock)
            {
                if (!_devs.TryGetValue(di.Id, out var d)) { d = new BtDevice { Id = di.Id, IsLe = le }; _devs[di.Id] = d; }
                d.Info = di;
                d.LastSeen = DateTime.UtcNow;
                d.Refresh();
            }
            Raise();
        };
        w.Updated += (_, u) =>
        {
            if (gen != _gen) return;
            lock (_lock)
            {
                if (_devs.TryGetValue(u.Id, out var d)) { d.Info.Update(u); d.LastSeen = DateTime.UtcNow; d.Refresh(); }
            }
            Raise();
        };
        w.Removed += (_, u) =>
        {
            if (gen != _gen) return;
            lock (_lock) { if (_devs.TryGetValue(u.Id, out var d) && !d.Paired && !d.Busy) _devs.Remove(u.Id); }
            Raise();
        };
        w.EnumerationCompleted += async (_, _) =>
        {
            Purge();
            Raise();
            // Keep it "live": re-run the inquiry every few seconds while the panel is open.
            await Task.Delay(8000);
            if (Active && gen == _gen && w.Status == DeviceWatcherStatus.EnumerationCompleted)
                try { w.Stop(); } catch { }
        };
        w.Stopped += (_, _) =>
        {
            if (Active && gen == _gen)
                try { w.Start(); } catch (Exception e) { Program.Log("bt restart: " + e.Message); }
        };
        return w;
    }

    void Purge()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-45);
            foreach (var d in _devs.Values.Where(d => !d.Paired && !d.Busy && d.LastSeen < cutoff).ToList())
                _devs.Remove(d.Id);
        }
    }

    async Task InitRadio()
    {
        if (_radioInit) return;
        _radioInit = true;
        try
        {
            await Radio.RequestAccessAsync();
            var radios = await Radio.GetRadiosAsync();
            _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_radio != null)
            {
                RadioOn = _radio.State == RadioState.On;
                _radio.StateChanged += (r, _) => { RadioOn = r.State == RadioState.On; Raise(); };
            }
        }
        catch (Exception e) { Program.Log("bt radio: " + e.Message); }
    }

    public async void ToggleRadio()
    {
        if (_radio == null) return;
        try
        {
            var st = await _radio.SetStateAsync(RadioOn == true ? RadioState.Off : RadioState.On);
            Message = st == RadioAccessStatus.Allowed ? null : "Windows blocked the Bluetooth switch";
            RadioOn = _radio.State == RadioState.On;
        }
        catch (Exception e) { Message = "Couldn't switch Bluetooth"; Program.Log("bt toggle radio: " + e.Message); }
        Raise();
    }

    static async Task<bool> WaitFor(BtDevice d, Func<BtDevice, bool> cond, int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            if (cond(d)) return true;
            await Task.Delay(300);
        }
        return cond(d);
    }

    public async void Toggle(BtDevice d)
    {
        if (d.Busy) return;
        d.Busy = true;
        try
        {
            if (!d.Paired)
            {
                d.Status = "Pairing…"; Raise();
                if (await Pair(d))
                {
                    d.Paired = true;
                    d.Status = "Connecting…"; Raise();
                    d.Status = await WaitFor(d, x => x.Connected, 12000) ? null : "Paired";
                }
                else d.Status = "Pairing failed";
            }
            else if (d.Connected)
            {
                d.Status = "Disconnecting…"; Raise();
                int n = await Task.Run(() => BtAudio.Send(d.AddressHex, disconnect: true));
                if (n == 0) d.Status = "Disconnect it on the device";
                else d.Status = await WaitFor(d, x => !x.Connected, 8000) ? null : "Still connected";
            }
            else
            {
                d.Status = "Connecting…"; Raise();
                int n = await Task.Run(() => BtAudio.Send(d.AddressHex, disconnect: false));
                if (n == 0) d.Status = "Turn the device on to reconnect";
                else d.Status = await WaitFor(d, x => x.Connected, 15000) ? null : "Couldn't connect";
            }
        }
        catch (Exception e) { d.Status = "Something went wrong"; Program.Log("bt toggle: " + e); }
        finally { d.Busy = false; Raise(); }

        if (d.Status != null)
        {
            var st = d.Status;
            await Task.Delay(5000);
            if (d.Status == st) { d.Status = null; Raise(); }
        }
    }

    async Task<bool> Pair(BtDevice d)
    {
        var custom = d.Info.Pairing.Custom;
        void Handler(DeviceInformationCustomPairing s, DevicePairingRequestedEventArgs a)
        {
            if (a.PairingKind == DevicePairingKinds.ProvidePin) a.Accept("0000");
            else a.Accept();
            if (a.PairingKind is DevicePairingKinds.DisplayPin or DevicePairingKinds.ConfirmPinMatch && !string.IsNullOrEmpty(a.Pin))
            {
                d.Status = $"Pairing… PIN {a.Pin}";
                Raise();
            }
        }
        custom.PairingRequested += Handler;
        try
        {
            var res = await custom.PairAsync(
                DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmPinMatch,
                DevicePairingProtectionLevel.Default);
            Program.Log($"Pair {d.Name}: {res.Status}");
            return res.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
        }
        finally { custom.PairingRequested -= Handler; }
    }

    public async void Forget(BtDevice d)
    {
        if (d.Busy || !d.Paired) return;
        d.Busy = true;
        d.Status = "Removing…"; Raise();
        try
        {
            var r = await d.Info.Pairing.UnpairAsync();
            if (r.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired)
            { d.Paired = false; d.Connected = false; d.Status = null; }
            else d.Status = "Couldn't remove";
        }
        catch (Exception e) { d.Status = "Couldn't remove"; Program.Log("bt forget: " + e.Message); }
        finally { d.Busy = false; Raise(); }
    }
}

/// <summary>
/// Connect / disconnect paired Bluetooth audio devices the same way the Windows volume flyout does:
/// a one-shot KS property on the device's Bluetooth audio filter.
/// </summary>
public static class BtAudio
{
    static readonly Guid KSPROPSETID_BtAudio = new("7fa06c40-b8f6-4c7e-8556-e8c33a12e54d");

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate([In] ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KSPROPERTY { public Guid Set; public uint Id; public uint Flags; }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KSPROPERTY prop, uint propLen, IntPtr data, uint dataLen, out uint returned);
    }

    /// <summary>Bluetooth audio KS filter device ids (diagnostics).</summary>
    public static List<string> Filters() => Walk(null, null);

    /// <returns>How many audio filters for that address accepted the request (0 = not an audio device).</returns>
    public static int Send(string addressHex, bool disconnect) => Walk(addressHex, disconnect).Count;

    static List<string> Walk(string? addressHex, bool? disconnect)
    {
        var result = new List<string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        if (en.EnumAudioEndpoints(2 /*eAll*/, 0xF /*all states*/, out var col) != 0) return result;
        col.GetCount(out uint n);
        for (uint i = 0; i < n; i++)
        {
            try
            {
                if (col.Item(i, out var dev) != 0) continue;
                var iid = typeof(IDeviceTopology).GUID;
                if (dev.Activate(ref iid, 0x17, IntPtr.Zero, out object o) != 0) continue;
                var topo = (IDeviceTopology)o;
                if (topo.GetConnector(0, out var conn) != 0) continue;
                if (conn.GetDeviceIdConnectedTo(out string filterId) != 0 || string.IsNullOrEmpty(filterId)) continue;
                var up = filterId.ToUpperInvariant();
                if (!up.Contains("BTHENUM") && !up.Contains("BTHHFENUM")) continue;
                if (addressHex != null && !up.Contains(addressHex)) continue;
                if (!done.Add(up)) continue;
                if (disconnect == null) { result.Add(filterId); continue; }

                if (en.GetDevice(filterId, out var filterDev) != 0) continue;
                var ksIid = typeof(IKsControl).GUID;
                if (filterDev.Activate(ref ksIid, 0x17, IntPtr.Zero, out object ko) != 0) continue;
                var ks = (IKsControl)ko;
                var prop = new KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = disconnect.Value ? 1u : 0u, Flags = 1 /*GET*/ };
                int hr = ks.KsProperty(ref prop, (uint)Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _);
                Program.Log($"BT audio {(disconnect.Value ? "disconnect" : "connect")} {filterId}: hr=0x{hr:X8}");
                result.Add(filterId);
            }
            catch (Exception e) { Program.Log("BtAudio: " + e.Message); }
        }
        return result;
    }
}
