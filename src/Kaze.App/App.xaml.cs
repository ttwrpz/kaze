using System.Net;
using Kaze.App.Helpers;
using Kaze.App.Services;
using Kaze.App.Tray;
using Kaze.App.ViewModels;
using Kaze.App.Views;
using Kaze.Core.Backends;
using Kaze.Core.Cloud;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Kaze.Core.Weather;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Serilog;
using ILaunchActivatedEventArgs = Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs;

namespace Kaze.App;

public partial class App : Application
{
    private readonly string[] _args;
    private ServiceProvider _services = null!;
    private DispatcherQueue _ui = null!;
    private Microsoft.Extensions.Logging.ILogger _log = null!;
    private MainWindow _main = null!;
    private FlyoutWindow _flyout = null!;
    private CommandLineRequest? _pendingRequest;

    public App(string[] args)
    {
        _args = args;
        Current = this;
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Unhandled UI exception");
            e.Handled = true;
        };
    }

    public static new App? Current { get; private set; }
    public static T GetService<T>() where T : notnull => Current!._services.GetRequiredService<T>();

    public bool IsExiting { get; private set; }
    public SettingsStore Settings => GetService<SettingsStore>();
    public MainWindow MainWindow => _main;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _services = ConfigureServices();
        _log = GetService<ILoggerFactory>().CreateLogger("App");
        // Arguments can name an account (an email address), so only the switches are logged.
        _log.LogInformation("Kaze starting ({Args})", string.Join(' ', _args.Where(a => a.StartsWith("--", StringComparison.Ordinal))));

        var settings = GetService<SettingsStore>();
        settings.Load();
        L.Init(settings.Current.Language);

        var controller = GetService<AcController>();
        controller.SelectedKey = settings.Current.SelectedUnitKey;
        controller.UnitsListChanged += (_, _) => _ui.TryEnqueue(ApplyPendingRequest);
        controller.StatusChanged += (status, detail) => _ui.TryEnqueue(() => OnBackendStatus(status, detail));

        var toasts = GetService<NotificationService>();
        toasts.Register();
        toasts.Clicked += () => _ui.TryEnqueue(() => ShowMainWindow());
        toasts.ActionInvoked += (action, unit, value) => _ui.TryEnqueue(() => OnToastAction(action, unit, value));

        _main = new MainWindow();
        _flyout = new FlyoutWindow();
        _flyout.OpenMainWindowRequested += () => ShowMainWindow();
        ApplyTheme();

        var hotkeys = GetService<HotkeyService>();
        hotkeys.Attach(_main);
        hotkeys.Apply(settings.Current.Hotkeys);
        hotkeys.Pressed += a => _ui.TryEnqueue(() => OnHotkey(a));

        var tray = GetService<TrayIconService>();
        tray.OpenFlyoutRequested += () => _ui.TryEnqueue(_flyout.Toggle);
        tray.OpenMainWindowRequested += () => _ui.TryEnqueue(() => ShowMainWindow());
        tray.ExitRequested += () => _ui.TryEnqueue(() => _ = ExitAsync());
        tray.Create();

        GetService<ScheduleService>().Start();
        GetService<AutomationService>().Start();
        GetService<HistoryService>().Start();
        GetService<WeatherService>().Start();
        GetService<ReminderService>().Start();
        GetService<UpdateService>().Start();
        var api = GetService<LocalApiServer>();
        api.Ui = _ui;
        api.Apply();
        var jumpList = GetService<JumpListService>();
        jumpList.Update();
        settings.Saved += () => _ui.TryEnqueue(jumpList.Update);

        if (settings.Current.StartWithWindows != StartupService.IsEnabled)
            StartupService.Set(settings.Current.StartWithWindows); // keep path current if the exe moved

        var request = CommandLineRequest.Parse(_args);
        if (GetService<CredentialStore>().Get() is null)
        {
            _main.ShowLogin();
            _main.ShowAndActivate();
        }
        else
        {
            _ = controller.StartAsync();
            _main.ShowApp();
            if (!request.Minimized && !request.HasUnitAction && !request.Flyout && !request.TrayMenu)
                _main.ShowAndActivate();
        }
        HandleRequest(request);
    }

    private ServiceProvider ConfigureServices()
    {
        var logDir = Path.Combine(SettingsStore.DataDirectory, "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logDir, "kaze-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var s = new ServiceCollection();
        s.AddLogging(b => b.AddSerilog(dispose: true));
        s.AddSingleton<SettingsStore>();
        s.AddSingleton<CredentialStore>();
        s.AddSingleton(new AirCloudOptions());
        s.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        })
        { Timeout = TimeSpan.FromSeconds(20) });
        s.AddSingleton(sp => new AirCloudAuth(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<AirCloudOptions>(),
            () => sp.GetRequiredService<CredentialStore>().Get(), TimeProvider.System,
            sp.GetRequiredService<ILogger<AirCloudAuth>>()));
        s.AddSingleton<AirCloudRestClient>();
        s.AddSingleton<AirCloudBackend>();
        s.AddSingleton<IAcBackend>(sp => sp.GetRequiredService<AirCloudBackend>());
        s.AddSingleton(sp => new AcController(sp.GetRequiredService<IAcBackend>(), TimeProvider.System, sp.GetRequiredService<ILogger<AcController>>()));
        s.AddSingleton(_ => new Scheduler());
        s.AddSingleton<ScheduleService>();
        s.AddSingleton<NotificationService>();
        s.AddSingleton<AutomationService>();
        s.AddSingleton<HotkeyService>();
        s.AddSingleton<TrayIconService>();
        s.AddSingleton<EnergyService>();
        s.AddSingleton<ControlViewModel>();
        s.AddSingleton<HistoryService>();
        s.AddSingleton<SleepService>();
        s.AddSingleton(sp => new OpenMeteoClient(sp.GetRequiredService<HttpClient>()));
        s.AddSingleton<WeatherService>();
        s.AddSingleton<ReminderService>();
        s.AddSingleton<LocalApiServer>();
        s.AddSingleton<UpdateService>();
        s.AddSingleton<JumpListService>();
        return s.BuildServiceProvider();
    }

    public void ShowMainApp() => _main.ShowApp();

    public void ShowMainWindow(string? page = null)
    {
        _flyout.HideFlyout();
        if (page is not null) _main.Navigate(page);
        _main.ShowAndActivate();
    }

    public void ApplyTheme()
    {
        var theme = Settings.Current.Theme;
        _main.ApplyTheme(theme);
        if (_flyout.Content is FrameworkElement fe)
            fe.RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    }

    public void ApplyHotkeys() => GetService<HotkeyService>().Apply(Settings.Current.Hotkeys);

    private void OnHotkey(HotkeyAction action)
    {
        var c = GetService<AcController>();
        var step = Settings.Current.TemperatureStep;
        switch (action)
        {
            case HotkeyAction.TogglePower when c.SelectedKey is { } k: c.TogglePower(k); break;
            case HotkeyAction.TemperatureUp when c.SelectedKey is { } k: c.AdjustTemperature(k, step); break;
            case HotkeyAction.TemperatureDown when c.SelectedKey is { } k: c.AdjustTemperature(k, -step); break;
            case HotkeyAction.ShowFlyout: _flyout.Toggle(); break;
            case HotkeyAction.ShowMainWindow: ShowMainWindow(); break;
        }
    }

    private BackendStatus _lastStatus;

    private void OnBackendStatus(BackendStatus status, string? detail)
    {
        if (status == BackendStatus.AuthFailed && _lastStatus != BackendStatus.AuthFailed)
        {
            GetService<NotificationService>().Show(L.T("airCloud sign-in failed"), detail ?? L.T("Please sign in again."));
            _main.ShowLogin(new LoginRequest(detail, GetService<CredentialStore>().ActiveLogin));
        }
        _lastStatus = status;
    }

    /// <summary>Validates the credentials against airCloud, stores them and (re)starts the connection. Returns an error message or null.</summary>
    public async Task<string?> SignInAsync(string login, string password)
    {
        var creds = new AirCloudCredentials(login, password);
        try
        {
            var probe = new AirCloudAuth(GetService<HttpClient>(), GetService<AirCloudOptions>(), () => creds);
            await probe.SignInAsync();
        }
        catch (AirCloudException ex)
        {
            _log.LogWarning("Sign-in failed: {Kind} HTTP {Status} errorState={ErrorState} loginType={LoginType}",
                ex.Kind, ex.HttpStatus, ex.ErrorState, creds.IsEmail ? "email" : "phone");
            return ex.Message;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sign-in failed");
            return L.F("Could not reach airCloud: {0}", ex.Message);
        }

        var store = GetService<CredentialStore>();
        var previous = store.ActiveLogin;
        store.Save(creds, activate: true);
        await ReconnectAsAsync(previous, creds.Login);
        _main.ShowApp();
        return null;
    }

    public IReadOnlyList<string> Accounts => GetService<CredentialStore>().Accounts;

    public string? ActiveAccount => GetService<CredentialStore>().ActiveLogin;

    /// <summary>Raised on the UI thread after the active account or the account list changes.</summary>
    public event Action? AccountsChanged;

    public async Task SwitchAccountAsync(string login)
    {
        var store = GetService<CredentialStore>();
        var previous = store.ActiveLogin;
        if (CredentialStore.SameLogin(previous, login)) return;
        if (store.Read(login) is null)
        {
            ShowAddAccount(login, L.F("The saved password for {0} is missing. Sign in again.", login));
            return;
        }
        _log.LogInformation("Switching account");
        store.SetActive(login);
        await ReconnectAsAsync(previous, login);
        _main.ShowApp();
        GetService<NotificationService>().Show(L.T("Switched account"), L.F("Kaze now uses {0}.", login));
    }

    /// <summary>Forgets a saved account. If it was active, switches to the next one or shows sign in.</summary>
    public async Task RemoveAccountAsync(string login)
    {
        var store = GetService<CredentialStore>();
        var wasActive = CredentialStore.SameLogin(store.ActiveLogin, login);
        store.Remove(login);
        if (!wasActive)
        {
            AccountsChanged?.Invoke();
            return;
        }
        if (store.Accounts.FirstOrDefault() is { } next)
        {
            store.SetActive(next);
            await ReconnectAsAsync(null, next);
            _main.ShowApp();
        }
        else
        {
            var c = GetService<AcController>();
            await c.StopAsync();
            GetService<AirCloudAuth>().Reset();
            c.ClearUnits();
            AccountsChanged?.Invoke();
            _main.ShowLogin();
        }
    }

    /// <summary>Signs out of the active account (it is removed from this PC).</summary>
    public Task SignOutAsync() => ActiveAccount is { } a ? RemoveAccountAsync(a) : Task.CompletedTask;

    public void ShowAddAccount(string? login = null, string? message = null)
    {
        _main.ShowLogin(new LoginRequest(message, login, Adding: ActiveAccount is not null));
        ShowMainWindow();
    }

    private async Task ReconnectAsAsync(string? previous, string next)
    {
        var c = GetService<AcController>();
        try { await c.FlushAllAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        if (previous is not null && c.SelectedKey is { } key)
            Settings.Update(s => s.AccountUnits[previous.Trim().ToLowerInvariant()] = key);
        await c.StopAsync();
        GetService<AirCloudAuth>().Reset();
        c.ClearUnits();
        if (Settings.Current.AccountUnits.TryGetValue(next.Trim().ToLowerInvariant(), out var unit))
            c.SelectedKey = unit;
        AccountsChanged?.Invoke();
        await c.StartAsync();
    }

    private void OnToastAction(string action, string? unit, string? value)
    {
        var c = GetService<AcController>();
        IEnumerable<string> Keys() => unit is null or "*" ? c.Units.Select(u => u.Key) : unit.Split(',', StringSplitOptions.RemoveEmptyEntries);
        switch (action)
        {
            case ToastActions.PowerOn:
                foreach (var k in Keys()) c.Send(k, AcCommand.Power(true));
                break;
            case ToastActions.PowerOff:
                foreach (var k in Keys()) c.Send(k, AcCommand.Power(false));
                break;
            case ToastActions.FrostWash when unit is not null:
                _ = c.StartFrostWashAsync(unit);
                break;
            case ToastActions.SnoozeFrostWash when unit is not null:
                GetService<ReminderService>().SnoozeFrostWash(unit, TimeSpan.FromDays(7));
                break;
            case ToastActions.SnoozeTimer when Guid.TryParse(value, out var id):
                GetService<ScheduleService>().SnoozeTimer(id, TimeSpan.FromMinutes(30));
                break;
            case ToastActions.CancelTimer when Guid.TryParse(value, out var id):
                GetService<ScheduleService>().CancelTimer(id);
                break;
            case ToastActions.CancelSleep when unit is not null:
                GetService<SleepService>().Cancel(unit);
                break;
            case ToastActions.InstallUpdate:
                _ = InstallUpdateAsync();
                break;
            case ToastActions.SkipUpdate:
                GetService<UpdateService>().SkipAvailable();
                break;
            case ToastActions.OpenEnergy:
                ShowMainWindow("Energy");
                break;
        }
    }

    public async Task InstallUpdateAsync()
    {
        if (await GetService<UpdateService>().InstallAsync())
            await ExitAsync();
    }

    /// <summary>Starts a new instance once this one has exited (used after changing the language).</summary>
    public async Task RestartAsync()
    {
        if (Environment.ProcessPath is { } exe)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--after-exit {Environment.ProcessId} --show") { UseShellExecute = false });
        await ExitAsync();
    }

    public async Task RestartConnectionAsync()
    {
        var c = GetService<AcController>();
        await c.StopAsync();
        GetService<AirCloudAuth>().Reset();
        await c.StartAsync();
    }

    public async Task ExitAsync()
    {
        if (IsExiting) return;
        IsExiting = true;
        _log.LogInformation("Exiting");
        try
        {
            var c = GetService<AcController>();
            await c.FlushAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await c.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Shutdown");
        }
        GetService<TrayIconService>().Dispose();
        GetService<HotkeyService>().Dispose();
        GetService<AutomationService>().Dispose();
        GetService<LocalApiServer>().Dispose();
        GetService<HistoryService>().Dispose();
        GetService<WeatherService>().Dispose();
        GetService<ReminderService>().Dispose();
        GetService<UpdateService>().Dispose();
        GetService<ScheduleService>().Dispose();
        GetService<NotificationService>().Dispose();
        _flyout.Close();
        _main.Close();
        await Log.CloseAndFlushAsync();
        Exit();
    }

    /// <summary>Called (on a background thread) when a second launch is redirected to this instance.</summary>
    public void OnRedirectedActivation(AppActivationArguments e)
    {
        string? commandLine = e.Kind == ExtendedActivationKind.Launch && e.Data is ILaunchActivatedEventArgs l ? l.Arguments : null;
        _ui.TryEnqueue(() =>
        {
            var tokens = CommandLineRequest.SplitCommandLine(commandLine).ToList();
            if (tokens.Count > 0 && tokens[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                tokens.RemoveAt(0);
            var request = CommandLineRequest.Parse(tokens);
            if (tokens.Count == 0)
                ShowMainWindow();
            else
                HandleRequest(request);
        });
    }

    private void HandleRequest(CommandLineRequest request)
    {
        if (request.Exit)
        {
            _ = ExitAsync();
            return;
        }
        foreach (var err in request.Errors) _log.LogWarning("Command line: {Error}", err);
        if (request.Errors.Count > 0)
            GetService<NotificationService>().Show(L.T("Kaze command line"), string.Join("\n", request.Errors));

        if (request.Account is { } account)
        {
            var match = Accounts.FirstOrDefault(a => CredentialStore.SameLogin(a, account));
            if (match is null)
                GetService<NotificationService>().Show(L.T("Kaze command line"), L.F("No saved account '{0}'.", account));
            else if (!CredentialStore.SameLogin(match, ActiveAccount))
                _ = SwitchAccountAsync(match);
        }

        if (request.HasUnitAction)
        {
            _pendingRequest = request;
            ApplyPendingRequest();
        }
        if (request.Flyout) _flyout.ShowFlyout();
        if (request.TrayMenu) GetService<TrayIconService>().ShowMenu();
        if (request.Show) ShowMainWindow();
    }

    private void ApplyPendingRequest()
    {
        if (_pendingRequest is not { } r) return;
        var c = GetService<AcController>();
        var units = c.Units;
        if (units.Count == 0) return; // wait for the first state push
        _pendingRequest = null;

        IEnumerable<AcState> targets = r.AllUnits ? units
            : r.Unit is { } name ? units.Where(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || u.Id == name || u.Key == name)
            : c.Selected is { } sel ? [sel] : [];

        var list = targets.ToList();
        if (list.Count == 0)
        {
            GetService<NotificationService>().Show(L.T("Kaze command line"), L.F("No unit named '{0}'.", r.Unit));
            return;
        }

        Preset? preset = null;
        if (r.Preset is { } pn)
        {
            preset = Settings.Current.Presets.FirstOrDefault(p => p.Name.Equals(pn, StringComparison.OrdinalIgnoreCase));
            if (preset is null) GetService<NotificationService>().Show(L.T("Kaze command line"), L.F("No preset named '{0}'.", pn));
        }

        foreach (var u in list)
        {
            if (preset is not null) c.Send(u.Key, preset.Command);
            if (r.TogglePower) c.TogglePower(u.Key);
            if (r.TemperatureDelta is { } d) c.AdjustTemperature(u.Key, d);
            if (!r.Command.IsEmpty) c.Send(u.Key, r.Command);
            if (r.Sleep) GetService<SleepService>().Start(u.Key);
        }
        _log.LogInformation("Command line applied to {Count} unit(s)", list.Count);
    }
}
