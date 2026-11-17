using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Tungsten;

public sealed class GameEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>.exe, .lnk or .url (e.g. a Steam shortcut).</summary>
    public string Path { get; set; } = "";
    public string? Arguments { get; set; }
    /// <summary>Extra executables that count as "this game is running".</summary>
    public List<string> Exes { get; set; } = new();
}

public sealed class AppConfig
{
    public List<GameEntry> Games { get; set; } = new();
    public bool StartWithWindows { get; set; } = true;
    public bool BlockGameBar { get; set; } = true;
}

public static class ConfigStore
{
    static string FilePath => System.IO.Path.Combine(Program.DataDir, "config.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Opts) ?? new AppConfig();
        }
        catch (Exception e) { Program.Log("Config load failed: " + e.Message); }
        return new AppConfig();
    }

    public static void Save(AppConfig c)
    {
        try
        {
            Directory.CreateDirectory(Program.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(c, Opts));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e) { Program.Log("Config save failed: " + e.Message); }
    }
}

public static class StartupReg
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool on, string? exe = null)
    {
        exe ??= Environment.ProcessPath;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("Tungsten", $"\"{exe}\" --background");
            else k.DeleteValue("Tungsten", false);
        }
        catch (Exception e) { Program.Log("Startup reg: " + e.Message); }
    }
}

/// <summary>
/// Keeps Windows' own controller bar (Game Bar "Nexus") from taking over the Home/Xbox button.
/// Settings → Gaming → Game Bar → "Allow your controller to open Game Bar".
/// </summary>
public static class GameBarGuard
{
    const string Key = @"Software\Microsoft\GameBar";
    const string Value = "UseNexusForGameBarEnabled";

    /// <returns>true if it had been re-enabled and was switched off again.</returns>
    public static bool Enforce()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (k.GetValue(Value) is int v && v == 0) return false;
            k.SetValue(Value, 0, RegistryValueKind.DWord);
            // Restart Game Bar so it picks the change up immediately (it relaunches on demand).
            foreach (var p in Process.GetProcessesByName("GameBar")) try { p.Kill(); } catch { }
            Program.Log("Game Bar controller button was enabled again - switched it back off.");
            return true;
        }
        catch (Exception e) { Program.Log("GameBarGuard: " + e.Message); return false; }
    }

    public static void Release()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue(Value, 1, RegistryValueKind.DWord);
        }
        catch (Exception e) { Program.Log("GameBarGuard release: " + e.Message); }
    }
}
