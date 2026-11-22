using System.Globalization;
using System.Text.Json;

namespace Kaze.Core.Weather;

public sealed record WeatherLocation(string Name, double Latitude, double Longitude, string? Country = null, string? Region = null, long Population = 0)
{
    public string Display => string.Join(", ", new[] { Name, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
}

public sealed record WeatherNow(double Temperature, double? Humidity, double? TodayMax, double? TodayMin, DateTimeOffset FetchedAt);

/// <summary>
/// Client for the free Open-Meteo forecast and geocoding APIs (no key needed).
/// Only the chosen location's coordinates are sent.
/// </summary>
public sealed class OpenMeteoClient(HttpClient http)
{
    public const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    public const string GeocodingUrl = "https://geocoding-api.open-meteo.com/v1/search";

    public async Task<WeatherNow> GetCurrentAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{ForecastUrl}?latitude={latitude:0.####}&longitude={longitude:0.####}&current=temperature_2m,relative_humidity_2m&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1");
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return ParseForecast(doc.RootElement, DateTimeOffset.Now) ?? throw new InvalidDataException("Open-Meteo returned no current temperature.");
    }

    public async Task<IReadOnlyList<WeatherLocation>> SearchAsync(string name, string language = "en", CancellationToken ct = default)
    {
        var url = $"{GeocodingUrl}?name={Uri.EscapeDataString(name)}&count=8&language={Uri.EscapeDataString(language)}&format=json";
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return ParseLocations(doc.RootElement);
    }

    public static WeatherNow? ParseForecast(JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("current", out var cur) || !TryNumber(cur, "temperature_2m", out var t))
            return null;
        double? humidity = TryNumber(cur, "relative_humidity_2m", out var h) ? h : null;
        double? max = null, min = null;
        if (root.TryGetProperty("daily", out var daily))
        {
            max = FirstNumber(daily, "temperature_2m_max");
            min = FirstNumber(daily, "temperature_2m_min");
        }
        return new WeatherNow(t, humidity, max, min, now);
    }

    public static IReadOnlyList<WeatherLocation> ParseLocations(JsonElement root)
    {
        var list = new List<WeatherLocation>();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in results.EnumerateArray())
        {
            if (!TryNumber(r, "latitude", out var lat) || !TryNumber(r, "longitude", out var lon)) continue;
            list.Add(new WeatherLocation(
                r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                lat, lon,
                r.TryGetProperty("country", out var c) ? c.GetString() : null,
                r.TryGetProperty("admin1", out var a) ? a.GetString() : null,
                r.TryGetProperty("population", out var pop) && pop.ValueKind == JsonValueKind.Number && pop.TryGetInt64(out var pp) ? pp : 0));
        }
        return list.OrderByDescending(l => l.Population).DistinctBy(l => l.Display).ToList();
    }

    private static bool TryNumber(JsonElement e, string name, out double value)
    {
        value = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out value);
    }

    private static double? FirstNumber(JsonElement e, string name) =>
        e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            && arr[0].ValueKind == JsonValueKind.Number ? arr[0].GetDouble() : null;
}

/// <summary>Weather conditions that can be attached to a schedule.</summary>
public static class WeatherConditions
{
    /// <summary>
    /// True when the outdoor temperature satisfies the limits. When there is a condition but no
    /// weather data, the rule still runs (the schedule is the user's explicit intent).
    /// </summary>
    public static bool Allows(double? onlyIfAbove, double? onlyIfBelow, double? outdoor)
    {
        if (onlyIfAbove is null && onlyIfBelow is null) return true;
        if (outdoor is not { } t) return true;
        if (onlyIfAbove is { } a && t < a) return false;
        if (onlyIfBelow is { } b && t > b) return false;
        return true;
    }
}
