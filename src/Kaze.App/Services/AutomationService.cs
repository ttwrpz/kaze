using Kaze.App.Helpers;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Kaze.App.Services;

/// <summary>
/// PC-driven automations: turn units off when the PC locks or sleeps, and back on at unlock.
/// Also notifications for changes made elsewhere and for offline units.
/// </summary>
public sealed class AutomationService : IDisposable
{
    private readonly AcController _controller;
    private readonly SettingsStore _settings;
    private readonly NotificationService _toasts;
    private readonly ILogger<AutomationService> _log;
    private readonly HashSet<string> _turnedOffByUs = [];
    private CancellationTokenSource? _lockDelay;

    public AutomationService(AcController controller, SettingsStore settings, NotificationService toasts, ILogger<AutomationService> log)
    {
        _controller = controller;
        _settings = settings;
        _toasts = toasts;
        _log = log;
    }

    private AutomationSettings S => _settings.Current.Automations;

    public void Start()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _controller.UnitChanged += OnUnitChanged;
        _controller.CommandFailed += (_, e) =>
            _toasts.Show(L.F("{0}: command failed", e.Unit.Name), L.F("Could not apply {0}. {1}", e.Command, e.Error.Message));
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock when S.TurnOffOnLock:
                _lockDelay?.Cancel();
                _lockDelay = new CancellationTokenSource();
                var token = _lockDelay.Token;
                var delay = TimeSpan.FromMinutes(Math.Max(0, S.LockDelayMinutes));
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delay, token);
                        TurnAllOff("PC locked");
                    }
                    catch (OperationCanceledException) { }
                });
                break;

            case SessionSwitchReason.SessionUnlock:
                _lockDelay?.Cancel();
                if (S.RestoreOnUnlock) RestoreAll("PC unlocked");
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend && S.TurnOffOnSleep)
        {
            TurnAllOff("PC going to sleep");
            // Give the HTTP request a moment before the machine sleeps.
            _controller.FlushAllAsync().Wait(TimeSpan.FromSeconds(3));
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _controller.Refresh();
            if (S.RestoreOnUnlock && !S.TurnOffOnLock) RestoreAll("PC resumed");
        }
    }

    private void TurnAllOff(string reason)
    {
        var on = _controller.Units.Where(u => u.Online && u.PowerOn).ToList();
        if (on.Count == 0) return;
        lock (_turnedOffByUs)
            foreach (var u in on) _turnedOffByUs.Add(u.Key);
        foreach (var u in on) _controller.Send(u.Key, AcCommand.Power(false));
        _log.LogInformation("Automation: turned off {Count} unit(s): {Reason}", on.Count, reason);
        _toasts.Show(L.T("Air conditioner turned off"), $"{string.Join(", ", on.Select(u => u.Name))}: {L.T(reason)}.",
            new ToastButton(L.T("Turn back on"), ToastActions.PowerOn, string.Join(",", on.Select(u => u.Key))));
    }

    private void RestoreAll(string reason)
    {
        List<string> keys;
        lock (_turnedOffByUs)
        {
            keys = [.. _turnedOffByUs];
            _turnedOffByUs.Clear();
        }
        var restored = new List<string>();
        foreach (var k in keys)
            if (_controller.Get(k) is { PowerOn: false, Online: true } u)
            {
                _controller.Send(k, AcCommand.Power(true));
                restored.Add(u.Name);
            }
        if (restored.Count > 0)
        {
            _log.LogInformation("Automation: restored {Units}: {Reason}", restored, reason);
            _toasts.Show(L.T("Air conditioner turned back on"), $"{string.Join(", ", restored)}: {L.T(reason)}.");
        }
    }

    private void OnUnitChanged(object? sender, UnitChangedEventArgs e)
    {
        if (e.Previous is null) return;
        if (S.NotifyOffline && e.Previous.Online != e.Current.Online)
            _toasts.Show(e.Current.Name, e.Current.Online ? L.T("Back online.") : L.T("Went offline (Wi-Fi or power lost?)."));
        else if (S.NotifyExternalChanges && e.External && e.Current.Online)
        {
            string? what = null;
            if (e.Previous.PowerOn != e.Current.PowerOn)
                what = e.Current.PowerOn ? L.T("Turned on from another device.") : L.T("Turned off from another device.");
            else if (e.Current.PowerOn && e.Previous.Mode != e.Current.Mode)
                what = L.F("Switched to {0} from another device.", AcModes.Display(e.Current.Mode));
            else if (e.Current.PowerOn && Math.Abs(e.Previous.TargetTemperature - e.Current.TargetTemperature) > 0.01)
                what = L.F("Set to {0:0.#}° from another device.", e.Current.TargetTemperature);
            if (what is not null)
                _toasts.Show(e.Current.Name, what);
        }
        // Someone turned it on manually: forget our auto-off memory.
        if (e.Current.PowerOn)
            lock (_turnedOffByUs) _turnedOffByUs.Remove(e.Current.Key);
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _lockDelay?.Cancel();
    }
}
