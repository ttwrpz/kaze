using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kaze.Core.Cloud;

internal static class JsonDefaults
{
    /// <summary>Write '+' and non-ASCII as-is, like the official (okhttp/gson) client.</summary>
    public static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
