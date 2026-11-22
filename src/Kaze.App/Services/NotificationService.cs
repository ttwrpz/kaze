using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Kaze.App.Services;

/// <summary>A button on a toast. <paramref name="Action"/> is handled by <see cref="App"/>.</summary>
public sealed record ToastButton(string Label, string Action, string? Unit = null, string? Value = null);

public static class ToastActions
{
    public const string PowerOn = "power-on";
    public const string PowerOff = "power-off";
    public const string FrostWash = "frost-wash";
    public const string SnoozeFrostWash = "snooze-frost-wash";
    public const string SnoozeTimer = "snooze-timer";
    public const string CancelTimer = "cancel-timer";
    public const string InstallUpdate = "install-update";
    public const string SkipUpdate = "skip-update";
    public const string OpenEnergy = "open-energy";
    public const string CancelSleep = "cancel-sleep";
}

/// <summary>Windows toast notifications (works for unpackaged apps).</summary>
public sealed class NotificationService(ILogger<NotificationService> log) : IDisposable
{
    private bool _registered;

    public event Action? Clicked;

    /// <summary>Raised when the user clicks a toast button: (action, unit, value).</summary>
    public event Action<string, string?, string?>? ActionInvoked;

    public void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Toast notifications unavailable");
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("action", out var action) && !string.IsNullOrEmpty(action))
        {
            args.Arguments.TryGetValue("unit", out var unit);
            args.Arguments.TryGetValue("value", out var value);
            ActionInvoked?.Invoke(action, string.IsNullOrEmpty(unit) ? null : unit, string.IsNullOrEmpty(value) ? null : value);
        }
        else
        {
            Clicked?.Invoke();
        }
    }

    public void Show(string title, string message, params ToastButton[] buttons) => Show(title, message, null, buttons);

    /// <summary>Shows a toast. A toast with the same <paramref name="tag"/> replaces the previous one.</summary>
    public void Show(string title, string message, string? tag, params ToastButton[] buttons)
    {
        if (!_registered) return;
        try
        {
            var b = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message);
            foreach (var button in buttons.Take(5))
            {
                var ab = new AppNotificationButton(button.Label).AddArgument("action", button.Action);
                if (button.Unit is not null) ab.AddArgument("unit", button.Unit);
                if (button.Value is not null) ab.AddArgument("value", button.Value);
                b.AddButton(ab);
            }
            if (tag is not null) b.SetTag(tag);
            var n = b.BuildNotification();
            n.Expiration = DateTimeOffset.Now.AddHours(1);
            AppNotificationManager.Default.Show(n);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Toast failed");
        }
    }

    public void Dispose()
    {
        if (!_registered) return;
        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch { }
    }
}
