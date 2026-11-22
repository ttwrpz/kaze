namespace Kaze.Core.Models;

public sealed record ModeCapabilities
{
    public required string Mode { get; init; }
    public bool RelativeTemperature { get; init; }
    public double MinTemperature { get; init; } = 16;
    public double MaxTemperature { get; init; } = 32;
    public double DefaultTemperature { get; init; } = 24;
    public bool TemperatureEnabled { get; init; } = true;
    public bool FanEnabled { get; init; } = true;
    public bool SwingEnabled { get; init; } = true;
    public IReadOnlyList<string> FanSpeeds { get; init; } = Models.FanSpeeds.All.Take(6).ToList();
    public string? DefaultFanSpeed { get; init; }
}

/// <summary>What a unit model supports, from airCloud's rac-configuration (or defaults).</summary>
public sealed record AcCapabilities
{
    public IReadOnlyDictionary<string, ModeCapabilities> Modes { get; init; } = new Dictionary<string, ModeCapabilities>();
    public IReadOnlyList<string> Swings { get; init; } = [FanSwings.Off, FanSwings.Vertical];

    public static AcCapabilities Default { get; } = new()
    {
        Modes = AcModes.All.ToDictionary(m => m, m => new ModeCapabilities
        {
            Mode = m,
            TemperatureEnabled = m != AcModes.Fan,
            FanSpeeds = m == AcModes.Dry ? [FanSpeeds.Auto] : [FanSpeeds.Auto, FanSpeeds.Lv1, FanSpeeds.Lv2, FanSpeeds.Lv3, FanSpeeds.Lv4, FanSpeeds.Lv5],
        }),
        Swings = [FanSwings.Off, FanSwings.Vertical],
    };

    public IReadOnlyList<string> SupportedModes => Modes.Count == 0 ? AcModes.All : [.. Modes.Keys.OrderBy(AcModes.SortKey)];

    public ModeCapabilities For(string mode) =>
        Modes.TryGetValue(mode.ToUpperInvariant(), out var c) ? c
        : Default.Modes.TryGetValue(mode.ToUpperInvariant(), out var d) ? d
        : new ModeCapabilities { Mode = mode };

    /// <summary>Clamps to the range of the mode. Modes without a temperature (FAN reports 0 to 0) leave it unchanged.</summary>
    public double Clamp(string mode, double t)
    {
        var c = For(mode);
        return HasTemperatureRange(c) ? Math.Clamp(t, c.MinTemperature, c.MaxTemperature) : t;
    }

    public static bool HasTemperatureRange(ModeCapabilities c) => c.TemperatureEnabled && c.MaxTemperature > c.MinTemperature;
}
