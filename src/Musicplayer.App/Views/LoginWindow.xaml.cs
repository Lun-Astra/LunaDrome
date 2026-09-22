using System.Windows;
using System.Windows.Media;
using Musicplayer.App.Services;

namespace Musicplayer.App.Views;

public partial class LoginWindow : Window
{
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly SolidColorBrush StatusBrush = new(Color.FromRgb(0x9A, 0x9A, 0x9A));

    public LoginWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        WindowPlacement.CenterOnPrimaryScreen(this);

        var saved = SettingsService.Load();
        if (!string.IsNullOrWhiteSpace(saved.ServerUrl))
            ServerUrlBox.Text = saved.ServerUrl;
        if (!string.IsNullOrWhiteSpace(saved.Username))
            UsernameBox.Text = saved.Username;

        var savedPassword = SettingsService.UnprotectPassword(saved.ProtectedPassword);
        if (!string.IsNullOrEmpty(savedPassword))
        {
            PasswordBox.Password = savedPassword;
            Loaded += async (_, _) => await TryLoginAsync(auto: true);
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await TryLoginAsync(auto: false);
    }

    private async System.Threading.Tasks.Task TryLoginAsync(bool auto)
    {
        StatusText.Text = "";

        var serverUrl = ServerUrlBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            if (!auto)
            {
                StatusText.Foreground = ErrorBrush;
                StatusText.Text = "Please fill in server URL, username, and password.";
            }
            return;
        }

        LoginButton.IsEnabled = false;
        StatusText.Foreground = StatusBrush;
        StatusText.Text = auto ? "Signing you in..." : "Connecting...";

        var client = new NavidromeClient();
        client.Configure(serverUrl, username, password);

        var (success, error) = await client.LoginAsync();

        if (!success)
        {
            LoginButton.IsEnabled = true;
            StatusText.Foreground = ErrorBrush;
            StatusText.Text = auto ? "Saved sign-in failed — check your details and sign in again." : (error ?? "Login failed.");
            return;
        }

        if (RememberMeBox.IsChecked == true)
        {
            SettingsService.Save(new AppSettings
            {
                ServerUrl = serverUrl,
                Username = username,
                ProtectedPassword = SettingsService.ProtectPassword(password)
            });
        }
        else
        {
            // Unchecking "remember me" clears login info, but the volume preference isn't a
            // credential - keep it rather than resetting it back to the default too.
            var current = SettingsService.Load();
            SettingsService.Save(new AppSettings { Volume = current.Volume });
        }

        var mainWindow = new MainWindow(client);
        mainWindow.Show();
        Close();
    }
}
