using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kaze.Core.Tests;

public partial class TranslationTests
{
    [GeneratedRegex(@"\{(\d+)(?:[:,][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string StringsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kaze.slnx")) && !File.Exists(Path.Combine(dir.FullName, "Kaze.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Kaze.App", "Strings");
    }

    private static Dictionary<string, string> Load(string lang) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(StringsDir(), lang + ".json")))!;

    private static string[] Holes(string s) =>
        Placeholder().Matches(s).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [Fact]
    public void Thai_covers_every_xaml_key()
    {
        var en = Load("en");
        var th = Load("th");
        Assert.NotEmpty(en);
        Assert.Empty(en.Keys.Where(k => !th.ContainsKey(k)));
    }

    [Fact]
    public void Thai_keeps_format_placeholders()
    {
        var en = Load("en");
        foreach (var (key, thai) in Load("th"))
        {
            var english = en.TryGetValue(key, out var e) ? e : key;
            Assert.True(Holes(english).SequenceEqual(Holes(thai)), $"Placeholders differ for '{key}'");
            Assert.False(string.IsNullOrWhiteSpace(thai), $"Empty translation for '{key}'");
        }
    }
}
