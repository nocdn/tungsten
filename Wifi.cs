using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace Tungsten;

public sealed class WifiNet
{
    public string Ssid = "";
    public byte Bars;
    public double Rssi;
    public bool Secured, Enterprise;
    public WiFiAvailableNetwork Raw = null!;
}

/// <summary>Continuously re-scans Wi-Fi while the panel is open; connect with saved profile or password.</summary>
public sealed class WifiService
{
    WiFiAdapter? _adapter;
    Radio? _radio;
    bool _init;
    CancellationTokenSource? _cts;

    public bool Active { get; private set; }
    public bool Scanning { get; private set; }
    public string? Message { get; private set; }
    public string? ConnectedSsid { get; private set; }
    public bool? RadioOn { get; private set; }
    public bool HasRadio => _radio != null;
    public List<WifiNet> Networks { get; private set; } = new();
    public readonly Dictionary<string, string> Status = new();
    public event Action? Changed;

    void Raise() { try { Changed?.Invoke(); } catch (Exception e) { Program.Log("wifi changed: " + e); } }

    public async void Start()
    {
        if (Active) return;
        Active = true;
        _cts = new CancellationTokenSource();
        var tok = _cts.Token;
        try
        {
            await Init();
            if (_adapter == null) return;
            while (!tok.IsCancellationRequested)
            {
                await ScanOnce();
                await Task.Delay(5000, tok);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Message = "Wi-Fi unavailable"; Program.Log("wifi: " + e); Raise(); }
    }

    public void Stop()
    {
        Active = false;
        Scanning = false;
        _cts?.Cancel();
    }

    async Task Init()
    {
        if (_init) return;
        _init = true;
        var access = await WiFiAdapter.RequestAccessAsync();
        if (access != WiFiAccessStatus.Allowed)
        {
            Message = "Turn on location access for desktop apps to scan Wi-Fi";
            Raise();
            _init = false;
            return;
        }
        var adapters = await WiFiAdapter.FindAllAdaptersAsync();
        foreach (var a in adapters)
        {
            try { if (await a.NetworkAdapter.GetConnectedProfileAsync() != null) { _adapter = a; break; } } catch { }
        }
        _adapter ??= adapters.FirstOrDefault();
        if (_adapter == null) { Message = "No Wi-Fi adapter found"; Raise(); return; }

        try
        {
            var radios = await Radio.GetRadiosAsync();
            _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            if (_radio != null)
            {
                RadioOn = _radio.State == RadioState.On;
                _radio.StateChanged += (r, _) => { RadioOn = r.State == RadioState.On; Raise(); };
            }
        }
        catch (Exception e) { Program.Log("wifi radio: " + e.Message); }
    }

    async Task ScanOnce()
    {
        if (_adapter == null) return;
        Scanning = true; Raise();
        try { await _adapter.ScanAsync(); }
        catch (Exception e) { Program.Log("wifi scan: " + e.Message); }
        await UpdateConnected();
        var all = _adapter.NetworkReport.AvailableNetworks;
        var list = all.Where(n => !string.IsNullOrEmpty(n.Ssid))
            .GroupBy(n => n.Ssid)
            .Select(g => g.OrderByDescending(n => n.NetworkRssiInDecibelMilliwatts).First())
            .Select(n =>
            {
                var auth = n.SecuritySettings.NetworkAuthenticationType.ToString();
                return new WifiNet
                {
                    Ssid = n.Ssid,
                    Bars = n.SignalBars,
                    Rssi = n.NetworkRssiInDecibelMilliwatts,
                    Secured = auth is not ("Open80211" or "None" or "Owe"),
                    Enterprise = auth is "Wpa" or "Rsna" or "Wpa3" or "Wpa3Enterprise" or "Wpa3Enterprise192Bits",
                    Raw = n,
                };
            })
            .OrderByDescending(n => n.Ssid == ConnectedSsid)
            .ThenByDescending(n => n.Rssi)
            .ToList();
        Message = list.Count == 0 && all.Count > 0 ? "Network names hidden - allow location access in Windows privacy settings" : null;
        Networks = list;
        Scanning = false;
        Raise();
    }

    async Task UpdateConnected()
    {
        try
        {
            var p = _adapter == null ? null : await _adapter.NetworkAdapter.GetConnectedProfileAsync();
            ConnectedSsid = p != null && p.IsWlanConnectionProfile ? p.WlanConnectionProfileDetails.GetConnectedSsid() : null;
        }
        catch { ConnectedSsid = null; }
    }

    public bool HasProfile(string ssid)
    {
        try { return NetworkInformation.GetConnectionProfiles().Any(p => p.IsWlanConnectionProfile && p.ProfileName == ssid); }
        catch { return false; }
    }

    public async Task<WiFiConnectionStatus> Connect(WifiNet n, string? password)
    {
        if (_adapter == null) return WiFiConnectionStatus.UnspecifiedFailure;
        Status[n.Ssid] = "Connecting…"; Raise();
        WiFiConnectionStatus st;
        try
        {
            var r = password == null
                ? await _adapter.ConnectAsync(n.Raw, WiFiReconnectionKind.Automatic)
                : await _adapter.ConnectAsync(n.Raw, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password });
            st = r.ConnectionStatus;
        }
        catch (Exception e) { Program.Log("wifi connect: " + e.Message); st = WiFiConnectionStatus.UnspecifiedFailure; }
        Program.Log($"Wi-Fi connect {n.Ssid}: {st}");
        Status.Remove(n.Ssid);
        if (st != WiFiConnectionStatus.Success)
            Status[n.Ssid] = st switch
            {
                WiFiConnectionStatus.InvalidCredential => "Wrong password",
                WiFiConnectionStatus.NetworkNotAvailable => "Out of range",
                WiFiConnectionStatus.Timeout => "Timed out",
                _ => "Couldn't connect",
            };
        await UpdateConnected();
        Raise();
        if (st != WiFiConnectionStatus.Success) ClearStatusLater(n.Ssid);
        return st;
    }

    async void ClearStatusLater(string ssid)
    {
        var s = Status.GetValueOrDefault(ssid);
        await Task.Delay(5000);
        if (Status.GetValueOrDefault(ssid) == s) { Status.Remove(ssid); Raise(); }
    }

    public async void Disconnect()
    {
        if (_adapter == null) return;
        if (ConnectedSsid != null) Status[ConnectedSsid] = "Disconnecting…";
        Raise();
        try { _adapter.Disconnect(); } catch (Exception e) { Program.Log("wifi disconnect: " + e.Message); }
        await Task.Delay(1000);
        Status.Clear();
        await UpdateConnected();
        Raise();
    }

    public async void ToggleRadio()
    {
        if (_radio == null) return;
        try
        {
            var st = await _radio.SetStateAsync(RadioOn == true ? RadioState.Off : RadioState.On);
            Message = st == RadioAccessStatus.Allowed ? null : "Windows blocked the Wi-Fi switch";
            RadioOn = _radio.State == RadioState.On;
        }
        catch (Exception e) { Message = "Couldn't switch Wi-Fi"; Program.Log("wifi radio toggle: " + e.Message); }
        Raise();
    }
}
