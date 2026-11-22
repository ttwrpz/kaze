using Kaze.App.Helpers;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Kaze.Core.Weather;
using Microsoft.Extensions.Logging;

namespace Kaze.App.Services;

/// <summary>Records room temperature history from state pushes and every few minutes.</summary>
public sealed class HistoryService(AcController controller, ILogger<HistoryService> log) : IDisposable
{
    public TemperatureHistory History { get; } = new(Path.Combine(SettingsStore.DataDirectory, "history"));
    private Timer? _timer;

    /// <summary>Raised (on a background thread) after a sample is written.</summary>
    public event Action<string>? Recorded;

    public void Start()
    {
        try { History.Prune(); } catch (Exception ex) { log.LogDebug(ex, "History prune failed"); }
        controller.UnitChanged += (_, e) => Record(e.Current);
        _timer = new Timer(_ =>
        {
            foreach (var u in controller.Units) Record(u);
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }

    private void Record(AcState unit)
    {
        if (History.Record(unit)) Recorded?.Invoke(unit.Key);
    }

    public IReadOnlyList<HistorySample> Last(string unitKey, TimeSpan span)
    {
        var now = DateTimeOffset.UtcNow;
        return History.Query(unitKey, now - span, now);
    }

    public void Dispose() => _timer?.Dispose();
}

/// <summary>Sleep curve: relaxes the set point overnight using one-shot timers (so it survives restarts).</summary>
public sealed class SleepService(ScheduleService schedules, AcController controller, SettingsStore settings, NotificationService toasts, ILogger<SleepService> log)
{
    public event Action? Changed;

    public bool IsActive(string key) => ActiveTimers(key).Count > 0;

    public DateTimeOffset? EndsAt(string key) => ActiveTimers(key).Select(t => (DateTimeOffset?)t.DueAt).Max();

    private List<OneShotTimer> ActiveTimers(string key)
    {
        if (!settings.Current.ActiveSleep.TryGetValue(key, out var ids)) return [];
        return schedules.Scheduler.Timers.Where(t => ids.Contains(t.Id)).ToList();
    }

    /// <summary>Describes the configured curve for a unit, for the UI ("26° after 1 h, 27° after 3 h, off after 8 h").</summary>
    public string Describe(string key)
    {
        if (controller.Get(key) is not { } unit) return "";
        var plan = SleepCurvePlanner.Plan(unit, settings.Current.SleepCurve, controller.CapabilitiesOf(key));
        return string.Join(", ", plan.Where(p => p.After > TimeSpan.Zero).Select(p =>
            p.Command.PowerOn == false
                ? L.F("off after {0}", Scheduler.Format(p.After))
                : L.F("{0} after {1}", p.Description, Scheduler.Format(p.After))));
    }

    public bool Start(string key)
    {
        if (controller.Get(key) is not { Online: true } unit) return false;
        Cancel(key, notify: false);
        var plan = SleepCurvePlanner.Plan(unit, settings.Current.SleepCurve, controller.CapabilitiesOf(key));
        var ids = new List<Guid>();
        foreach (var step in plan)
        {
            if (step.After == TimeSpan.Zero)
            {
                controller.Send(key, step.Command);
                continue;
            }
            var label = step.Command.PowerOn == false
                ? L.F("{0}: sleep curve ends, turn off", unit.Name)
                : L.F("{0}: sleep curve, {1}", unit.Name, step.Description);
            ids.Add(schedules.Scheduler.AddTimer(step.After, step.Command, key, label).Id);
        }
        settings.Update(s => s.ActiveSleep[key] = ids);
        log.LogInformation("Sleep curve started with {Steps} steps", ids.Count);
        toasts.Show(L.F("{0}: sleep curve on", unit.Name), Describe(key), "sleep-" + key,
            new ToastButton(L.T("Cancel sleep curve"), ToastActions.CancelSleep, key));
        Changed?.Invoke();
        return true;
    }

    public void Cancel(string key, bool notify = true)
    {
        if (!settings.Current.ActiveSleep.TryGetValue(key, out var ids)) return;
        foreach (var id in ids) schedules.Scheduler.Remove(id);
        settings.Update(s => s.ActiveSleep.Remove(key));
        if (notify) log.LogInformation("Sleep curve cancelled");
        Changed?.Invoke();
    }
}

/// <summary>Outdoor weather from Open-Meteo for the location chosen in settings.</summary>
public sealed class WeatherService(OpenMeteoClient client, SettingsStore settings, ILogger<WeatherService> log) : IDisposable
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(25);
    private Timer? _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WeatherNow? Current { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Raised (on a background thread) after a refresh.</summary>
    public event Action? Changed;

    public void Start() =>
        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30));

