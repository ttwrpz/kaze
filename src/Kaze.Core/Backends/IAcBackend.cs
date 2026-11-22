using Kaze.Core.Models;

namespace Kaze.Core.Backends;

public enum BackendStatus
{
    Stopped,
    Connecting,
    Connected,
    Reconnecting,
    AuthFailed,
}

/// <summary>
/// A way of talking to the air conditioners. <c>AirCloudBackend</c> is the cloud implementation.
/// A local backend would implement the same contract.
/// </summary>
public interface IAcBackend : IAsyncDisposable
{
    string Name { get; }
    BackendStatus Status { get; }

    /// <summary>Raised (on a background thread) with every unit the backend reported in one update.</summary>
    event Action<IReadOnlyList<AcState>>? UnitsReported;

    /// <summary>Raised on status change. The string is a human readable detail (e.g. last error).</summary>
    event Action<BackendStatus, string?>? StatusChanged;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();

    void RequestRefresh();

    /// <summary>Applies <paramref name="command"/> on top of <paramref name="current"/> and sends the full state.</summary>
    Task SendAsync(AcState current, AcCommand command, CancellationToken ct = default);

    Task<AcCapabilities> GetCapabilitiesAsync(AcState unit, CancellationToken ct = default);

    /// <summary>Energy usage for a family between two dates, or null if unsupported.</summary>
    Task<EnergyReport?> GetEnergyAsync(string familyId, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>Starts a Frost Wash (indoor coil self cleaning) cycle.</summary>
    Task StartFrostWashAsync(AcState unit, CancellationToken ct = default);

    /// <summary>Units of a family that support energy tracking but have it switched off.</summary>
    Task<IReadOnlyList<string>> GetEnergyTrackingOffAsync(string familyId, CancellationToken ct = default);
}
