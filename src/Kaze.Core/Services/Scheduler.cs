using Kaze.Core.Models;

namespace Kaze.Core.Services;

public sealed record ScheduleRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Schedule";
    public bool Enabled { get; init; } = true;
    /// <summary>Days the rule runs on. Empty = every day.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];
    public TimeOnly Time { get; init; } = new(22, 0);
    /// <summary>Unit key, or null for all units.</summary>
    public string? UnitKey { get; init; }
    public AcCommand Action { get; init; } = AcCommand.Power(false);
    /// <summary>Only run when the outdoor temperature is at least this (needs a weather location).</summary>
    public double? OnlyIfOutdoorAbove { get; init; }
    /// <summary>Only run when the outdoor temperature is at most this.</summary>
    public double? OnlyIfOutdoorBelow { get; init; }

    public bool RunsOn(DayOfWeek d) => Days.Count == 0 || Days.Contains(d);
}

/// <summary>A one-off countdown timer, e.g. "turn off in 1 hour".</summary>
public sealed record OneShotTimer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Label { get; init; } = "Timer";
    public DateTimeOffset DueAt { get; init; }
    public string? UnitKey { get; init; }
    public AcCommand Action { get; init; } = AcCommand.Power(false);
}

public sealed record ScheduledFire(string Label, string? UnitKey, AcCommand Action, Guid SourceId);

/// <summary>
/// In-app scheduler. Checks every few seconds. Occurrences missed by more than <see cref="MissedGrace"/>
/// (PC asleep / app closed) are skipped, not replayed.
/// </summary>
public sealed class Scheduler : IDisposable
{
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private List<ScheduleRule> _rules = [];
    private List<OneShotTimer> _timers = [];
    private DateTimeOffset _lastCheck;
    private ITimer? _ticker;

    public Scheduler(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _lastCheck = _time.GetLocalNow();
    }

    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MissedGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Raised on a background thread when something is due.</summary>
    public event Action<ScheduledFire>? Fired;
    public event Action? Changed;

    public IReadOnlyList<ScheduleRule> Rules { get { lock (_sync) return [.. _rules]; } }
    public IReadOnlyList<OneShotTimer> Timers { get { lock (_sync) return [.. _timers.OrderBy(t => t.DueAt)]; } }

    public void Start()
    {
        _lastCheck = _time.GetLocalNow();
        _ticker ??= _time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public void Load(IEnumerable<ScheduleRule> rules, IEnumerable<OneShotTimer> timers)
    {
        lock (_sync)
        {
            _rules = [.. rules];
            var now = _time.GetLocalNow();
            _timers = [.. timers.Where(t => t.DueAt > now - MissedGrace)];
        }
        Changed?.Invoke();
    }

    public void Upsert(ScheduleRule rule)
    {
        lock (_sync)
        {
            var i = _rules.FindIndex(r => r.Id == rule.Id);
            if (i >= 0) _rules[i] = rule; else _rules.Add(rule);
        }
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        lock (_sync)
        {
            _rules.RemoveAll(r => r.Id == id);
            _timers.RemoveAll(t => t.Id == id);
        }
        Changed?.Invoke();
    }

    public OneShotTimer AddTimer(TimeSpan after, AcCommand action, string? unitKey, string? label = null)
    {
        var t = new OneShotTimer
        {
            DueAt = _time.GetLocalNow() + after,
            Action = action,
            UnitKey = unitKey,
            Label = label ?? $"{action} in {Format(after)}",
        };
        lock (_sync) _timers.Add(t);
        Changed?.Invoke();
        return t;
    }

    public void ClearTimers()
    {
        lock (_sync) _timers.Clear();
        Changed?.Invoke();
    }

    /// <summary>Evaluates due rules/timers. Public for tests.</summary>
    public void Tick()
    {
        var now = _time.GetLocalNow();
        var fires = new List<ScheduledFire>();
        var changed = false;
        lock (_sync)
        {
            var from = _lastCheck;
            if (now - from > MissedGrace) from = now - MissedGrace;
            _lastCheck = now;

            foreach (var r in _rules.Where(r => r.Enabled))
                if (Occurrences(r, from, now).Any())
                    fires.Add(new ScheduledFire(r.Name, r.UnitKey, r.Action, r.Id));

            foreach (var t in _timers.Where(t => t.DueAt <= now).ToList())
            {
                _timers.Remove(t);
                changed = true;
                if (t.DueAt > now - MissedGrace)
                    fires.Add(new ScheduledFire(t.Label, t.UnitKey, t.Action, t.Id));
            }
        }
        if (changed) Changed?.Invoke();
        foreach (var f in fires) Fired?.Invoke(f);
    }

    /// <summary>Occurrences of <paramref name="rule"/> in the half-open window (from, to].</summary>
    public static IEnumerable<DateTimeOffset> Occurrences(ScheduleRule rule, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) yield break;
        for (var day = DateOnly.FromDateTime(from.DateTime); day <= DateOnly.FromDateTime(to.DateTime); day = day.AddDays(1))
        {
            if (!rule.RunsOn(day.DayOfWeek)) continue;
            var local = day.ToDateTime(rule.Time);
            var at = new DateTimeOffset(local, to.Offset);
            if (at > from && at <= to)
                yield return at;
        }
    }

    /// <summary>Next time the rule will fire after <paramref name="after"/>.</summary>
    public static DateTimeOffset? NextOccurrence(ScheduleRule rule, DateTimeOffset after) =>
        rule.Enabled ? Occurrences(rule, after, after.AddDays(8)).Cast<DateTimeOffset?>().FirstOrDefault() : null;

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? (t.Minutes == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalHours} h {t.Minutes} min") : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";

    public void Dispose() => _ticker?.Dispose();
}
