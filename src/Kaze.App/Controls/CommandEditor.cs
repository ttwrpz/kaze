using Kaze.App.Helpers;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace Kaze.App.Views;

/// <summary>Edits an <see cref="AcCommand"/> (each field can be "no change"). Used by schedules and presets.</summary>
public sealed partial class CommandEditor : StackPanel
{
    private static readonly string NoChange = L.T("No change");
    private static readonly string On = L.T("On");
    private static readonly string Off = L.T("Off");
    private readonly ComboBox? _unit;
    private readonly ComboBox _power = new() { Header = L.T("Power"), MinWidth = 140 };
    private readonly ComboBox _mode = new() { Header = L.T("Mode"), MinWidth = 140 };
    private readonly NumberBox _temp = new()
    {
        Header = L.T("Temperature (°C)"),
        MinWidth = 140,
        Minimum = 16,
        Maximum = 32,
        SmallChange = 0.5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        PlaceholderText = NoChange,
    };
    private readonly ComboBox _fan = new() { Header = L.T("Fan speed"), MinWidth = 140 };
    private readonly ComboBox _swing = new() { Header = L.T("Swing"), MinWidth = 140 };

    public CommandEditor(AcController controller, AcCommand initial, bool allowUnit, string? unitKey)
    {
        Spacing = 12;

        if (allowUnit)
        {
            _unit = new ComboBox { Header = L.T("Unit"), MinWidth = 200 };
            foreach (var u in controller.Units) _unit.Items.Add(new UnitChoiceItem(u.Key, u.Name));
            _unit.Items.Add(new UnitChoiceItem(null, L.T("All units")));
            _unit.SelectedItem = _unit.Items.OfType<UnitChoiceItem>().FirstOrDefault(i => i.Key == unitKey)
                ?? _unit.Items.OfType<UnitChoiceItem>().Last();
            Children.Add(_unit);
        }

        Fill(_power, [On, Off], initial.PowerOn switch { true => On, false => Off, _ => null });
        Fill(_mode, AcModes.All.Select(AcModes.Display).ToList(), initial.Mode is { } m ? AcModes.Display(m) : null);
        Fill(_fan, FanSpeeds.All.Take(7).Select(FanSpeeds.Display).ToList(), initial.FanSpeed is { } f ? FanSpeeds.Display(f) : null);
        Fill(_swing, FanSwings.All.Select(FanSwings.Display).ToList(), initial.FanSwing is { } s ? FanSwings.Display(s) : null);
        _temp.Value = initial.TargetTemperature ?? double.NaN;

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(_power);
        row1.Children.Add(_mode);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(_temp);
        row2.Children.Add(_fan);
        Children.Add(row1);
        Children.Add(row2);
        Children.Add(_swing);
    }

    public string? UnitKey => (_unit?.SelectedItem as UnitChoiceItem)?.Key;

    public AcCommand Result => new()
    {
        PowerOn = _power.SelectedItem as string == On ? true : _power.SelectedItem as string == Off ? false : null,
        Mode = Pick(_mode, AcModes.All, AcModes.Display),
        FanSpeed = Pick(_fan, FanSpeeds.All, FanSpeeds.Display),
        FanSwing = Pick(_swing, FanSwings.All, FanSwings.Display),
        TargetTemperature = double.IsNaN(_temp.Value) ? null : Math.Round(_temp.Value * 2) / 2,
    };

    private static void Fill(ComboBox box, IReadOnlyList<string> items, string? selected)
    {
        box.Items.Add(NoChange);
        foreach (var i in items) box.Items.Add(i);
        box.SelectedItem = selected ?? NoChange;
    }

    private static string? Pick(ComboBox box, IReadOnlyList<string> keys, Func<string, string> display) =>
        box.SelectedItem is string label && label != NoChange ? keys.FirstOrDefault(k => display(k) == label) : null;
}
