using Kaze.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Kaze.App.Helpers;

public enum FluentIconKind
{
    Snowflake, Sunny, Drop, Wind, Sync, Power, Timer, Broom, Temperature, Warning,
    SwingVertical, Star, Sparkle, Refresh, OpenWindow, Dismiss, Exit, Home, Settings, CalendarClock,
    SwingHorizontal, SwingBoth, Pause, Flash, FanLevel1, FanLevel2, FanLevel3, FanLevel4,
}

/// <summary>Builds icons from Fluent UI System Icons path data (designed on a 20 px grid).</summary>
public static class FluentIcons
{
    public static string Data(FluentIconKind kind) => kind switch
    {
        FluentIconKind.Snowflake => FluentIconData.Snowflake,
        FluentIconKind.Sunny => FluentIconData.Sunny,
        FluentIconKind.Drop => FluentIconData.Drop,
        FluentIconKind.Wind => FluentIconData.Wind,
        FluentIconKind.Sync => FluentIconData.Sync,
        FluentIconKind.Power => FluentIconData.Power,
        FluentIconKind.Timer => FluentIconData.Timer,
        FluentIconKind.Broom => FluentIconData.Broom,
        FluentIconKind.Temperature => FluentIconData.Temperature,
        FluentIconKind.Warning => FluentIconData.Warning,
        FluentIconKind.SwingVertical => FluentIconData.SwingVertical,
        FluentIconKind.Star => FluentIconData.Star,
        FluentIconKind.Sparkle => FluentIconData.Sparkle,
        FluentIconKind.Refresh => FluentIconData.Refresh,
        FluentIconKind.OpenWindow => FluentIconData.OpenWindow,
        FluentIconKind.Dismiss => FluentIconData.Dismiss,
        FluentIconKind.Exit => FluentIconData.Exit,
        FluentIconKind.Home => FluentIconData.Home,
        FluentIconKind.Settings => FluentIconData.Settings,
        FluentIconKind.CalendarClock => FluentIconData.CalendarClock,
        FluentIconKind.SwingHorizontal => FluentIconData.SwingHorizontal,
        FluentIconKind.SwingBoth => FluentIconData.SwingBoth,
        FluentIconKind.Pause => FluentIconData.Pause,
        FluentIconKind.Flash => FluentIconData.Flash,
        FluentIconKind.FanLevel1 => FanLevel(1),
        FluentIconKind.FanLevel2 => FanLevel(2),
        FluentIconKind.FanLevel3 => FanLevel(3),
        FluentIconKind.FanLevel4 => FanLevel(4),
        _ => FluentIconData.Sparkle,
    };

    public static FluentIconKind? ForFan(string? speed) => speed?.ToUpperInvariant() switch
    {
        FanSpeeds.Auto or FanSpeeds.LvA => FluentIconKind.Sync,
        FanSpeeds.Lv1 => FluentIconKind.FanLevel1,
        FanSpeeds.Lv2 => FluentIconKind.FanLevel2,
        FanSpeeds.Lv3 => FluentIconKind.FanLevel3,
        FanSpeeds.Lv4 => FluentIconKind.FanLevel4,
        FanSpeeds.Lv5 or FanSpeeds.Lv6 => FluentIconKind.Flash,
        _ => null,
    };

    public static FluentIconKind? ForSwing(string? swing) => swing?.ToUpperInvariant() switch
    {
        FanSwings.Off => FluentIconKind.Pause,
        FanSwings.Vertical => FluentIconKind.SwingVertical,
        FanSwings.Horizontal => FluentIconKind.SwingHorizontal,
        FanSwings.Both => FluentIconKind.SwingBoth,
        _ => null,
    };

    private static string FanLevel(int level)
    {
        var sb = new System.Text.StringBuilder("F0 ");
        for (var i = 0; i < 4; i++)
        {
            double w = 3, x = 1.75 + i * 4.5, h = 5 + i * 3, y = 17 - h;
            sb.Append(RoundRect(x, y, w, h, 1));
            if (i >= level) sb.Append(RoundRect(x + 1, y + 1, w - 2, h - 2, 0));
        }
        return sb.ToString();
    }

    private static string RoundRect(double x, double y, double w, double h, double r)
    {
        static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (r <= 0) return $"M{F(x)} {F(y)}H{F(x + w)}V{F(y + h)}H{F(x)}Z";
        return $"M{F(x + r)} {F(y)}H{F(x + w - r)}A{F(r)} {F(r)} 0 0 1 {F(x + w)} {F(y + r)}V{F(y + h - r)}" +
               $"A{F(r)} {F(r)} 0 0 1 {F(x + w - r)} {F(y + h)}H{F(x + r)}A{F(r)} {F(r)} 0 0 1 {F(x)} {F(y + h - r)}" +
               $"V{F(y + r)}A{F(r)} {F(r)} 0 0 1 {F(x + r)} {F(y)}Z";
    }

    public static FluentIconKind ForMode(string? mode) => mode?.ToUpperInvariant() switch
    {
        AcModes.Cooling => FluentIconKind.Snowflake,
        AcModes.Heating => FluentIconKind.Sunny,
        AcModes.Dry => FluentIconKind.Drop,
        AcModes.Fan => FluentIconKind.Wind,
        _ => FluentIconKind.Sync,
    };

    /// <summary>Creates a themed PathIcon of <paramref name="size"/> epx (use 16, 20 or 24).</summary>
    public static IconElement Create(FluentIconKind kind, double size = 16)
    {
        // Path markup only converts through Geometry typed properties, so let the parser build it
        // via PathIcon.Data and scale the 20 px design grid in code.
        var xaml =
            "<PathIcon xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            $"HorizontalAlignment='Center' VerticalAlignment='Center' Data='{Data(kind)}' />";
        var icon = (PathIcon)XamlReader.Load(xaml);
        icon.Width = icon.Height = size;
        if (size != 20)
            icon.Data.Transform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = size / 20.0, ScaleY = size / 20.0 };
        return icon;
    }
}

/// <summary>XAML: <c>Icon="{h:FluentIcon Kind=Power, Size=16}"</c>.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(IconElement))]
public sealed partial class FluentIconExtension : MarkupExtension
{
    public FluentIconKind Kind { get; set; }
    public double Size { get; set; } = 16;

    protected override object ProvideValue() => FluentIcons.Create(Kind, Size);
}

/// <summary>Bindable host for a Fluent icon (for templates and lists).</summary>
public sealed partial class FluentIconPresenter : ContentPresenter
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(FluentIconKind), typeof(FluentIconPresenter),
        new PropertyMetadata(FluentIconKind.Sparkle, (d, _) => ((FluentIconPresenter)d).Update()));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(FluentIconPresenter),
        new PropertyMetadata(20.0, (d, _) => ((FluentIconPresenter)d).Update()));

    public FluentIconPresenter()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Update();
    }

    public FluentIconKind Kind
    {
        get => (FluentIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    private void Update() => Content = FluentIcons.Create(Kind, IconSize);
}
