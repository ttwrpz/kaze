using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kaze.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Kaze.App.Services;

/// <summary>
/// Small HTTP API for Stream Deck, PowerToys, scripts and Home Assistant.
/// Listens on 127.0.0.1 only, unless the user opens it to the home network. Every request needs the token.
/// </summary>
public sealed class LocalApiServer(
    AcController controller,
    SettingsStore settings,
    SleepService sleep,
    HistoryService history,
    ILogger<LocalApiServer> log) : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private TcpListener? _listener;
    private readonly SemaphoreSlim _connections = new(16, 16);
    private CancellationTokenSource? _cts;

    /// <summary>The UI dispatcher. Unit commands and settings changes run there.</summary>
    public DispatcherQueue? Ui { get; set; }
    public bool Running => _listener is not null;
    public string? Error { get; private set; }
    public event Action? StateChanged;

    public string BaseUrl => $"http://localhost:{settings.Current.LocalApi.Port}/api";

    /// <summary>Starts, stops or restarts the listener to match settings.</summary>
    public void Apply()
    {
        Stop();
        var s = settings.Current.LocalApi;
        Error = null;
        if (s.Enabled)
        {
            try
            {
                var listener = new TcpListener(s.AllowNetwork ? IPAddress.Any : IPAddress.Loopback, s.Port);
                listener.Start();
                _listener = listener;
                _cts = new CancellationTokenSource();
                _ = AcceptLoopAsync(listener, _cts.Token);
                log.LogInformation("Local API listening on port {Port} (network {Network})", s.Port, s.AllowNetwork);
            }
            catch (SocketException ex)
            {
                Error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"Port {s.Port} is already in use. Pick another port."
                    : ex.Message;
                log.LogWarning("Local API could not start: {Error}", Error);
            }
        }
        StateChanged?.Invoke();
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }
            _ = HandleAsync(client, ct);
        }
    }

    private sealed record Request(string Method, string Path, string Query, Dictionary<string, string> Headers, string Body);
    private sealed record Response(int Status, object Body);

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        // Refuse callers outside the home network, and cap how many connections can be open at once,
        // so a flood of slow connections can't use up memory without the token.
        if (client.Client.RemoteEndPoint is not IPEndPoint remote || !LocalApi.IsHomeNetworkAddress(remote.Address)) return;
        if (!_connections.Wait(0)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var stream = client.GetStream();
            var (request, status) = await ReadRequestAsync(stream, timeout.Token);
            var response = status switch
            {
                401 => Unauthorized,
                _ when request is null => new Response(400, Err("Bad request")),
                _ => await RespondAsync(request),
            };
            await WriteAsync(stream, response, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
            // Client went away or was too slow.
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Local API request failed");
        }
        finally
        {
            _connections.Release();
        }
    }

    private static readonly Response Unauthorized =
        new(401, Err("Missing or wrong token. Send the header 'Authorization: Bearer <token>' (see Kaze Settings)."));

    /// <summary>Reads the headers, checks the token, and only then reads the body.</summary>
    private async Task<(Request? Request, int Status)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = new byte[MaxHeaderBytes];
        var read = 0;
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            if (read >= MaxHeaderBytes) return (null, 400);
            var n = await stream.ReadAsync(head.AsMemory(read, MaxHeaderBytes - read), ct);
            if (n == 0) return (null, 400);
            read += n;
            headerEnd = IndexOf(head.AsSpan(0, read), "\r\n\r\n"u8);
        }

        var lines = Encoding.ASCII.GetString(head, 0, headerEnd).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3) return (null, 400);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        var target = first[1];
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = q < 0 ? "" : target[(q + 1)..];

        var s = settings.Current.LocalApi;
        headers.TryGetValue("Host", out var host);
        headers.TryGetValue("Authorization", out var auth);
        if (!LocalApi.IsAuthorized(first[0], host, auth, QueryValue(query, "token"), s.Token, s.AllowNetwork))
            return (null, 401);

        var length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var l) ? l : 0;
        if (length < 0 || length > MaxBodyBytes) return (null, 400);
        var body = new byte[length];
        var bodyStart = headerEnd + 4;
        var have = Math.Min(read - bodyStart, length);
        head.AsSpan(bodyStart, have).CopyTo(body);
        while (have < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(have, length - have), ct);
            if (n == 0) return (null, 400);
            have += n;
        }
        return (new Request(first[0], path, query, headers, Encoding.UTF8.GetString(body)), 200);
    }

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern) => data.IndexOf(pattern);

    private static string? QueryValue(string query, string name)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            var key = Uri.UnescapeDataString(i < 0 ? part : part[..i]);
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i < 0 ? "" : Uri.UnescapeDataString(part[(i + 1)..].Replace('+', ' '));
        }
        return null;
    }

    private static object Err(string message) => new { error = message };

    private async Task<Response> RespondAsync(Request r)
    {
        var route = LocalApi.Route(r.Method, r.Path, out var arg);
        if (route == ApiRoute.NotFound) return new Response(404, Err("Unknown endpoint. GET /api lists them."));
        return await OnUiAsync(() => Handle(route, arg, r));
    }

    private Response Handle(ApiRoute route, string? arg, Request r)
    {
        var units = controller.Units;
        switch (route)
        {
            case ApiRoute.Info:
                return new Response(200, new
                {
                    app = "Kaze",
                    version = UpdateService.CurrentVersion.ToString(3),
                    units = units.Count,
                    endpoints = new[]
                    {
                        "GET  /api/units",
                        "GET  /api/units/{name|id|selected}",
                        "POST /api/units/{unit}/control   {\"power\":\"on|off|toggle\",\"mode\":\"cool\",\"temperature\":25 or \"+1\",\"fan\":\"auto\",\"swing\":\"vertical\"}",
                        "POST /api/units/{unit}/sleep     (add ?cancel=1 to stop)",
                        "POST /api/units/{unit}/frost-wash",
                        "GET  /api/units/{unit}/history?hours=24",
                        "GET  /api/presets",
                        "POST /api/presets/{name}         (add ?unit=... or ?all=1)",
                    },
                });

            case ApiRoute.Units:
                return new Response(200, units.Select(LocalApi.Describe).ToList());

            case ApiRoute.Presets:
                return new Response(200, settings.Current.Presets.Select(p => new { name = p.Name, action = p.Command.ToString() }).ToList());

            case ApiRoute.RunPreset:
                var preset = settings.Current.Presets.FirstOrDefault(p => p.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));
                if (preset is null) return new Response(404, Err($"No preset named '{arg}'."));
                var targets = QueryValue(r.Query, "all") is "1" or "true" ? units.ToList()
                    : LocalApi.FindUnit(units, QueryValue(r.Query, "unit") ?? "selected", controller.SelectedKey) is { } t ? [t] : [];
                if (targets.Count == 0) return new Response(404, Err("Unit not found."));
                foreach (var u in targets) controller.Send(u.Key, preset.Command);
                return new Response(200, new { ok = true, units = targets.Select(u => u.Name) });
        }

        var unit = LocalApi.FindUnit(units, arg, controller.SelectedKey);
        if (unit is null) return new Response(404, Err($"No unit '{arg}'. GET /api/units lists them."));

        switch (route)
        {
            case ApiRoute.Unit:
                return new Response(200, LocalApi.Describe(unit));

            case ApiRoute.Control:
                var req = LocalApi.ParseControl(r.Body);
                if (req.Errors.Count > 0) return new Response(400, new { errors = req.Errors });
                if (!unit.Online) return new Response(409, Err($"{unit.Name} is offline."));
                if (req.TogglePower) controller.TogglePower(unit.Key);
                if (req.TemperatureDelta is { } d) controller.AdjustTemperature(unit.Key, d);
                if (!req.Command.IsEmpty) controller.Send(unit.Key, req.Command);
                return new Response(200, LocalApi.Describe(controller.Get(unit.Key) ?? unit));

            case ApiRoute.Sleep:
                if (QueryValue(r.Query, "cancel") is "1" or "true")
                {
                    sleep.Cancel(unit.Key);
                    return new Response(200, new { ok = true, sleep = false });
                }
                return sleep.Start(unit.Key)
                    ? new Response(200, new { ok = true, sleep = true, endsAt = sleep.EndsAt(unit.Key) })
                    : new Response(409, Err($"{unit.Name} is offline."));

            case ApiRoute.FrostWash:
                if (!unit.FrostWashSupported) return new Response(409, Err($"{unit.Name} doesn't support Frost Wash."));
                _ = controller.StartFrostWashAsync(unit.Key);
                return new Response(202, new { ok = true });

            case ApiRoute.History:
                var hours = double.TryParse(QueryValue(r.Query, "hours"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var h) && double.IsFinite(h) ? Math.Clamp(h, 1, 24 * 90) : 24;
                return new Response(200, history.Last(unit.Key, TimeSpan.FromHours(hours)).Select(x => new
                {
                    at = x.At,
                    room = x.Room,
                    target = x.Target,
                    power = x.PowerOn ? "on" : "off",
                    mode = x.Mode.ToLowerInvariant(),
                }).ToList());
        }
        return new Response(404, Err("Unknown endpoint."));
    }

    private Task<Response> OnUiAsync(Func<Response> work)
    {
        if (Ui is null) return Task.FromResult(work());
        var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Ui.TryEnqueue(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
            tcs.SetResult(new Response(503, Err("Kaze is shutting down.")));
        return tcs.Task;
    }

    private static async Task WriteAsync(NetworkStream stream, Response response, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(response.Body, Json);
        var reason = response.Status switch
        {
            200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized",
            404 => "Not Found", 409 => "Conflict", 503 => "Service Unavailable", _ => "Error",
        };
        var head = $"HTTP/1.1 {response.Status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\n" +
                   $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(body, ct);
    }

    public void Dispose() => Stop();
}
