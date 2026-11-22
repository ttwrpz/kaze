using System.Drawing;
using System.Drawing.Drawing2D;
using Kaze.App.Services;
using Kaze.Core.Models;
using Microsoft.Win32;

namespace Kaze.App.Tray;

public enum TrayIconKind { Disconnected, Offline, Off, On }

/// <summary>
/// Draws the tray icon in the Windows 11 style: a single colour glyph that matches the taskbar
/// (white on a dark taskbar, near black on a light one), like the system's own tray icons.
/// On: the set temperature with a small bar in the mode colour (or the unit with coloured airflow
/// when the temperature is hidden). Off: the unit outline. Offline or not connected: the unit with
/// a warning badge.
/// </summary>
public static class TrayIconRenderer
{
    public static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }

    /// <summary>The small icon size for the system DPI: 16 px at 100%, 24 at 150%, 32 at 200%.</summary>
    public static int TraySize()
    {
        var dpi = Native.GetDpiForSystem();
        var size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi);
        return size is >= 16 and <= 64 ? size : 16;
    }

    public static Icon Render(TrayIconKind kind, string? mode, double? temperature, bool showText, int size, bool lightTaskbar)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            var fg = lightTaskbar ? Color.FromArgb(255, 26, 26, 26) : Color.White;

            switch (kind)
            {
                case TrayIconKind.On when showText && temperature is { } t:
                    DrawTemperature(g, size, ((int)Math.Floor(t)).ToString(), fg);
                    DrawModeBar(g, size, ModeColor(mode, lightTaskbar));
                    break;
                case TrayIconKind.On:
                    DrawUnit(g, size, fg, ModeColor(mode, lightTaskbar));
                    break;
                case TrayIconKind.Off:
                    DrawUnit(g, size, fg, airflow: null);
                    break;
                default:
                    DrawUnit(g, size, Color.FromArgb(lightTaskbar ? 150 : 170, fg), ModeColor(null, lightTaskbar, muted: true));
                    DrawWarningBadge(g, size, lightTaskbar);
                    break;
            }
        }

        var h = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(h);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(h);
        }
    }

    private static void DrawTemperature(Graphics g, int size, string text, Color fg)
    {
        using var path = new GraphicsPath();
        using (var family = PickFamily())
        using (var format = StringFormat.GenericTypographic)
            path.AddString(text, family, (int)FontStyle.Bold, 100, PointF.Empty, format);

        var ink = path.GetBounds();
        var boxW = size * (text.Length >= 2 ? 1.0f : 0.62f);
        var boxH = size * 0.70f;
        var scale = Math.Min(boxW / ink.Width, boxH / ink.Height);
        var w = ink.Width * scale;
        var h = ink.Height * scale;
        // Snap the top to a whole pixel so the digits' flat tops stay sharp.
        var top = MathF.Round((size * 0.80f - h) / 2);
        using var m = new Matrix();
        m.Translate((size - w) / 2, top);
        m.Scale(scale, scale);
        m.Translate(-ink.X, -ink.Y);
        path.Transform(m);
        using var brush = new SolidBrush(fg);
        g.FillPath(brush, path);
    }

    private static FontFamily PickFamily()
    {
        foreach (var name in new[] { "Segoe UI Variable Display", "Segoe UI" })
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { }
        }
        return new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif);
    }

    private static void DrawModeBar(Graphics g, int size, Color color)
    {
        var h = MathF.Max(2, MathF.Round(size / 8f));
        var w = MathF.Round(size * 0.56f);
        var rect = new RectangleF((size - w) / 2, size - h - MathF.Floor(size / 32f), w, h);
        using var path = RoundedRect(rect, h / 2);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static void DrawUnit(Graphics g, int size, Color fg, Color? airflow)
    {
        var stroke = MathF.Max(1, MathF.Round(size / 16f));
        var half = stroke / 2;
        var bodyH = MathF.Round(size * (airflow is null ? 0.56f : 0.50f));
        var top = airflow is null ? MathF.Round((size - bodyH) / 2) : MathF.Round(size * 0.03f);
        var body = new RectangleF(half, top + half, size - stroke, bodyH - stroke);

        using (var pen = new Pen(fg, stroke) { LineJoin = LineJoin.Round })
        using (var path = RoundedRect(body, MathF.Max(1.5f, size * 0.10f)))
            g.DrawPath(pen, path);

        var slotH = MathF.Max(1, MathF.Round(size / 12f));
        var slotY = top + bodyH - stroke - slotH - MathF.Max(1, MathF.Round(size / 16f));
        using (var brush = new SolidBrush(fg))
            g.FillRectangle(brush, MathF.Round(size * 0.22f), slotY, size - 2 * MathF.Round(size * 0.22f), slotH);

        if (airflow is not { } color) return;

        var w = MathF.Max(1.25f, size / 12f);
        using var flow = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var y0 = top + bodyH + MathF.Max(1.5f, size * 0.08f);
        var y1 = size - w / 2 - MathF.Max(0, size / 32f);
        var amp = size * 0.06f;
        foreach (var fx in new[] { 0.28f, 0.50f, 0.72f })
        {
            var x = size * fx;
            var dy = (y1 - y0) / 3;
            g.DrawBezier(flow,
                new PointF(x, y0), new PointF(x + amp * 1.6f, y0 + dy),
                new PointF(x - amp * 1.6f, y0 + 2 * dy), new PointF(x, y1));
        }
    }

    /// <summary>A yellow "!" badge in the bottom right corner, cut out from the glyph like Windows' own badges.</summary>
    private static void DrawWarningBadge(Graphics g, int size, bool lightTaskbar)
    {
        var d = MathF.Round(size * 0.56f);
        var gap = MathF.Max(1, MathF.Round(size / 16f));
        var badge = new RectangleF(size - d, size - d, d, d);

        // Clear a ring around the badge so it stands apart from the unit.
        var oldMode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
            g.FillEllipse(clear, RectangleF.Inflate(badge, gap, gap));
        g.CompositingMode = oldMode;

        using (var fill = new SolidBrush(lightTaskbar ? Color.FromArgb(255, 157, 93, 0) : Color.FromArgb(255, 252, 225, 0)))
            g.FillEllipse(fill, badge);

        // The "!" drawn as a bar and a dot, which stays crisp at 16 px where a font glyph would blur.
        var ink = lightTaskbar ? Color.White : Color.Black;
        var cx = badge.X + d / 2;
        var bw = MathF.Max(1.5f, d / 6f);
        using var brush = new SolidBrush(ink);
        g.FillRectangle(brush, cx - bw / 2, badge.Y + d * 0.20f, bw, d * 0.38f);
        g.FillEllipse(brush, cx - bw / 2, badge.Y + d * 0.66f, bw, bw);
    }

    /// <summary>Mode colours, lighter on a dark taskbar and deeper on a light one so both have contrast.</summary>
    public static Color ModeColor(string? mode, bool lightTaskbar, bool muted = false)
    {
        if (muted) return lightTaskbar ? Color.FromArgb(255, 138, 138, 138) : Color.FromArgb(255, 150, 150, 150);
        return (mode?.ToUpperInvariant(), lightTaskbar) switch
        {
            (AcModes.Heating, false) => Color.FromArgb(255, 255, 143, 107),
            (AcModes.Heating, true) => Color.FromArgb(255, 196, 66, 27),
            (AcModes.Dry, false) => Color.FromArgb(255, 79, 209, 197),
            (AcModes.Dry, true) => Color.FromArgb(255, 0, 128, 127),
            (AcModes.Fan, false) => Color.FromArgb(255, 139, 209, 124),
            (AcModes.Fan, true) => Color.FromArgb(255, 63, 122, 46),
            (AcModes.Auto, false) => Color.FromArgb(255, 183, 156, 240),
            (AcModes.Auto, true) => Color.FromArgb(255, 107, 79, 168),
            (_, false) => Color.FromArgb(255, 76, 194, 255),
            (_, true) => Color.FromArgb(255, 0, 95, 184),
        };
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        radius = MathF.Min(radius, MathF.Min(r.Width, r.Height) / 2);
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
