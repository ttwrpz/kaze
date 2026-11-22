using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kaze.Core.Cloud;

/// <summary>
/// Holds airCloud access/refresh tokens. Token refresh is serialized. Tokens live only in memory.
/// </summary>
public sealed partial class AirCloudAuth(
    HttpClient http,
    AirCloudOptions options,
    Func<AirCloudCredentials?> credentials,
    TimeProvider? time = null,
    ILogger<AirCloudAuth>? logger = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _log = logger ?? NullLogger<AirCloudAuth>.Instance;

    private string? _token;
    private string? _refreshToken;
    private DateTimeOffset _issuedAt;

    public bool HasToken => _token is not null;

    /// <summary>Returns a usable access token, refreshing or signing in as needed.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var t = _token;
        if (t is not null && _time.GetUtcNow() - _issuedAt < options.TokenLifetime)
            return t;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && _time.GetUtcNow() - _issuedAt < options.TokenLifetime)
                return _token;
            await RenewLockedAsync(ct).ConfigureAwait(false);
            return _token!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called when the server rejected <paramref name="rejectedToken"/>. Renews unless someone already did.</summary>
    public async Task<string> RenewAfterRejectionAsync(string rejectedToken, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && _token != rejectedToken)
                return _token;
            // A rejected access token: refresh first. If that fails, full sign-in.
            await RenewLockedAsync(ct).ConfigureAwait(false);
            return _token!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forces a full sign-in (e.g. to validate new credentials).</summary>
    public async Task SignInAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SignInLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Reset()
    {
        _token = null;
        _refreshToken = null;
    }

    private async Task RenewLockedAsync(CancellationToken ct)
    {
        if (_refreshToken is not null)
        {
            try
            {
                await RefreshLockedAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogInformation("Token refresh failed ({Message}), signing in again", ex.Message);
            }
        }
        await SignInLockedAsync(ct).ConfigureAwait(false);
    }

    private async Task SignInLockedAsync(CancellationToken ct)
    {
        var creds = credentials() ?? throw new AirCloudException(AirCloudErrorKind.InvalidCredentials, "No airCloud credentials configured.");

        var body = new JsonObject { ["password"] = creds.Password };
        if (creds.IsEmail)
            body["email"] = creds.Login.Trim();
        else
            body["mobileNumber"] = NonPhoneChars().Replace(creds.Login, "");

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(options.ApiBase, "iam/auth/sign-in"))
        {
            Content = new StringContent(body.ToJsonString(JsonDefaults.Wire), Encoding.UTF8, "application/json"),
        };
        AddCommonHeaders(req);

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw MapSignInError(resp.StatusCode, text);

        StoreTokens(text);
        _log.LogInformation("Signed in to airCloud");
    }

    private async Task RefreshLockedAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(options.ApiBase, "iam/auth/refresh-token"))
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        };
        AddCommonHeaders(req);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _refreshToken);
        req.Headers.TryAddWithoutValidation("isRefreshToken", "true");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AirCloudException(AirCloudErrorKind.Unauthorized, $"Refresh failed: HTTP {(int)resp.StatusCode}")
            { HttpStatus = (int)resp.StatusCode, ResponseBody = text };

        StoreTokens(text);
        _log.LogDebug("Refreshed airCloud token");
    }

    /// <summary>Validates both tokens before changing either stored value.</summary>
    private void StoreTokens(string json)
    {
        string? token = null, refresh = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                token = Str(doc.RootElement, "token") ?? Str(doc.RootElement, "accessToken");
                refresh = Str(doc.RootElement, "refreshToken");
            }
        }
        catch (JsonException) { }

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(refresh))
            // Never attach the body here: it may hold one valid token.
            throw new AirCloudException(AirCloudErrorKind.Protocol,
                $"airCloud returned no token (fields: {TopLevelKeys(json)}). The API may have changed.");

        _token = token;
        _refreshToken = refresh;
        _issuedAt = _time.GetUtcNow();

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    internal static AirCloudException MapSignInError(HttpStatusCode status, string body)
    {
        string? state = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "errorState", "message", "error" })
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        state = v.GetString();
                        break;
                    }
            }
        }
        catch (JsonException) { }

        var s = state ?? "";
        var (kind, msg) = s switch
        {
            _ when s.Contains("LOCK", StringComparison.OrdinalIgnoreCase) => (AirCloudErrorKind.AccountLocked, "The airCloud account is locked. Unlock it in the airCloud Go app."),
            _ when s.Equals("USER_NOT_VERIFIED", StringComparison.OrdinalIgnoreCase) => (AirCloudErrorKind.UserNotVerified, "The airCloud account is not verified yet."),
            _ when s.Contains("UNKNOWN_USER", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Invalid UserId Or Password", StringComparison.OrdinalIgnoreCase)
                || status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                => (AirCloudErrorKind.InvalidCredentials,
                    $"airCloud rejected the login (HTTP {(int)status}{(state is null ? "" : $", {state}")}). Check the email/phone and password. " +
                    "Phone numbers must include the country code, e.g. +66812345678. Accounts created with Google/Apple/LINE sign-in have no airCloud password."),
            _ when status == HttpStatusCode.TooManyRequests => (AirCloudErrorKind.RateLimited, "Too many sign-in attempts. Try again later."),
            _ => (AirCloudErrorKind.Http, $"Sign-in failed: HTTP {(int)status} {state}".Trim()),
        };
        return new AirCloudException(kind, msg) { HttpStatus = (int)status, ErrorState = state, ResponseBody = body };
    }

    private void AddCommonHeaders(HttpRequestMessage req)
    {
        req.Headers.UserAgent.Clear();
        req.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static string TopLevelKeys(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name))
                : doc.RootElement.ValueKind.ToString();
        }
        catch (JsonException) { return "not JSON"; }
    }

    [GeneratedRegex(@"[^\d+]")]
    private static partial Regex NonPhoneChars();
}
