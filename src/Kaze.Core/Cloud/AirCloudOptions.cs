namespace Kaze.Core.Cloud;

public sealed class AirCloudOptions
{
    public Uri ApiBase { get; set; } = new("https://api-global-prod.aircloudhome.com/");
    public Uri NotificationSocket { get; set; } = new("wss://notification-global-prod.aircloudhome.com/rac-notifications/websocket");

    /// <summary>The official Android app uses okhttp. The backend has been seen to care.</summary>
    public string UserAgent { get; set; } = "okhttp/4.9.1";

    /// <summary>Access tokens are refreshed after this age (the server does not report expiry).</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>If no state message arrived for this long, reconnect to force a fresh snapshot.</summary>
    public TimeSpan ResyncInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StompHeartbeat { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed record AirCloudCredentials(string Login, string Password)
{
    public bool IsEmail => Login.Contains('@');
}

public enum AirCloudErrorKind
{
    Unknown,
    InvalidCredentials,
    UserNotVerified,
    AccountLocked,
    Unauthorized,
    RateLimited,
    Http,
    Protocol,
}

public sealed class AirCloudException(AirCloudErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AirCloudErrorKind Kind { get; } = kind;
    public TimeSpan? RetryAfter { get; init; }
    public int? HttpStatus { get; init; }

    /// <summary>Server's "errorState" (or message), if any. Safe to log.</summary>
    public string? ErrorState { get; init; }

    /// <summary>Response body with secrets/PII redacted, truncated. Diagnostic use only.</summary>
    public string? ResponseBody
    {
        get => _body;
        init => _body = SensitiveData.Redact(value);
    }
    private readonly string? _body;
}

/// <summary>Removes tokens, passwords and contact details from JSON before it is logged or shown.</summary>
public static class SensitiveData
{
    private static readonly string[] SensitiveKeyParts = ["token", "password", "secret", "email", "mobile", "phone", "authorization"];

    public static bool IsSensitiveKey(string key) =>
        SensitiveKeyParts.Any(p => key.Contains(p, StringComparison.OrdinalIgnoreCase));

    public static string? Redact(string? body, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(body)) return body;
        string result;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(body);
            Scrub(node);
            result = node?.ToJsonString() ?? "";
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON: only keep it if it can't contain credentials.
            result = SensitiveKeyParts.Any(p => body.Contains(p, StringComparison.OrdinalIgnoreCase))
                ? "[non-JSON body redacted]" : body;
        }
        return result.Length > maxLength ? result[..maxLength] + "…" : result;
    }

    private static void Scrub(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (IsSensitiveKey(key)) obj[key] = "***";
                    else Scrub(obj[key]);
                }
                break;
            case System.Text.Json.Nodes.JsonArray arr:
                foreach (var item in arr) Scrub(item);
                break;
        }
    }
}
