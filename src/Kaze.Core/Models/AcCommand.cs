using System.Text.Json.Serialization;

namespace Kaze.Core.Models;

/// <summary>A partial change. Null fields keep the unit's current value.</summary>
public sealed record AcCommand
{
    public bool? PowerOn { get; init; }
    public string? Mode { get; init; }
    public string? FanSpeed { get; init; }
    public string? FanSwing { get; init; }
    public double? TargetTemperature { get; init; }

    public static AcCommand Power(bool on) => new() { PowerOn = on };
    public static AcCommand SetMode(string mode) => new() { Mode = mode };
    public static AcCommand SetFan(string fan) => new() { FanSpeed = fan };
    public static AcCommand SetSwing(string swing) => new() { FanSwing = swing };
    public static AcCommand SetTemperature(double t) => new() { TargetTemperature = t };

    [JsonIgnore]
    public bool IsEmpty => PowerOn is null && Mode is null && FanSpeed is null && FanSwing is null && TargetTemperature is null;

    /// <summary>Later command wins field-by-field.</summary>
    public AcCommand Then(AcCommand next) => new()
    {
        PowerOn = next.PowerOn ?? PowerOn,
        Mode = next.Mode ?? Mode,
        FanSpeed = next.FanSpeed ?? FanSpeed,
        FanSwing = next.FanSwing ?? FanSwing,
        TargetTemperature = next.TargetTemperature ?? TargetTemperature,
    };

    public AcState ApplyTo(AcState s) => s with
    {
        PowerOn = PowerOn ?? s.PowerOn,
        Mode = Mode ?? s.Mode,
        FanSpeed = FanSpeed ?? s.FanSpeed,
        FanSwing = FanSwing ?? s.FanSwing,
        TargetTemperature = TargetTemperature ?? s.TargetTemperature,
        UpdatedAt = DateTimeOffset.Now,
    };

    public override string ToString()
    {
        var parts = new List<string>();
        if (PowerOn is { } p) parts.Add(CoreText.T(p ? "power on" : "power off"));
        if (Mode is not null) parts.Add(CoreText.F("mode {0}", AcModes.Display(Mode)));
        if (TargetTemperature is { } t) parts.Add($"{t:0.#}°");
        if (FanSpeed is not null) parts.Add(CoreText.F("fan {0}", FanSpeeds.Display(FanSpeed)));
        if (FanSwing is not null) parts.Add(CoreText.F("swing {0}", FanSwings.Display(FanSwing)));
        return parts.Count == 0 ? CoreText.T("no change") : string.Join(", ", parts);
    }
}
