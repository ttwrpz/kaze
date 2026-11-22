using Kaze.App.Helpers;
using System.Globalization;
using Kaze.App.Services;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace Kaze.App.Views;

public sealed record TimerRow(Guid Id, string Text);
public sealed record RuleRow(Guid Id, string Time, string Title, string Detail, bool Enabled);

public sealed partial class SchedulesPage : Page
{
    private readonly ScheduleService _schedules = App.GetService<ScheduleService>();
    private readonly AcController _controller = App.GetService<AcController>();
    private bool _refreshing;

    public SchedulesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _schedules.Scheduler.Changed += OnChanged;
        RefreshUnits();
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _schedules.Scheduler.Changed -= OnChanged;

    private void OnChanged() => DispatcherQueue.TryEnqueue(Refresh);

    private void RefreshUnits()
    {
        TimerUnit.Items.Clear();
        foreach (var u in _controller.Units) TimerUnit.Items.Add(new UnitChoiceItem(u.Key, u.Name));
        TimerUnit.Items.Add(new UnitChoiceItem(null, L.T("All units")));
        TimerUnit.SelectedIndex = Math.Max(0, _controller.Units.ToList().FindIndex(u => u.Key == _controller.SelectedKey));
    }

    private void Refresh()
    {
        _refreshing = true;
        var timers = _schedules.Scheduler.Timers;
        TimersList.ItemsSource = timers.Select(t => new TimerRow(t.Id,
            L.F("{0}: {1} at {2:HH:mm} (in {3})", UnitName(t.UnitKey), t.Action, t.DueAt.LocalDateTime, Scheduler.Format(t.DueAt - DateTimeOffset.Now)))).ToList();
        NoTimers.Visibility = timers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var rules = _schedules.Scheduler.Rules.OrderBy(r => r.Time).ToList();
        RulesList.ItemsSource = rules.Select(r => new RuleRow(r.Id, r.Time.ToString("HH:mm", CultureInfo.InvariantCulture),
            r.Name, $"{DaysText(r.Days)} · {UnitName(r.UnitKey)} · {r.Action}" + WeatherText(r) +
                (Scheduler.NextOccurrence(r, DateTimeOffset.Now) is { } next && r.Enabled ? " · " + L.F("next {0:ddd HH:mm}", next.LocalDateTime) : ""),
            r.Enabled)).ToList();
        NoRules.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _refreshing = false;
    }

    private static string WeatherText(ScheduleRule r) =>
        r.OnlyIfOutdoorAbove is { } a ? " · " + L.F("if {0:0.#}° or warmer outside", a)
        : r.OnlyIfOutdoorBelow is { } b ? " · " + L.F("if {0:0.#}° or cooler outside", b)
        : "";

    private string UnitName(string? key) => key is null ? L.T("All units") : _controller.Get(key)?.Name ?? L.T("Unknown unit");

