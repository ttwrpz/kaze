namespace Kaze.Core.Models;

/// <summary>Wire values used by the airCloud API. Kept as strings so unknown values round-trip safely.</summary>
public static class AcModes
{
    public const string Cooling = "COOLING";
    public const string Heating = "HEATING";
    public const string Dry = "DRY";
    public const string Fan = "FAN";
    public const string Auto = "AUTO";

    public static readonly IReadOnlyList<string> All = [Cooling, Heating, Dry, Fan, Auto];

    public static int SortKey(string mode)
    {
        for (var i = 0; i < All.Count; i++)
            if (string.Equals(All[i], mode, StringComparison.OrdinalIgnoreCase)) return i;
        return 100;
    }

    public static string Display(string? mode) => CoreText.T(DisplayEnglish(mode));

    public static string DisplayEnglish(string? mode) => mode?.ToUpperInvariant() switch
    {
        Cooling => "Cool",
        Heating => "Heat",
        Dry => "Dry",
        Fan => "Fan",
        Auto => "Auto",
        null or "" => "--",
        _ => mode,
    };
}

public static class FanSpeeds
{
    public const string Auto = "AUTO";
    public const string Lv1 = "LV1";
    public const string Lv2 = "LV2";
    public const string Lv3 = "LV3";
    public const string Lv4 = "LV4";
    public const string Lv5 = "LV5";
    public const string Lv6 = "LV6";
    public const string LvA = "LVA";
    public const string NoWind = "NOWIND";

    public static readonly IReadOnlyList<string> All = [Auto, Lv1, Lv2, Lv3, Lv4, Lv5, Lv6, LvA, NoWind];

    public static string Display(string? speed) => CoreText.T(DisplayEnglish(speed));

    public static string DisplayEnglish(string? speed) => speed?.ToUpperInvariant() switch
    {
        Auto => "Auto",
        Lv1 => "Silent",
        Lv2 => "Low",
        Lv3 => "Medium",
        Lv4 => "High",
        Lv5 => "Turbo",
        Lv6 => "Turbo+",
        LvA => "Low-Auto",
        NoWind => "No wind",
        null or "" => "--",
        _ => speed,
    };

    public static int SortKey(string speed)
    {
        for (var i = 0; i < All.Count; i++)
            if (string.Equals(All[i], speed, StringComparison.OrdinalIgnoreCase)) return i;
        return 100;
    }
}

public static class FanSwings
{
    public const string Off = "OFF";
    public const string Vertical = "VERTICAL";
    public const string Horizontal = "HORIZONTAL";
    public const string Both = "BOTH";

    public static readonly IReadOnlyList<string> All = [Off, Vertical, Horizontal, Both];

    public static string Display(string? swing) => CoreText.T(DisplayEnglish(swing));

    public static string DisplayEnglish(string? swing) => swing?.ToUpperInvariant() switch
    {
        Off => "Off",
        Vertical => "Vertical",
        Horizontal => "Horizontal",
        Both => "Both",
        null or "" => "--",
        _ => swing,
    };
}
