using Kaze.App.Helpers;
using System.Diagnostics;
using System.Reflection;
using Kaze.App.Services;
using Kaze.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Kaze.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsStore _settings = App.GetService<SettingsStore>();
    private readonly AcController _controller = App.GetService<AcController>();
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        var s = _settings.Current;
        BuildAccountRows();
        StartWithWindows.IsOn = s.StartWithWindows;
        TempStep.SelectedIndex = s.TemperatureStep >= 1 ? 1 : 0;
        TempInIcon.IsOn = s.ShowTemperatureInIcon;
        Theme.ItemsSource = Themes.Select(t => t.Label).ToList();
        Theme.SelectedIndex = Math.Max(0, Array.FindIndex(Themes, t => t.Key == s.Theme));
        LanguageBox.ItemsSource = Languages.Select(l => l.Label).ToList();
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, l => l.Key == s.Language));
        UpdateLanguageNote();
        LoadApi();
        LoadUpdates();

        DefaultUnit.Items.Clear();
        foreach (var u in _controller.Units) DefaultUnit.Items.Add(new UnitChoiceItem(u.Key, u.Name));
        DefaultUnit.SelectedItem = DefaultUnit.Items.OfType<UnitChoiceItem>().FirstOrDefault(i => i.Key == _controller.SelectedKey);
        DefaultUnit.IsEnabled = DefaultUnit.Items.Count > 1;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Kaze {v?.ToString(3)}";
        BuildHotkeyRows();
        _loading = false;
    }

    private static readonly (string Key, string Label)[] Themes =
        [("System", L.T("Use system setting")), ("Light", L.T("Light")), ("Dark", L.T("Dark"))];

    private static readonly (string Key, string Label)[] Languages =
        [("System", L.T("Windows display language")), ("en", "English"), ("th", "ไทย (Thai)")];

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedIndex < 0) return;
        _settings.Update(s => s.Language = Languages[LanguageBox.SelectedIndex].Key);
        UpdateLanguageNote();
    }

    private void UpdateLanguageNote()
    {
        var pending = L.Resolve(_settings.Current.Language) != L.Language;
        RestartButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        LanguageNote.Text = pending ? L.T("The new language is used after a restart.") : L.T("Windows display language is used by default.");
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => _ = App.Current!.RestartAsync();

    private readonly LocalApiServer _api = App.GetService<LocalApiServer>();

    private void LoadApi()
    {
        var a = _settings.Current.LocalApi;
        ApiEnabled.IsOn = a.Enabled;
        ApiPort.Value = a.Port;
        ApiNetwork.IsOn = a.AllowNetwork;
        ApiExpander.IsExpanded = a.Enabled;
        UpdateApiText();
    }

    private void UpdateApiText()
    {
        var a = _settings.Current.LocalApi;
        ApiToken.Text = a.Token;
        ApiStatus.Text = !a.Enabled ? L.T("Off.")
            : _api.Error is { } err ? L.F("Not running: {0}", err)
            : _api.Running ? L.F("Running at {0}", _api.BaseUrl)
            : L.T("Starting...");
        ApiExample.Text =
            $"curl -H \"Authorization: Bearer {a.Token}\" {_api.BaseUrl}/units\n" +
            $"curl -X POST -H \"Authorization: Bearer {a.Token}\" -d \"{{\\\"power\\\":\\\"toggle\\\"}}\" {_api.BaseUrl}/units/selected/control\n" +
            $"curl -X POST -H \"Authorization: Bearer {a.Token}\" \"{_api.BaseUrl}/presets/Sleep\"";
    }

    private void Api_Changed(object sender, object e)
    {
        if (_loading) return;
        _settings.Update(s =>
        {
            s.LocalApi.Enabled = ApiEnabled.IsOn;
            if (!double.IsNaN(ApiPort.Value)) s.LocalApi.Port = (int)Math.Clamp(ApiPort.Value, 1024, 65535);
            s.LocalApi.AllowNetwork = ApiNetwork.IsOn;
        });
        _api.Apply();
        UpdateApiText();
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(_settings.Current.LocalApi.Token);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
    }

    private void NewToken_Click(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.LocalApi.Token = LocalApiSettings.NewToken());
        UpdateApiText();
    }

    private readonly UpdateService _updates = App.GetService<UpdateService>();

    private void LoadUpdates()
    {
        var u = _settings.Current.Updates;
        UpdateRepo.Text = u.Repository ?? "";
        UpdateAuto.IsOn = u.CheckAutomatically;
        _updates.Changed -= OnUpdatesChanged;
        _updates.Changed += OnUpdatesChanged;
        UpdateUpdatesText();
    }

    private void OnUpdatesChanged() => DispatcherQueue.TryEnqueue(UpdateUpdatesText);

    private void UpdateUpdatesText()
    {
        var u = _settings.Current.Updates;
        UpdatesExpander.Description = _updates.Status
            ?? (UpdateService.IsValidRepository(u.Repository)
                ? (u.LastCheck is { } t ? L.F("Last checked {0:g}.", t.LocalDateTime) : L.T("Not checked yet."))
                : L.T("Set the release source to turn on update checks."));
        InstallUpdateButton.Visibility = _updates.Available is not null ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = CheckUpdatesButton.IsEnabled = !_updates.Busy;
    }

    private void UpdateRepo_LostFocus(object sender, RoutedEventArgs e)
    {
        var repo = UpdateRepo.Text.Trim();
        if (repo.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) repo = repo["https://github.com/".Length..].Trim('/');
        _settings.Update(s => s.Updates.Repository = repo.Length == 0 ? null : repo);
        UpdateRepo.Text = repo;
        UpdateUpdatesText();
    }

    private void UpdateAuto_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s.Updates.CheckAutomatically = UpdateAuto.IsOn);
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateRepo_LostFocus(sender, e);
        _ = _updates.CheckAsync(userInitiated: true);
    }

    private void InstallUpdate_Click(object sender, RoutedEventArgs e) => _ = App.Current!.InstallUpdateAsync();

    private void General_Changed(object sender, object e)
    {
        if (_loading) return;
        var themeBefore = _settings.Current.Theme;
        _settings.Update(s =>
        {
            s.StartWithWindows = StartWithWindows.IsOn;
            s.TemperatureStep = TempStep.SelectedIndex == 1 ? 1 : 0.5;
            s.ShowTemperatureInIcon = TempInIcon.IsOn;
            s.Theme = Theme.SelectedIndex >= 0 ? Themes[Theme.SelectedIndex].Key : "System";
            if (DefaultUnit.SelectedItem is UnitChoiceItem { Key: { } k }) s.SelectedUnitKey = k;
        });
        try { StartupService.Set(StartWithWindows.IsOn); } catch { }
        if (DefaultUnit.SelectedItem is UnitChoiceItem { Key: { } key } && key != _controller.SelectedKey)
            _controller.SelectedKey = key;
        if (themeBefore != _settings.Current.Theme) App.Current!.ApplyTheme();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (App.Current is { } app) app.AccountsChanged -= OnAccountsChanged;
        _updates.Changed -= OnUpdatesChanged;
    }

    private void OnAccountsChanged() => DispatcherQueue.TryEnqueue(BuildAccountRows);

    private void BuildAccountRows()
    {
        var app = App.Current!;
        app.AccountsChanged -= OnAccountsChanged;
        app.AccountsChanged += OnAccountsChanged;

        var active = app.ActiveAccount;
        var accounts = app.Accounts;
        AccountText.Text = active is null ? L.T("Not signed in")
            : accounts.Count == 1 ? L.F("Signed in as {0}", active)
            : L.F("Signed in as {0}. {1} accounts saved on this PC.", active, accounts.Count);

        AccountsExpander.Items.Clear();
        foreach (var account in accounts)
        {
            var login = account;
            var isActive = CredentialStore.SameLogin(login, active);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (isActive)
            {
                var reconnect = new Button { Content = L.T("Reconnect") };
                reconnect.Click += async (_, _) => await app.RestartConnectionAsync();
                buttons.Children.Add(reconnect);
            }
            else
            {
                var use = new Button { Content = L.T("Switch"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                use.Click += async (_, _) => await app.SwitchAccountAsync(login);
                buttons.Children.Add(use);
            }

            var remove = new Button { Content = isActive ? L.T("Sign out") : L.T("Remove") };
            var confirm = new Button { Content = isActive ? L.T("Sign out") : L.T("Remove"), HorizontalAlignment = HorizontalAlignment.Right };
            var flyout = new Flyout
            {
                Content = new StackPanel
                {
                    MaxWidth = 280,
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = isActive
                                ? L.T("Sign out and remove this account from Kaze? Its saved password is deleted from this PC.")
                                : L.T("Remove this account from Kaze? Its saved password is deleted from this PC."),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        confirm,
                    },
                },
            };
            confirm.Click += async (_, _) =>
            {
                flyout.Hide();
                await app.RemoveAccountAsync(login);
            };
            remove.Flyout = flyout;
            buttons.Children.Add(remove);

            var card = new CommunityToolkit.WinUI.Controls.SettingsCard { Header = login, Content = buttons };
            if (isActive) card.Description = L.T("In use");
            AccountsExpander.Items.Add(card);
        }
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e) => App.Current!.ShowAddAccount();

    private static string ActionName(HotkeyAction a) => a switch
    {
        HotkeyAction.TogglePower => L.T("Power on/off"),
        HotkeyAction.TemperatureUp => L.T("Warmer"),
        HotkeyAction.TemperatureDown => L.T("Cooler"),
        HotkeyAction.ShowFlyout => L.T("Show quick panel"),
        HotkeyAction.ShowMainWindow => L.T("Open main window"),
        _ => a.ToString(),
    };

    private void BuildHotkeyRows()
    {
        HotkeyRows.Children.Clear();
        var failed = App.GetService<HotkeyService>().Failed;
        foreach (var b in _settings.Current.Hotkeys)
        {
            var binding = b;
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = ActionName(binding.Action) });
            if (binding.Enabled && failed.Contains(binding.Action))
                label.Children.Add(new TextBlock
                {
                    Text = L.T("In use by another app. Pick a different shortcut."),
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                });

            var box = new TextBox { Text = HotkeyService.Describe(binding), MinWidth = 180, IsReadOnly = true };
            box.PreviewKeyDown += (_, e) => Capture(binding, box, e);
            Grid.SetColumn(box, 1);

            var toggle = new ToggleSwitch { IsOn = binding.Enabled, MinWidth = 0, OnContent = "", OffContent = "" };
            toggle.Toggled += (_, _) => Save(binding with { Enabled = toggle.IsOn }, rebuild: false);
            Grid.SetColumn(toggle, 2);

            row.Children.Add(label);
            row.Children.Add(box);
            row.Children.Add(toggle);
            HotkeyRows.Children.Add(row);
        }
    }

    private void Capture(HotkeyBinding binding, TextBox box, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        var key = e.Key;
        if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftWindows or VirtualKey.RightWindows
            or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftShift or VirtualKey.RightShift)
            return;
        if (key == VirtualKey.Tab) { e.Handled = false; return; }

        uint mods = 0;
        if (IsDown(VirtualKey.Menu)) mods |= 1;
        if (IsDown(VirtualKey.Control)) mods |= 2;
        if (IsDown(VirtualKey.Shift)) mods |= 4;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= 8;
        if (mods == 0)
        {
            box.Text = L.T("Add Ctrl, Alt, Shift or Win");
            return;
        }
        var updated = binding with { Modifiers = mods, Key = (uint)key, Enabled = true };
        box.Text = HotkeyService.Describe(updated);
        Save(updated, rebuild: true);
    }

    private static bool IsDown(VirtualKey k) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void Save(HotkeyBinding updated, bool rebuild)
    {
        _settings.Update(s =>
        {
            var i = s.Hotkeys.FindIndex(h => h.Action == updated.Action);
            if (i >= 0) s.Hotkeys[i] = updated; else s.Hotkeys.Add(updated);
        });
        App.Current!.ApplyHotkeys();
        if (rebuild) DispatcherQueue.TryEnqueue(BuildHotkeyRows);
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.Hotkeys = AppSettings.DefaultHotkeys());
        App.Current!.ApplyHotkeys();
        BuildHotkeyRows();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(SettingsStore.DataDirectory, "logs"));

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(SettingsStore.DataDirectory);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{path}\"") { UseShellExecute = false });
    }
}
