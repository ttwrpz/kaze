using Kaze.App.Helpers;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Kaze.Core.Weather;
using Microsoft.Extensions.Logging;

namespace Kaze.App.Services;

/// <summary>Connects the Core scheduler to the controller and persists rules/timers in settings.</summary>
public sealed class ScheduleService : IDisposable
{
    private static readonly TimeSpan HeadsUp = TimeSpan.FromMinutes(5);
    private readonly Scheduler _scheduler;
    private readonly AcController _controller;
    private readonly SettingsStore _settings;
    private readonly NotificationService _toasts;
    private readonly WeatherService _weather;
    private readonly ILogger<ScheduleService> _log;
    private readonly HashSet<Guid> _warned = [];
    private Timer? _headsUpTimer;
    private bool _loading;

    public ScheduleService(Scheduler scheduler, AcController controller, SettingsStore settings, NotificationService toasts,
        WeatherService weather, ILogger<ScheduleService> log)
    {
        _scheduler = scheduler;
        _controller = controller;
        _settings = settings;
        _toasts = toasts;
        _weather = weather;
        _log = log;
    }

    public Scheduler Scheduler => _scheduler;

    public void Start()
    {
        _loading = true;
        _scheduler.Load(_settings.Current.Schedules, _settings.Current.Timers);
        _loading = false;
        _scheduler.Changed += Persist;
        _scheduler.Fired += OnFired;
        _scheduler.Start();
        _headsUpTimer = new Timer(_ => CheckHeadsUp(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
    }

    public OneShotTimer StartTimer(TimeSpan after, AcCommand action, string? unitKey)
    {
        var unitName = unitKey is null ? L.T("All units") : _controller.Get(unitKey)?.Name ?? L.T("Unit");
        return _scheduler.AddTimer(after, action, unitKey, $"{unitName}: {action} {L.F("at {0:HH:mm}", DateTime.Now + after)}");
    }

    public bool SnoozeTimer(Guid id, TimeSpan by)
    {
        if (_scheduler.Timers.FirstOrDefault(t => t.Id == id) is not { } timer) return false;
        _scheduler.Remove(id);
        var after = timer.DueAt - DateTimeOffset.Now + by;
        if (after < TimeSpan.FromMinutes(1)) after = TimeSpan.FromMinutes(1);
        var moved = _scheduler.AddTimer(after, timer.Action, timer.UnitKey, timer.Label);
        // Keep a running sleep curve pointing at the new timer.
        _settings.Update(s =>
        {
            foreach (var ids in s.ActiveSleep.Values)
                for (var i = 0; i < ids.Count; i++)
                    if (ids[i] == id) ids[i] = moved.Id;
        });
        return true;
    }

    public void CancelTimer(Guid id) => _scheduler.Remove(id);

    private void Persist()
    {
        if (_loading) return;
        _settings.Update(s =>
        {
            s.Schedules = [.. _scheduler.Rules];
            s.Timers = [.. _scheduler.Timers];
        });
    }

    private void CheckHeadsUp()
    {
        if (!_settings.Current.Reminders.TimerHeadsUp) return;
        var now = DateTimeOffset.Now;
        foreach (var t in _scheduler.Timers)
        {
            if (t.Action.PowerOn != false || t.DueAt - now > HeadsUp || t.DueAt <= now) continue;
            lock (_warned)
                if (!_warned.Add(t.Id)) continue;
            var on = t.UnitKey is null ? _controller.Units.Any(u => u.PowerOn) : _controller.Get(t.UnitKey)?.PowerOn == true;
            if (!on) continue;
            _toasts.Show(L.T("Turning off soon"), L.F("{0} (in {1} minutes)", t.Label, Math.Max(1, (int)Math.Round((t.DueAt - now).TotalMinutes))),
                "timer-" + t.Id,
                new ToastButton(L.T("Snooze 30 minutes"), ToastActions.SnoozeTimer, Value: t.Id.ToString()),
                new ToastButton(L.T("Cancel timer"), ToastActions.CancelTimer, Value: t.Id.ToString()));
        }
    }

    private async void OnFired(ScheduledFire fire)
    {
        try
        {
            if (_scheduler.Rules.FirstOrDefault(r => r.Id == fire.SourceId) is { } rule
                && (rule.OnlyIfOutdoorAbove is not null || rule.OnlyIfOutdoorBelow is not null))
            {
                var outdoor = await _weather.OutdoorForDecisionAsync();
                if (!WeatherConditions.Allows(rule.OnlyIfOutdoorAbove, rule.OnlyIfOutdoorBelow, outdoor))
                {
                    _log.LogInformation("Schedule skipped by weather: {Label} at {Outdoor}°", fire.Label, outdoor);
                    if (_settings.Current.Automations.NotifyScheduleFired)
                        _toasts.Show(L.T("Schedule skipped"), L.F("{0}: it is {1:0.#}° outside.", fire.Label, outdoor));
                    return;
                }
            }

            _log.LogInformation("Schedule fired: {Label} → {Action}", fire.Label, fire.Action);
            if (fire.UnitKey is null)
                _controller.SendToAll(fire.Action);
            else if (!_controller.Send(fire.UnitKey, fire.Action))
                _log.LogWarning("Scheduled unit {Unit} not found", fire.UnitKey);

            if (_settings.Current.Automations.NotifyScheduleFired)
            {
                var buttons = fire.Action.PowerOn == false
                    ? new[] { new ToastButton(L.T("Turn back on"), ToastActions.PowerOn, fire.UnitKey ?? "*") }
                    : [];
                _toasts.Show(L.T("Schedule"), $"{fire.Label}: {fire.Action}", buttons);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Schedule handling failed");
        }
    }

    public void Dispose()
    {
        _headsUpTimer?.Dispose();
        _scheduler.Dispose();
    }
}
