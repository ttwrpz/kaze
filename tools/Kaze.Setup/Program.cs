using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Kaze.Setup;

/// <summary>
/// Per-user installer for Kaze.
///   Kaze-Setup.exe             asks first, installs, starts the app
///   Kaze-Setup.exe /silent     no questions, does not start the app
///   Kaze-Setup.exe /silent /relaunch   used by the in-app updater
/// Exit codes: 0 installed, 1 cancelled, 2 failed.
/// </summary>
internal static partial class Program
{
    private const uint MB_OK = 0x0, MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20;
    private const int IDYES = 6;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint hwnd, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        bool Has(string flag) => args.Any(a => a.Equals("/" + flag, StringComparison.OrdinalIgnoreCase)
                                            || a.Equals("--" + flag, StringComparison.OrdinalIgnoreCase));
        var silent = Has("silent") || Has("s");
        var relaunch = Has("relaunch");
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        var title = $"Kaze {version} setup";

        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Kaze");
        var exe = Path.Combine(target, "Kaze.exe");

        if (!silent)
        {
            var verb = File.Exists(exe) ? "update" : "install";
            var answer = MessageBox(0,
                $"This will {verb} Kaze {version} for your Windows account in\n{target}\n\n" +
                "Kaze is an unofficial app and is not affiliated with Hitachi. It is provided as is, without warranty. " +
                "Use it at your own risk. Continue?",
                title, MB_YESNO | MB_ICONQUESTION);
            if (answer != IDYES) return 1;
        }

        try
        {
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                ?? throw new InvalidOperationException("This setup file has no app inside. Build it with build/publish.ps1.");

            StopRunningApp(exe);

            // Extract next to the target first so a failed extraction leaves the old version working.
            var staging = target + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
                zip.ExtractToDirectory(staging, overwriteFiles: true);
            if (!File.Exists(Path.Combine(staging, "Kaze.exe")))
                throw new InvalidDataException("The package does not contain Kaze.exe.");

            ReplaceDirectory(staging, target);

            Run(exe, "--register-install", wait: true);

            if (relaunch) Run(exe, "", wait: false);
            else if (!silent) Run(exe, "--show", wait: false);
            return 0;
        }
        catch (Exception ex)
        {
            if (!silent) MessageBox(0, "Kaze could not be installed.\n\n" + ex.Message, title, MB_OK | MB_ICONERROR);
            else Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static void StopRunningApp(string exe)
    {
        var running = Process.GetProcessesByName("Kaze");
        if (running.Length == 0) return;
        // Ask nicely first (saves settings and timers), then make sure.
        if (File.Exists(exe)) Run(exe, "--exit", wait: true, timeoutMs: 8000);
        foreach (var p in running)
        {
            try
            {
                if (!p.WaitForExit(10000)) { p.Kill(); p.WaitForExit(5000); }
            }
            catch { }
        }
    }

    /// <summary>
    /// Swaps the new files in. The previous install is only deleted once the new one is in place,
    /// and is put back if the swap keeps failing, so a failed update never leaves nothing installed.
    /// </summary>
    private static void ReplaceDirectory(string staging, string target)
    {
        var old = target + ".old";
        // A leftover from an earlier run is only stale while a working install exists.
        if (Directory.Exists(old) && Directory.Exists(target)) Directory.Delete(old, true);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(target)) Directory.Move(target, old);
                Directory.Move(staging, target);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!Directory.Exists(target) && Directory.Exists(old))
                {
                    try { Directory.Move(old, target); } catch { }
                }
                if (attempt < 6) { Thread.Sleep(500); continue; }
                // The folder itself can't be renamed while, for example, an Explorer window shows it.
                // Its files usually can still be replaced, so swap the contents instead.
                if (!Directory.Exists(target)) throw;
                ReplaceContents(staging, target, old);
                break;
            }
        }
        try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
    }

    /// <summary>Replaces everything inside <paramref name="target"/>, restoring it from a copy on failure.</summary>
    private static void ReplaceContents(string staging, string target, string backup)
    {
        CopyDirectory(target, backup);
        try
        {
            foreach (var dir in Directory.GetDirectories(target)) Directory.Delete(dir, true);
            foreach (var file in Directory.GetFiles(target)) File.Delete(file);
            CopyDirectory(staging, target);
        }
        catch
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(target)) Directory.Delete(dir, true);
                foreach (var file in Directory.GetFiles(target)) File.Delete(file);
                CopyDirectory(backup, target);
            }
            catch { }
            throw;
        }
        try { Directory.Delete(staging, true); } catch { }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    private static void Run(string exe, string args, bool wait, int timeoutMs = 60000)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
        if (wait) p?.WaitForExit(timeoutMs);
    }
}
