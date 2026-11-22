using System.Globalization;

namespace Kaze.Core.Models;

/// <summary>
/// Hook for translating the few user-facing strings the Core builds (mode names, summaries).
/// The app sets <see cref="Translate"/>. English text is the lookup key.
/// </summary>
public static class CoreText
{
    public static Func<string, string> Translate { get; set; } = s => s;

    public static string T(string english) => Translate(english);

    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Translate(englishFormat), args);
}
