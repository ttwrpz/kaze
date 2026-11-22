using Kaze.App.Helpers;
using Kaze.App.Services;
using Kaze.Core.Backends;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Kaze.App.Views;

public sealed record UnitRow(string Key, string Name, string Detail, bool IsOn, bool Online);

public sealed partial class ControlPage : Page
{
    private readonly AcController _controller = App.GetService<AcController>();
    private readonly HistoryService _history = App.GetService<HistoryService>();
    private readonly WeatherService _weather = App.GetService<WeatherService>();
    private string? _chartKey;
    private bool _refreshing;

    public ControlPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _controller.UnitChanged += OnChanged;
        _controller.UnitsListChanged += OnChanged;
        _controller.StatusChanged += OnStatus;
        _history.Recorded += OnRecorded;
        _weather.Changed += OnWeather;
        Refresh();
        RefreshChart();
        RefreshWeather();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _controller.UnitChanged -= OnChanged;
        _controller.UnitsListChanged -= OnChanged;
        _controller.StatusChanged -= OnStatus;
        _history.Recorded -= OnRecorded;
        _weather.Changed -= OnWeather;
    }

    private void OnRecorded(string key) => DispatcherQueue.TryEnqueue(RefreshChart);
    private void OnWeather() => DispatcherQueue.TryEnqueue(RefreshWeather);

    private void HistoryRange_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => RefreshChart();

    private void RefreshChart()
    {
        var hours = HistoryRange.SelectedItem?.Tag is string t && int.TryParse(t, out var h) ? h : 24;
        _chartKey = _controller.SelectedKey;
        var now = DateTimeOffset.UtcNow;
        var span = TimeSpan.FromHours(hours);
        HistoryChart.EmptyText = L.T("No readings yet. Kaze records the room temperature every 5 minutes while it runs.");
        HistoryChart.SetData(_chartKey is null ? [] : _history.Last(_chartKey, span), now - span, now);
    }

    private void RefreshWeather()
    {
        var w = App.Current!.Settings.Current.Weather;
        WeatherSetup.Visibility = w.HasLocation ? Visibility.Collapsed : Visibility.Visible;
        if (!w.HasLocation)
        {
            WeatherText.Text = "--";
            WeatherDetail.Text = L.T("Add your location to see the outdoor temperature and to use it in schedules.");
            return;
        }
        if (_weather.Current is { } now)
        {
            WeatherText.Text = $"{now.Temperature:0.#}°C";
            var parts = new List<string>();
            if (now.TodayMin is { } lo && now.TodayMax is { } hi) parts.Add(L.F("Today {0:0}° to {1:0}°", lo, hi));
            if (now.Humidity is { } hum) parts.Add(L.F("humidity {0:0}%", hum));
            parts.Add(w.LocationName ?? "");
            WeatherDetail.Text = string.Join(" · ", parts.Where(p => p.Length > 0));
        }
        else
        {
            WeatherText.Text = "--";
            WeatherDetail.Text = _weather.Error is { } e ? L.F("Weather unavailable: {0}", e) : L.T("Loading weather...");
        }
    }

    private void WeatherSetup_Click(object sender, RoutedEventArgs e) => App.Current!.ShowMainWindow("Automations");

    private void OnChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);
    private void OnStatus(BackendStatus s, string? d) => DispatcherQueue.TryEnqueue(Refresh);

    private readonly System.Collections.ObjectModel.ObservableCollection<UnitRow> _rows = [];

    private void Refresh()
    {
        if (_controller.SelectedKey != _chartKey) RefreshChart();
        _refreshing = true;
        var rows = _controller.Units.Select(u => new UnitRow(u.Key, u.Name,
            !u.Online ? L.T("Offline") : (u.PowerOn ? $"{AcModes.Display(u.Mode)} {u.TargetTemperature:0.#}°" : L.T("Off")) + (u.RoomTemperature is { } r ? " · " + L.F("room {0:0.#}°", r) : ""),
            u.PowerOn, u.Online)).ToList();
        if (UnitsList.ItemsSource != _rows) UnitsList.ItemsSource = _rows;
        if (!_rows.Select(x => x.Key).SequenceEqual(rows.Select(x => x.Key)))
        {
            _rows.Clear();
            foreach (var row in rows) _rows.Add(row);
        }
        else
        {
            for (var i = 0; i < rows.Count; i++)
                if (_rows[i] != rows[i]) _rows[i] = rows[i];
        }
        _refreshing = false;

        var connection = _controller.Status switch
        {
            BackendStatus.Connected => L.T("Connected to airCloud Go. Live updates are on."),
            BackendStatus.Connecting => L.T("Connecting…"),
            BackendStatus.Reconnecting => L.T("Connection lost, retrying…"),
            BackendStatus.AuthFailed => L.T("Sign-in failed. Sign in again from Settings."),
            _ => L.T("Stopped."),
        };
        if (ConnectionText.Text != connection) ConnectionText.Text = connection;
        var last = _controller.Units.Select(u => u.UpdatedAt).DefaultIfEmpty().Max();
        UpdatedText.Text = last == default ? "" : L.F("Last update {0:HH:mm:ss}", last.LocalDateTime);
    }

    private void UnitPower_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing || sender is not ToggleSwitch { Tag: string key } t) return;
        _controller.Send(key, AcCommand.Power(t.IsOn));
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e) => _ = App.Current!.RestartConnectionAsync();
}
