using Kaze.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Kaze.App.Views;

public sealed record LoginRequest(string? Message = null, string? Login = null, bool Adding = false);

public sealed partial class LoginPage : Page
{
    public LoginPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var request = e.Parameter as LoginRequest ?? new LoginRequest(e.Parameter as string);
        LoginBox.Text = request.Login ?? (request.Adding ? "" : App.Current?.Settings.Current.Login ?? "");
        PasswordBox.Password = "";
        TitleText.Text = request.Adding ? L.T("Add an airCloud Go account") : L.T("Sign in to airCloud Go");
        CancelButton.Visibility = request.Adding ? Visibility.Visible : Visibility.Collapsed;
        ShowError(request.Message);
        (LoginBox.Text.Length == 0 ? (Control)LoginBox : PasswordBox).Focus(FocusState.Programmatic);
    }

    private void ShowError(string? message)
    {
        ErrorBar.Message = message ?? "";
        ErrorBar.IsOpen = !string.IsNullOrEmpty(message);
    }

    private void Field_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) SignIn_Click(sender, e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => App.Current?.ShowMainApp();

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var login = LoginBox.Text.Trim();
        var password = PasswordBox.Password;
        if (login.Length == 0 || password.Length == 0)
        {
            ShowError(L.T("Enter your email/phone and password."));
            return;
        }

        SignInButton.IsEnabled = false;
        Busy.IsActive = true;
        ShowError(null);
        try
        {
            var error = await App.Current!.SignInAsync(login, password);
            if (error is not null) ShowError(error);
        }
        finally
        {
            SignInButton.IsEnabled = true;
            Busy.IsActive = false;
        }
    }
}
