using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Kaze.Core.Models;
using Microsoft.UI.Xaml.Markup;

namespace Kaze.App.Helpers;

/// <summary>
/// UI text in the chosen language. Strings/en.json holds the XAML keys. Code passes English text
/// as the key, so English needs no entry. Strings/th.json maps both kinds of key to Thai.
/// Missing translations fall back to English.
/// </summary>
public static class L
{
    private static Dictionary<string, string> _english = [];
    private static Dictionary<string, string> _current = [];

    /// <summary>"en" or "th".</summary>
    public static string Language { get; private set; } = "en";

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = [("en", "English"), ("th", "ไทย (Thai)")];

    /// <summary>Resolves the setting ("System", "en", "th") and loads the strings. Call once before any UI is created.</summary>
    public static void Init(string? setting)
    {
        Language = Resolve(setting);
        _english = Load("en");
        _current = Language == "en" ? _english : Load(Language);
        if (Language == "th")
        {
            var thai = CultureInfo.GetCultureInfo("th-TH");
            CultureInfo.DefaultThreadCurrentUICulture = thai;
            CultureInfo.CurrentUICulture = thai;
        }
        CoreText.Translate = T;
    }

    public static string Resolve(string? setting) => setting switch
    {
        "en" or "th" => setting,
        _ => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "th" || CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "th" ? "th" : "en",
    };

    public static string T(string key) =>
        _current.TryGetValue(key, out var v) ? v
        : _english.TryGetValue(key, out var e) ? e
        : key;

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    private static Dictionary<string, string> Load(string language)
    {
        var name = $"Kaze.App.Strings.{language}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}

/// <summary>XAML text lookup: <c>Text="{h:T Key=Settings_Title}"</c>.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class T : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => L.T(Key);
}
