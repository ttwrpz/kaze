using System.Text.Json;
using System.Text.Json.Serialization;
using Kaze.Core.Models;
using Kaze.Core.Services;

namespace Kaze.App.Services;

public sealed record Preset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Preset";
    public AcCommand Command { get; init; } = new();
}

public enum HotkeyAction
{
    TogglePower,
    TemperatureUp,
    TemperatureDown,
    ShowFlyout,
    ShowMainWindow,
}

public sealed record HotkeyBinding
{
    public HotkeyAction Action { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8.</summary>
    public uint Modifiers { get; init; }
    /// <summary>Virtual-key code.</summary>
    public uint Key { get; init; }
}

public sealed class AutomationSettings
{
    public bool TurnOffOnLock { get; set; }
    public int LockDelayMinutes { get; set; } = 5;
    public bool TurnOffOnSleep { get; set; }
    public bool RestoreOnUnlock { get; set; }
    public bool NotifyExternalChanges { get; set; } = true;
    public bool NotifyOffline { get; set; } = true;
    public bool NotifyScheduleFired { get; set; } = true;
}

public sealed class WeatherSettings
{
    public string? LocationName { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool HasLocation => Latitude is not null && Longitude is not null;
}

public sealed class ReminderSettings
{
    /// <summary>Remind to run Frost Wash after this many days. 0 turns the reminder off.</summary>
    public int FrostWashIntervalDays { get; set; } = 28;
    /// <summary>Last time each unit was seen washing (unit key to time).</summary>
    public Dictionary<string, DateTimeOffset> LastFrostWash { get; set; } = [];
    public Dictionary<string, DateTimeOffset> FrostWashSnoozedUntil { get; set; } = [];
    /// <summary>Monthly energy budget in kWh. 0 turns the alert off.</summary>
    public double MonthlyKwhBudget { get; set; }
    /// <summary>yyyy-MM of the month <see cref="BudgetNotified"/> belongs to.</summary>
    public string? BudgetMonth { get; set; }
    public List<int> BudgetNotified { get; set; } = [];
    /// <summary>Warn 5 minutes before an off timer runs, with a snooze button.</summary>
    public bool TimerHeadsUp { get; set; } = true;
}

public sealed class LocalApiSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8765;
    /// <summary>Allow devices on the home network (Home Assistant, phones). Off means this PC only.</summary>
    public bool AllowNetwork { get; set; }
    public string Token { get; set; } = NewToken();

    public static string NewToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

public sealed class UpdateSettings
{
    public bool CheckAutomatically { get; set; } = true;
    public const string DefaultRepository = "ttwrpz/kaze";

    /// <summary>The GitHub repository that publishes releases. Empty means the official one.</summary>
    public string? Repository
    {
        get => _repository ?? DefaultRepository;
        set => _repository = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
    private string? _repository;
    public DateTimeOffset? LastCheck { get; set; }
    public string? SkippedVersion { get; set; }
}

public sealed class AppSettings
{
    /// <summary>"System", "en" or "th".</summary>
    public string Language { get; set; } = "System";
    public SleepCurveSettings SleepCurve { get; set; } = new();
    /// <summary>Timers created by a running sleep curve, per unit key.</summary>
    public Dictionary<string, List<Guid>> ActiveSleep { get; set; } = [];
    public WeatherSettings Weather { get; set; } = new();
    public ReminderSettings Reminders { get; set; } = new();
    public LocalApiSettings LocalApi { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();

    /// <summary>The active airCloud account (email or phone).</summary>
    public string? Login { get; set; }
    /// <summary>All saved airCloud accounts. Passwords live in Credential Manager.</summary>
    public List<string> Accounts { get; set; } = [];
    /// <summary>Last selected unit per account (lower case login to unit key).</summary>
    public Dictionary<string, string> AccountUnits { get; set; } = [];
    public string? SelectedUnitKey { get; set; }
    public double TemperatureStep { get; set; } = 0.5;
    public bool StartWithWindows { get; set; }
    public bool ShowTemperatureInIcon { get; set; } = true;
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    public AutomationSettings Automations { get; set; } = new();
    public List<HotkeyBinding> Hotkeys { get; set; } = DefaultHotkeys();
    public List<Preset> Presets { get; set; } = DefaultPresets();
    public List<ScheduleRule> Schedules { get; set; } = [];
    public List<OneShotTimer> Timers { get; set; } = [];

    private const uint Alt = 1, Ctrl = 2;

    public static List<HotkeyBinding> DefaultHotkeys() =>
    [
        new() { Action = HotkeyAction.TogglePower, Modifiers = Ctrl | Alt, Key = 0x50 /* P */ },
        new() { Action = HotkeyAction.TemperatureUp, Modifiers = Ctrl | Alt, Key = 0x26 /* Up */ },
        new() { Action = HotkeyAction.TemperatureDown, Modifiers = Ctrl | Alt, Key = 0x28 /* Down */ },
        new() { Action = HotkeyAction.ShowFlyout, Modifiers = Ctrl | Alt, Key = 0x41 /* A */ },
        new() { Action = HotkeyAction.ShowMainWindow, Enabled = false, Modifiers = Ctrl | Alt, Key = 0x48 /* H */ },
    ];

    public static List<Preset> DefaultPresets() =>
    [
        new() { Name = "Quick cool", Command = new AcCommand { PowerOn = true, Mode = AcModes.Cooling, TargetTemperature = 23, FanSpeed = FanSpeeds.Lv4 } },
        new() { Name = "Comfort", Command = new AcCommand { PowerOn = true, Mode = AcModes.Cooling, TargetTemperature = 25.5, FanSpeed = FanSpeeds.Auto } },
        new() { Name = "Sleep", Command = new AcCommand { PowerOn = true, Mode = AcModes.Cooling, TargetTemperature = 26, FanSpeed = FanSpeeds.Lv1 } },
        new() { Name = "Dry", Command = new AcCommand { PowerOn = true, Mode = AcModes.Dry } },
    ];
}

/// <summary>Loads/saves settings JSON in %LOCALAPPDATA%\Kaze.</summary>
public sealed class SettingsStore
{
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kaze");
    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();
    public AppSettings Current { get; private set; } = new();

    public event Action? Saved;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception)
        {
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
            Current = new();
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(DataDirectory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        Saved?.Invoke();
    }

    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Save();
    }
}
