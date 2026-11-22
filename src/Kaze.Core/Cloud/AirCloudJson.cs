using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kaze.Core.Models;

namespace Kaze.Core.Cloud;

/// <summary>Lenient mapping between airCloud JSON and the app's models.</summary>
public static class AirCloudJson
{
    /// <summary>Parses the "data" array of a notification message (or a bare array) into unit states.</summary>
    public static IReadOnlyList<AcState> ParseUnits(JsonElement root, string familyId)
    {
        var arr = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array => d,
            _ => default,
        };
        if (arr.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<AcState>();
        foreach (var e in arr.EnumerateArray())
            if (ParseUnit(e, familyId) is { } s)
                list.Add(s);
        return list;
    }

    public static AcState? ParseUnit(JsonElement e, string familyId)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        var id = Text(e, "id") ?? Text(e, "racId");
        if (id is null)
            return null;

        var online = Bool(e, "online") ?? Bool(e, "isOnline") ?? true;
        var power = Text(e, "power");

        return new AcState
        {
            Id = id,
            FamilyId = familyId,
            Name = Text(e, "name") is { Length: > 0 } n ? n : "Air conditioner",
            CloudId = Text(e, "cloudId"),
            VendorThingId = Text(e, "vendorThingId"),
            Online = online,
            PowerOn = string.Equals(power, "ON", StringComparison.OrdinalIgnoreCase),
            Mode = Text(e, "mode")?.ToUpperInvariant() ?? AcModes.Cooling,
            FanSpeed = Text(e, "fanSpeed")?.ToUpperInvariant() ?? FanSpeeds.Auto,
            FanSwing = Text(e, "fanSwing")?.ToUpperInvariant() ?? FanSwings.Off,
            TargetTemperature = Number(e, "iduTemperature") ?? 25,
            RelativeTemperature = Number(e, "relativeTemperature") ?? 0,
            RoomTemperature = ValidRoomTemp(Number(e, "roomTemperature")),
            HumiditySetting = (long)(Number(e, "humidity") ?? AcState.NoHumidityValue),
            FrostWashSupported = (Bool(e, "iduFrostWash") ?? false) || e.TryGetProperty("iduFrostWashStatus", out _),
            FrostWashActive = StatusActive(e, "iduFrostWashStatus"),
            ErrorActive = StatusActive(e, "errorStatus"),
            ErrorCode = ErrorCodeOf(e),
            RawJson = e.GetRawText(),
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    private static double? ValidRoomTemp(double? t) => t is { } v && v > -50 && v < 80 ? v : null;

    /// <summary>Reads objects shaped like {"active": true, ...} (iduFrostWashStatus, errorStatus).</summary>
    private static bool StatusActive(JsonElement e, string name) =>
        e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.Object && (Bool(s, "active") ?? false);

    private static string? ErrorCodeOf(JsonElement e)
    {
        if (!StatusActive(e, "errorStatus") || !e.TryGetProperty("errorStatus", out var s)) return null;
        var code = Text(s, "errorCode");
        var category = Text(s, "subCategory");
        return code ?? (category is null or "NO_ERROR" ? null : category);
    }

    /// <summary>
    /// Body for PUT rac/clean/idu-frost-wash/{id}. The Android app sends the whole device snapshot back.
    /// </summary>
    public static JsonNode BuildFrostWashPayload(AcState unit) =>
        (unit.RawJson is { Length: > 0 } raw ? JsonNode.Parse(raw) : null) ?? new JsonObject();

    public sealed record EnergyRac(string RacId, string Name, bool Selected, bool Supported);

    /// <summary>Parses rac/energy-consumptions/all/racs ("allRacList").</summary>
    public static IReadOnlyList<EnergyRac> ParseEnergyRacs(JsonElement root)
    {
        var list = new List<EnergyRac>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("allRacList", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var r in arr.EnumerateArray())
                if (Text(r, "racId") is { } id)
                    list.Add(new EnergyRac(id, Text(r, "racName") ?? "Air conditioner",
                        Bool(r, "isSelected") ?? false, Bool(r, "isEnergyConsumptionSupported") ?? false));
        return list;
    }

    /// <summary>Full-state body for PUT rac/basic-idu-control/general-control-command-status/{id}.</summary>
    public static JsonObject BuildCommandPayload(AcState current, AcCommand command)
    {
        var s = command.ApplyTo(current);
        var humidity = s.HumiditySetting >= AcState.NoHumidityValue ? "0" : s.HumiditySetting.ToString(CultureInfo.InvariantCulture);
        var relative = string.Equals(s.Mode, AcModes.Auto, StringComparison.OrdinalIgnoreCase) ? s.RelativeTemperature : 0.0;
        return new JsonObject
        {
            ["power"] = s.PowerOn ? "ON" : "OFF",
            ["mode"] = s.Mode,
            ["fanSpeed"] = s.FanSpeed,
            ["fanSwing"] = s.FanSwing,
            ["humidity"] = humidity,
            ["iduTemperature"] = s.TargetTemperature,
            ["relativeTemperature"] = relative,
        };
    }

    public static IReadOnlyList<string> ParseFamilyIds(JsonElement root)
    {
        var ids = new List<string>();
        void Add(JsonElement item)
        {
            if (Text(item, "familyId") is { } id && !ids.Contains(id))
                ids.Add(id);
        }
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray()) Add(item);
        else if (root.ValueKind == JsonValueKind.Object)
            Add(root);
        return ids;
    }

