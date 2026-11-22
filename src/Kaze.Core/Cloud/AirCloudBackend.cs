using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Kaze.Core.Backends;
using Kaze.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kaze.Core.Cloud;

/// <summary>
/// airCloud Go cloud backend. Keeps a STOMP websocket open for state pushes. Reconnects
/// with exponential back-off, and re-subscribes (which yields a fresh snapshot) when quiet.
/// </summary>
public sealed class AirCloudBackend : IAcBackend
{
    private readonly AirCloudOptions _options;
    private readonly AirCloudAuth _auth;
    private readonly AirCloudRestClient _rest;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, AcCapabilities> _capabilities = new();

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _connectionCts;
    private Task? _loop;
    private IReadOnlyList<string> _families = [];

    public AirCloudBackend(AirCloudOptions options, AirCloudAuth auth, AirCloudRestClient rest, ILogger<AirCloudBackend>? logger = null)
    {
        _options = options;
        _auth = auth;
        _rest = rest;
        _log = logger ?? NullLogger<AirCloudBackend>.Instance;
    }

    public string Name => "airCloud Go (cloud)";
    public BackendStatus Status { get; private set; } = BackendStatus.Stopped;
    public IReadOnlyList<string> FamilyIds => _families;

    public event Action<IReadOnlyList<AcState>>? UnitsReported;
    public event Action<BackendStatus, string?>? StatusChanged;

