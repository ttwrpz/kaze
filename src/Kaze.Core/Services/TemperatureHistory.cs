using System.Globalization;
using System.Text;
using Kaze.Core.Models;

namespace Kaze.Core.Services;

public sealed record HistorySample(DateTimeOffset At, string UnitKey, double? Room, double Target, bool PowerOn, string Mode);

/// <summary>
/// Records room temperature and set point over time in monthly CSV files
/// (history-yyyy-MM.csv). Readings are taken at most every <see cref="MinInterval"/>
/// unless the power or set point changes. Files older than <see cref="RetentionDays"/> are deleted.
/// </summary>
public sealed class TemperatureHistory
{
    private const string Header = "time,unit,room,target,power,mode";
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly Dictionary<string, List<HistorySample>> _months = new();
    private readonly Dictionary<string, HistorySample> _last = new();

    public TemperatureHistory(string directory, TimeProvider? time = null)
    {
        _directory = directory;
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(5);
    public int RetentionDays { get; init; } = 90;

    /// <summary>Records the unit if enough time has passed or something important changed. Returns true when written.</summary>
    public bool Record(AcState unit)
    {
        if (!unit.Online) return false;
        var now = _time.GetUtcNow();
        var sample = new HistorySample(now, unit.Key, unit.RoomTemperature, unit.TargetTemperature, unit.PowerOn, unit.Mode);
        lock (_sync)
        {
            if (_last.TryGetValue(unit.Key, out var prev)
                && now - prev.At < MinInterval
                && prev.PowerOn == sample.PowerOn
                && prev.Target == sample.Target
                && prev.Mode == sample.Mode)
                return false;

            _last[unit.Key] = sample;
            var month = MonthKey(now);
            LoadMonth(month).Add(sample);
            try
            {
                Directory.CreateDirectory(_directory);
                var path = PathFor(month);
                var line = Format(sample);
                if (!File.Exists(path)) File.WriteAllText(path, Header + "\n" + line + "\n", Encoding.UTF8);
                else File.AppendAllText(path, line + "\n", Encoding.UTF8);
            }
            catch (IOException)
            {
                // Disk problems must never break control of the unit.
            }
            return true;
        }
    }

    /// <summary>Samples for one unit in [from, to], oldest first.</summary>
    public IReadOnlyList<HistorySample> Query(string unitKey, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<HistorySample>();
        lock (_sync)
        {
            for (var m = new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, TimeSpan.Zero);
                 m <= to;
                 m = m.AddMonths(1))
            {
                foreach (var s in LoadMonth(MonthKey(m)))
                    if (s.UnitKey == unitKey && s.At >= from && s.At <= to)
                        result.Add(s);
            }
        }
        result.Sort((a, b) => a.At.CompareTo(b.At));
        return result;
    }

    /// <summary>Deletes files older than the retention period.</summary>
    public void Prune()
    {
        if (!Directory.Exists(_directory)) return;
        var cutoff = _time.GetUtcNow().AddDays(-RetentionDays);
        var oldest = MonthKey(new DateTimeOffset(cutoff.Year, cutoff.Month, 1, 0, 0, 0, TimeSpan.Zero));
        foreach (var f in Directory.EnumerateFiles(_directory, "history-*.csv"))
        {
            var key = Path.GetFileNameWithoutExtension(f)["history-".Length..];
            if (string.CompareOrdinal(key, oldest) < 0)
            {
                try { File.Delete(f); } catch (IOException) { }
                lock (_sync) _months.Remove(key);
            }
        }
    }

    private List<HistorySample> LoadMonth(string month)
    {
        if (_months.TryGetValue(month, out var list)) return list;
        list = [];
        var path = PathFor(month);
        if (File.Exists(path))
        {
            try
            {
                foreach (var line in File.ReadLines(path).Skip(1))
                    if (Parse(line) is { } s) list.Add(s);
            }
            catch (IOException) { }
        }
        _months[month] = list;
        return list;
    }

    private string PathFor(string month) => Path.Combine(_directory, $"history-{month}.csv");

    private static string MonthKey(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string Format(HistorySample s) => string.Join(',',
        s.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        s.UnitKey.Replace(",", ""),
        s.Room?.ToString("0.#", CultureInfo.InvariantCulture) ?? "",
        s.Target.ToString("0.#", CultureInfo.InvariantCulture),
        s.PowerOn ? "1" : "0",
        s.Mode);

    internal static HistorySample? Parse(string line)
    {
        var p = line.Split(',');
        if (p.Length < 6) return null;
        if (!DateTimeOffset.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return null;
        double? room = double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
        if (!double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var target)) return null;
        return new HistorySample(at, p[1], room, target, p[4] == "1", p[5]);
    }
}
