namespace Kaze.Core.Models;

public sealed record UnitEnergy(string? VendorThingId, string Name, double? KWh, double? Cost);

public sealed record EnergyReport
{
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public string? Currency { get; init; }
    public IReadOnlyList<UnitEnergy> Units { get; init; } = [];
    /// <summary>Raw JSON for diagnostics.</summary>
    public string? Raw { get; init; }

    public double? TotalKWh => Units.Any(u => u.KWh is not null) ? Units.Sum(u => u.KWh ?? 0) : null;
}