    /// <summary>Raised with every raw STOMP frame received (diagnostics / probe tool).</summary>
    public event Action<StompFrame>? FrameReceived;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is { IsCompleted: false })
            return Task.CompletedTask;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunAsync(_runCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_runCts is null) return;
        await _runCts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loop is not null) await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        _runCts.Dispose();
        _runCts = null;
        _loop = null;
        SetStatus(BackendStatus.Stopped, null);
    }

    public void RequestRefresh()
    {
        try { _connectionCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task SendAsync(AcState current, AcCommand command, CancellationToken ct = default)
    {
        await _rest.SendCommandAsync(current, command, ct).ConfigureAwait(false);
    }

    public async Task<AcCapabilities> GetCapabilitiesAsync(AcState unit, CancellationToken ct = default)
    {
        var key = unit.CloudId ?? "";
        if (_capabilities.TryGetValue(key, out var cached))
            return cached;
        if (string.IsNullOrEmpty(unit.CloudId))
            return AcCapabilities.Default;

        try
        {
            var raw = await _rest.GetConfigurationRawAsync([unit.CloudId], ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            var entry = AirCloudJson.FindConfiguration(doc.RootElement, unit.CloudId);
            var caps = entry is { } e ? AirCloudJson.ParseCapabilities(e) : AcCapabilities.Default;
            _capabilities[key] = caps;
            return caps;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not load capabilities for {Unit}, using defaults", unit.Name);
            return AcCapabilities.Default;
        }
    }

    public async Task<EnergyReport?> GetEnergyAsync(string familyId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var raw = await _rest.GetEnergySummaryRawAsync(familyId, from, to, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        using var doc = JsonDocument.Parse(raw);
        return AirCloudJson.ParseEnergySummary(doc.RootElement, from, to);
    }

    public async Task StartFrostWashAsync(AcState unit, CancellationToken ct = default)
    {
        await _rest.StartFrostWashAsync(unit, ct).ConfigureAwait(false);
        RequestRefresh();
    }

    public async Task<IReadOnlyList<string>> GetEnergyTrackingOffAsync(string familyId, CancellationToken ct = default)
    {
        var racs = await _rest.GetEnergyRacsAsync(familyId, ct).ConfigureAwait(false);
        return racs.Where(r => r.Supported && !r.Selected).Select(r => r.RacId).ToList();
    }

    /// <summary>Connects once, waits for the first state message for every family, and disconnects.</summary>
    public async Task<IReadOnlyList<AcState>> FetchSnapshotAsync(CancellationToken ct = default)
    {
        var families = await _rest.GetFamilyIdsAsync(ct).ConfigureAwait(false);
        _families = families;
        var result = new List<AcState>();
        var pending = new HashSet<string>(families);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        await RunConnectionAsync(families, frame =>
        {
            if (frame.Command == "MESSAGE" && TryParseUnits(frame, out var fid, out var units))
            {
                result.AddRange(units);
                pending.Remove(fid);
            }
            return pending.Count > 0; // keep reading?
        }, timeout.Token).ConfigureAwait(false);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var authRetried = false;
        var planned = false;
        while (!ct.IsCancellationRequested)
        {
            var receivedState = false;
            try
            {
                // A planned resubscribe (watchdog or refresh) keeps the status, so the UI doesn't blink.
                if (!(planned && Status == BackendStatus.Connected))
                    SetStatus(Status == BackendStatus.Connected ? BackendStatus.Reconnecting : BackendStatus.Connecting, null);
                planned = false;
                _families = await _rest.GetFamilyIdsAsync(ct).ConfigureAwait(false);
                if (_families.Count == 0)
                    throw new AirCloudException(AirCloudErrorKind.Protocol, "No homes (families) found on this airCloud account.");

                using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _connectionCts = connCts;

                var lastMessage = DateTimeOffset.UtcNow;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!connCts.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), connCts.Token).ConfigureAwait(false);
                            if (DateTimeOffset.UtcNow - lastMessage > _options.ResyncInterval)
                            {
                                _log.LogDebug("No state for {Interval}, resubscribing", _options.ResyncInterval);
                                await connCts.CancelAsync().ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                }, connCts.Token);

                await RunConnectionAsync(_families, frame =>
                {
                    if (frame.Command == "CONNECTED")
                    {
                        lastMessage = DateTimeOffset.UtcNow;
                        SetStatus(BackendStatus.Connected, null);
                    }
                    else if (frame.Command == "MESSAGE" && TryParseUnits(frame, out _, out var units))
                    {
                        lastMessage = DateTimeOffset.UtcNow;
                        receivedState = true;
                        if (Status != BackendStatus.Connected)
                            SetStatus(BackendStatus.Connected, null);
                        if (units.Count > 0)
                            UnitsReported?.Invoke(units);
                    }
                    return true;
                }, connCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                delay = TimeSpan.FromSeconds(2);
                planned = true;
                continue;
            }
            catch (AirCloudException ex) when (ex.Kind is AirCloudErrorKind.InvalidCredentials or AirCloudErrorKind.AccountLocked or AirCloudErrorKind.UserNotVerified)
            {
                _log.LogWarning("airCloud sign-in rejected: {Message}", ex.Message);
                SetStatus(BackendStatus.AuthFailed, ex.Message);
                break; // needs user action
            }
            catch (StompAuthException)
            {
                _auth.Reset();
                if (!authRetried)
                {
                    authRetried = true;
                    continue;
                }
                SetStatus(BackendStatus.Reconnecting, "Notification service rejected the session.");
            }
            catch (Exception ex)
            {
                _log.LogWarning("airCloud connection error: {Message}", ex.Message);
                SetStatus(BackendStatus.Reconnecting, ex.Message);
            }
            finally
            {
                _connectionCts = null;
            }

            if (receivedState)
            {
                delay = TimeSpan.FromSeconds(2);
                authRetried = false;
            }
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxReconnectDelay.Ticks));
        }
    }

    /// <summary>Opens a websocket, subscribes to all families and pumps frames until <paramref name="onFrame"/> returns false.</summary>
    private async Task RunConnectionAsync(IReadOnlyList<string> families, Func<StompFrame, bool> onFrame, CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", _options.UserAgent);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await ws.ConnectAsync(_options.NotificationSocket, ct).ConfigureAwait(false);

        var sendLock = new SemaphoreSlim(1, 1);
        async Task SendText(string text)
        {
            await sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally { sendLock.Release(); }
        }

        // Same shape the official app / community clients use: CONNECT + SUBSCRIBE(s) in one message.
        var hello = new StringBuilder(StompCodec.Connect(token, _options.StompHeartbeat)).Append('\n');
        foreach (var fid in families)
            hello.Append(StompCodec.Subscribe(Guid.NewGuid().ToString(), fid));
        await SendText(hello.ToString()).ConfigureAwait(false);

        using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!hbCts.IsCancellationRequested)
                {
                    await Task.Delay(_options.StompHeartbeat, hbCts.Token).ConfigureAwait(false);
                    await SendText(StompCodec.HeartBeat).ConfigureAwait(false);
                }
            }
            catch (Exception) { /* connection ending */ }
        }, hbCts.Token);

        var reader = new StompFrameReader();
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException($"Server closed the notification socket ({result.CloseStatus} {result.CloseStatusDescription}).");

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);

                foreach (var frame in reader.Feed(text))
                {
                    FrameReceived?.Invoke(frame);
                    switch (frame.Command)
                    {
                        case "CONNECTED" when frame.Header("user-name") is null:
                            throw new StompAuthException();
                        case "ERROR":
                            var msg = frame.Header("message") ?? frame.Body;
                            if (msg.Contains("auth", StringComparison.OrdinalIgnoreCase) || msg.Contains("token", StringComparison.OrdinalIgnoreCase))
                                throw new StompAuthException();
                            throw new AirCloudException(AirCloudErrorKind.Protocol, $"Notification error: {msg}");
                    }
                    if (!onFrame(frame))
                        return;
                }
            }
        }
        finally
        {
            await hbCts.CancelAsync().ConfigureAwait(false);
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.SendAsync(Encoding.UTF8.GetBytes(StompCodec.Disconnect()), WebSocketMessageType.Text, true, closeCts.Token).ConfigureAwait(false);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false);
                }
                catch { /* best effort */ }
            }
            try { await heartbeat.ConfigureAwait(false); } catch { }
        }
    }

    private bool TryParseUnits(StompFrame frame, out string familyId, out IReadOnlyList<AcState> units)
    {
        familyId = FamilyFromDestination(frame.Header("destination")) ?? _families.FirstOrDefault() ?? "";
        units = [];
        var body = frame.Body;
        var start = body.IndexOfAny(['{', '[']);
        if (start < 0)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(body[start..].TrimEnd('\0'));
            units = AirCloudJson.ParseUnits(doc.RootElement, familyId);
            return true;
        }
        catch (JsonException ex)
        {
            _log.LogDebug("Unparseable notification: {Message}", ex.Message);
            return false;
        }
    }

    internal static string? FamilyFromDestination(string? destination)
    {
        if (string.IsNullOrEmpty(destination)) return null;
        var parts = destination.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : null;
    }

    private void SetStatus(BackendStatus status, string? detail)
    {
        if (Status == status && detail is null) return;
        Status = status;
        StatusChanged?.Invoke(status, detail);
    }

    private sealed class StompAuthException() : Exception("STOMP authentication failed");
}
