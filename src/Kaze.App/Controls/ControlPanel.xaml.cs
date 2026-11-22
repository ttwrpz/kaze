using Kaze.App.Helpers;
using Kaze.App.Services;
using Kaze.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kaze.App.Controls;

public sealed partial class ControlPanel : UserControl
{
    private readonly HistoryService _history = App.GetService<HistoryService>();
    private DispatcherTimer? _trendTimer;

    public ControlPanel()
    {
        ViewModel = App.GetService<ControlViewModel>();
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!ShowTrend)
            {
                Trend.Visibility = Visibility.Collapsed;
                return;
            }
            _history.Recorded += OnRecorded;
            ViewModel.PropertyChanged += OnViewModelChanged;
            _trendTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _trendTimer.Tick += (_, _) => UpdateTrend();
            _trendTimer.Start();
            UpdateTrend();
        };
        Unloaded += (_, _) =>
        {
            _history.Recorded -= OnRecorded;
            ViewModel.PropertyChanged -= OnViewModelChanged;
            _trendTimer?.Stop();
        };
    }

    private void OnRecorded(string key) => DispatcherQueue.TryEnqueue(UpdateTrend);

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControlViewModel.UnitName)) UpdateTrend();
    }

    private void UpdateTrend()
    {
        var key = App.GetService<Core.Services.AcController>().SelectedKey;
        var now = DateTimeOffset.UtcNow;
        var samples = key is null ? [] : _history.Last(key, TimeSpan.FromHours(24));
        var first = samples.FirstOrDefault(s => s.Room is not null);
        var last = samples.LastOrDefault(s => s.Room is not null);
        // A few minutes of readings drawn on a 24 hour scale is a stray tick with empty space beside it.
        // Show the sparkline once there are 2 hours of data, scaled to the data it has.
        var enough = first is not null && last is not null && last.At - first.At >= TimeSpan.FromHours(2);
        Trend.Visibility = enough ? Visibility.Visible : Visibility.Collapsed;
        if (enough) Trend.SetData(samples, first!.At, now);
    }

    public ControlViewModel ViewModel { get; }

    /// <summary>
    /// The small room temperature line beside "Room". Off on the Control page, which has the full
    /// chart next to the panel, and on in the quick panel, which has no other chart.
    /// </summary>
    public bool ShowTrend { get; set; } = true;

    private void PresetsFlyout_Opening(object sender, object e)
    {
        PresetsFlyout.Items.Clear();
        foreach (var p in ViewModel.Presets)
            PresetsFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = p.Name,
                Icon = FluentIcons.Create(p.Command.Mode is { } m ? FluentIcons.ForMode(m) : FluentIconKind.Star),
                Command = ViewModel.ApplyPresetCommand,
                CommandParameter = p,
            });
        if (ViewModel.Presets.Count == 0)
            PresetsFlyout.Items.Add(new MenuFlyoutItem { Text = L.T("No presets yet. Add them in Automations."), IsEnabled = false });
    }

    private void StartFrostWash_Click(object sender, RoutedEventArgs e)
    {
        FrostWashFlyout.Hide();
        ViewModel.StartFrostWashCommand.Execute(null);
    }
}
