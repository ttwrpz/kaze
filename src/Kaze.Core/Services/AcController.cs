using Kaze.Core.Backends;
using Kaze.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kaze.Core.Services;

public sealed class CommandFailedEventArgs(AcState unit, AcCommand command, Exception error, string? action = null) : EventArgs
{
    public AcState Unit { get; } = unit;
    public AcCommand Command { get; } = command;
    public Exception Error { get; } = error;
    public string Action { get; } = action ?? command.ToString();
}

public sealed class UnitChangedEventArgs(AcState? previous, AcState current, bool external) : EventArgs
{
    public AcState? Previous { get; } = previous;
    public AcState Current { get; } = current;
    /// <summary>True when the change came from outside this app (phone app, remote, schedule on the unit).</summary>
    public bool External { get; } = external;
}

/// <summary>
/// Single source of truth for unit state in the app.
/// <list type="bullet">
/// <item>Optimistic: commands update the visible state immediately.</item>
/// <item>Debounced: rapid commands (e.g. temp +,+,+) are merged and sent once.</item>
/// <item>Stale-push guard: right after a command the cloud may still push the old state. It is ignored for a while.</item>
/// </list>
/// Events are raised on background threads. UI must marshal.
/// </summary>
public sealed class AcController : IAsyncDisposable
{
    private readonly IAcBackend _backend;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _units = new();

    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromMilliseconds(600);
    public TimeSpan StaleGuard { get; set; } = TimeSpan.FromSeconds(25);

    public AcController(IAcBackend backend, TimeProvider? time = null, ILogger<AcController>? logger = null)
    {
        _backend = backend;
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger<AcController>.Instance;
        _backend.UnitsReported += OnUnitsReported;
        _status = backend.Status;
        _backend.StatusChanged += OnBackendStatus;
    }

    public IAcBackend Backend => _backend;

    /// <summary>
    /// The connection status shown to the user. A drop from Connected is reported only after
    /// <see cref="ReconnectGrace"/>, so a quick reconnect doesn't make the UI blink.
    /// </summary>
    public BackendStatus Status { get { lock (_sync) return _status; } }
    private BackendStatus _status;
    private ITimer? _statusTimer;

    public TimeSpan ReconnectGrace { get; set; } = TimeSpan.FromSeconds(5);

    private void OnBackendStatus(BackendStatus status, string? detail)
    {
        lock (_sync)
        {
            _statusTimer?.Dispose();
            _statusTimer = null;
            if (status == _status) return;
            if (_status == BackendStatus.Connected && status is BackendStatus.Reconnecting or BackendStatus.Connecting)
            {
                _statusTimer = _time.CreateTimer(_ =>
                {
                    var now = _backend.Status;
                    lock (_sync)
                    {
                        if (now == BackendStatus.Connected || now == _status) return;
                        _status = now;
                    }
                    StatusChanged?.Invoke(now, detail);
                }, null, ReconnectGrace, Timeout.InfiniteTimeSpan);
                return;
            }
            _status = status;
        }
        StatusChanged?.Invoke(status, detail);
    }

    public event EventHandler<UnitChangedEventArgs>? UnitChanged;
    public event EventHandler? UnitsListChanged;
    public event EventHandler<CommandFailedEventArgs>? CommandFailed;
    public event EventHandler<AcState>? CommandSent;
    public event Action<BackendStatus, string?>? StatusChanged;

