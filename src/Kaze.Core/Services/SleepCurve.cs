using Kaze.Core.Models;

namespace Kaze.Core.Services;

/// <summary>One change during the night, relative to the set point when sleep mode started.</summary>
public sealed record SleepStep
{
    public int AfterMinutes { get; init; }
    /// <summary>Degrees away from the starting set point. Positive means "more relaxed":
    /// warmer when cooling, cooler when heating.</summary>
    public double Delta { get; init; }
}

public sealed record SleepCurveSettings
{
    public List<SleepStep> Steps { get; init; } =
    [
        new() { AfterMinutes = 60, Delta = 1 },
        new() { AfterMinutes = 180, Delta = 2 },
    ];

    /// <summary>Fan speed to switch to when sleep mode starts, or null to keep the current one.</summary>
    public string? FanSpeed { get; init; } = FanSpeeds.Lv1;

    /// <summary>Turn the unit off after this many minutes, or null to leave it running.</summary>
    public int? OffAfterMinutes { get; init; } = 480;
}

public sealed record PlannedStep(TimeSpan After, AcCommand Command, string Description);

/// <summary>Turns a <see cref="SleepCurveSettings"/> into concrete timed commands for one unit.</summary>
public static class SleepCurvePlanner
{
    public static IReadOnlyList<PlannedStep> Plan(AcState unit, SleepCurveSettings settings, AcCapabilities caps)
    {
        var plan = new List<PlannedStep>();
        var modeCaps = caps.For(unit.Mode);
        var hasTemp = AcCapabilities.HasTemperatureRange(modeCaps);
        // Heating relaxes downwards, everything else upwards.
        var sign = unit.Mode == AcModes.Heating ? -1 : 1;

        var start = new AcCommand { PowerOn = true };
        if (settings.FanSpeed is { } fan && modeCaps.FanEnabled && modeCaps.FanSpeeds.Contains(fan))
            start = start with { FanSpeed = fan };
        plan.Add(new PlannedStep(TimeSpan.Zero, start, "Sleep mode on"));

        if (hasTemp)
        {
            foreach (var step in settings.Steps.Where(s => s.AfterMinutes > 0).OrderBy(s => s.AfterMinutes))
            {
                var t = caps.Clamp(unit.Mode, RoundHalf(unit.TargetTemperature + sign * step.Delta));
                plan.Add(new PlannedStep(TimeSpan.FromMinutes(step.AfterMinutes), AcCommand.SetTemperature(t), $"{t:0.#}°"));
            }
        }

        if (settings.OffAfterMinutes is { } off && off > 0)
        {
            plan.RemoveAll(p => p.After >= TimeSpan.FromMinutes(off) && p.After > TimeSpan.Zero);
            plan.Add(new PlannedStep(TimeSpan.FromMinutes(off), AcCommand.Power(false), "Off"));
        }
        return plan;
    }

    private static double RoundHalf(double t) => Math.Round(t * 2, MidpointRounding.AwayFromZero) / 2;
}
