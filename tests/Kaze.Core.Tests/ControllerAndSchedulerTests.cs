using Kaze.Core.Backends;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace Kaze.Core.Tests;

internal sealed class FakeBackend : IAcBackend
{
    public List<(AcState Base, AcCommand Cmd)> Sent { get; } = [];
    public bool Fail { get; set; }
    public string Name => "fake";
    public BackendStatus Status { get; private set; } = BackendStatus.Connected;
    public event Action<IReadOnlyList<AcState>>? UnitsReported;
    public event Action<BackendStatus, string?>? StatusChanged;

    public void SetStatus(BackendStatus s) { Status = s; StatusChanged?.Invoke(s, null); }

    public void Report(params AcState[] s) => UnitsReported?.Invoke(s);
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public void RequestRefresh() { }

    public Task SendAsync(AcState current, AcCommand command, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("boom");
        Sent.Add((current, command));
        return Task.CompletedTask;
    }

    public Task<AcCapabilities> GetCapabilitiesAsync(AcState unit, CancellationToken ct = default) => Task.FromResult(AcCapabilities.Default);
    public Task<EnergyReport?> GetEnergyAsync(string familyId, DateOnly from, DateOnly to, CancellationToken ct = default) => Task.FromResult<EnergyReport?>(null);
    public List<AcState> FrostWashes { get; } = [];
    public Task StartFrostWashAsync(AcState unit, CancellationToken ct = default) { FrostWashes.Add(unit); return Task.CompletedTask; }
    public Task<IReadOnlyList<string>> GetEnergyTrackingOffAsync(string familyId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class ConnectionStatusTests
{
    [Fact]
    public void Quick_reconnect_is_not_reported()
    {
        var time = new FakeTimeProvider();
        var backend = new FakeBackend();
        var c = new AcController(backend, time);
        var seen = new List<BackendStatus>();
        c.StatusChanged += (s, _) => seen.Add(s);

        backend.SetStatus(BackendStatus.Reconnecting);
        time.Advance(TimeSpan.FromSeconds(2));
        backend.SetStatus(BackendStatus.Connected);
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(seen);
        Assert.Equal(BackendStatus.Connected, c.Status);
    }

    [Fact]
    public void Lasting_drop_is_reported_after_the_grace_period()
    {
        var time = new FakeTimeProvider();
        var backend = new FakeBackend();
        var c = new AcController(backend, time);
        var seen = new List<BackendStatus>();
        c.StatusChanged += (s, _) => seen.Add(s);

        backend.SetStatus(BackendStatus.Reconnecting);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(seen);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal([BackendStatus.Reconnecting], seen);
        Assert.Equal(BackendStatus.Reconnecting, c.Status);

        backend.SetStatus(BackendStatus.Connected);
        Assert.Equal([BackendStatus.Reconnecting, BackendStatus.Connected], seen);
    }

    [Fact]
    public void Sign_in_failure_is_reported_at_once()
    {
        var backend = new FakeBackend();
        var c = new AcController(backend, new FakeTimeProvider());
        var seen = new List<BackendStatus>();
        c.StatusChanged += (s, _) => seen.Add(s);

        backend.SetStatus(BackendStatus.AuthFailed);

        Assert.Equal([BackendStatus.AuthFailed], seen);
    }
}

public class AcControllerTests
{
    private static readonly AcState Unit = new() { Id = "1", FamilyId = "f", Name = "Bed", PowerOn = true, Mode = AcModes.Cooling, TargetTemperature = 25, FanSpeed = FanSpeeds.Auto };

    private static (AcController, FakeBackend, FakeTimeProvider) Create()
    {
        var backend = new FakeBackend();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-22T10:00:00Z"));
        var c = new AcController(backend, time);
        backend.Report(Unit);
        return (c, backend, time);
    }

    [Fact]
    public void Rapid_temperature_changes_are_debounced_into_one_command()
    {
        var (c, backend, time) = Create();
        c.AdjustTemperature(Unit.Key, 0.5);
        c.AdjustTemperature(Unit.Key, 0.5);
        c.AdjustTemperature(Unit.Key, 0.5);
        Assert.Equal(26.5, c.Get(Unit.Key)!.TargetTemperature); // optimistic
        Assert.Empty(backend.Sent);

        time.Advance(TimeSpan.FromMilliseconds(700));
        var sent = Assert.Single(backend.Sent);
        Assert.Equal(26.5, sent.Cmd.TargetTemperature);
        Assert.Equal(25, sent.Base.TargetTemperature);
    }

    [Fact]
    public void Stale_push_is_ignored_during_guard_then_trusted()
    {
        var (c, backend, time) = Create();
        c.Send(Unit.Key, AcCommand.Power(false));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(backend.Sent);

        backend.Report(Unit with { RoomTemperature = 27 }); // still ON: stale
        Assert.False(c.Get(Unit.Key)!.PowerOn);
        Assert.Equal(27, c.Get(Unit.Key)!.RoomTemperature);

        time.Advance(TimeSpan.FromSeconds(30));
        backend.Report(Unit); // device really is ON (command lost) -> trust after guard
        Assert.True(c.Get(Unit.Key)!.PowerOn);
    }

    [Fact]
    public void Confirmation_clears_guard_so_later_external_changes_show()
    {
        var (c, backend, time) = Create();
        c.Send(Unit.Key, AcCommand.Power(false));
        time.Advance(TimeSpan.FromSeconds(1));
        backend.Report(Unit with { PowerOn = false });

        UnitChangedEventArgs? seen = null;
        c.UnitChanged += (_, e) => seen = e;
        backend.Report(Unit with { PowerOn = true }); // phone app turned it on
        Assert.True(c.Get(Unit.Key)!.PowerOn);
        Assert.True(seen!.External);
    }

    [Fact]
    public void Failed_command_reverts_and_reports()
    {
        var (c, backend, time) = Create();
        backend.Fail = true;
        CommandFailedEventArgs? failed = null;
        c.CommandFailed += (_, e) => failed = e;
        c.Send(Unit.Key, AcCommand.SetTemperature(20));
        Assert.Equal(20, c.Get(Unit.Key)!.TargetTemperature);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(failed);
        Assert.Equal(25, c.Get(Unit.Key)!.TargetTemperature);
    }

    [Fact]
    public void Temperature_is_clamped_to_mode_range()
    {
        var (c, _, _) = Create();
        c.Send(Unit.Key, AcCommand.SetTemperature(50));
        Assert.Equal(32, c.Get(Unit.Key)!.TargetTemperature);
    }

    [Fact]
    public void Fan_mode_without_temperature_range_keeps_set_point()
    {
        var caps = new AcCapabilities
        {
            Modes = new Dictionary<string, ModeCapabilities>
            {
                [AcModes.Cooling] = new() { Mode = AcModes.Cooling, MinTemperature = 16, MaxTemperature = 32 },
                [AcModes.Fan] = new() { Mode = AcModes.Fan, MinTemperature = 0, MaxTemperature = 0, TemperatureEnabled = false, FanSpeeds = [FanSpeeds.Lv1, FanSpeeds.Lv2] },
            },
        };
        var cmd = AcController.Sanitize(Unit, AcCommand.SetMode(AcModes.Fan), caps);
        Assert.Null(cmd.TargetTemperature);
        Assert.Equal(25, caps.Clamp(AcModes.Fan, 25));
    }

    [Fact]
    public async Task Frost_wash_is_optimistic_and_sent()
    {
        var backend = new FakeBackend();
        var c = new AcController(backend, new FakeTimeProvider());
        backend.Report(Unit with { FrostWashSupported = true });
        Assert.True(await c.StartFrostWashAsync(Unit.Key));
        Assert.True(c.Get(Unit.Key)!.FrostWashActive);
        Assert.Single(backend.FrostWashes);
    }

    [Fact]
    public async Task Frost_wash_refused_when_unsupported()
    {
        var (c, backend, _) = Create();
        Assert.False(await c.StartFrostWashAsync(Unit.Key));
        Assert.Empty(backend.FrostWashes);
    }

    [Fact]
    public void Clear_units_forgets_previous_account()
    {
        var (c, backend, _) = Create();
        c.SelectedKey = Unit.Key;
        c.ClearUnits();
        Assert.Empty(c.Units);
        Assert.Null(c.SelectedKey);
        backend.Report(Unit with { Id = "2", FamilyId = "g", Name = "Other" });
        Assert.Equal("g/2", c.SelectedKey);
    }

    [Fact]
    public void Switching_to_dry_picks_supported_fan()
    {
        var cmd = AcController.Sanitize(Unit with { FanSpeed = FanSpeeds.Lv4 }, AcCommand.SetMode(AcModes.Dry), AcCapabilities.Default);
        Assert.Equal(FanSpeeds.Auto, cmd.FanSpeed);
    }
}

public class SchedulerTests
{
    [Fact]
    public void Occurrences_respect_days_and_window()
    {
        var rule = new ScheduleRule { Time = new TimeOnly(22, 0), Days = [DayOfWeek.Monday] };
        var off = TimeSpan.FromHours(7);
        var from = new DateTimeOffset(2026, 11, 22, 0, 0, 0, off); // Sunday
        var to = from.AddDays(7);
        var occ = Scheduler.Occurrences(rule, from, to).ToList();
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 22, 0, 0, off), Assert.Single(occ));
    }

