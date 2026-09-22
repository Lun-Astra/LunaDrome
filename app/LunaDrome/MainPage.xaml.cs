using LunaDrome.Core.Services;

namespace LunaDrome;

public sealed partial class MainPage : Page
{
    private readonly NavidromeClient _client = new();

    public MainPage()
    {
        this.InitializeComponent();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "";

        var serverUrl = ServerUrlBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(username))
        {
            StatusText.Text = "Server URL and username are required.";
            return;
        }

        LoginButton.IsEnabled = false;
        StatusText.Text = "Signing in...";

        _client.Configure(serverUrl, username, password);
        var (success, error) = await _client.LoginAsync();

        if (!success)
        {
            StatusText.Text = error ?? "Login failed.";
            LoginButton.IsEnabled = true;
            return;
        }

        var artists = await _client.GetArtistsAsync();
        ArtistsList.ItemsSource = artists;

        LoginPanel.Visibility = Visibility.Collapsed;
        ArtistsPanel.Visibility = Visibility.Visible;
    }
}
