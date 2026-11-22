using Kaze.App.Helpers;
using Kaze.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace Kaze.App.Views;

public sealed partial class MainWindow : WindowEx
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        PersistenceId = "MainWindow";

        AppWindow.Closing += (_, e) =>
        {
            if (App.Current?.IsExiting == true) return;
            e.Cancel = true;
            AppWindow.Hide();
        };
    }

    public XamlRoot? RootXaml => Root.XamlRoot;

    public void ShowLogin(string? message = null) => ShowLogin(new LoginRequest(message));

    public void ShowLogin(LoginRequest request)
    {
        Nav.Visibility = Visibility.Collapsed;
        AppTitleBar.IsPaneToggleButtonVisible = false;
        LoginFrame.Visibility = Visibility.Visible;
        AccountButton.Visibility = Visibility.Collapsed;
        LoginFrame.Navigate(typeof(LoginPage), request);
    }

    public void ShowApp(string page = "Control")
    {
        LoginFrame.Visibility = Visibility.Collapsed;
        LoginFrame.Content = null;
        Nav.Visibility = Visibility.Visible;
        AppTitleBar.IsPaneToggleButtonVisible = true;
        UpdateAccountButton();
        if (Nav.SelectedItem is null || page != "Control") Navigate(page);
    }

    public void Navigate(string tag)
    {
        if (tag == "Settings")
        {
            Nav.SelectedItem = Nav.SettingsItem;
            return;
        }
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag)
            ?? Nav.MenuItems[0];
    }

    public void ShowAndActivate()
    {
        AppWindow.Show(true);
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p)
            p.Restore();
        Activate();
        Native.SetForegroundWindow(this.GetWindowHandle());
    }

    public void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    public void UpdateAccountButton()
    {
        var active = App.Current?.ActiveAccount;
        AccountButton.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        AccountPicture.DisplayName = active is null ? "" : active.Split('@')[0];
        ToolTipService.SetToolTip(AccountButton, active is null ? L.T("Accounts") : L.F("Signed in as {0}", active));
    }

    private void AccountMenu_Opening(object sender, object e)
    {
        var app = App.Current!;
        AccountMenu.Items.Clear();
        var active = app.ActiveAccount;
        foreach (var account in app.Accounts)
        {
            var login = account;
            var item = new RadioMenuFlyoutItem
            {
                Text = login,
                GroupName = "accounts",
                IsChecked = Services.CredentialStore.SameLogin(login, active),
            };
            item.Click += async (_, _) => await app.SwitchAccountAsync(login);
            AccountMenu.Items.Add(item);
        }
        AccountMenu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = L.T("Add account"), Icon = new FontIcon { Glyph = "\uE8FA" } };
        add.Click += (_, _) => app.ShowAddAccount();
        AccountMenu.Items.Add(add);
        var manage = new MenuFlyoutItem { Text = L.T("Manage accounts"), Icon = new FontIcon { Glyph = "\uE713" } };
        manage.Click += (_, _) => Navigate("Settings");
        AccountMenu.Items.Add(manage);
    }

    private void AppTitleBar_PaneToggleRequested(Microsoft.UI.Xaml.Controls.TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected ? typeof(SettingsPage)
            : (args.SelectedItem as NavigationViewItem)?.Tag switch
            {
                "Schedules" => typeof(SchedulesPage),
                "Energy" => typeof(EnergyPage),
                "Automations" => typeof(AutomationsPage),
                _ => typeof(ControlPage),
            };
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }
}
