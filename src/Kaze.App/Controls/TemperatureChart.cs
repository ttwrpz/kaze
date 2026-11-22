using Kaze.App.Helpers;
using Kaze.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Kaze.App.Controls;

/// <summary>
/// Room temperature (solid accent line) and set point (dashed step line, only while on) over time.
/// One shared °C axis. Compact mode draws only the room line, as a sparkline.
/// </summary>
public sealed partial class TemperatureChart : Grid
{
    private const double AxisWidth = 32;
    private const double AxisHeight = 20;
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(20);

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly Line _cursor = new() { StrokeThickness = 1, Visibility = Visibility.Collapsed };
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly Border _tip = new() { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
    private readonly TextBlock _tipText = new();
    private IReadOnlyList<HistorySample> _samples = [];
    private DateTimeOffset _from, _to;
    private double _min, _max;
    private Rect _plot;

    public TemperatureChart()
    {
        Children.Add(_canvas);
        Children.Add(_empty);
        _tip.Child = _tipText;
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerExited += (_, _) => HideHover();
    }

    /// <summary>Sparkline: no axes, labels or hover.</summary>
    public bool Compact { get; set; }

    public string EmptyText { get; set; } = "";

    public void SetData(IReadOnlyList<HistorySample> samples, DateTimeOffset from, DateTimeOffset to)
    {
        _samples = samples;
        _from = from;
        _to = to;
        Redraw();
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void Redraw()
    {
        _canvas.Children.Clear();
        var rooms = _samples.Where(s => s.Room is not null).ToList();
        var hasData = rooms.Count >= 2;
        _empty.Text = hasData || Compact ? "" : EmptyText;
        _empty.Foreground = Res("TextFillColorSecondaryBrush");
        _empty.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        if (!hasData || ActualWidth < 40 || ActualHeight < 20) return;

        var values = rooms.Select(s => s.Room!.Value).ToList();
        if (!Compact) values.AddRange(_samples.Where(s => s.PowerOn).Select(s => s.Target));
        _min = Math.Floor(values.Min()) - (Compact ? 0.2 : 1);
        _max = Math.Ceiling(values.Max()) + (Compact ? 0.2 : 1);
        if (_max - _min < 2) { _min -= 1; _max += 1; }

        _plot = Compact
            ? new Rect(2, 2, ActualWidth - 4, ActualHeight - 4)
            : new Rect(AxisWidth, 6, Math.Max(1, ActualWidth - AxisWidth - 6), Math.Max(1, ActualHeight - AxisHeight - 6));

        if (!Compact) DrawAxes();

        if (!Compact)
        {
            var target = Res("TextFillColorSecondaryBrush");
            Polyline? seg = null;
            HistorySample? prev = null;
            foreach (var s in _samples)
            {
                if (!s.PowerOn || (prev is not null && s.At - prev.At > Gap)) seg = null;
                if (s.PowerOn)
                {
                    if (seg is null)
                    {
                        seg = new Polyline { Stroke = target, StrokeThickness = 2, StrokeDashArray = [3, 2], StrokeLineJoin = PenLineJoin.Round };
                        _canvas.Children.Add(seg);
                    }
                    else if (prev is not null)
                    {
                        seg.Points.Add(new Point(X(s.At), Y(prev.Target)));
                    }
                    seg.Points.Add(new Point(X(s.At), Y(s.Target)));
                }
                prev = s;
            }
        }

        // Gaps in recording (app closed, PC asleep) break the line.
        var accent = Res("AccentFillColorDefaultBrush");
        Polyline? line = null;
        HistorySample? last = null;
        foreach (var s in rooms)
        {
            if (line is null || (last is not null && s.At - last.At > Gap))
            {
                line = new Polyline { Stroke = accent, StrokeThickness = Compact ? 1.5 : 2, StrokeLineJoin = PenLineJoin.Round };
                _canvas.Children.Add(line);
            }
            line.Points.Add(new Point(X(s.At), Y(s.Room!.Value)));
            last = s;
        }

        if (!Compact)
        {
            _cursor.Stroke = Res("ControlStrokeColorDefaultBrush");
            _dot.Fill = accent;
            _dot.Stroke = Res("CardBackgroundFillColorDefaultBrush");
            _tip.Background = Res("AcrylicInAppFillColorDefaultBrush");
            _tip.BorderBrush = Res("SurfaceStrokeColorFlyoutBrush");
            _tipText.Foreground = Res("TextFillColorPrimaryBrush");
            _canvas.Children.Add(_cursor);
            _canvas.Children.Add(_dot);
            _canvas.Children.Add(_tip);
        }
    }

    private void DrawAxes()
    {
        var grid = Res("DividerStrokeColorDefaultBrush");
        var muted = Res("TextFillColorTertiaryBrush");
        var step = Math.Max(1, Math.Ceiling((_max - _min) / 4));
        for (var v = Math.Ceiling(_min); v <= _max; v += step)
        {
            var y = Y(v);
            _canvas.Children.Add(new Line { X1 = _plot.Left, X2 = _plot.Right, Y1 = y, Y2 = y, Stroke = grid, StrokeThickness = 1 });
            var label = new TextBlock { Text = $"{v:0}°", FontSize = 11, Foreground = muted };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 8);
            _canvas.Children.Add(label);
        }

        var span = _to - _from;
        var format = span > TimeSpan.FromDays(2) ? "ddd d" : "HH:mm";
        for (var i = 0; i <= 4; i++)
        {
            var t = _from + span * (i / 4.0);
            var label = new TextBlock { Text = t.ToLocalTime().ToString(format), FontSize = 11, Foreground = muted };
            label.Measure(new Size(200, 50));
            var x = X(t) - label.DesiredSize.Width / 2;
            Canvas.SetLeft(label, Math.Clamp(x, _plot.Left, ActualWidth - label.DesiredSize.Width));
            Canvas.SetTop(label, _plot.Bottom + 4);
            _canvas.Children.Add(label);
        }
    }

    private double X(DateTimeOffset t) => _plot.Left + _plot.Width * Math.Clamp((t - _from) / (_to - _from), 0, 1);

    private double Y(double v) => _plot.Bottom - _plot.Height * (v - _min) / (_max - _min);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Compact || _samples.Count < 2) return;
        var p = e.GetCurrentPoint(_canvas).Position;
        if (p.X < _plot.Left || p.X > _plot.Right) { HideHover(); return; }
        var t = _from + (_to - _from) * ((p.X - _plot.Left) / _plot.Width);
        var nearest = _samples.MinBy(s => Math.Abs((s.At - t).Ticks))!;
        if (Math.Abs((nearest.At - t).TotalMinutes) > 30) { HideHover(); return; }