    public static string DaysText(IReadOnlyList<DayOfWeek> days)
    {
        if (days.Count is 0 or 7) return L.T("Every day");
        var set = days.ToHashSet();
        if (set.SetEquals([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday])) return L.T("Weekdays");
        if (set.SetEquals([DayOfWeek.Saturday, DayOfWeek.Sunday])) return L.T("Weekends");
        return string.Join(", ", OrderedDays.Where(set.Contains).Select(d => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d)));
    }

    private static readonly DayOfWeek[] OrderedDays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private void AddTimer_Click(object sender, RoutedEventArgs e)
    {
        if (TimerUnit.SelectedItem is not UnitChoiceItem unit || double.IsNaN(TimerMinutes.Value)) return;
        var action = AcCommand.Power(TimerAction.SelectedIndex != 1);
        _schedules.StartTimer(TimeSpan.FromMinutes(TimerMinutes.Value), action, unit.Key);
    }

    private void CancelTimer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id }) _schedules.Scheduler.Remove(id);
    }

    private void RuleEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing || sender is not ToggleSwitch { Tag: Guid id } t) return;
        if (_schedules.Scheduler.Rules.FirstOrDefault(r => r.Id == id) is { } rule && rule.Enabled != t.IsOn)
            _schedules.Scheduler.Upsert(rule with { Enabled = t.IsOn });
    }

    private async void AddRule_Click(object sender, RoutedEventArgs e) =>
        await EditAsync(new ScheduleRule { Name = L.T("Turn off"), Time = new TimeOnly(22, 0), UnitKey = _controller.SelectedKey, Action = AcCommand.Power(false) }, isNew: true);

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id } && _schedules.Scheduler.Rules.FirstOrDefault(r => r.Id == id) is { } rule)
            await EditAsync(rule, isNew: false);
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id }) _schedules.Scheduler.Remove(id);
    }

    private async Task EditAsync(ScheduleRule rule, bool isNew)
    {
        var editor = new CommandEditor(_controller, rule.Action, allowUnit: true, rule.UnitKey);
        var name = new TextBox { Header = L.T("Name"), Text = rule.Name };
        var time = new TimePicker { Header = L.T("Time"), ClockIdentifier = "24HourClock", Time = rule.Time.ToTimeSpan(), MinuteIncrement = 5 };
        var daysPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var dayButtons = new List<(DayOfWeek Day, ToggleButton Button)>();
        foreach (var d in OrderedDays)
        {
            var b = new ToggleButton
            {
                Content = CultureInfo.CurrentCulture.DateTimeFormat.GetShortestDayName(d),
                MinWidth = 40,
                IsChecked = rule.Days.Count == 0 || rule.Days.Contains(d),
            };
            dayButtons.Add((d, b));
            daysPanel.Children.Add(b);
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(name);
        panel.Children.Add(time);
        panel.Children.Add(new TextBlock { Text = L.T("Days") });
        panel.Children.Add(daysPanel);
        panel.Children.Add(editor);

        // Weather conditions need a location set in Automations.
        var hasLocation = App.Current!.Settings.Current.Weather.HasLocation;
        var weatherMode = new ComboBox
        {
            Header = L.T("Weather condition"),
            ItemsSource = new[] { L.T("Always run"), L.T("Only if it is at least this warm outside"), L.T("Only if it is at most this warm outside") },
            SelectedIndex = rule.OnlyIfOutdoorAbove is not null ? 1 : rule.OnlyIfOutdoorBelow is not null ? 2 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = hasLocation,
        };
        var weatherTemp = new NumberBox
        {
            Header = L.T("Outdoor temperature (°C)"),
            Value = rule.OnlyIfOutdoorAbove ?? rule.OnlyIfOutdoorBelow ?? 30,
            Minimum = -30,
            Maximum = 50,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            IsEnabled = hasLocation,
        };
        void SyncWeather() => weatherTemp.Visibility = weatherMode.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        weatherMode.SelectionChanged += (_, _) => SyncWeather();
        SyncWeather();
        panel.Children.Add(weatherMode);
        panel.Children.Add(weatherTemp);
        if (!hasLocation)
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Set your location in Automations to use weather conditions."),
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isNew ? L.T("New schedule") : L.T("Edit schedule"),
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = L.T("Save"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var days = dayButtons.Where(x => x.Button.IsChecked == true).Select(x => x.Day).ToList();
        if (days.Count == 7) days.Clear();
        if (days.Count == 0 && dayButtons.All(x => x.Button.IsChecked != true)) return; // no days selected = nothing to do

        var action = editor.Result;
        if (action.IsEmpty) action = AcCommand.Power(false);
        _schedules.Scheduler.Upsert(rule with
        {
            Name = string.IsNullOrWhiteSpace(name.Text) ? action.ToString() : name.Text.Trim(),
            Time = TimeOnly.FromTimeSpan(time.Time),
            Days = days,
            UnitKey = editor.UnitKey,
            Action = action,
            OnlyIfOutdoorAbove = weatherMode.SelectedIndex == 1 && !double.IsNaN(weatherTemp.Value) ? weatherTemp.Value : null,
            OnlyIfOutdoorBelow = weatherMode.SelectedIndex == 2 && !double.IsNaN(weatherTemp.Value) ? weatherTemp.Value : null,
        });
    }
}

public sealed record UnitChoiceItem(string? Key, string Name)
{
    public override string ToString() => Name;
}
