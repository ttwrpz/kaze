using Microsoft.Extensions.Logging;
using WinUIEx.Messaging;

namespace Kaze.App.Services;

/// <summary>System-wide hotkeys via RegisterHotKey on the (hidden) main window.</summary>
public sealed class HotkeyService : IDisposable
{
    private readonly ILogger<HotkeyService> _log;
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private WindowMessageMonitor? _monitor;
    private nint _hwnd;

    public HotkeyService(ILogger<HotkeyService> log) => _log = log;

    public event Action<HotkeyAction>? Pressed;

    /// <summary>Bindings that could not be registered (in use by another app).</summary>
    public IReadOnlyCollection<HotkeyAction> Failed => _failed;
    private readonly HashSet<HotkeyAction> _failed = [];

    public void Attach(Microsoft.UI.Xaml.Window window)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _monitor = new WindowMessageMonitor(window);
        _monitor.WindowMessageReceived += (_, e) =>
        {
            if (e.Message.MessageId == Native.WM_HOTKEY && _registered.TryGetValue((int)e.Message.WParam, out var action))
            {
                e.Handled = true;
                Pressed?.Invoke(action);
            }
        };
    }

    public void Apply(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        _failed.Clear();
        if (_hwnd == 0) return;
        var id = 0x4A00;
        foreach (var b in bindings.Where(b => b.Enabled && b.Key != 0))
        {
            id++;
            if (Native.RegisterHotKey(_hwnd, id, b.Modifiers | Native.MOD_NOREPEAT, b.Key))
                _registered[id] = b.Action;
            else
            {
                _failed.Add(b.Action);
                _log.LogWarning("Hotkey {Hotkey} for {Action} is in use by another application", Describe(b), b.Action);
            }
        }
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys) Native.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    public static string Describe(HotkeyBinding b)
    {
        if (b.Key == 0) return Kaze.App.Helpers.L.T("None");
        var parts = new List<string>();
        if ((b.Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((b.Modifiers & 1) != 0) parts.Add("Alt");
        if ((b.Modifiers & 4) != 0) parts.Add("Shift");
        if ((b.Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(KeyName(b.Key));
        return string.Join(" + ", parts);
    }

    public static string KeyName(uint vk) => vk switch
    {
        0x26 => "Up",
        0x28 => "Down",
        0x25 => "Left",
        0x27 => "Right",
        0x20 => "Space",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x24 => "Home",
        0x23 => "End",
        0xBB => "=",
        0xBD => "-",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
        0x6B => "Num+",
        0x6D => "Num-",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => $"0x{vk:X2}",
    };

    public void Dispose()
    {
        UnregisterAll();
        _monitor?.Dispose();
    }
}
