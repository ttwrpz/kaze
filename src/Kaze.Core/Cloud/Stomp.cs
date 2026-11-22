using System.Text;

namespace Kaze.Core.Cloud;

public sealed record StompFrame(string Command, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>Minimal STOMP 1.1/1.2 text codec, just what the airCloud notification socket needs.</summary>
public static class StompCodec
{
    public const char Nul = '\0';

    public static string Connect(string accessToken, TimeSpan heartbeat)
    {
        var ms = (int)heartbeat.TotalMilliseconds;
        return $"CONNECT\naccept-version:1.1,1.2\nheart-beat:{ms},{ms}\nAuthorization:Bearer {accessToken}\n\n{Nul}";
    }

    public static string Subscribe(string subscriptionId, string familyId) =>
        $"SUBSCRIBE\nid:{subscriptionId}\ndestination:{Destination(familyId)}\nack:auto\n\n{Nul}";

    public static string Destination(string familyId) => $"/notification/{familyId}/{familyId}";

    public static string Disconnect() => $"DISCONNECT\n\n{Nul}";

    public const string HeartBeat = "\n";

    /// <summary>Parses one complete frame (without the trailing NUL). Returns null for heart-beats/garbage.</summary>
    public static StompFrame? Parse(string raw)
    {
        var text = raw.TrimStart('\r', '\n');
        if (text.Length == 0)
            return null;

        var headerEnd = text.IndexOf("\n\n", StringComparison.Ordinal);
        string head, body;
        if (headerEnd < 0)
        {
            head = text;
            body = "";
        }
        else
        {
            head = text[..headerEnd];
            body = text[(headerEnd + 2)..];
        }

        var lines = head.Replace("\r", "").Split('\n');
        var command = lines[0].Trim();
        if (command.Length == 0 || command.Any(c => !char.IsAsciiLetterUpper(c)))
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers.TryAdd(line[..colon], Unescape(line[(colon + 1)..]));
        }
        return new StompFrame(command, headers, body.TrimEnd(Nul));
    }

    private static string Unescape(string v) =>
        v.Contains('\\') ? v.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\c", ":").Replace("\\\\", "\\") : v;
}

/// <summary>Accumulates websocket text and yields complete STOMP frames (NUL-terminated).</summary>
public sealed class StompFrameReader
{
    private readonly StringBuilder _buffer = new();

    public IReadOnlyList<StompFrame> Feed(string chunk)
    {
        _buffer.Append(chunk);
        var frames = new List<StompFrame>();
        while (true)
        {
            var s = _buffer.ToString();
            var nul = s.IndexOf(StompCodec.Nul);
            if (nul < 0)
            {
                // Only heart-beats are buffered, so drop them.
                if (s.Length > 0 && s.All(c => c is '\n' or '\r'))
                    _buffer.Clear();
                break;
            }
            var raw = s[..nul];
            _buffer.Remove(0, nul + 1);
            if (StompCodec.Parse(raw) is { } f)
                frames.Add(f);
        }
        return frames;
    }

    public void Reset() => _buffer.Clear();
}
