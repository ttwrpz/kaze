namespace Kaze.Core.Models;

/// <summary>Snapshot of one indoor unit as last reported by the backend.</summary>
public sealed record AcState
{
    /// <summary>Backend unit id (airCloud "id"), as a string.</summary>
    public required string Id { get; init; }
    public required string FamilyId { get; init; }
    public string Name { get; init; } = "Air conditioner";
    public string? CloudId { get; init; }
    public string? VendorThingId { get; init; }
    public bool Online { get; init; } = true;

    public bool PowerOn { get; init; }
    public string Mode { get; init; } = AcModes.Cooling;
    public string FanSpeed { get; init; } = FanSpeeds.Auto;
    public string FanSwing { get; init; } = FanSwings.Off;

    /// <summary>Set point ("iduTemperature").</summary>
    public double TargetTemperature { get; init; } = 25;
    /// <summary>Offset set point used in AUTO mode on some models.</summary>
    public double RelativeTemperature { get; init; }
    /// <summary>Indoor sensor reading, if reported.</summary>
    public double? RoomTemperature { get; init; }
    /// <summary>Raw "humidity" setting. 2147483647 means "not set".</summary>
    public long HumiditySetting { get; init; } = NoHumidityValue;

    /// <summary>The unit reports Frost Wash (indoor coil self cleaning).</summary>
    public bool FrostWashSupported { get; init; }
    public bool FrostWashActive { get; init; }

    public bool ErrorActive { get; init; }
    /// <summary>Fault code or category reported by the unit, if any.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The device JSON exactly as the cloud sent it. Some commands (Frost Wash) send it back.</summary>
    public string? RawJson { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public const long NoHumidityValue = 2_147_483_647;

    public string Key => $"{FamilyId}/{Id}";

    public string Summary =>
        !Online ? CoreText.F("{0}: offline", Name)
        : FrostWashActive ? CoreText.F("{0}: Frost Wash running", Name)
        : !PowerOn ? CoreText.F("{0}: off", Name) + (RoomTemperature is { } r0 ? CoreText.F(" · Room {0:0.#}°", r0) : "")
        : $"{Name}: {AcModes.Display(Mode)} {TargetTemperature:0.#}° · " + CoreText.F("Fan {0}", FanSpeeds.Display(FanSpeed))
          + (RoomTemperature is { } r ? CoreText.F(" · Room {0:0.#}°", r) : "");
}