    /// <summary>Parses one entry of the rac-configuration response.</summary>
    public static AcCapabilities ParseCapabilities(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
            return AcCapabilities.Default;

        var modes = new Dictionary<string, ModeCapabilities>(StringComparer.OrdinalIgnoreCase);
        if (config.TryGetProperty("racOperationModes", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                var mode = Text(m, "mode")?.ToUpperInvariant();
                if (string.IsNullOrEmpty(mode))
                    continue;
                var enable = m.TryGetProperty("enableSettings", out var es) ? es : default;
                var fans = TruthyKeys(m, "enableFanSpeed").OrderBy(FanSpeeds.SortKey).ToList();
                var defFan = TruthyKeys(m, "defaultFanSpeed").OrderBy(FanSpeeds.SortKey).FirstOrDefault() ?? fans.FirstOrDefault();
                modes[mode] = new ModeCapabilities
                {
                    Mode = mode,
                    RelativeTemperature = string.Equals(Text(m, "temperatureSetting"), "RELATIVE", StringComparison.OrdinalIgnoreCase),
                    MinTemperature = Number(m, "minTemperature") ?? 16,
                    MaxTemperature = Number(m, "maxTemperature") ?? 32,
                    DefaultTemperature = Number(m, "defaultTemperature") ?? 24,
                    TemperatureEnabled = Bool(enable, "temperature") ?? true,
                    FanEnabled = Bool(enable, "fan") ?? true,
                    SwingEnabled = Bool(enable, "swing") ?? true,
                    FanSpeeds = fans.Count > 0 ? fans : AcCapabilities.Default.For(mode).FanSpeeds,
                    DefaultFanSpeed = defFan,
                };
            }
        }

        var swings = new List<string> { FanSwings.Off };
        if (config.TryGetProperty("swing", out var sw))
        {
            var v = Bool(sw, "VERTICAL") ?? false;
            var h = Bool(sw, "HORIZONTAL") ?? false;
            if (v) swings.Add(FanSwings.Vertical);
            if (h) swings.Add(FanSwings.Horizontal);
            if (v && h) swings.Add(FanSwings.Both);
        }
        else
        {
            swings.Add(FanSwings.Vertical);
        }

        return new AcCapabilities
        {
            Modes = modes.Count > 0 ? modes : AcCapabilities.Default.Modes,
            Swings = swings,
        };
    }

    /// <summary>Finds the configuration entry for <paramref name="cloudId"/> in the rac-configuration response.</summary>
    public static JsonElement? FindConfiguration(JsonElement root, string? cloudId)
    {
        IEnumerable<JsonElement> items = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray(),
            JsonValueKind.Object when root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array => d.EnumerateArray(),
            JsonValueKind.Object => [root],
            _ => [],
        };
        JsonElement? first = null;
        foreach (var item in items)
        {
            first ??= item;
            if (cloudId is not null && Text(item, "cloudId") == cloudId)
                return item;
        }
        return first;
    }

    public static EnergyReport ParseEnergySummary(JsonElement root, DateOnly from, DateOnly to)
    {
        var units = new List<UnitEnergy>();
        string? currency = null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("individualRacsData", out var racs) && racs.ValueKind == JsonValueKind.Array)
                foreach (var r in racs.EnumerateArray())
                    units.Add(new UnitEnergy(
                        Text(r, "vendorThingId"),
                        Text(r, "racName") ?? "Air conditioner",
                        Number(r, "energyConsumed"),
                        Number(r, "cost")));
            if (root.TryGetProperty("allRacsData", out var all) && all.ValueKind == JsonValueKind.Object)
                currency = Text(all, "currency");
        }
        return new EnergyReport { From = from, To = to, Currency = currency, Units = units, Raw = root.GetRawText() };
    }

    internal static string? Text(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    internal static double? Number(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    internal static bool? Bool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        return Truthy(v);
    }

    private static bool? Truthy(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.String => v.GetString() is { } s && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("ON", StringComparison.OrdinalIgnoreCase)),
        JsonValueKind.Null => false,
        JsonValueKind.Object or JsonValueKind.Array => true,
        _ => null,
    };

    private static IEnumerable<string> TruthyKeys(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var obj))
            yield break;
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
                if (Truthy(p.Value) == true)
                    yield return p.Name.ToUpperInvariant();
        }
        else if (obj.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in obj.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    yield return s.ToUpperInvariant();
        }
        else if (obj.ValueKind == JsonValueKind.String && obj.GetString() is { } single)
        {
            yield return single.ToUpperInvariant();
        }
    }
}
