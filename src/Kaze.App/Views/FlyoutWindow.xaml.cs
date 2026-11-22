using Kaze.App.Services;
using Kaze.Core.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinUIEx;

namespace Kaze.App.Views;

/// <summary>Compact popup shown when the tray icon is clicked, positioned above the taskbar.</summary>
public sealed partial class FlyoutWindow : WindowEx
{
    private const int WidthDip = 372;
    private const int HeightDip = 660;
    private const int MarginDip = 12;
    private DateTime _hiddenAt = DateTime.MinValue;

    public FlyoutWindow()
    {
        InitializeComponent();
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            p.IsResizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        Activated += OnActivated;
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            HideFlyout();
        };
        Root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) HideFlyout();
        };
    }

    public event Action? OpenMainWindowRequested;

    public bool IsOpen => AppWindow.IsVisible;

    public void Toggle()
    {
        if (IsOpen) HideFlyout();
        // A click on the tray icon first deactivates (hides) the flyout. Don't immediately re-open it.
        else if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds > 300) ShowFlyout();
    }

    public void ShowFlyout()
    {
        PositionNearTray();
        AppWindow.Show(true);
        Activate();
        Native.SetForegroundWindow(this.GetWindowHandle());
    }

    public void HideFlyout()
    {
        if (!AppWindow.IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        AppWindow.Hide();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            HideFlyout();
    }

    private void PositionNearTray()
    {
        Native.GetCursorPos(out var cursor);
        var display = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        var outer = display.OuterBounds;
        var scale = Native.GetDpiForWindow(this.GetWindowHandle()) / 96.0;
        if (scale <= 0) scale = 1;

        var w = (int)(WidthDip * scale);
        var h = (int)Math.Min(HeightDip * scale, work.Height - 2 * MarginDip * scale);
        var m = (int)(MarginDip * scale);

        var x = work.X + work.Width - w - m;
        var y = work.Y + work.Height - h - m;
        if (work.Y > outer.Y) y = work.Y + m;                       // taskbar on top
        if (work.X > outer.X) x = work.X + m;                       // taskbar on left
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => App.GetService<AcController>().Refresh();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        OpenMainWindowRequested?.Invoke();
    }
}