    /// <summary>Key of the unit the tray/hotkeys act on. Falls back to the first unit.</summary>
    public string? SelectedKey
    {
        get
        {
            lock (_sync)
                return _selectedKey is not null && _units.ContainsKey(_selectedKey) ? _selectedKey : _units.Keys.FirstOrDefault();
        }
        set
        {
            lock (_sync) _selectedKey = value;
            UnitsListChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private string? _selectedKey;

    public IReadOnlyList<AcState> Units
    {
        get { lock (_sync) return _units.Values.Select(e => e.Visible).OrderBy(u => u.Name).ToList(); }
    }

    public AcState? Selected => SelectedKey is { } k ? Get(k) : null;

    public AcState? Get(string key)
    {
        lock (_sync) return _units.TryGetValue(key, out var e) ? e.Visible : null;
    }

    public AcCapabilities CapabilitiesOf(string key)
    {
        lock (_sync) return _units.TryGetValue(key, out var e) ? e.Capabilities : AcCapabilities.Default;
    }

    public Task StartAsync(CancellationToken ct = default) => _backend.StartAsync(ct);

    public Task StopAsync() => _backend.StopAsync();

    public void Refresh() => _backend.RequestRefresh();

    /// <summary>Forgets all units (used when switching accounts). Pending changes are dropped.</summary>
    public void ClearUnits()
    {
        lock (_sync)
        {
            foreach (var e in _units.Values) e.DebounceTimer?.Dispose();
            _units.Clear();
            _selectedKey = null;
        }
        UnitsListChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Queues a change for one unit. Returns immediately. The visible state updates at once.</summary>
    public bool Send(string key, AcCommand command)
    {
        if (command.IsEmpty) return false;
        AcState? before, after;
        lock (_sync)
        {
            if (!_units.TryGetValue(key, out var e)) return false;
            var caps = e.Capabilities;
            command = Sanitize(e.Visible, command, caps);
            before = e.Visible;
            e.Pending = e.Pending?.Then(command) ?? command;
            e.Visible = command.ApplyTo(e.Visible);
            after = e.Visible;

            e.DebounceTimer?.Dispose();
            e.DebounceTimer = _time.CreateTimer(_ => _ = FlushAsync(key), null, DebounceDelay, Timeout.InfiniteTimeSpan);
        }
        UnitChanged?.Invoke(this, new UnitChangedEventArgs(before, after, external: false));
        return true;
    }

    public bool SendToSelected(AcCommand command) => SelectedKey is { } k && Send(k, command);

    public void SendToAll(AcCommand command)
    {
        foreach (var u in Units) Send(u.Key, command);
    }

    public bool TogglePower(string key) => Get(key) is { } s && Send(key, AcCommand.Power(!s.PowerOn));

    public bool AdjustTemperature(string key, double delta)
    {
        if (Get(key) is not { } s) return false;
        var caps = CapabilitiesOf(key);
        if (!AcCapabilities.HasTemperatureRange(caps.For(s.Mode))) return false;
        var t = caps.Clamp(s.Mode, Math.Round((s.TargetTemperature + delta) * 2, MidpointRounding.AwayFromZero) / 2);
        return Math.Abs(t - s.TargetTemperature) > 0.01 && Send(key, AcCommand.SetTemperature(t));
    }

    /// <summary>Starts a Frost Wash cycle. Pending changes are sent first. Returns false if not possible.</summary>
    public async Task<bool> StartFrostWashAsync(string key)
    {
        await FlushAsync(key).ConfigureAwait(false);
        AcState unit, before, after;
        lock (_sync)
        {
            if (!_units.TryGetValue(key, out var e) || !e.Visible.FrostWashSupported || !e.Visible.Online) return false;
            unit = e.Reported;
            before = e.Visible;
            e.Visible = e.Visible with { FrostWashActive = true };
            after = e.Visible;
        }
        UnitChanged?.Invoke(this, new UnitChangedEventArgs(before, after, external: false));
        try
        {
            await _backend.StartFrostWashAsync(unit).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Frost Wash failed for {Unit}", unit.Name);
            lock (_sync)
            {
                if (_units.TryGetValue(key, out var e))
                {
                    before = e.Visible;
                    e.Visible = e.Visible with { FrostWashActive = e.Reported.FrostWashActive };
                    after = e.Visible;
                }
            }
            UnitChanged?.Invoke(this, new UnitChangedEventArgs(before, after, external: false));
            CommandFailed?.Invoke(this, new CommandFailedEventArgs(unit, new AcCommand(), ex, "Frost Wash"));
            return false;
        }
    }

    /// <summary>Sends any debounced command now (for example before exit).</summary>
    public async Task FlushAllAsync()
    {
        List<string> keys;
        lock (_sync) keys = _units.Where(kv => kv.Value.Pending is not null).Select(kv => kv.Key).ToList();
        foreach (var k in keys) await FlushAsync(k).ConfigureAwait(false);
    }

    private async Task FlushAsync(string key)
    {
        AcState baseState;
        AcCommand command;
        lock (_sync)
        {
            if (!_units.TryGetValue(key, out var e) || e.Pending is null) return;
            command = e.Pending;
            e.Pending = null;
            e.DebounceTimer?.Dispose();
            e.DebounceTimer = null;
            // Base on the last confirmed state plus earlier in-flight commands. The visible state already includes this command.
            baseState = e.InFlight?.ApplyTo(e.Reported) ?? e.Reported;
            e.InFlight = e.InFlight?.Then(command) ?? command;
            e.GuardUntil = _time.GetUtcNow() + StaleGuard;
        }

        try
        {
            await _backend.SendAsync(baseState, command).ConfigureAwait(false);
            CommandSent?.Invoke(this, command.ApplyTo(baseState));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Command failed for {Unit}", baseState.Name);
            AcState? before, after;
            lock (_sync)
            {
                if (!_units.TryGetValue(key, out var e)) return;
                before = e.Visible;
                e.InFlight = null;
                e.GuardUntil = DateTimeOffset.MinValue;
                e.Visible = e.Pending?.ApplyTo(e.Reported) ?? e.Reported;
                after = e.Visible;
            }
            UnitChanged?.Invoke(this, new UnitChangedEventArgs(before, after, external: false));
            CommandFailed?.Invoke(this, new CommandFailedEventArgs(baseState, command, ex));
        }
    }

    private void OnUnitsReported(IReadOnlyList<AcState> reported)
    {
        var changes = new List<UnitChangedEventArgs>();
        var listChanged = false;
        var newUnits = new List<AcState>();
        var now = _time.GetUtcNow();

        lock (_sync)
        {
            foreach (var r in reported)
            {
                if (!_units.TryGetValue(r.Key, out var e))
                {
                    e = new Entry { Reported = r, Visible = r };
                    _units[r.Key] = e;
                    listChanged = true;
                    newUnits.Add(r);
                    changes.Add(new UnitChangedEventArgs(null, r, external: true));
                    continue;
                }

                var before = e.Visible;
                e.Reported = r;

                if (e.InFlight is { } inflight)
                {
                    var expected = inflight.ApplyTo(r);
                    if (Matches(r, expected) || now >= e.GuardUntil)
                    {
                        // Confirmed (or guard expired: trust the device).
                        e.InFlight = null;
                    }
                }

                var overlay = e.InFlight is { } f ? (e.Pending is { } p ? f.Then(p) : f) : e.Pending;
                e.Visible = overlay?.ApplyTo(r) ?? r;

                if (!SameVisible(before, e.Visible))
                {
                    var external = e.InFlight is null && e.Pending is null && !Matches(e.Visible, before);
                    changes.Add(new UnitChangedEventArgs(before, e.Visible, external));
                }
            }
        }

        foreach (var c in changes) UnitChanged?.Invoke(this, c);
        if (listChanged) UnitsListChanged?.Invoke(this, EventArgs.Empty);
        foreach (var u in newUnits) _ = LoadCapabilitiesAsync(u);
    }

    private async Task LoadCapabilitiesAsync(AcState unit)
    {
        var caps = await _backend.GetCapabilitiesAsync(unit).ConfigureAwait(false);
        lock (_sync)
        {
            if (_units.TryGetValue(unit.Key, out var e)) e.Capabilities = caps;
        }
        UnitsListChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clamp temperature to the mode's range. Pick a valid fan speed when switching modes.</summary>
    internal static AcCommand Sanitize(AcState current, AcCommand cmd, AcCapabilities caps)
    {
        var mode = cmd.Mode ?? current.Mode;
        var modeCaps = caps.For(mode);
        if (cmd.TargetTemperature is { } t)
            cmd = cmd with { TargetTemperature = double.IsFinite(t) ? caps.Clamp(mode, t) : null };
        else if (cmd.Mode is not null && AcCapabilities.HasTemperatureRange(modeCaps)
                 && (current.TargetTemperature < modeCaps.MinTemperature || current.TargetTemperature > modeCaps.MaxTemperature))
            cmd = cmd with { TargetTemperature = caps.Clamp(mode, current.TargetTemperature) };

        var fan = cmd.FanSpeed ?? current.FanSpeed;
        if (modeCaps.FanSpeeds.Count > 0 && !modeCaps.FanSpeeds.Contains(fan, StringComparer.OrdinalIgnoreCase))
            cmd = cmd with { FanSpeed = modeCaps.DefaultFanSpeed ?? modeCaps.FanSpeeds[0] };
        return cmd;
    }

    internal static bool Matches(AcState a, AcState b) =>
        a.PowerOn == b.PowerOn
        && string.Equals(a.Mode, b.Mode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.FanSpeed, b.FanSpeed, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.FanSwing, b.FanSwing, StringComparison.OrdinalIgnoreCase)
        && Math.Abs(a.TargetTemperature - b.TargetTemperature) < 0.05;

    private static bool SameVisible(AcState a, AcState b) =>
        Matches(a, b) && a.Online == b.Online && a.RoomTemperature == b.RoomTemperature && a.Name == b.Name
        && a.FrostWashActive == b.FrostWashActive && a.ErrorActive == b.ErrorActive && a.ErrorCode == b.ErrorCode;

    public async ValueTask DisposeAsync()
    {
        await FlushAllAsync().ConfigureAwait(false);
        await _backend.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Entry
    {
        public required AcState Reported { get; set; }
        public required AcState Visible { get; set; }
        public AcCommand? Pending { get; set; }
        public AcCommand? InFlight { get; set; }
        public DateTimeOffset GuardUntil { get; set; }
        public ITimer? DebounceTimer { get; set; }
        public AcCapabilities Capabilities { get; set; } = AcCapabilities.Default;
    }
}
