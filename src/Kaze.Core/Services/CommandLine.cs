using System.Globalization;
using System.Text;
using Kaze.Core.Models;

namespace Kaze.Core.Services;

/// <summary>
/// Parsed command line, e.g. <c>Kaze.exe --power off</c>, <c>--temp 25 --mode cool --unit Bedroom</c>,
/// <c>--preset Sleep</c>, <c>--toggle</c>, <c>--show</c>, <c>--flyout</c>, <c>--minimized</c>, <c>--exit</c>.
/// </summary>
public sealed record CommandLineRequest
{
    public AcCommand Command { get; init; } = new();
    public bool TogglePower { get; init; }
    public double? TemperatureDelta { get; init; }
    public string? Unit { get; init; }
    /// <summary>Switch to this saved airCloud account first (email or phone).</summary>
    public string? Account { get; init; }
    public bool AllUnits { get; init; }
    public string? Preset { get; init; }
    public bool Show { get; init; }
    public bool Flyout { get; init; }
    /// <summary>Opens the tray menu at the pointer (handy for testing).</summary>
    public bool TrayMenu { get; init; }
    public bool Sleep { get; init; }
    public bool Minimized { get; init; }
    public bool Exit { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasUnitAction => !Command.IsEmpty || TogglePower || TemperatureDelta is not null || Preset is not null || Sleep;

    public static CommandLineRequest Parse(IReadOnlyList<string> args)
    {
        var r = new CommandLineRequest();
        var cmd = new AcCommand();
        var errors = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i].ToLowerInvariant();
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (a)
            {
                case "--power":
                    var p = Next()?.ToLowerInvariant();
                    if (p is "on" or "1" or "true") cmd = cmd with { PowerOn = true };
                    else if (p is "off" or "0" or "false") cmd = cmd with { PowerOn = false };
                    else if (p is "toggle") r = r with { TogglePower = true };
                    else errors.Add("--power expects on|off|toggle");
                    break;
                case "--on": cmd = cmd with { PowerOn = true }; break;
                case "--off": cmd = cmd with { PowerOn = false }; break;
                case "--toggle": r = r with { TogglePower = true }; break;
                case "--temp" or "--temperature":
                    var t = Next();
                    if (t is not null && (t.StartsWith('+') || t.StartsWith('-')) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && IsSaneDelta(d))
                        r = r with { TemperatureDelta = d };
                    else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var abs) && IsSaneTemperature(abs))
                        cmd = cmd with { TargetTemperature = abs };
                    else errors.Add("--temp expects a number, e.g. 25 or +1");
                    break;
                case "--mode":
                    var m = NormalizeMode(Next());
                    if (m is null) errors.Add("--mode expects cool|heat|dry|fan|auto");
                    else cmd = cmd with { Mode = m };
                    break;
                case "--fan":
                    var f = NormalizeFan(Next());
                    if (f is null) errors.Add("--fan expects auto|1..6|silent|low|medium|high|turbo");
                    else cmd = cmd with { FanSpeed = f };
                    break;
                case "--swing":
                    var s = NormalizeSwing(Next());
                    if (s is not null) cmd = cmd with { FanSwing = s };
                    else errors.Add("--swing expects off|vertical|horizontal|both");
                    break;
                case "--unit": r = r with { Unit = Next() }; break;
                case "--account": r = r with { Account = Next() }; break;
                case "--all": r = r with { AllUnits = true }; break;
                case "--preset": r = r with { Preset = Next() }; break;
                case "--show": r = r with { Show = true }; break;
                case "--flyout": r = r with { Flyout = true }; break;
                case "--tray-menu": r = r with { TrayMenu = true }; break;
                case "--sleep": r = r with { Sleep = true }; break;
                case "--minimized" or "--tray": r = r with { Minimized = true }; break;
                case "--exit" or "--quit": r = r with { Exit = true }; break;
                default:
                    if (a.StartsWith('-')) errors.Add($"Unknown option {args[i]}");
                    break;
            }
        }
        return r with { Command = cmd, Errors = errors };
    }

    /// <summary>Set temperatures outside 0 to 50 °C (or NaN) are typing mistakes or attacks, not settings.</summary>
    public static bool IsSaneTemperature(double t) => double.IsFinite(t) && t is >= 0 and <= 50;

    public static bool IsSaneDelta(double d) => double.IsFinite(d) && Math.Abs(d) <= 20;

    public static string? NormalizeMode(string? v) => v?.ToLowerInvariant() switch
    {
        "cool" or "cooling" => AcModes.Cooling,
        "heat" or "heating" => AcModes.Heating,
        "dry" or "dehumidify" => AcModes.Dry,
        "fan" => AcModes.Fan,
        "auto" => AcModes.Auto,
        _ => null,
    };

    public static string? NormalizeSwing(string? v)
    {
        var s = v?.Trim().ToUpperInvariant();
        if (s is "ON") s = FanSwings.Vertical;
        return s is not null && FanSwings.All.Contains(s) ? s : null;
    }

    public static string? NormalizeFan(string? v) => v?.ToLowerInvariant() switch
    {
        "auto" => FanSpeeds.Auto,
        "1" or "silent" or "lv1" => FanSpeeds.Lv1,
        "2" or "low" or "lv2" => FanSpeeds.Lv2,
        "3" or "medium" or "lv3" => FanSpeeds.Lv3,
        "4" or "high" or "lv4" => FanSpeeds.Lv4,
        "5" or "turbo" or "lv5" => FanSpeeds.Lv5,
        "6" or "lv6" => FanSpeeds.Lv6,
        "lva" => FanSpeeds.LvA,
        "nowind" => FanSpeeds.NoWind,
        _ => null,
    };

    /// <summary>Splits a raw Windows command line (as delivered on redirected activation).</summary>
    public static IReadOnlyList<string> SplitCommandLine(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;
        var sb = new StringBuilder();
        var inQuotes = false;
        var any = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; any = true; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any) { result.Add(sb.ToString()); sb.Clear(); any = false; }
                continue;
            }
            sb.Append(c);
            any = true;
        }
        if (any) result.Add(sb.ToString());
        return result;
    }
}
