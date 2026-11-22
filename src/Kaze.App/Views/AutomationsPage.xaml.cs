using Kaze.App.Helpers;
using Kaze.App.Services;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Kaze.App.Views;

public sealed record PresetRow(Guid Id, string Name, string Detail);

public sealed record LocationChoice(Kaze.Core.Weather.WeatherLocation? Location, string? Message = null)
{
    public override string ToString() => Location?.Display ?? Message ?? L.T("No places found.");
}

public sealed partial class AutomationsPage : Page
{
    private readonly SettingsStore _settings = App.GetService<SettingsStore>();
    private bool _loading;

    public AutomationsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        var a = _settings.Current.Automations;
        OffOnLock.IsOn = a.TurnOffOnLock;
        LockDelay.Value = a.LockDelayMinutes;
        OffOnSleep.IsOn = a.TurnOffOnSleep;
        RestoreOnUnlock.IsOn = a.RestoreOnUnlock;
        NotifyExternal.IsOn = a.NotifyExternalChanges;
        NotifyOffline.IsOn = a.NotifyOffline;
        NotifySchedule.IsOn = a.NotifyScheduleFired;

        var r = _settings.Current.Reminders;
        FrostInterval.ItemsSource = FrostChoices.Select(c => c.Label).ToList();
        FrostInterval.SelectedIndex = Math.Max(0, Array.FindIndex(FrostChoices, c => c.Days == r.FrostWashIntervalDays));
        EnergyBudget.Value = r.MonthlyKwhBudget;
        TimerHeadsUp.IsOn = r.TimerHeadsUp;

