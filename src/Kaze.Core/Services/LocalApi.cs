using System.Globalization;
using System.Text.Json;
using Kaze.Core.Models;

namespace Kaze.Core.Services;

public sealed record ApiControlRequest(AcCommand Command, double? TemperatureDelta, bool TogglePower, IReadOnlyList<string> Errors)
{
    public bool IsEmpty => Command.IsEmpty && TemperatureDelta is null && !TogglePower;
}

public enum ApiRoute
{
    NotFound,
    Info,
    Units,
    Unit,
    Control,
    FrostWash,
    Sleep,
    History,
    Presets,
    RunPreset,
}

/// <summary>Request parsing and routing for the local HTTP API. The HTTP server lives in the app.</summary>
public static class LocalApi
{
    /// <summary>
    /// Body example: <c>{"power":"on","mode":"cool","temperature":25,"fan":"auto","swing":"vertical"}</c>.
    /// "power" also takes true/false/"toggle". "temperature" also takes "+1" or "-0.5" for a relative change.
    /// </summary>
    public static ApiControlRequest ParseControl(string? body)
    {
        var errors = new List<string>();
        var cmd = new AcCommand();
        double? delta = null;
        var toggle = false;
        if (string.IsNullOrWhiteSpace(body))
            return new ApiControlRequest(cmd, null, false, ["Empty body. Send JSON such as {\"power\":\"on\"}."]);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return new ApiControlRequest(cmd, null, false, ["Body is not valid JSON."]); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ApiControlRequest(cmd, null, false, ["Body must be a JSON object."]);

            foreach (var p in root.EnumerateObject())
            {
                switch (p.Name.ToLowerInvariant())
                {
                    case "power":
                        var pv = p.Value.ValueKind switch
                        {
                            JsonValueKind.True => "on",
                            JsonValueKind.False => "off",
                            JsonValueKind.String => p.Value.GetString()?.ToLowerInvariant(),
                            _ => null,
                        };
                        if (pv is "on" or "true") cmd = cmd with { PowerOn = true };
                        else if (pv is "off" or "false") cmd = cmd with { PowerOn = false };
                        else if (pv is "toggle") toggle = true;
                        else errors.Add("power must be on, off, toggle, true or false");
                        break;
                    case "mode":
                        if (CommandLineRequest.NormalizeMode(Str(p.Value)) is { } m) cmd = cmd with { Mode = m };
                        else errors.Add("mode must be cool, heat, dry, fan or auto");
                        break;
                    case "fan":
                        if (CommandLineRequest.NormalizeFan(Str(p.Value)) is { } f) cmd = cmd with { FanSpeed = f };
                        else errors.Add("fan must be auto or 1 to 6");
                        break;
                    case "swing":
                        if (CommandLineRequest.NormalizeSwing(Str(p.Value)) is { } s) cmd = cmd with { FanSwing = s };
                        else errors.Add("swing must be off, vertical, horizontal or both");
                        break;
                    case "temperature" or "temp":
                        if (p.Value.ValueKind == JsonValueKind.Number && CommandLineRequest.IsSaneTemperature(p.Value.GetDouble()))
                            cmd = cmd with { TargetTemperature = p.Value.GetDouble() };
                        else if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } ts
                                 && double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out var tv)
                                 && (ts.StartsWith('+') || ts.StartsWith('-') ? CommandLineRequest.IsSaneDelta(tv) : CommandLineRequest.IsSaneTemperature(tv)))
                        {
                            if (ts.StartsWith('+') || ts.StartsWith('-')) delta = tv;
                            else cmd = cmd with { TargetTemperature = tv };
                        }
                        else errors.Add("temperature must be a number such as 25, or a change such as \"+1\"");
                        break;
                    default:
                        errors.Add($"Unknown field '{p.Name}'");
                        break;
                }
            }
        }
        if (errors.Count == 0 && cmd.IsEmpty && delta is null && !toggle)
            errors.Add("Nothing to change.");
        return new ApiControlRequest(cmd, delta, toggle, errors);
    }

    /// <summary>Maps a request to a route. <paramref name="arg"/> is the unit or preset name, URL decoded.</summary>
    public static ApiRoute Route(string method, string path, out string? arg)
    {
        arg = null;
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        var get = method.Equals("GET", StringComparison.OrdinalIgnoreCase);
        var post = method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        if (parts.Length == 0 || !parts[0].Equals("api", StringComparison.OrdinalIgnoreCase)) return ApiRoute.NotFound;

        switch (parts.Length)
        {
            case 1 when get:
                return ApiRoute.Info;
            case 2 when get && Is(parts[1], "units"):
                return ApiRoute.Units;
            case 2 when get && Is(parts[1], "presets"):
                return ApiRoute.Presets;
            case 3 when get && Is(parts[1], "units"):
                arg = parts[2];
                return ApiRoute.Unit;
            case 3 when post && Is(parts[1], "presets"):
                arg = parts[2];
                return ApiRoute.RunPreset;
            case 4 when Is(parts[1], "units"):
                arg = parts[2];
                if (post && Is(parts[3], "control")) return ApiRoute.Control;
                if (post && Is(parts[3], "frost-wash")) return ApiRoute.FrostWash;
                if (post && Is(parts[3], "sleep")) return ApiRoute.Sleep;
                if (get && Is(parts[3], "history")) return ApiRoute.History;
                break;
        }
        arg = null;
        return ApiRoute.NotFound;
    }

    /// <summary>Finds a unit by key, id or name (case insensitive). "selected" means the default unit.</summary>
    public static AcState? FindUnit(IReadOnlyList<AcState> units, string? arg, string? selectedKey)
    {
        if (arg is null) return null;
        if (Is(arg, "selected") || Is(arg, "default")) return units.FirstOrDefault(u => u.Key == selectedKey) ?? units.FirstOrDefault();
        return units.FirstOrDefault(u => u.Key == arg || u.Id == arg)
            ?? units.FirstOrDefault(u => u.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The token must match. Unless the API is opened to the home network, the Host header also has
    /// to be localhost or 127.0.0.1, which blocks DNS rebinding from web pages. A token in the query
    /// string (<c>?token=</c>) only works for GET, which only reads, so a leaked link can't change anything.
    /// </summary>
    public static bool IsAuthorized(string method, string? host, string? authorization, string? queryToken, string expectedToken, bool allowAnyHost = false)
    {
        var h = host?.Split(':')[0].ToLowerInvariant();
        if (!allowAnyHost && h is not ("localhost" or "127.0.0.1")) return false;
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase)) queryToken = null;
        var presented = authorization is { } a && a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? a[7..].Trim() : queryToken;
        return presented is { Length: > 0 }
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(presented), System.Text.Encoding.UTF8.GetBytes(expectedToken));
    }

    /// <summary>
    /// Addresses that belong to a home network: loopback, private IPv4 ranges, link-local, carrier
    /// grade NAT (some routers and Tailscale) and IPv6 unique local. Anything else, such as a public
    /// address reaching the PC through a hotel or office network, is refused even with the token.
    /// </summary>
    public static bool IsHomeNetworkAddress(System.Net.IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(address)) return true;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        return address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
    }

    public static object Describe(AcState u) => new
    {
        key = u.Key,
        id = u.Id,
        name = u.Name,
        online = u.Online,
        power = u.PowerOn ? "on" : "off",
        mode = u.Mode.ToLowerInvariant(),
        temperature = u.TargetTemperature,
        roomTemperature = u.RoomTemperature,
        fan = u.FanSpeed.ToLowerInvariant(),
        swing = u.FanSwing.ToLowerInvariant(),
        frostWashSupported = u.FrostWashSupported,
        frostWashActive = u.FrostWashActive,
        fault = u.ErrorActive ? u.ErrorCode ?? "unknown" : null,
    };

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        _ => null,
    };
}
