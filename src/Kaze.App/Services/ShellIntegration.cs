using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Kaze.App.Helpers;
using Kaze.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Kaze.App.Services;

/// <summary>Minimal shell COM interop: shell links, the taskbar jump list.</summary>
internal static class ShellCom
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink;

    [ComImport, Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6")]
    internal class DestinationList;

    [ComImport, Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a")]
    internal class EnumerableObjectCollection;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9")]
    internal interface IObjectArray
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5632b1a4-e38a-400a-928a-d4cd63230295")]
    internal interface IObjectCollection
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, ref Guid riid, out IntPtr ppv);
        void AddObject(IShellLinkW punk);
        void AddFromArray(IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6332debf-87b5-4670-90c0-5e57b408a49e")]
    internal interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void BeginList(out uint pcMinSlots, ref Guid riid, out IntPtr ppv);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray poa);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, out IntPtr ppv);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? pszAppID);
        void AbortList();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    internal interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid FormatId = fmtid;
        public uint PropertyId = pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;

        public static PropVariant FromString(string s) => new() { VarType = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(s) };
    }

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant pvar);

    internal static readonly PropertyKey TitleKey = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);

    internal static IShellLinkW CreateLink(string exe, string arguments, string title, string? description = null, string? icon = null)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exe);
        link.SetArguments(arguments);
        link.SetIconLocation(icon is not null && File.Exists(icon) ? icon : exe, 0);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
        if (description is not null) link.SetDescription(description);
        var store = (IPropertyStore)link;
        var key = TitleKey;
        var pv = PropVariant.FromString(title);
        try
        {
            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        finally
        {
            PropVariantClear(ref pv);
        }
        return link;
    }
}

/// <summary>Right-click menu of the taskbar button: power, presets, sleep curve, quick panel.</summary>
public sealed class JumpListService(SettingsStore settings, ILogger<JumpListService> log)
{
    private string _last = "";

    public void Update()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var tasks = new List<(string Args, string Title, string Icon)>
        {
            ("--power on", L.T("Turn on"), Icon("power-on")),
            ("--power off", L.T("Turn off"), Icon("power-off")),
            ("--sleep", L.T("Start sleep curve"), Icon("sleep")),
            ("--flyout", L.T("Quick panel"), Icon("panel")),
        };
        var presets = settings.Current.Presets.Take(8)
            .Select(p => ($"--preset \"{p.Name.Replace("\"", "")}\"", p.Name, Icon(PresetIcon(p.Command)))).ToList();

        var signature = string.Join("|", tasks.Concat(presets).Select(t => t.Item1 + t.Item2 + t.Item3)) + exe;
        if (signature == _last) return;
        try
        {
            var list = (ShellCom.ICustomDestinationList)new ShellCom.DestinationList();
            var iid = typeof(ShellCom.IObjectArray).GUID;
            list.BeginList(out _, ref iid, out var removed);
            if (removed != IntPtr.Zero) Marshal.Release(removed);

            var presetsInTasks = false;
            if (presets.Count > 0)
            {
                var presetItems = (ShellCom.IObjectCollection)new ShellCom.EnumerableObjectCollection();
                foreach (var (args, title, icon) in presets)
                    presetItems.AddObject(ShellCom.CreateLink(exe, args, title, L.F("Run the preset {0}", title), icon));
                try
                {
                    list.AppendCategory(L.T("Presets"), (ShellCom.IObjectArray)presetItems);
                }
                catch (UnauthorizedAccessException)
                {
                    // Custom categories are refused when "Show recently opened items" is off
                    // in Windows privacy settings. Tasks still work, so list the presets there.
                    presetsInTasks = true;
                }
            }

            var taskItems = (ShellCom.IObjectCollection)new ShellCom.EnumerableObjectCollection();
            foreach (var (args, title, icon) in tasks)
                taskItems.AddObject(ShellCom.CreateLink(exe, args, title, null, icon));
            if (presetsInTasks)
                foreach (var (args, title, icon) in presets)
                    taskItems.AddObject(ShellCom.CreateLink(exe, args, title, L.F("Run the preset {0}", title), icon));
            list.AddUserTasks((ShellCom.IObjectArray)taskItems);
            list.CommitList();
            _last = signature;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Jump list update failed");
        }
    }

    /// <summary>Colour icons for the actions, made by build/icon/make_action_icons.py.</summary>
    private static string Icon(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", "Actions", name + ".ico");

    private static string PresetIcon(AcCommand c) => c.PowerOn == false ? "power-off" : c.Mode?.ToUpperInvariant() switch
    {
        AcModes.Cooling => "cool",
        AcModes.Heating => "heat",
        AcModes.Dry => "dry",
        AcModes.Fan => "fan",
        AcModes.Auto => "auto",
        _ => "preset",
    };
}

/// <summary>
/// Per-user install support used by the setup program (<c>--register-install</c>) and by
/// "Apps > Installed apps > Uninstall" (<c>--uninstall</c>). Runs before any UI is created.
/// </summary>
public static class InstallSupport
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Kaze";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string StartMenuLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Kaze.lnk");

    public static void Register()
    {
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var link = ShellCom.CreateLink(exe, "", "Kaze", "Control air conditioners that use airCloud Go (unofficial)");
        ((IPersistFile)link).Save(StartMenuLink, true);

        long sizeKb = 0;
        try { sizeKb = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024; } catch { }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Kaze for airCloud Go");
        key.SetValue("DisplayVersion", UpdateService.CurrentVersion.ToString(3));
        key.SetValue("Publisher", "ttwrpz");
        key.SetValue("DisplayIcon", exe);
        key.SetValue("InstallLocation", dir);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --silent");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
    }

    /// <summary>Removes shortcuts, registry entries and the program folder. Settings and saved accounts are kept unless asked.</summary>
    public static int Uninstall(bool silent)
    {
        if (!silent && Native.MessageBox(IntPtr.Zero,
                "Remove Kaze from this PC?\n\nYour settings and history stay in %LOCALAPPDATA%\\Kaze. Saved passwords stay in Credential Manager.",
                "Uninstall Kaze", 0x24 /* MB_YESNO | MB_ICONQUESTION */) != 6 /* IDYES */)
            return 1;

        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        StopOtherInstances(exe);

        try { File.Delete(StartMenuLink); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue("Kaze", false);
        }
        catch { }

        // The folder can only go once this process has exited. Only the install folder the setup
        // program creates is removed, never a portable copy somewhere else.
        var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Kaze");
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir)), StringComparison.OrdinalIgnoreCase))
        {
            var system = Environment.SystemDirectory;
            Process.Start(new ProcessStartInfo(Path.Combine(system, "cmd.exe"),
                $"/d /c \"{Path.Combine(system, "ping.exe")}\" -n 3 127.0.0.1 >nul & rmdir /s /q \"{installDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = system,
            });
        }
        return 0;
    }

    public static void StopOtherInstances(string exe)
    {
        var me = Environment.ProcessId;
        var others = Process.GetProcessesByName("Kaze").Where(p => p.Id != me).ToList();
        if (others.Count == 0) return;
        try { Process.Start(new ProcessStartInfo(exe, "--exit") { UseShellExecute = false })?.WaitForExit(5000); } catch { }
        foreach (var p in others)
        {
            try
            {
                if (!p.WaitForExit(8000)) p.Kill();
            }
            catch { }
        }
    }
}
