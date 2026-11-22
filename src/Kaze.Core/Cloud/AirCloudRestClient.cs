using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kaze.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kaze.Core.Cloud;

/// <summary>Authenticated REST calls to the airCloud API. Retries once with a renewed token on 401/403.</summary>
public sealed class AirCloudRestClient(
    HttpClient http,
    AirCloudOptions options,
    AirCloudAuth auth,
    ILogger<AirCloudRestClient>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<AirCloudRestClient>.Instance;

    public const string FamiliesPath = "rac/location-controls/status";
    public const string ConfigurationPath = "rac/model-wise/rac-configuration";
    public const string ControlPath = "rac/basic-idu-control/general-control-command-status";
    public const string EnergySummaryPath = "rac/energy-consumptions/summary/v3";
    public const string EnergyAllRacsPath = "rac/energy-consumptions/all/racs";

    public async Task<IReadOnlyList<string>> GetFamilyIdsAsync(CancellationToken ct = default)
    {
        var json = await SendRawAsync(HttpMethod.Get, FamiliesPath, null, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return AirCloudJson.ParseFamilyIds(doc.RootElement);
    }

    /// <summary>Raw rac-configuration JSON for the given cloud ids.</summary>
    public Task<string> GetConfigurationRawAsync(IEnumerable<string> cloudIds, CancellationToken ct = default) =>
        SendRawAsync(HttpMethod.Post, ConfigurationPath, new JsonArray([.. cloudIds.Select(id => (JsonNode?)id)]), ct);

    public async Task SendCommandAsync(AcState current, AcCommand command, CancellationToken ct = default)
    {
        var payload = AirCloudJson.BuildCommandPayload(current, command);
        var path = $"{ControlPath}/{Uri.EscapeDataString(current.Id)}?familyId={Uri.EscapeDataString(current.FamilyId)}";
        _log.LogInformation("Command {Unit}: {Command}", current.Name, command);
        await SendRawAsync(HttpMethod.Put, path, payload, ct).ConfigureAwait(false);
    }

    public const string FrostWashPath = "rac/clean/idu-frost-wash";

    public async Task StartFrostWashAsync(AcState unit, CancellationToken ct = default)
    {
        var path = $"{FrostWashPath}/{Uri.EscapeDataString(unit.Id)}?familyId={Uri.EscapeDataString(unit.FamilyId)}";
        _log.LogInformation("Frost Wash start: {Unit}", unit.Name);
        await SendRawAsync(HttpMethod.Put, path, AirCloudJson.BuildFrostWashPayload(unit), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AirCloudJson.EnergyRac>> GetEnergyRacsAsync(string familyId, CancellationToken ct = default)
    {
        var raw = await GetEnergyAllRacsRawAsync(familyId, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        return AirCloudJson.ParseEnergyRacs(doc.RootElement);
    }

    public Task<string> GetEnergySummaryRawAsync(string familyId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        SendRawAsync(HttpMethod.Post, $"{EnergySummaryPath}?familyId={Uri.EscapeDataString(familyId)}",
            new JsonObject
            {
                ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }, ct);

    public Task<string> GetEnergyAllRacsRawAsync(string familyId, CancellationToken ct = default) =>
        SendRawAsync(HttpMethod.Get, $"{EnergyAllRacsPath}?familyId={Uri.EscapeDataString(familyId)}", null, ct);

    public async Task<string> SendRawAsync(HttpMethod method, string relativePath, JsonNode? body, CancellationToken ct = default)
    {
        var token = await auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, new Uri(options.ApiBase, relativePath));
            req.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                req.Content = new StringContent(body.ToJsonString(JsonDefaults.Wire), Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
                return text;

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt == 0)
            {
                _log.LogInformation("{Method} {Path} returned {Status}, renewing token", method, relativePath, (int)resp.StatusCode);
                token = await auth.RenewAfterRejectionAsync(token, ct).ConfigureAwait(false);
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = resp.Headers.RetryAfter?.Delta
                    ?? (resp.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
                throw new AirCloudException(AirCloudErrorKind.RateLimited, "airCloud rate limit reached.")
                { RetryAfter = retry, HttpStatus = 429, ResponseBody = text };
            }

            var kind = resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? AirCloudErrorKind.Unauthorized : AirCloudErrorKind.Http;
            throw new AirCloudException(kind, $"airCloud {method} {relativePath.Split('?')[0]} failed: HTTP {(int)resp.StatusCode}")
            { HttpStatus = (int)resp.StatusCode, ResponseBody = text.Length > 500 ? text[..500] : text };
        }
    }
}
