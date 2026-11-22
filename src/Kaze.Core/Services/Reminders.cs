using System.Text.Json;

namespace Kaze.Core.Services;

/// <summary>Pure rules for the Frost Wash reminder and the energy budget alert.</summary>
public static class Reminders
{
    /// <summary>True when the last wash is older than the interval and the reminder isn't snoozed.</summary>
    public static bool FrostWashDue(DateTimeOffset? lastWash, DateTimeOffset now, int intervalDays, DateTimeOffset? snoozedUntil)
    {
        if (intervalDays <= 0) return false;
        if (snoozedUntil is { } s && now < s) return false;
        return lastWash is not { } last || now - last >= TimeSpan.FromDays(intervalDays);
    }

    /// <summary>
    /// Thresholds (percent of the budget, e.g. 80 and 100) newly crossed by <paramref name="used"/>
    /// that haven't been announced yet this month.
    /// </summary>
    public static IReadOnlyList<int> NewBudgetThresholds(double used, double budget, IEnumerable<int> alreadyNotified, params int[] thresholds)
    {
        if (budget <= 0 || used <= 0) return [];
        var done = alreadyNotified.ToHashSet();
        var percent = used / budget * 100;
        var crossed = thresholds.Where(t => percent >= t && !done.Contains(t)).OrderBy(t => t).ToList();
        return crossed;
    }
}

/// <summary>Release information for the update check (GitHub releases API shape).</summary>
/// <param name="InstallerUrl">The setup program, only when it is named exactly <c>Kaze-Setup-{version}.exe</c> and hosted in the release itself.</param>
/// <param name="SignatureUrl">Its signature, <c>Kaze-Setup-{version}.exe.sig</c>.</param>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string? InstallerUrl, string? SignatureUrl, string? Notes);

public static class UpdateCheck
{
    /// <summary>
    /// Parses https://api.github.com/repos/{owner}/{repo}/releases/latest. Assets are only taken from
    /// that repository's own release downloads, and only under the exact expected names.
    /// </summary>
    public static ReleaseInfo? ParseLatest(string json, string repository)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (tag is null || ParseVersion(tag) is not { } version) return null;

        var releasePages = $"https://github.com/{repository}/releases/";
        var page = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        if (!page.StartsWith(releasePages, StringComparison.OrdinalIgnoreCase)) page = releasePages;

        var setupName = $"Kaze-Setup-{version.ToString(3)}.exe";
        var downloads = $"https://github.com/{repository}/releases/download/";
        string? installer = null, signature = null;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (!url.StartsWith(downloads, StringComparison.OrdinalIgnoreCase) || !url.EndsWith("/" + name, StringComparison.Ordinal)) continue;
                if (name == setupName) installer = url;
                else if (name == setupName + ".sig") signature = url;
            }
        }
        var notes = r.TryGetProperty("body", out var b) ? b.GetString() : null;
        return new ReleaseInfo(version, tag, page, installer, signature, notes);
    }

    /// <summary>Accepts "v1.2.3", "1.2", "release-1.2.3".</summary>
    public static Version? ParseVersion(string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    public static bool IsNewer(Version candidate, Version current) =>
        new Version(candidate.Major, candidate.Minor, Math.Max(candidate.Build, 0))
            .CompareTo(new Version(current.Major, current.Minor, Math.Max(current.Build, 0))) > 0;
}
