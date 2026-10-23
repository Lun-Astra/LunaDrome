using LunaDrome.Core.Models;
using LunaDrome.Controls;
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
        e.Handled = true;

        const string RadioIcon = "M8,5 A1.5,1.5 0 1,1 7.99,5 M4.5,2.5 A5,5 0 0,0 4.5,10.5 M11.5,2.5 A5,5 0 0,1 11.5,10.5 M2,0.5 A8,8 0 0,0 2,12.5 M14,0.5 A8,8 0 0,1 14,12.5";
        const string ShareIcon = "M8,10 L8,1 M4.5,4.5 L8,1 L11.5,4.5 M2,8 L2,14 L14,14 L14,8";
        const string AlbumIcon = "M8,1 A7,7 0 1,1 7.99,1 Z M8,6 A2,2 0 1,1 7.99,6 Z";
        const string ArtistIcon = "M8,1.5 A3.5,3.5 0 1,1 7.99,1.5 Z M1.5,15 C1.5,9.5 14.5,9.5 14.5,15";
        const string HeartIcon = "M8,14.5 C8,14.5 1,10 1,5.5 C1,3 3,1.3 5.2,1.3 C6.7,1.3 7.6,2.1 8,2.7 C8.4,2.1 9.3,1.3 10.8,1.3 C13,1.3 15,3 15,5.5 C15,10 8,14.5 8,14.5 Z";

        var items = new List<RichMenuItem?>
        {
            new("Start radio", RadioIcon, () => _ = StartSongRadioAsync(song), "songs like this"),
            new("Share song…", ShareIcon, () => ShowShareDialog(song), "public link"),
            null,
            new("Go to album", AlbumIcon, () => _ = OpenAlbumAsync(new Album
            {
                Id = song.AlbumId, Name = song.AlbumName, ArtistId = song.ArtistId, ArtistName = song.AlbumArtistName,
                CoverArtUrl = song.CoverArtUrl
            })),
        };
        // One row per artist when the song has several (they're separate links elsewhere too).
        if (song.Artists.Count > 1)
            items.AddRange(song.Artists.Select(a => (RichMenuItem?)new RichMenuItem($"Go to {a.Name}", ArtistIcon,
                () => _ = NavigateToArtistAsync(a.Id, a.Name))));
        else
            items.Add(new RichMenuItem("Go to artist", ArtistIcon, () => _ = NavigateToArtistAsync(song.ArtistId, song.ArtistName)));
        items.Add(new RichMenuItem(song.IsFavorited ? "Remove from favorites" : "Add to favorites", HeartIcon,
            () => _ = ToggleNowPlayingFavoriteAsync(song), Filled: song.IsFavorited));

        RichMenu.Show(NowPlayingTitleLink, e.GetPosition(NowPlayingTitleLink),
            song.CoverArtUrl, song.Title, string.Join(", ", song.Artists.Count > 1 ? song.Artists.Select(a => a.Name) : new[] { song.ArtistName }),
            items);
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
