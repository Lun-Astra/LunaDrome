using LunaDrome.Core.Models;
using Microsoft.UI.Xaml.Input;

namespace LunaDrome;

// Share the song that's playing (right-click its title in the bottom bar), like Feishin: a public
// Navidrome share link - anyone who opens it can play the song in a browser on the Navidrome host,
// no account or app needed.
public sealed partial class MainPage
{
    private void NowPlayingText_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_current?.Song is not { } song)
            return;
        var flyout = new MenuFlyout();
        AddMenuItem(flyout, "Share song…", () => ShowShareDialog(song));
        flyout.ShowAt(NowPlayingTitleLink, e.GetPosition(NowPlayingTitleLink));
        e.Handled = true;
    }

    private async void ShowShareDialog(Song song)
    {
        var expiries = new (string Label, TimeSpan? Ttl)[]
        {
            ("1 day", TimeSpan.FromDays(1)), ("7 days", TimeSpan.FromDays(7)), ("30 days", TimeSpan.FromDays(30)),
            ("1 year", null),   // Navidrome's own default
        };
        var descriptionBox = new TextBox { PlaceholderText = "Description (optional)" };
        var expiryBox = new ComboBox { ItemsSource = expiries.Select(x => x.Label).ToList(), SelectedIndex = 1, MinWidth = 140 };
        var createButton = new Button { Content = "Create link", Style = (Style)Application.Current.Resources["PrimaryButtonStyle"], Padding = new Thickness(16, 6, 16, 6) };
        var linkBox = new TextBox { IsReadOnly = true, Visibility = Visibility.Collapsed };
        var copyButton = new Button { Content = "Copy", Style = (Style)Application.Current.Resources["TextButtonStyle"], Visibility = Visibility.Collapsed };
        var status = new TextBlock { Style = (Style)Resources["SettingsHintStyle"], Margin = new Thickness(0), TextWrapping = TextWrapping.Wrap,
            Text = "Anyone with the link can play this song in their browser - no account needed. The link stops working when it expires." };

        var linkRow = new Grid { ColumnSpacing = 8 };
        linkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        linkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        linkRow.Children.Add(linkBox);
        Grid.SetColumn(copyButton, 1);
        linkRow.Children.Add(copyButton);

        var expiryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        expiryRow.Children.Add(new TextBlock { Text = "Valid for", VerticalAlignment = VerticalAlignment.Center });
        expiryRow.Children.Add(expiryBox);
        expiryRow.Children.Add(createButton);

        var content = new StackPanel { Spacing = 12, MaxWidth = 420 };
        content.Children.Add(new TextBlock { Text = $"{song.Title} - {song.ArtistName}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(status);
        content.Children.Add(descriptionBox);
        content.Children.Add(expiryRow);
        content.Children.Add(linkRow);

        createButton.Click += async (_, _) =>
        {
            createButton.IsEnabled = false;
            try
            {
                var ttl = expiries[Math.Max(0, expiryBox.SelectedIndex)].Ttl;
                var link = await _client.CreateShareAsync(song.Id, descriptionBox.Text,
                    ttl is { } t ? DateTimeOffset.Now + t : null);
                linkBox.Text = link;
                linkBox.Visibility = Visibility.Visible;
                copyButton.Visibility = Visibility.Visible;
                CopyToClipboard(link);
                status.Text = "Link copied. Anyone who opens it can play the song.";
            }
            catch (Exception ex)
            {
                status.Text = $"Couldn't create a link: {ex.Message}";
            }
            createButton.IsEnabled = true;
        };
        copyButton.Click += (_, _) =>
        {
            CopyToClipboard(linkBox.Text);
            status.Text = "Link copied.";
        };

        var dialog = new ContentDialog
        {
            Title = "Share song",
            Content = content,
            CloseButtonText = "Done",
            XamlRoot = this.XamlRoot
        };
        try { await dialog.ShowAsync(); }
        catch { /* another dialog already open */ }
    }
}
