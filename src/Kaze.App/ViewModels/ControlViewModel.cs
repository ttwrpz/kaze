using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kaze.App.Helpers;
using Kaze.App.Services;
using Kaze.Core.Backends;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Kaze.App.ViewModels;

/// <summary>One selectable option (mode, fan or swing) rendered as a toggle tile.</summary>
public sealed partial class OptionItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public FluentIconKind? Icon { get; init; }
    public required IRelayCommand<string> SelectCommand { get; init; }

    public FluentIconKind IconKind => Icon ?? FluentIconKind.Sparkle;
    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    /// <summary>Raises the change even when the value is the same, so a clicked ToggleButton snaps back to the real state.</summary>
    public void Refresh(bool selected, bool enabled)
    {
        IsEnabled = enabled;
        IsSelected = selected;
        OnPropertyChanged(nameof(IsSelected));
    }
}

public sealed record UnitChoice(string Key, string Name)
{
    public override string ToString() => Name;
}

/// <summary>State and commands for the selected unit. Shared by the tray flyout and the Control page.</summary>
public sealed partial class ControlViewModel : ObservableObject
{
    private readonly AcController _controller;
    private readonly SettingsStore _settings;
    private readonly ScheduleService _schedules;
    private readonly SleepService _sleep;
    private readonly DispatcherQueue _ui;
    private bool _syncingUnits;

    public ControlViewModel(AcController controller, SettingsStore settings, ScheduleService schedules, SleepService sleep)
    {
        _sleep = sleep;
        _controller = controller;
        _settings = settings;
        _schedules = schedules;
        _ui = DispatcherQueue.GetForCurrentThread();

        _controller.UnitChanged += (_, e) => OnUi(() => { if (e.Current.Key == _controller.SelectedKey) Refresh(); });
        _controller.UnitsListChanged += (_, _) => OnUi(RefreshAll);
        _controller.StatusChanged += (_, _) => OnUi(Refresh);
        _schedules.Scheduler.Changed += () => OnUi(RefreshTimers);
        _settings.Saved += () => OnUi(RefreshPresets);
        _sleep.Changed += () => OnUi(Refresh);

        RefreshAll();
    }

    public ObservableCollection<OptionItem> Modes { get; } = [];
    public ObservableCollection<OptionItem> Fans { get; } = [];
    public ObservableCollection<OptionItem> Swings { get; } = [];
    public ObservableCollection<UnitChoice> UnitChoices { get; } = [];
    public ObservableCollection<Preset> Presets { get; } = [];
    public ObservableCollection<OneShotTimer> ActiveTimers { get; } = [];

