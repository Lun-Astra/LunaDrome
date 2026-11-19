using System.Text.RegularExpressions;
using LunaDrome.Core.Models;
using LunaDrome.Core.Services;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LunaDrome;

// Artist page with SpotiDrome connected: below the library's albums, the artist's other albums,
// EPs and singles on YouTube Music (faded cover + download badge). Tapping one shows its tracks
// with a "Download album" button that sends it to SpotiDrome, like the search page does.
// An artist with no releases at all (a small YouTube channel) gets their videos instead.
public sealed partial class MainPage
{
    private async Task LoadRemoteArtistAlbumsAsync(string artistName, IReadOnlyList<Album> libraryAlbums, int token)
    {
        if (!_spotiDrome.IsConfigured || string.IsNullOrWhiteSpace(artistName))
            return;

        try
        {
            var remote = await _spotiDrome.GetArtistAlbumsAsync(artistName);
            if (token != _artistLoadToken)
                return;

            // Hide what the library already has - also under a slightly different name
            // ("Senjutsu" vs "Senjutsu (2021 Remaster)").
            var owned = libraryAlbums.Select(a => EditionFreeTitle(a.Name)).ToHashSet();
            var missing = remote.Where(a => a.VideoId != null || !owned.Contains(EditionFreeTitle(a.Title))).ToList();

            // A video (artist without releases) is one song: hide it when that song is in the library.
            var videos = missing.Where(a => a.VideoId != null)
                .ToDictionary(a => a, a => new SpotiDromeTrack { VideoId = a.VideoId!, Title = a.Title, Artist = artistName, Kind = "video" });
            if (videos.Count > 0)
            {
                await MarkLibraryMatchesAsync(videos.Values, Array.Empty<SpotiDromeAlbum>());
                if (token != _artistLoadToken)
                    return;
                missing = missing.Where(a => !(videos.TryGetValue(a, out var t) && t.InLibrary)).ToList();
            }
            ArtistRemoteAlbumsList.ItemsSource = missing;
            ArtistRemoteCountText.Text = missing.Count.ToString();
            ArtistRemoteSection.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log($"artist: SpotiDrome albums for {artistName} failed: {ex.Message}");
        }
    }

    private void ClearRemoteArtistAlbums()
    {
        ArtistRemoteAlbumsList.ItemsSource = null;
        ArtistRemoteSection.Visibility = Visibility.Collapsed;
    }

    private static readonly Regex AnyBrackets = new(@"[\(\[][^\)\]]*[\)\]]");

    /// <summary>Lowercase title without brackets or punctuation: "Ultrakill: Violence (Original
    /// Game Soundtrack)" and "ULTRAKILL - Violence" both become "ultrakill violence".</summary>
    private static string EditionFreeTitle(string title)
    {
        var t = AnyBrackets.Replace(title ?? "", " ");
        t = Regex.Replace(t, @"\s+-\s+(single|ep)\s*$", "", RegexOptions.IgnoreCase);
        return NonWord.Replace(t.ToLowerInvariant(), " ").Trim();
    }

    private async void RemoteAlbumCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SpotiDromeAlbum album })
            return;
        e.Handled = true;

        var secondary = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        var title = new TextBlock { Text = album.Title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        var meta = new TextBlock { Text = $"{album.Artist}  •  {album.TypeAndYear}", Foreground = secondary, TextWrapping = TextWrapping.Wrap };
        var status = new TextBlock { Text = "Loading tracks…", Foreground = secondary, Margin = new Thickness(0, 4, 0, 0) };
        var art = new Border
        {
            Width = 110, Height = 110, CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["RaisedBrush"],
            Child = new Image
            {
                Stretch = Stretch.UniformToFill,
                Source = string.IsNullOrEmpty(album.Thumbnail) ? null : new BitmapImage(new Uri(album.Thumbnail)),
            },
        };
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(meta);
        Grid.SetColumn(text, 1);
        header.Children.Add(art);
        header.Children.Add(text);

        var tracks = new ItemsControl { ItemTemplate = (DataTemplate)Resources["YtAlbumTrackTemplate"] };
        var content = new StackPanel { Spacing = 12, MinWidth = 300 };
        content.Children.Add(new TextBlock { Text = "DOWNLOAD VIA SPOTIDROME", Style = (Style)Application.Current.Resources["SectionHeaderStyle"], Margin = new Thickness(0) });
        content.Children.Add(header);
        content.Children.Add(status);
        content.Children.Add(tracks);

        var isVideo = album.VideoId != null;
        var dialog = new ContentDialog
        {
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460 },
            PrimaryButtonText = isVideo ? "Download" : "Download album",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            // All in the app's own colors: the track list uses the app's text color, which was
            // unreadable on a dialog drawn in the system's light theme.
            Background = (Brush)Application.Current.Resources["PanelBrush"],
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            PrimaryButtonStyle = (Style)Application.Current.Resources["PrimaryButtonStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["TextButtonStyle"],
            XamlRoot = this.XamlRoot
        };

        SpotiDromeAlbum? detail = null;
        _ = LoadAsync();
        async Task LoadAsync()
        {
            if (isVideo)
            {
                status.Text = "1 track";
                tracks.ItemsSource = new[] { new SpotiDromeTrack { VideoId = album.VideoId!, Title = album.Title, Artist = album.Artist, Kind = "video" } };
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrEmpty(album.Url);
                return;
            }
            try
            {
                detail = await _spotiDrome.GetAlbumAsync(album.BrowseId);
                tracks.ItemsSource = detail.Tracks;
                status.Text = $"{detail.Tracks.Count} track{(detail.Tracks.Count == 1 ? "" : "s")}";
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrEmpty(detail.Url);
                // Which of the album's songs you already have (e.g. as a single).
                _ = MarkLibraryMatchesAsync(detail.Tracks, Array.Empty<SpotiDromeAlbum>());
            }
            catch (Exception ex)
            {
                status.Text = $"Couldn't load the tracks: {ex.Message}";
            }
        }

        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch { return; /* another dialog already open */ }

        if (result == ContentDialogResult.Primary && isVideo && album.Url is { Length: > 0 } videoUrl)
        {
            var artist = _currentArtist?.Name is { Length: > 0 } name ? name : album.Artist;
            await StartDownloadAsync(videoUrl, isAlbum: false, $"{album.Title} – {artist}", album.Title, artist, null);
        }
        else if (result == ContentDialogResult.Primary && detail is { Url: { Length: > 0 } url })
        {
            // Tagged with the artist as the library knows them, so it lands on this artist page.
            var artist = _currentArtist?.Name is { Length: > 0 } name ? name : detail.Artist;
            await StartDownloadAsync(url, isAlbum: true, $"{detail.Title} – {artist}", detail.Title, artist, detail.Title);
        }
    }
}
