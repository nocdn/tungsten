using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Tungsten;

/// <summary>
/// "Admin mode": games such as GTA V run as administrator, and Windows will not let a normal
/// program take focus from, draw over or minimise an elevated window. Tungsten registers a
/// Task Scheduler task with highest privileges (one UAC prompt, once) and the normal start-up
/// entry launches through it, so Tungsten runs elevated with no prompt at every start.
/// Games are still launched un-elevated through Explorer.
/// </summary>
public static class Elevation
{
    public const string TaskName = "Tungsten";
    public static readonly bool IsElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static int Schtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            if (p.ExitCode != 0 && !args.StartsWith("/query")) Program.Log($"schtasks {args}: {p.ExitCode} {err.Trim()}");
            return p.ExitCode;
        }
        catch (Exception e) { Program.Log("schtasks: " + e.Message); return -1; }
    }

    public static bool TaskInstalled() => Schtasks($"/query /tn \"{TaskName}\"") == 0;
    public static bool RunTask() => Schtasks($"/run /tn \"{TaskName}\"") == 0;

    /// <summary>
    /// Admin mode runs Tungsten from Program Files: only administrators can change files there, so
    /// nothing running as the normal user can swap the exe the elevated task starts.
    /// </summary>
    public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tungsten");
    public static string InstalledExe => Path.Combine(InstallDir, "Tungsten.exe");

    /// <summary>Must run elevated: copies this build to Program Files (unless running from there).</summary>
    public static bool CopyToProgramFiles()
    {
        try
        {
            string src = Path.GetDirectoryName(Environment.ProcessPath!)!;
            if (string.Equals(Path.GetFullPath(src).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(InstallDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(InstallDir, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, true);
            }
            return true;
        }
        catch (Exception e) { Program.Log("Copy to Program Files failed: " + e.Message); return false; }
    }

    /// <summary>Must run elevated.</summary>
    public static bool Install()
    {
        string user = WindowsIdentity.GetCurrent().Name;
        string exe = InstalledExe;
        string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Starts Tungsten with administrator rights so it can show over games that run as administrator.</Description></RegistrationInfo>
  <Triggers />
  <Principals>
    <Principal id=""Author""><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{Esc(exe)}</Command><Arguments>--background --replace</Arguments><WorkingDirectory>{Esc(Path.GetDirectoryName(exe)!)}</WorkingDirectory></Exec>
  </Actions>
</Task>";
        string tmp = Path.Combine(Path.GetTempPath(), "Tungsten-task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try { return Schtasks($"/create /tn \"{TaskName}\" /xml \"{tmp}\" /f") == 0; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    public static bool Uninstall() => Schtasks($"/delete /tn \"{TaskName}\" /f") == 0;

    /// <summary>Starts `Tungsten.exe arg` elevated (shows the UAC prompt). False if the user said no.</summary>
    public static bool RunElevated(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Exception e) { Program.Log("Elevate: " + e.Message); return false; }
    }

    /// <summary>True when the process runs at a higher integrity level than we can control.</summary>
    public static bool IsProcessElevated(uint pid)
    {
        IntPtr p = OpenProcess(0x1000, false, pid);
        if (p == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(p, 0x8, out var tok)) return !IsElevated; // access denied => higher than us
            try
            {
                return GetTokenInformation(tok, 20 /*TokenElevation*/, out int elevated, 4, out _) && elevated != 0;
            }
            finally { CloseHandle(tok); }
        }
        finally { CloseHandle(p); }
    }

    // ───────────── launching un-elevated (through the desktop's Explorer) ─────────────

    /// <summary>ShellExecute as the normal user even when we are elevated.</summary>
    public static void Shell(string file, string? args = null, string? dir = null)
    {
        if (IsElevated)
        {
            try { ShellViaExplorer(file, args ?? "", dir ?? ""); return; }
            catch (Exception e) { Program.Log("Un-elevated launch failed, launching directly: " + e.Message); }
        }
        var psi = new ProcessStartInfo(file) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
        Process.Start(psi);
    }

    static void ShellViaExplorer(string file, string args, string dir)
    {
        var sw = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
        object loc = 0 /*CSIDL_DESKTOP*/, root = Missing.Value;
        object disp = sw.FindWindowSW(ref loc, ref root, 8 /*SWC_DESKTOP*/, out _, 1 /*SWFO_NEEDDISPATCH*/);
        var sp = (IServiceProvider)disp;
        Guid sid = new("4C96BE40-915C-11CF-99D3-00AA004AE837"), iidBrowser = typeof(IShellBrowser).GUID;
        var browser = (IShellBrowser)sp.QueryService(ref sid, ref iidBrowser);
        var view = browser.QueryActiveShellView();
        Guid iidDispatch = new("00020400-0000-0000-C000-000000000046");
        object folderView = view.GetItemObject(0 /*SVGIO_BACKGROUND*/, ref iidDispatch);
        object app = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null)!;
        app.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, app, new object[] { file, args, dir, "", 1 });
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        void _Count(); void _Item(); void _NewEnum(); void _Register(); void _RegisterPending(); void _Revoke(); void _OnNavigate(); void _OnActivated();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object loc, [In, MarshalAs(UnmanagedType.Struct)] ref object root, int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid sid, ref Guid iid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void _GetWindow(); void _ContextSensitiveHelp(); void _InsertMenusSB(); void _SetMenuSB(); void _RemoveMenusSB(); void _SetStatusTextSB();
        void _EnableModelessSB(); void _TranslateAcceleratorSB(); void _BrowseObject(); void _GetViewStateStream(); void _GetControlWindow(); void _SendControlMsg();
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void _GetWindow(); void _ContextSensitiveHelp(); void _TranslateAccelerator(); void _EnableModeless(); void _UIActivate(); void _Refresh();
        void _CreateViewWindow(); void _DestroyViewWindow(); void _GetCurrentInfo(); void _AddPropertySheetPages(); void _SaveViewState(); void _SelectItem();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetItemObject(uint item, ref Guid iid);
    }

    // ───────────── IPC objects any Tungsten (elevated or not) can open ─────────────

    // Authenticated users + SYSTEM may use the object; medium integrity label so a normal
    // process can signal an elevated Tungsten (and the other way round).
    const string Sddl = "D:(A;;GA;;;AU)(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NW;;;ME)";

    static IntPtr Attributes(out IntPtr sd)
    {
        sd = IntPtr.Zero;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out sd, out _)) return IntPtr.Zero;
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
        Marshal.StructureToPtr(sa, p, false);
        return p;
    }

    public static EventWaitHandle CreateEvent(string name)
    {
        IntPtr sa = Attributes(out var sd);
        try
        {
            IntPtr h = CreateEventW(sa, false, false, name);
            if (h == IntPtr.Zero) return new EventWaitHandle(false, EventResetMode.AutoReset, name);
            return new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = new SafeWaitHandle(h, true) };
        }
        finally { if (sa != IntPtr.Zero) Marshal.FreeHGlobal(sa); if (sd != IntPtr.Zero) LocalFree(sd); }
    }

    /// <summary>Creates the single-instance mutex; <paramref name="exists"/> is true when another Tungsten holds it.</summary>
    public static SafeWaitHandle CreateMutex(string name, out bool exists)
    {
        IntPtr sa = Attributes(out var sd);
        try
        {
            IntPtr h = CreateMutexW(sa, false, name);
            int err = Marshal.GetLastWin32Error();
            exists = h == IntPtr.Zero || err == 183 /*ERROR_ALREADY_EXISTS*/;
            return new SafeWaitHandle(h, true);
        }
        finally { if (sa != IntPtr.Zero) Marshal.FreeHGlobal(sa); if (sd != IntPtr.Zero) LocalFree(sd); }
    }

    public static bool MutexExists(string name)
    {
        IntPtr h = OpenMutexW(0x00100000 /*SYNCHRONIZE*/, false, name);
        if (h != IntPtr.Zero) { CloseHandle(h); return true; }
        return Marshal.GetLastWin32Error() == 5; // access denied => it exists
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint rev, out IntPtr sd, out uint size);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateEventW(IntPtr sa, bool manual, bool initial, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateMutexW(IntPtr sa, bool owner, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenMutexW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr tok, int cls, out int info, int len, out int ret);
}
