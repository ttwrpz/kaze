using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Kaze.App.Helpers;
using Kaze.App.Services;
using Kaze.Core.Backends;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinUIEx;

namespace Kaze.App.Tray;

/// <summary>
/// Owns the notification area icon: the live icon and tooltip, and the right click menu.
/// The menu is a real WinUI MenuFlyout (Fluent, acrylic, rounded corners), like the menus of
/// built in Windows 11 apps. It opens from a 1 px helper window placed under the pointer and
/// renders as a windowed popup, so it can extend past that window.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly AcController _controller;
    private readonly SettingsStore _settings;
    private readonly ScheduleService _schedules;
    private readonly DispatcherQueue _ui;
    private readonly MenuFlyout _menu = new() { AreOpenCloseAnimationsEnabled = true };
    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _currentIcon;
    private string _iconSignature = "";
    private bool _updateQueued;
    private bool _menuOpen;
    private bool _menuStale = true;
    private WindowEx? _menuHost;
    private Grid? _menuAnchor;

    public TrayIconService(AcController controller, SettingsStore settings, ScheduleService schedules)
    {
        _controller = controller;
        _settings = settings;
        _schedules = schedules;
        _ui = DispatcherQueue.GetForCurrentThread();
    }

    public event Action? OpenFlyoutRequested;
    public event Action? OpenMainWindowRequested;
    public event Action? ExitRequested;

    public void Create()
    {
        _menu.Opening += (_, _) =>
        {
            _menuOpen = true;
            if (_menuStale) BuildMenu();
        };
        // Native menus announce themselves with these accessibility events. Explorer keeps the
        // hidden icons panel open while a tray app's menu is up, and WinUI menus don't send them.
        _menu.Opened += (_, _) => AnnounceMenu(open: true);
        _menu.Closed += (_, _) => AnnounceMenu(open: false);
        _menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (_menuStale) BuildMenu();
        };

        _icon = new TaskbarIcon
        {
            ToolTipText = L.T("Kaze: connecting…"),
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(() => OpenFlyoutRequested?.Invoke()),
            DoubleClickCommand = new RelayCommand(() => OpenMainWindowRequested?.Invoke()),
            RightClickCommand = new RelayCommand(ShowMenu),
        };
        _icon.ForceCreate(false); // false: keep the process out of Efficiency mode

        _controller.UnitChanged += (_, _) => QueueUpdate();
        _controller.UnitsListChanged += (_, _) => QueueUpdate();
        _controller.StatusChanged += (_, _) => QueueUpdate();
        _schedules.Scheduler.Changed += QueueUpdate;
        _settings.Saved += QueueUpdate;
        // The icon follows the taskbar theme and the tray size for the DPI, so redraw when either changes.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemChanged;
        Update();
    }

    /// <summary>Opens the tray menu at the cursor (used by the --tray-menu switch for testing).</summary>
    public void ShowMenu()
    {
        Native.GetCursorPos(out var p);
        var host = EnsureMenuHost();
        var hwnd = host.GetWindowHandle();
        // A plain window can't be smaller than about 136x39 and gets a DWM outline. As a borderless
        // 1 px popup with a transparent backdrop it is invisible. Only the menu shows.
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, p.X, p.Y, 1, 1, Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);
        host.Activate();
        Native.SetForegroundWindow(hwnd);

        _menuAnchor!.RequestedTheme = _settings.Current.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        // Wait one layout pass so the anchor has a XamlRoot.
        _ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_menuAnchor?.XamlRoot is null) return;
            _menu.ShowAt(_menuAnchor, new FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(0, 0),
                ShowMode = FlyoutShowMode.Standard,
            });
        });
    }

    private void AnnounceMenu(bool open)
    {
        if (_menuHost is null) return;
        var hwnd = _menuHost.GetWindowHandle();
        if (open)
        {
            Native.NotifyWinEvent(Native.EVENT_SYSTEM_MENUSTART, hwnd, Native.OBJID_CLIENT, Native.CHILDID_SELF);
            Native.NotifyWinEvent(Native.EVENT_SYSTEM_MENUPOPUPSTART, hwnd, Native.OBJID_CLIENT, Native.CHILDID_SELF);
        }
        else
        {
            Native.NotifyWinEvent(Native.EVENT_SYSTEM_MENUPOPUPEND, hwnd, Native.OBJID_CLIENT, Native.CHILDID_SELF);
            Native.NotifyWinEvent(Native.EVENT_SYSTEM_MENUEND, hwnd, Native.OBJID_CLIENT, Native.CHILDID_SELF);
        }
    }

    private WindowEx EnsureMenuHost()
    {
        if (_menuHost is not null) return _menuHost;
        _menuAnchor = new Grid();
        _menuHost = new WindowEx
        {
            Title = "Kaze menu",
            Content = _menuAnchor,
            IsTitleBarVisible = false,
            IsShownInSwitchers = false,
            IsAlwaysOnTop = true,
            IsResizable = false,
            IsMinimizable = false,
            IsMaximizable = false,
            SystemBackdrop = new TransparentTintBackdrop(),
        };
        if (_menuHost.AppWindow.Presenter is OverlappedPresenter p)
            p.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        var hwnd = _menuHost.GetWindowHandle();
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, unchecked((nint)Native.WS_POPUP));
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (nint)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST));
        var none = Native.DWMWA_COLOR_NONE;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref none, sizeof(uint));
        _menuHost.AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            _menuHost.AppWindow.Hide();
        };
        _menu.ShouldConstrainToRootBounds = false;
        _menu.Closed += (_, _) => _menuHost.AppWindow.Hide();
        return _menuHost;
    }

    private void OnSystemChanged(object? sender, EventArgs e) => QueueUpdate();

    private void QueueUpdate()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        _ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _updateQueued = false;
            Update();
        });
    }

    private void Update()
    {
        if (_icon is null) return;
        var u = _controller.Selected;
        var status = _controller.Status;
        var connected = status == BackendStatus.Connected;

        var kind = u is null || (!connected && status != BackendStatus.Reconnecting) ? TrayIconKind.Disconnected
            : !u.Online ? TrayIconKind.Offline
            : u.PowerOn ? TrayIconKind.On : TrayIconKind.Off;
        var showText = _settings.Current.ShowTemperatureInIcon;
        var size = TrayIconRenderer.TraySize();
        var light = TrayIconRenderer.TaskbarIsLight();
        var sig = $"{kind}|{u?.Mode}|{u?.TargetTemperature}|{showText}|{size}|{light}";
        if (sig != _iconSignature)
        {
            var newIcon = TrayIconRenderer.Render(kind, u?.Mode, u?.TargetTemperature, showText, size, light);
            _icon.UpdateIcon(newIcon);
            _currentIcon?.Dispose();
            _currentIcon = newIcon;
            _iconSignature = sig;
        }

        // The shell allows 127 tooltip characters.
        string tip;
        if (u is null)
            tip = status == BackendStatus.AuthFailed ? L.T("Kaze: sign in needed") : L.T("Kaze: connecting…");
        else
        {
            tip = u.Summary;
            if (status is BackendStatus.Reconnecting or BackendStatus.Connecting) tip += L.T(" (reconnecting)");
            if (_schedules.Scheduler.Timers.FirstOrDefault() is { } t) tip += "\n" + L.F("Timer: {0} at {1:HH:mm}", t.Action, t.DueAt.LocalDateTime);
        }
        _icon.ToolTipText = tip.Length > 127 ? tip[..126] + "…" : tip;

        // Menu: never rebuild while it is open, the items would vanish under the pointer.
        _menuStale = true;
        if (!_menuOpen) BuildMenu();
    }

    private void BuildMenu()
    {
        _menuStale = false;
        var items = _menu.Items;
        items.Clear();

        var u = _controller.Selected;
        var key = u?.Key;
        var enabled = u is { Online: true, FrostWashActive: false };

        items.Add(new MenuFlyoutItem
        {
            Text = u?.Summary ?? L.T("No air conditioner connected"),
            Icon = FluentIcons.Create(u is null ? FluentIconKind.Warning : FluentIcons.ForMode(u.Mode)),
            IsEnabled = false,
        });
        items.Add(new MenuFlyoutSeparator());

        if (u is not null && key is not null)
        {
            var caps = _controller.CapabilitiesOf(key);
            var modeCaps = caps.For(u.Mode);

            items.Add(Item(u.PowerOn ? L.T("Turn off") : L.T("Turn on"), () => _controller.TogglePower(key), enabled, FluentIcons.Create(FluentIconKind.Power)));

            var step = _settings.Current.TemperatureStep;
            var tempOk = enabled && AcCapabilities.HasTemperatureRange(modeCaps);
            items.Add(Item(L.F("Warmer, now {0:0.#}°", u.TargetTemperature), () => _controller.AdjustTemperature(key, step), tempOk, Glyph(""), "+"));
            items.Add(Item(L.T("Cooler"), () => _controller.AdjustTemperature(key, -step), tempOk, Glyph(""), "-"));
            items.Add(new MenuFlyoutSeparator());

            items.Add(Sub(L.T("Mode"), caps.SupportedModes, u.Mode, AcModes.Display,
                m => _controller.Send(key, new AcCommand { Mode = m, PowerOn = true }), enabled,
                FluentIcons.Create(FluentIcons.ForMode(u.Mode)), m => FluentIcons.Create(FluentIcons.ForMode(m))));
            if (modeCaps.FanEnabled && modeCaps.FanSpeeds.Count > 0)
                items.Add(Sub(L.T("Fan speed"), modeCaps.FanSpeeds, u.FanSpeed, FanSpeeds.Display,
                    f => _controller.Send(key, AcCommand.SetFan(f)), enabled, FluentIcons.Create(FluentIconKind.Wind)));
            if (modeCaps.SwingEnabled && caps.Swings.Count > 1)
                items.Add(Sub(L.T("Swing"), caps.Swings, u.FanSwing, FanSwings.Display,
                    s => _controller.Send(key, AcCommand.SetSwing(s)), enabled, FluentIcons.Create(FluentIconKind.SwingVertical)));

            var presets = new MenuFlyoutSubItem
            {
                Text = L.T("Presets"),
                Icon = FluentIcons.Create(FluentIconKind.Star),
                IsEnabled = enabled && _settings.Current.Presets.Count > 0,
            };
            foreach (var p in _settings.Current.Presets)
            {
                var preset = p;
                presets.Items.Add(Item(preset.Name, () => _controller.Send(key, preset.Command)));
            }
            items.Add(presets);

            var timer = new MenuFlyoutSubItem { Text = L.T("Off timer"), Icon = FluentIcons.Create(FluentIconKind.Timer) };
            foreach (var (label, minutes) in new[] { (L.T("In 30 minutes"), 30), (L.T("In 1 hour"), 60), (L.T("In 2 hours"), 120), (L.T("In 4 hours"), 240), (L.T("In 8 hours"), 480) })
            {
                var m = minutes;
                timer.Items.Add(Item(label, () => _schedules.StartTimer(TimeSpan.FromMinutes(m), AcCommand.Power(false), key)));
            }
            var timers = _schedules.Scheduler.Timers;
            if (timers.Count > 0)
            {
                timer.Items.Add(new MenuFlyoutSeparator());
                foreach (var t in timers)
                {
                    var id = t.Id;
                    timer.Items.Add(Item(L.F("Cancel {0} at {1:HH:mm}", t.Action, t.DueAt.LocalDateTime), () => _schedules.Scheduler.Remove(id), icon: Glyph("")));
                }
            }
            items.Add(timer);

            var sleep = App.GetService<SleepService>();
            var sleeping = sleep.IsActive(key);
            items.Add(Item(sleeping && sleep.EndsAt(key) is { } end ? L.F("Stop sleep curve (until {0:HH:mm})", end.LocalDateTime) : L.T("Start sleep curve"),
                () => { if (sleeping) sleep.Cancel(key); else sleep.Start(key); },
                enabled, Glyph("\uE708")));

            if (u.FrostWashSupported)
            {
                items.Add(Item(u.FrostWashActive ? L.T("Frost Wash running") : L.T("Start Frost Wash"),
                    () => _ = _controller.StartFrostWashAsync(key),
                    u.Online && !u.FrostWashActive, FluentIcons.Create(FluentIconKind.Broom)));
            }

            var units = _controller.Units;
            if (units.Count > 1)
            {
                items.Add(new MenuFlyoutSeparator());
                items.Add(Sub(L.T("Unit"), units.Select(x => x.Key).ToList(), key,
                    k => units.First(x => x.Key == k).Name,
                    k => { _controller.SelectedKey = k; _settings.Update(s => s.SelectedUnitKey = k); }, true,
                    FluentIcons.Create(FluentIconKind.Home)));
                items.Add(Item(L.T("Turn all off"), () => _controller.SendToAll(AcCommand.Power(false)), icon: FluentIcons.Create(FluentIconKind.Power)));
            }
            items.Add(new MenuFlyoutSeparator());
        }

        if (App.Current is { } app && app.Accounts.Count > 1)
        {
            var active = app.ActiveAccount;
            var accounts = new MenuFlyoutSubItem { Text = L.F("Account: {0}", active ?? L.T("none")), Icon = Glyph("\uE716") };
            foreach (var a in app.Accounts)
            {
                var login = a;
                accounts.Items.Add(new RadioMenuFlyoutItem
                {
                    Text = login,
                    GroupName = "accounts",
                    IsChecked = CredentialStore.SameLogin(login, active),
                    Command = new RelayCommand(() => _ = app.SwitchAccountAsync(login)),
                });
            }
            accounts.Items.Add(new MenuFlyoutSeparator());
            accounts.Items.Add(Item(L.T("Add account"), () => app.ShowAddAccount(), icon: Glyph("\uE8FA")));
            items.Add(accounts);
            items.Add(new MenuFlyoutSeparator());
        }

        items.Add(Item(L.T("Open Kaze"), () => OpenMainWindowRequested?.Invoke(), icon: FluentIcons.Create(FluentIconKind.OpenWindow)));
        items.Add(Item(L.T("Refresh"), () => _controller.Refresh(), icon: FluentIcons.Create(FluentIconKind.Refresh)));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Item(L.T("Exit"), () => ExitRequested?.Invoke(), icon: FluentIcons.Create(FluentIconKind.Exit)));
    }

    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph, FontSize = 16 };

    private static MenuFlyoutItem Item(string text, Action action, bool enabled = true, IconElement? icon = null, string? accelerator = null) =>
        new()
        {
            Text = text,
            IsEnabled = enabled,
            Icon = icon,
            KeyboardAcceleratorTextOverride = accelerator ?? "",
            Command = new RelayCommand(action),
        };

    private static MenuFlyoutSubItem Sub(string text, IReadOnlyList<string> keys, string selected,
        Func<string, string> label, Action<string> onSelect, bool enabled, IconElement? icon = null,
        Func<string, IconElement>? itemIcon = null)
    {
        var sub = new MenuFlyoutSubItem { Text = $"{text}: {label(selected)}", IsEnabled = enabled, Icon = icon };
        foreach (var k in keys)
        {
            var key = k;
            var isSelected = string.Equals(key, selected, StringComparison.OrdinalIgnoreCase);
            sub.Items.Add(itemIcon is null
                ? new RadioMenuFlyoutItem
                {
                    Text = label(key),
                    GroupName = text,
                    IsChecked = isSelected,
                    Command = new RelayCommand(() => onSelect(key)),
                }
                : new RadioMenuFlyoutItem
                {
                    Text = label(key),
                    GroupName = text,
                    IsChecked = isSelected,
                    Icon = itemIcon(key),
                    Command = new RelayCommand(() => onSelect(key)),
                });
        }
        return sub;
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnSystemChanged;
        _menuHost?.Close();
        _icon?.Dispose();
        _currentIcon?.Dispose();
    }
}
