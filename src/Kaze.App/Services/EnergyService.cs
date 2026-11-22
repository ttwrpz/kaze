using System.Text.Json;
using Kaze.App.Helpers;
using Kaze.Core.Backends;
using Kaze.Core.Cloud;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Microsoft.Extensions.Logging;

namespace Kaze.App.Services;

public sealed record MonthEnergy(DateOnly Month, EnergyReport? Report)
{
    public string Label => Month.ToString("MMM yy");
}

public sealed record EnergySource(DateTimeOffset? SavedAt, bool FromSaved, DateTimeOffset BlockedUntil);

/// <summary>
/// Loads energy summaries sparingly, because airCloud rate limits them. Figures are saved to disk:
/// a finished month never changes and is kept for good, the current month is refreshed at most every
/// <see cref="CurrentMonthTtl"/>, and a manual refresh at most every <see cref="ManualRefreshGap"/>.
/// A rate limit is remembered across restarts, and saved figures are shown in the meantime.
/// </summary>
public sealed class EnergyService(IAcBackend backend, AcController controller, ILogger<EnergyService> log)
{
    public static readonly TimeSpan CurrentMonthTtl = TimeSpan.FromHours(6);
    public static readonly TimeSpan ManualRefreshGap = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TrackingTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultBlock = TimeSpan.FromMinutes(30);
    private static string FilePath => Path.Combine(SettingsStore.DataDirectory, "energy-cache.json");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Cache? _cache;

    public DateTimeOffset BlockedUntil => Load().BlockedUntil;

    public IReadOnlyList<string> FamilyIds => controller.Units.Select(u => u.FamilyId).Distinct().ToList();

    /// <summary>Names of units that support energy tracking but have it switched off in airCloud Go.</summary>
    public async Task<IReadOnlyList<string>> GetTrackingOffNamesAsync(string familyId, CancellationToken ct = default)
    {
        var cache = Load();
        IReadOnlyList<string> ids;
        if (cache.Tracking.TryGetValue(familyId, out var saved) && DateTimeOffset.Now - saved.At < TrackingTtl)
            ids = saved.Ids;
        else
        {
            try
            {
                ids = await backend.GetEnergyTrackingOffAsync(familyId, ct);
                cache.Tracking[familyId] = new TrackingEntry(DateTimeOffset.Now, [.. ids]);
                Save();
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Energy tracking status unavailable");
                ids = saved?.Ids ?? [];
            }
        }
        return ids.Select(id => controller.Units.FirstOrDefault(u => u.FamilyId == familyId && u.Id == id)?.Name ?? L.F("Unit {0}", id)).ToList();
    }

    /// <summary>Energy for one month to date. Uses saved figures whenever they are recent enough.</summary>
    public async Task<(EnergyReport? Report, DateTimeOffset? SavedAt, bool FromSaved)> GetMonthAsync(string familyId, DateOnly month, bool force = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var cache = Load();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var start = new DateOnly(month.Year, month.Month, 1);
            var end = start.AddMonths(1).AddDays(-1);
            if (end > today) end = today;
            var key = $"{familyId}|{start:yyyy-MM}";
            cache.Months.TryGetValue(key, out var saved);

            if (saved is not null)
            {
                var finished = DateOnly.FromDateTime(saved.At.LocalDateTime) > start.AddMonths(1).AddDays(-1);
                var age = DateTimeOffset.Now - saved.At;
                if (finished || age < (force ? ManualRefreshGap : CurrentMonthTtl))
                    return (saved.ToReport(), saved.At, true);
            }
            if (DateTimeOffset.Now < cache.BlockedUntil)
            {
                if (saved is not null) return (saved.ToReport(), saved.At, true);
                throw new AirCloudException(AirCloudErrorKind.RateLimited, L.F("airCloud asked us to wait until {0:HH:mm}.", cache.BlockedUntil.LocalDateTime));
            }

            try
            {
                var report = await backend.GetEnergyAsync(familyId, start, end, ct);
                var now = DateTimeOffset.Now;
                cache.Months[key] = MonthEntry.Create(report, start, end, now);
                Save();
                return (report, now, false);
            }
            catch (AirCloudException ex) when (ex.Kind == AirCloudErrorKind.RateLimited)
            {
                cache.BlockedUntil = DateTimeOffset.Now + (ex.RetryAfter is { } r && r > TimeSpan.Zero ? r : DefaultBlock);
                Save();
                log.LogWarning("Energy API rate limited until {Until}", cache.BlockedUntil);
                if (saved is not null) return (saved.ToReport(), saved.At, true);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Energy for the current month to date (used by the budget reminder).</summary>
    public async Task<EnergyReport?> GetCurrentMonthAsync(string familyId, CancellationToken ct = default) =>
        (await GetMonthAsync(familyId, DateOnly.FromDateTime(DateTime.Today), false, ct)).Report;

    /// <summary>
    /// The last <paramref name="count"/> calendar months, oldest first. Only the current month is
    /// refreshed by <paramref name="force"/>. A month that can't be loaded is returned without figures.
    /// </summary>
    public async Task<(IReadOnlyList<MonthEnergy> Months, EnergySource Source)> GetMonthsAsync(string familyId, int count, bool force = false, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var first = new DateOnly(today.Year, today.Month, 1);
        var result = new List<MonthEnergy>();
        DateTimeOffset? currentSavedAt = null;
        var currentFromSaved = false;

        // Current month first: it matters most, and a rate limit then stops the older requests.
        var (report, savedAt, fromSaved) = await GetMonthAsync(familyId, first, force, ct);
        currentSavedAt = savedAt;
        currentFromSaved = fromSaved;
        for (var i = count - 1; i >= 1; i--)
        {
            EnergyReport? past = null;
            try { past = (await GetMonthAsync(familyId, first.AddMonths(-i), false, ct)).Report; }
            catch (AirCloudException ex) when (ex.Kind == AirCloudErrorKind.RateLimited) { }
            result.Add(new MonthEnergy(first.AddMonths(-i), past));
        }
        result.Add(new MonthEnergy(first, report));
        return (result, new EnergySource(currentSavedAt, currentFromSaved, Load().BlockedUntil));
    }

    private Cache Load()
    {
        if (_cache is not null) return _cache;
        try
        {
            if (File.Exists(FilePath))
                _cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(FilePath));
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Energy cache unreadable, starting fresh");
        }
        return _cache ??= new Cache();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not save the energy cache");
        }
    }

    private sealed class Cache
    {
        public DateTimeOffset BlockedUntil { get; set; } = DateTimeOffset.MinValue;
        public Dictionary<string, MonthEntry> Months { get; set; } = [];
        public Dictionary<string, TrackingEntry> Tracking { get; set; } = [];
    }

    private sealed record TrackingEntry(DateTimeOffset At, List<string> Ids);

    /// <summary>A saved month. <c>Empty</c> means airCloud answered with no data, which is also worth remembering.</summary>
    private sealed record MonthEntry(DateTimeOffset At, DateOnly From, DateOnly To, bool Empty, string? Currency, List<UnitEnergy> Units)
    {
        public static MonthEntry Create(EnergyReport? r, DateOnly from, DateOnly to, DateTimeOffset at) =>
            new(at, from, to, r is null, r?.Currency, r is null ? [] : [.. r.Units]);

        public EnergyReport? ToReport() => Empty ? null : new EnergyReport { From = From, To = To, Currency = Currency, Units = Units };
    }
}