    [ObservableProperty] public partial bool HasUnit { get; set; }
    [ObservableProperty] public partial string UnitName { get; set; } = "";
    [ObservableProperty] public partial bool IsOn { get; set; }
    [ObservableProperty] public partial bool IsOnline { get; set; }
    [ObservableProperty] public partial bool CanControl { get; set; }
    [ObservableProperty] public partial bool CanSetTemperature { get; set; }
    [ObservableProperty] public partial string TargetText { get; set; } = "--";
    [ObservableProperty] public partial string RoomText { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = L.T("Connecting…");
    [ObservableProperty] public partial string ModeText { get; set; } = "";
    [ObservableProperty] public partial string PowerText { get; set; } = L.T("Off");
    [ObservableProperty] public partial UnitChoice? SelectedUnit { get; set; }
    [ObservableProperty] public partial bool HasMultipleUnits { get; set; }
    [ObservableProperty] public partial bool HasTimers { get; set; }
    [ObservableProperty] public partial bool HasFans { get; set; }
    [ObservableProperty] public partial bool HasSwings { get; set; }
    [ObservableProperty] public partial string TimerText { get; set; } = "";
    [ObservableProperty] public partial FluentIconKind ModeIcon { get; set; } = FluentIconKind.Snowflake;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SleepColumnSpan))] public partial bool FrostWashSupported { get; set; }

    public int SleepColumnSpan => FrostWashSupported ? 1 : 2;
    [ObservableProperty] public partial bool FrostWashActive { get; set; }
    [ObservableProperty] public partial bool CanStartFrostWash { get; set; }
    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial string ErrorText { get; set; } = "";
    [ObservableProperty] public partial bool SleepActive { get; set; }
    [ObservableProperty] public partial bool CanSleep { get; set; }
    [ObservableProperty] public partial string SleepText { get; set; } = "";
    [ObservableProperty] public partial string SleepDescription { get; set; } = "";

    [RelayCommand]
    private void ToggleSleep()
    {
        if (Key is not { } k) return;
        if (_sleep.IsActive(k)) _sleep.Cancel(k);
        else _sleep.Start(k);
    }

    partial void OnSelectedUnitChanged(UnitChoice? value)
    {
        if (_syncingUnits || value is null) return;
        _controller.SelectedKey = value.Key;
        _settings.Update(s => s.SelectedUnitKey = value.Key);
        Refresh();
    }

    private string? Key => _controller.SelectedKey;

    [RelayCommand]
    private void TogglePower()
    {
        if (Key is { } k) _controller.TogglePower(k);
    }

    [RelayCommand]
    private void TemperatureUp()
    {
        if (Key is { } k) _controller.AdjustTemperature(k, _settings.Current.TemperatureStep);
    }

    [RelayCommand]
    private void TemperatureDown()
    {
        if (Key is { } k) _controller.AdjustTemperature(k, -_settings.Current.TemperatureStep);
    }

    [RelayCommand]
    private void SetMode(string? mode)
    {
        if (Key is { } k && mode is not null) _controller.Send(k, new AcCommand { Mode = mode, PowerOn = true });
        Refresh();
    }

    [RelayCommand]
    private void SetFan(string? fan)
    {
        if (Key is { } k && fan is not null) _controller.Send(k, AcCommand.SetFan(fan));
        Refresh();
    }

    [RelayCommand]
    private void SetSwing(string? swing)
    {
        if (Key is { } k && swing is not null) _controller.Send(k, AcCommand.SetSwing(swing));
        Refresh();
    }

    [RelayCommand]
    private void ApplyPreset(Preset? preset)
    {
        if (Key is { } k && preset is not null) _controller.Send(k, preset.Command);
    }

    /// <summary>Parameter: minutes until power off.</summary>
    [RelayCommand]
    private void StartOffTimer(string? minutes)
    {
        if (Key is { } k && int.TryParse(minutes, out var m) && m > 0)
            _schedules.StartTimer(TimeSpan.FromMinutes(m), AcCommand.Power(false), k);
    }

    [RelayCommand]
    private void CancelTimers()
    {
        foreach (var t in _schedules.Scheduler.Timers) _schedules.Scheduler.Remove(t.Id);
    }

    [RelayCommand]
    private void CancelTimer(OneShotTimer? timer)
    {
        if (timer is not null) _schedules.Scheduler.Remove(timer.Id);
    }

    [RelayCommand]
    private async Task StartFrostWash()
    {
        if (Key is { } k) await _controller.StartFrostWashAsync(k);
    }

    [RelayCommand]
    private void Reconnect() => _controller.Refresh();

    private void RefreshAll()
    {
        RefreshUnits();
        RefreshPresets();
        RefreshTimers();
        Refresh();
    }

    private void RefreshUnits()
    {
        _syncingUnits = true;
        try
        {
            UnitChoices.Clear();
            foreach (var u in _controller.Units) UnitChoices.Add(new UnitChoice(u.Key, u.Name));
            SelectedUnit = UnitChoices.FirstOrDefault(c => c.Key == _controller.SelectedKey);
            HasMultipleUnits = UnitChoices.Count > 1;
        }
        finally { _syncingUnits = false; }
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var p in _settings.Current.Presets) Presets.Add(p);
    }

    private void RefreshTimers()
    {
        ActiveTimers.Clear();
        foreach (var t in _schedules.Scheduler.Timers) ActiveTimers.Add(t);
        HasTimers = ActiveTimers.Count > 0;
        TimerText = ActiveTimers.FirstOrDefault() is { } first
            ? L.F("Timer: {0} at {1:HH:mm}", first.Action, first.DueAt.LocalDateTime) + (ActiveTimers.Count > 1 ? $" (+{ActiveTimers.Count - 1})" : "")
            : "";
    }

    public void Refresh()
    {
        var u = _controller.Selected;
        var status = _controller.Status;
        HasUnit = u is not null;
        if (u is null)
        {
            UnitName = L.T("No air conditioner");
            StatusText = status switch
            {
                BackendStatus.AuthFailed => L.T("Sign in failed. Open Settings."),
                BackendStatus.Connected => L.T("No units found on this account"),
                BackendStatus.Stopped => L.T("Not signed in"),
                _ => L.T("Connecting to airCloud…"),
            };
            CanControl = CanSetTemperature = false;
            FrostWashSupported = FrostWashActive = CanStartFrostWash = HasError = false;
            SleepActive = CanSleep = false;
            TargetText = "--";
            RoomText = "";
            Modes.Clear(); Fans.Clear(); Swings.Clear();
            HasFans = HasSwings = false;
            return;
        }

        var caps = _controller.CapabilitiesOf(u.Key);
        var modeCaps = caps.For(u.Mode);
        var hasRange = AcCapabilities.HasTemperatureRange(modeCaps);
        var controllable = u.Online && !u.FrostWashActive;

        UnitName = u.Name;
        IsOn = u.PowerOn;
        IsOnline = u.Online;
        PowerText = u.PowerOn ? L.T("On") : L.T("Off");
        CanControl = controllable;
        CanSetTemperature = controllable && hasRange;
        TargetText = hasRange ? $"{u.TargetTemperature:0.#}°" : "--";
        RoomText = u.RoomTemperature is { } r ? L.F("Room {0:0.#}°C", r) : "";
        ModeText = $"{AcModes.Display(u.Mode)} · " + L.F("Fan {0}", FanSpeeds.Display(u.FanSpeed));
        ModeIcon = FluentIcons.ForMode(u.Mode);
        FrostWashSupported = u.FrostWashSupported;
        FrostWashActive = u.FrostWashActive;
        CanStartFrostWash = u.FrostWashSupported && u.Online && !u.FrostWashActive;
        SleepActive = _sleep.IsActive(u.Key);
        CanSleep = controllable;
        SleepText = SleepActive && _sleep.EndsAt(u.Key) is { } end
            ? L.F("Sleep curve until {0:HH:mm}", end.LocalDateTime)
            : L.T("Sleep curve");
        SleepDescription = SleepActive
            ? L.T("Click to stop the sleep curve.")
            : L.F("Gently relaxes the set point overnight: {0}. Change the steps in Automations.", _sleep.Describe(u.Key));
        HasError = u.ErrorActive;
        ErrorText = u.ErrorActive
            ? (u.ErrorCode is { } c ? L.F("The unit reports a fault (code {0}). Check the unit display or the airCloud Go app.", c) : L.T("The unit reports a fault. Check the unit display or the airCloud Go app."))
            : "";

        StatusText = !u.Online ? L.T("Unit offline")
            : u.FrostWashActive ? L.T("Frost Wash running")
            : status is BackendStatus.Reconnecting or BackendStatus.Connecting ? L.T("Reconnecting…")
            : status == BackendStatus.AuthFailed ? L.T("Sign in failed")
            : u.PowerOn ? L.F("{0} to {1:0.#}°", AcModes.Display(u.Mode), u.TargetTemperature) : L.T("Off");

        Sync(Modes, caps.SupportedModes, u.Mode, AcModes.Display, m => FluentIcons.ForMode(m), SetModeCommand, controllable);
        Sync(Fans, modeCaps.FanEnabled ? modeCaps.FanSpeeds : [], u.FanSpeed, FanSpeeds.Display, FluentIcons.ForFan, SetFanCommand, controllable);
        Sync(Swings, modeCaps.SwingEnabled ? caps.Swings : [], u.FanSwing, FanSwings.Display, FluentIcons.ForSwing, SetSwingCommand, controllable);
        HasFans = Fans.Count > 0;
        HasSwings = Swings.Count > 0;
    }

    private static void Sync(ObservableCollection<OptionItem> target, IReadOnlyList<string> keys, string selected,
        Func<string, string> label, Func<string, FluentIconKind?> icon, IRelayCommand<string> command, bool enabled)
    {
        if (!target.Select(o => o.Key).SequenceEqual(keys))
        {
            target.Clear();
            foreach (var k in keys)
                target.Add(new OptionItem { Key = k, Label = label(k), Icon = icon(k), SelectCommand = command });
        }
        foreach (var o in target) o.Refresh(string.Equals(o.Key, selected, StringComparison.OrdinalIgnoreCase), enabled);
    }

    private void OnUi(Action a)
    {
        if (_ui.HasThreadAccess) a();
        else _ui.TryEnqueue(() => a());
    }
}
