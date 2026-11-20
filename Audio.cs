using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Tungsten;

public sealed record AudioOutput(string Id, string Name, bool IsDefault);

/// <summary>Windows playback endpoints. Only polls while the volume menu is visible.</summary>
public sealed class AudioService
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    public List<AudioOutput> Outputs { get; private set; } = new();
    public string? DefaultId { get; private set; }
    public int Volume { get; private set; }
    public bool Muted { get; private set; }
    public string? Message { get; private set; }
    public bool HasOutput => DefaultId != null;
    public event Action? Changed;
    string _signature = "";

    public AudioService() => _timer.Tick += (_, _) => Refresh();
    public void Start() { Message = null; Refresh(); _timer.Start(); }
    public void Stop() => _timer.Stop();

    public void Refresh()
    {
        try
        {
            using var devices = new AudioDevices();
            DefaultId = devices.DefaultId();
            Outputs = devices.Outputs(DefaultId);
            if (DefaultId != null)
            {
                var state = devices.Volume(DefaultId);
                Volume = state.volume; Muted = state.muted;
            }
            else { Volume = 0; Muted = false; }
            if (Message == "Audio is unavailable") Message = null;
        }
        catch (Exception e)
        {
            Outputs = new(); DefaultId = null; Volume = 0; Muted = false;
            Message = "Audio is unavailable";
            Program.Log("audio refresh: " + e.Message);
        }
        RaiseIfChanged();
    }

    public void Select(string id) => Run(() =>
    {
        using var devices = new AudioDevices();
        if (!devices.Outputs(null).Any(d => d.Id == id)) throw new InvalidOperationException("The output was disconnected");
        AudioDevices.SetDefault(id);
        // Read it back: a failed policy change must never show a false selected checkmark.
        if (devices.DefaultId() != id) throw new InvalidOperationException("Windows did not switch the output");
    }, "Couldn't switch output");

    public void SetVolume(int value) => Run(() =>
    {
        using var devices = new AudioDevices();
        string id = devices.DefaultId() ?? throw new InvalidOperationException("No output connected");
        devices.SetVolume(id, Math.Clamp(value, 0, 100));
    }, "Couldn't change volume");

    public void ChangeVolume(int delta) { Refresh(); if (HasOutput) SetVolume(Volume + delta); }
    public void ToggleMute() => Run(() =>
    {
        using var devices = new AudioDevices();
        string id = devices.DefaultId() ?? throw new InvalidOperationException("No output connected");
        devices.SetMute(id, !devices.Volume(id).muted);
    }, "Couldn't change mute");

    void Run(Action action, string error)
    {
        try { action(); Message = null; }
        catch (Exception e) { Message = error; Program.Log("audio: " + e); }
        Refresh();
    }

    void RaiseIfChanged()
    {
        string sig = string.Join("|", Outputs.Select(d => $"{d.Id}:{d.Name}:{d.IsDefault}")) + $"/{DefaultId}/{Volume}/{Muted}/{Message}";
        if (sig == _signature) return;
        _signature = sig;
        Changed?.Invoke();
    }
}

/// <summary>Short-lived COM handles, always created/used/released on the calling thread.</summary>
sealed class AudioDevices : IDisposable
{
    readonly IDeviceEnumerator _enumerator = (IDeviceEnumerator)new DeviceEnumerator();
    static readonly Guid VolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    static readonly PropertyKey FriendlyName = new() { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
    public void Dispose() => Release(_enumerator);
    static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    static void Release(object? o) { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }

    public string? DefaultId(int role = 1)
    {
        int hr = _enumerator.GetDefaultAudioEndpoint(0, role, out var device);
        if (hr == unchecked((int)0x80070490)) { Release(device); return null; }
        try { Check(hr); Check(device.GetId(out string id)); return id; }
        finally { Release(device); }
    }

    public List<AudioOutput> Outputs(string? selected)
    {
        var result = new List<AudioOutput>();
        Check(_enumerator.EnumAudioEndpoints(0, 1 /*active render devices*/, out var collection));
        try
        {
            Check(collection.GetCount(out uint count));
            for (uint i = 0; i < count; i++)
            {
                IDevice? device = null; IPropertyStore? store = null;
                try
                {
                    Check(collection.Item(i, out device));
                    Check(device.GetId(out string id));
                    Check(device.OpenPropertyStore(0, out store));
                    var key = FriendlyName;
                    Check(store.GetValue(ref key, out var value));
                    string name;
                    try { name = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "Audio output" : "Audio output"; }
                    finally { PropVariantClear(ref value); }
                    result.Add(new AudioOutput(id, name, id == selected));
                }
                catch (COMException) { /* A device can disappear between enumeration and reading its name. */ }
                finally { Release(store); Release(device); }
            }
        }
        finally { Release(collection); }
        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    T WithVolume<T>(string id, Func<IEndpointVolume, T> action)
    {
        IDevice? device = null; object? volume = null;
        try
        {
            Check(_enumerator.GetDevice(id, out device));
            var iid = VolumeIid;
            Check(device.Activate(ref iid, 23, IntPtr.Zero, out volume));
            return action((IEndpointVolume)volume);
        }
        finally { Release(volume); Release(device); }
    }

    public (int volume, bool muted) Volume(string id) => WithVolume(id, v =>
    {
        Check(v.GetMasterVolumeLevelScalar(out float level)); Check(v.GetMute(out bool mute));
        return ((int)Math.Round(level * 100), mute);
    });
    public void SetVolume(string id, int level) => WithVolume(id, v => { var context = Guid.Empty; Check(v.SetMasterVolumeLevelScalar(level / 100f, ref context)); return 0; });
    public void SetMute(string id, bool mute) => WithVolume(id, v => { var context = Guid.Empty; Check(v.SetMute(mute, ref context)); return 0; });

    // Windows exposes no public setter for the default playback endpoint. This is the
    // PolicyConfig interface used by Windows audio switchers (Win7 through current Win11).
    public static void SetDefault(string id, int? onlyRole = null)
    {
        var policy = (IPolicyConfig)new PolicyConfig();
        try { for (int role = 0; role < 3; role++) if (onlyRole == null || role == onlyRole) policy.SetDefaultEndpoint(id, role); }
        finally { Release(policy); }
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint states, out IDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEndpointVolume
    {
        void RegisterControlChangeNotify();
        void UnregisterControlChangeNotify();
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfig { }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        void Unused1(); void Unused2(); void Unused3(); void Unused4();
        void Unused5(); void Unused6(); void Unused7(); void Unused8();
        void Unused9(); void Unused10();
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
}