    [Fact]
    public void Rule_fires_once_when_time_passes()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 14, 59, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var s = new Scheduler(time);
        s.Load([new ScheduleRule { Name = "Off", Time = new TimeOnly(15, 0) }], []);
        var fired = new List<ScheduledFire>();
        s.Fired += fired.Add;
        s.Start();
        time.Advance(TimeSpan.FromSeconds(30));
        s.Tick();
        time.Advance(TimeSpan.FromSeconds(40));
        s.Tick();
        Assert.Single(fired);
    }

    [Fact]
    public void Missed_occurrences_after_sleep_are_skipped()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 14, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var s = new Scheduler(time);
        s.Load([new ScheduleRule { Time = new TimeOnly(15, 0) }], []);
        var fired = 0;
        s.Fired += _ => fired++;
        s.Tick();
        // PC sleeps for 3 hours (no ticks).
        time.AdjustTime(time.GetUtcNow() + TimeSpan.FromHours(3));
        s.Tick();
        Assert.Equal(0, fired);
    }

    [Fact]
    public void One_shot_timer_fires_and_is_removed()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 14, 0, 0, TimeSpan.Zero));
        var s = new Scheduler(time);
        ScheduledFire? fire = null;
        s.Fired += f => fire = f;
        s.AddTimer(TimeSpan.FromMinutes(30), AcCommand.Power(false), "f/1");
        time.Advance(TimeSpan.FromMinutes(31));
        s.Tick();
        Assert.NotNull(fire);
        Assert.Equal("f/1", fire!.UnitKey);
        Assert.Empty(s.Timers);
    }
}
