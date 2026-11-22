using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Kaze.App;

public static class Program
{
    private const string InstanceKey = "Kaze-7d1b3f0e-main";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--register-install", StringComparer.OrdinalIgnoreCase))
        {
            Kaze.App.Services.InstallSupport.Register();
            return 0;
        }
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            return Kaze.App.Services.InstallSupport.Uninstall(args.Contains("--silent", StringComparer.OrdinalIgnoreCase));

        // Restart (language change): wait for the previous instance to finish first.
        var wait = Array.FindIndex(args, a => a.Equals("--after-exit", StringComparison.OrdinalIgnoreCase));
        if (wait >= 0 && wait + 1 < args.Length && int.TryParse(args[wait + 1], out var pid))
        {
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000); } catch (ArgumentException) { }
            args = [.. args.Where((_, i) => i != wait && i != wait + 1)];
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToRunningInstance())
            return 0;

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(args);
        });
        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            main.Activated += (_, e) => App.Current?.OnRedirectedActivation(e);
            return false;
        }

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        Task.Run(async () => await main.RedirectActivationToAsync(activation)).Wait(TimeSpan.FromSeconds(5));
        return true;
    }
}