    public async Task<WeatherNow?> RefreshAsync(bool force = false)
    {
        var w = settings.Current.Weather;
        if (!w.HasLocation)
        {
            if (Current is not null) { Current = null; Changed?.Invoke(); }
            return null;
        }
        if (!force && Current is { } c && DateTimeOffset.Now - c.FetchedAt < Fresh) return c;
        await _gate.WaitAsync();
        try
        {
            Current = await client.GetCurrentAsync(w.Latitude!.Value, w.Longitude!.Value);
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            log.LogDebug(ex, "Weather refresh failed");
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
        return Current;
    }

    /// <summary>Outdoor temperature for a schedule decision (refreshes when older than 45 minutes).</summary>
    public async Task<double?> OutdoorForDecisionAsync()
    {
        if (Current is null || DateTimeOffset.Now - Current.FetchedAt > TimeSpan.FromMinutes(45))
        {
            try { await RefreshAsync(force: true).WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
        }
        return Current?.Temperature;
    }

    public Task<IReadOnlyList<WeatherLocation>> SearchAsync(string name) => client.SearchAsync(name, L.Language);

    public void SetLocation(WeatherLocation? location)
    {
        settings.Update(s =>
        {
            s.Weather.LocationName = location?.Display;
            s.Weather.Latitude = location?.Latitude;
            s.Weather.Longitude = location?.Longitude;
        });
        Current = null;
        _ = RefreshAsync(force: true);
    }

    public void Dispose() => _timer?.Dispose();
}

/// <summary>Frost Wash reminder and the monthly energy budget alert.</summary>
public sealed class ReminderService(AcController controller, SettingsStore settings, NotificationService toasts, EnergyService energy, ILogger<ReminderService> log) : IDisposable
{
    private Timer? _timer;
    private readonly Dictionary<string, DateOnly> _remindedOn = [];

    public void Start()
    {
        controller.UnitChanged += OnUnitChanged;
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));
    }

    private ReminderSettings R => settings.Current.Reminders;

    private void OnUnitChanged(object? sender, UnitChangedEventArgs e)
    {
        var u = e.Current;
        if (!u.FrostWashSupported) return;
        var washing = u.FrostWashActive && e.Previous?.FrostWashActive != true;
        // First time we see a unit, start counting from now instead of nagging immediately.
        var firstSeen = !R.LastFrostWash.ContainsKey(u.Key);
        if (washing || firstSeen)
            settings.Update(s =>
            {
                s.Reminders.LastFrostWash[u.Key] = DateTimeOffset.Now;
                if (washing) s.Reminders.FrostWashSnoozedUntil.Remove(u.Key);
            });
    }

    public DateTimeOffset? LastFrostWash(string key) => R.LastFrostWash.TryGetValue(key, out var t) ? t : null;

    public void SnoozeFrostWash(string key, TimeSpan span) =>
        settings.Update(s => s.Reminders.FrostWashSnoozedUntil[key] = DateTimeOffset.Now + span);

    public async Task CheckAsync()
    {
        try
        {
            CheckFrostWash();
            await CheckBudgetAsync();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Reminder check failed");
        }
    }

    private void CheckFrostWash()
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        foreach (var u in controller.Units.Where(u => u.FrostWashSupported && u.Online && !u.FrostWashActive))
        {
            R.FrostWashSnoozedUntil.TryGetValue(u.Key, out var snooze);
            if (!Reminders.FrostWashDue(LastFrostWash(u.Key), now, R.FrostWashIntervalDays, snooze == default ? null : snooze)) continue;
            if (_remindedOn.TryGetValue(u.Key, out var d) && d == today) continue;
            _remindedOn[u.Key] = today;
            var days = LastFrostWash(u.Key) is { } last ? (int)(now - last).TotalDays : R.FrostWashIntervalDays;
            toasts.Show(L.F("{0}: time for Frost Wash", u.Name),
                L.F("It has been {0} days. Frost Wash freezes and rinses the indoor coil to remove dust and mould.", days),
                "frost-" + u.Key,
                new ToastButton(L.T("Start Frost Wash"), ToastActions.FrostWash, u.Key),
                new ToastButton(L.T("Remind me in a week"), ToastActions.SnoozeFrostWash, u.Key));
        }
    }

    private async Task CheckBudgetAsync()
    {
        var budget = R.MonthlyKwhBudget;
        if (budget <= 0) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monthKey = today.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        if (R.BudgetMonth != monthKey)
            settings.Update(s => { s.Reminders.BudgetMonth = monthKey; s.Reminders.BudgetNotified.Clear(); });

        double used = 0;
        foreach (var family in energy.FamilyIds)
        {
            var report = await energy.GetCurrentMonthAsync(family);
            used += report?.TotalKWh ?? 0;
        }
        var crossed = Reminders.NewBudgetThresholds(used, budget, R.BudgetNotified, 80, 100);
        if (crossed.Count == 0) return;
        settings.Update(s => s.Reminders.BudgetNotified.AddRange(crossed));
        var top = crossed.Max();
        toasts.Show(top >= 100 ? L.T("Energy budget used up") : L.F("{0}% of the energy budget used", top),
            L.F("{0:0.#} of {1:0.#} kWh used this month.", used, budget), "budget",
            new ToastButton(L.T("Open energy"), ToastActions.OpenEnergy));
    }

    public void Dispose() => _timer?.Dispose();
}