        var x = X(nearest.At);
        _cursor.X1 = _cursor.X2 = x;
        _cursor.Y1 = _plot.Top;
        _cursor.Y2 = _plot.Bottom;
        _cursor.Visibility = Visibility.Visible;
        if (nearest.Room is { } room)
        {
            Canvas.SetLeft(_dot, x - 5);
            Canvas.SetTop(_dot, Y(room) - 5);
            _dot.Visibility = Visibility.Visible;
        }
        else _dot.Visibility = Visibility.Collapsed;

        var local = nearest.At.ToLocalTime();
        var lines = new List<string> { local.ToString(_to - _from > TimeSpan.FromDays(2) ? "ddd d MMM HH:mm" : "HH:mm") };
        if (nearest.Room is { } r) lines.Add(L.F("Room {0:0.#}°", r));
        lines.Add(nearest.PowerOn ? L.F("Set {0:0.#}° · {1}", nearest.Target, Core.Models.AcModes.Display(nearest.Mode)) : L.T("Off"));
        _tipText.Text = string.Join("\n", lines);
        _tip.Visibility = Visibility.Visible;
        _tip.Measure(new Size(300, 200));
        var w = _tip.DesiredSize.Width;
        Canvas.SetLeft(_tip, x + 12 + w > _plot.Right ? x - 12 - w : x + 12);
        Canvas.SetTop(_tip, _plot.Top);
    }

    private void HideHover()
    {
        _cursor.Visibility = _dot.Visibility = _tip.Visibility = Visibility.Collapsed;
    }
}