        var sc = _settings.Current.SleepCurve;
        var steps = sc.Steps.OrderBy(x => x.AfterMinutes).ToList();
        (NumberBox M, NumberBox D)[] rows = [(Step1Minutes, Step1Delta), (Step2Minutes, Step2Delta), (Step3Minutes, Step3Delta)];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].M.Value = i < steps.Count ? steps[i].AfterMinutes : 0;
            rows[i].D.Value = i < steps.Count ? steps[i].Delta : 0;
        }
        SleepFan.ItemsSource = SleepFans.Select(f => f.Label).ToList();
        SleepFan.SelectedIndex = Math.Max(0, Array.FindIndex(SleepFans, f => f.Speed == sc.FanSpeed));
        SleepOffHours.Value = (sc.OffAfterMinutes ?? 0) / 60.0;

        RefreshLocation();
        _loading = false;
        RefreshPresets();
    }

    private void Setting_Changed(object sender, object e)
    {
        if (_loading) return;
        _settings.Update(s =>
        {
            var a = s.Automations;
            a.TurnOffOnLock = OffOnLock.IsOn;
            a.LockDelayMinutes = double.IsNaN(LockDelay.Value) ? 0 : (int)LockDelay.Value;
            a.TurnOffOnSleep = OffOnSleep.IsOn;
            a.RestoreOnUnlock = RestoreOnUnlock.IsOn;
            a.NotifyExternalChanges = NotifyExternal.IsOn;
            a.NotifyOffline = NotifyOffline.IsOn;
            a.NotifyScheduleFired = NotifySchedule.IsOn;
            if (FrostInterval.SelectedIndex >= 0) s.Reminders.FrostWashIntervalDays = FrostChoices[FrostInterval.SelectedIndex].Days;
            s.Reminders.MonthlyKwhBudget = double.IsNaN(EnergyBudget.Value) ? 0 : Math.Max(0, EnergyBudget.Value);
            s.Reminders.TimerHeadsUp = TimerHeadsUp.IsOn;
        });
    }

    private static readonly (int Days, string Label)[] FrostChoices =
    [
        (0, L.T("Off")), (14, L.T("Every 2 weeks")), (28, L.T("Every 4 weeks")), (56, L.T("Every 8 weeks")), (91, L.T("Every 3 months")),
    ];

    private static readonly (string? Speed, string Label)[] SleepFans =
    [
        (null, L.T("Keep current")), (FanSpeeds.Lv1, FanSpeeds.Display(FanSpeeds.Lv1)), (FanSpeeds.Lv2, FanSpeeds.Display(FanSpeeds.Lv2)), (FanSpeeds.Auto, FanSpeeds.Display(FanSpeeds.Auto)),
    ];

    private void Sleep_Changed(object sender, object e)
    {
        if (_loading) return;
        static int Minutes(NumberBox b) => double.IsNaN(b.Value) ? 0 : (int)Math.Round(b.Value);
        static double Delta(NumberBox b) => double.IsNaN(b.Value) ? 0 : Math.Round(b.Value * 2) / 2;
        var steps = new List<SleepStep>();
        foreach (var (m, d) in new[] { (Step1Minutes, Step1Delta), (Step2Minutes, Step2Delta), (Step3Minutes, Step3Delta) })
            if (Minutes(m) > 0) steps.Add(new SleepStep { AfterMinutes = Minutes(m), Delta = Delta(d) });
        var off = double.IsNaN(SleepOffHours.Value) ? 0 : (int)Math.Round(SleepOffHours.Value * 60);
        _settings.Update(s => s.SleepCurve = new SleepCurveSettings
        {
            Steps = steps,
            FanSpeed = SleepFan.SelectedIndex >= 0 ? SleepFans[SleepFan.SelectedIndex].Speed : null,
            OffAfterMinutes = off > 0 ? off : null,
        });
    }

    private readonly WeatherService _weather = App.GetService<WeatherService>();
    private CancellationTokenSource? _searchCts;

    private void RefreshLocation()
    {
        var w = _settings.Current.Weather;
        WeatherExpander.Header = w.HasLocation ? L.F("Location: {0}", w.LocationName) : L.T("Location: not set");
        ClearLocation.Visibility = w.HasLocation ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LocationSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var text = sender.Text.Trim();
        if (text.Length < 2) { sender.ItemsSource = null; return; }
        try
        {
            await Task.Delay(350, cts.Token);
            var results = await _weather.SearchAsync(text);
            if (cts.IsCancellationRequested) return;
            sender.ItemsSource = results.Count > 0 ? results.Select(r => new LocationChoice(r)).ToList() : [new LocationChoice(null)];
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            sender.ItemsSource = new[] { new LocationChoice(null, L.T("Search failed. Check the internet connection.")) };
        }
    }

    private void LocationSearch_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is LocationChoice { Location: { } loc })
        {
            _weather.SetLocation(loc);
            sender.Text = "";
            sender.ItemsSource = null;
            RefreshLocation();
        }
    }

    private void LocationSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is LocationChoice { Location: { } loc })
        {
            _weather.SetLocation(loc);
            sender.Text = "";
            RefreshLocation();
        }
    }

    private void ClearLocation_Click(object sender, RoutedEventArgs e)
    {
        _weather.SetLocation(null);
        RefreshLocation();
    }

    private void RefreshPresets() =>
        PresetsList.ItemsSource = _settings.Current.Presets.Select(p => new PresetRow(p.Id, p.Name, p.Command.ToString())).ToList();

    private async void AddPreset_Click(object sender, RoutedEventArgs e) =>
        await EditAsync(new Preset { Name = L.T("New preset"), Command = new AcCommand { PowerOn = true } }, isNew: true);

    private async void EditPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id } && _settings.Current.Presets.FirstOrDefault(p => p.Id == id) is { } preset)
            await EditAsync(preset, isNew: false);
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id }) return;
        _settings.Update(s => s.Presets.RemoveAll(p => p.Id == id));
        RefreshPresets();
    }

    private async Task EditAsync(Preset preset, bool isNew)
    {
        var name = new TextBox { Header = L.T("Name"), Text = preset.Name };
        var editor = new CommandEditor(App.GetService<AcController>(), preset.Command, allowUnit: false, null);
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(name);
        panel.Children.Add(editor);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isNew ? L.T("New preset") : L.T("Edit preset"),
            Content = panel,
            PrimaryButtonText = L.T("Save"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var updated = preset with
        {
            Name = string.IsNullOrWhiteSpace(name.Text) ? L.T("Preset") : name.Text.Trim(),
            Command = editor.Result,
        };
        _settings.Update(s =>
        {
            var i = s.Presets.FindIndex(p => p.Id == updated.Id);
            if (i >= 0) s.Presets[i] = updated; else s.Presets.Add(updated);
        });
        RefreshPresets();
    }
}
