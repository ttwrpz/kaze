using Kaze.App.Helpers;
using Kaze.App.Services;
using Kaze.Core.Cloud;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Kaze.App.Views;

public sealed record EnergyUnitRow(string Name, string Value);
public sealed record EnergyBar(string Label, string Value, double Height, double Opacity);

public sealed partial class EnergyPage : Page
{
    private readonly EnergyService _energy = App.GetService<EnergyService>();
    private bool _loading;

    public EnergyPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var families = _energy.FamilyIds;
        FamilyBox.ItemsSource = families;
        FamilyBox.Visibility = families.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (families.Count > 0) FamilyBox.SelectedIndex = 0;
        else ShowInfo(InfoBarSeverity.Informational, L.T("Waiting for airCloud to report your units…"));
        await LoadAsync(force: false);
    }

    private async void FamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadAsync(false);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(force: true);

    private async Task LoadAsync(bool force)
    {
        if (_loading || FamilyBox.SelectedItem is not string family) return;
        _loading = true;
        LoadingBar.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        Info.IsOpen = false;
        try
        {
            var off = await _energy.GetTrackingOffNamesAsync(family);
            TrackingInfo.Message = off.Count == 0 ? ""
                : L.F("Energy tracking is off for {0}. Turn it on in the airCloud Go app to see figures here.", string.Join(", ", off));
            TrackingInfo.IsOpen = off.Count > 0;

            var (months, source) = await _energy.GetMonthsAsync(family, 6, force);
            var current = months[^1].Report;
            ShowSource(source, force);
            MonthTitle.Text = L.F("This month ({0:MMMM yyyy})", months[^1].Month);

            if (current is null || current.Units.Count == 0)
            {
                TotalKWh.Text = "--";
                TotalCost.Text = "--";
                UnitsList.ItemsSource = null;
                if (off.Count == 0)
                    ShowInfo(InfoBarSeverity.Informational, L.T("airCloud returned no energy data for this month."));
            }
            else
            {
                TotalKWh.Text = current.TotalKWh is { } k ? $"{k:0.##} kWh" : "--";
                var hasCost = current.Units.Any(u => u.Cost is not null);
                var cost = current.Units.Sum(u => u.Cost ?? 0);
                TotalCost.Text = hasCost ? $"{cost:0.##} {current.Currency}".Trim() : "--";
                UnitsList.ItemsSource = current.Units.Select(u => new EnergyUnitRow(u.Name,
                    (u.KWh is { } kw ? $"{kw:0.##} kWh" : "--") + (u.Cost is { } c ? $" · {c:0.##} {current.Currency}" : ""))).ToList();
            }

            var max = months.Select(m => m.Report?.TotalKWh ?? 0).DefaultIfEmpty(0).Max();
            Bars.ItemsSource = months.Select(m =>
            {
                var v = m.Report?.TotalKWh;
                var h = max > 0 && v is { } x ? Math.Max(4, 150 * x / max) : 4;
                return new EnergyBar(m.Label, v is { } y ? $"{y:0.#}" : "--", h, v is null ? 0.3 : 1);
            }).ToList();
        }
        catch (AirCloudException ex) when (ex.Kind == AirCloudErrorKind.RateLimited)
        {
            ShowInfo(InfoBarSeverity.Warning, L.F("airCloud is rate limiting energy requests. Try again after {0:HH:mm}.", _energy.BlockedUntil.LocalDateTime));
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, L.F("Could not load energy data: {0}", ex.Message));
        }
        finally
        {
            _loading = false;
            LoadingBar.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ShowSource(EnergySource source, bool forced)
    {
        if (source.SavedAt is not { } at)
        {
            SourceText.Text = "";
            return;
        }
        var text = L.F("Figures from {0:HH:mm}.", at.LocalDateTime);
        if (DateTimeOffset.Now < source.BlockedUntil)
            text += " " + L.F("airCloud limits how often energy can be loaded, so they refresh after {0:HH:mm}.", source.BlockedUntil.LocalDateTime);
        else if (forced && source.FromSaved)
            text += " " + L.T("They were loaded a few minutes ago, so they weren't loaded again.");
        else
            text += " " + L.T("They refresh every 6 hours.");
        SourceText.Text = text;
    }

    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        Info.Severity = severity;
        Info.Message = message;
        Info.IsOpen = true;
    }
}
