using System.Collections.ObjectModel;
using LunaDrome.Controls;
using LunaDrome.Core.Models;
using LunaDrome.Core.Services;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LunaDrome;

// 3.0: the search bar, and the optional "Download via SpotiDrome" (Luna's downloader for his
// Navidrome): search the library first; if SpotiDrome is configured, YouTube Music results
// sit below (opened automatically when the library has nothing) and a song or album can be
// sent to SpotiDrome, which downloads it straight into the Navidrome library.
public sealed partial class MainPage
{
    private readonly SpotiDromeClient _spotiDrome = new();
    private readonly ObservableCollection<DownloadItem> _downloads = new();
    private readonly DispatcherTimer _downloadPollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private Action? _toastAction;

    private string _searchQuery = "";
    private int _searchToken;
    private string? _ytSearchedFor;
    private SpotiDromeAlbum? _ytAlbum;

    private void InitSearch()
    {
        _spotiDrome.Configure(_prefs.SpotiDromeUrl);
        DownloadsList.ItemsSource = _downloads;
        _downloadPollTimer.Tick += async (_, _) => await PollDownloadsAsync();
        _toastTimer.Tick += (_, _) => HideToast();
    }

    // ================= Search =================

    private async void GlobalSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        await SubmitSearchAsync();
    }

    private async void GlobalSearchButton_Click(object sender, RoutedEventArgs e) => await SubmitSearchAsync();

    private async Task SubmitSearchAsync()
    {
        var query = GlobalSearchBox.Text.Trim();
        if (query.Length > 0)
            await RunSearchAsync(query);
    }

    private async Task RunSearchAsync(string query)
    {
        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();
        ClearSidebarSelection();
        HideAllPages();
        ShowPage(SearchPage);
        SearchPage.ChangeView(null, 0, null, true);

        _searchQuery = query;
        var token = ++_searchToken;
        SearchHeaderText.Text = $"Results for “{query}”";

        // SpotiDrome part: reset, collapsed until the library has been checked.
        _ytSearchedFor = null;
        _ytAlbum = null;
        SpotiDromeSection.Visibility = _spotiDrome.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        SetSpotiDromeExpanded(false);
        YtSongsList.ItemsSource = YtVideosList.ItemsSource = null;
        YtAlbumsList.ItemsSource = null;

        var found = await RunLibrarySearchAsync(query, token);
        if (token != _searchToken)
            return;

        // Nothing in the library: go straight to the downloader.
        if (found == 0 && _spotiDrome.IsConfigured)
        {
            SetSpotiDromeExpanded(true);
            await RunYtSearchAsync(query, token);
        }
    }

    /// <summary>Fills the library sections; returns how many results there were.</summary>
    private async Task<int> RunLibrarySearchAsync(string query, int token)
    {
        SearchLibraryStatusText.Text = "Searching your library…";
        SearchLibraryStatusText.Visibility = Visibility.Visible;
        try
        {
            var (artists, albums, songs) = await _client.SearchAsync(query);
            if (token != _searchToken)
                return 0;

            foreach (var album in albums)
                album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, 240);
            PopulateSongArt(songs);

            SearchArtistsList.ItemsSource = artists;
            SearchAlbumsList.ItemsSource = albums;
            SearchSongsList.ItemsSource = songs;
            SearchArtistsSection.Visibility = artists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchAlbumsSection.Visibility = albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchSongsSection.Visibility = songs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var total = artists.Count + albums.Count + songs.Count;
            SearchLibraryStatusText.Text = "Nothing in your library matches.";
            SearchLibraryStatusText.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            return total;
        }
        catch (Exception ex)
        {
            SearchLibraryStatusText.Text = $"Library search failed: {ex.Message}";
            return 0;
        }
    }

    private async void SearchArtist_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Artist artist })
            await NavigateToArtistAsync(artist.Id, artist.Name);
    }

    // ================= Download via SpotiDrome: results =================

    private bool _spotiDromeExpanded;

    private void SetSpotiDromeExpanded(bool expanded)
    {
        _spotiDromeExpanded = expanded;
        SpotiDromeContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SpotiDromeChevronRotation.Angle = expanded ? 90 : 0;
    }

    private async void SpotiDromeToggle_Click(object sender, RoutedEventArgs e)
    {
        SetSpotiDromeExpanded(!_spotiDromeExpanded);
        if (_spotiDromeExpanded && _ytSearchedFor != _searchQuery)
            await RunYtSearchAsync(_searchQuery, _searchToken);
    }

    private async Task RunYtSearchAsync(string query, int token)
    {
        _ytSearchedFor = query;
        ShowYtResults();
        SpotiDromeStatusText.Text = "Searching YouTube Music…";
        SpotiDromeStatusText.Visibility = Visibility.Visible;
        YtSongsSection.Visibility = YtAlbumsSection.Visibility = YtVideosSection.Visibility = Visibility.Collapsed;

        try
        {
            var result = await _spotiDrome.SearchAsync(query);
            if (token != _searchToken)
                return;

            YtSongsList.ItemsSource = result.Songs;
            YtAlbumsList.ItemsSource = result.Albums;
            YtVideosList.ItemsSource = result.Videos;
            YtSongsSection.Visibility = result.Songs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            YtAlbumsSection.Visibility = result.Albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            YtVideosSection.Visibility = result.Videos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var any = result.Songs.Count + result.Albums.Count + result.Videos.Count > 0;
            SpotiDromeStatusText.Text = any ? "" : "YouTube Music has nothing for this either.";
            SpotiDromeStatusText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (token != _searchToken)
                return;
            _ytSearchedFor = null; // allow a retry by collapsing/expanding
            SpotiDromeStatusText.Text = $"SpotiDrome search failed: {ex.Message}";
        }
    }

    private void ShowYtResults()
    {
        _ytAlbum = null;
        SpotiDromeResults.Visibility = Visibility.Visible;
        YtAlbumDetail.Visibility = Visibility.Collapsed;
    }

    private async void YtAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SpotiDromeAlbum album })
            return;

        SpotiDromeResults.Visibility = Visibility.Collapsed;
        YtAlbumDetail.Visibility = Visibility.Visible;
        YtAlbumTitle.Text = album.Title;
        YtAlbumMeta.Text = album.Subtitle;
        YtAlbumArt.Source = string.IsNullOrEmpty(album.Thumbnail) ? null : new BitmapImage(new Uri(album.Thumbnail));
        YtAlbumTracksList.ItemsSource = null;
        YtAlbumDownloadButton.IsEnabled = false;
        YtAlbumDownloadButton.Content = "Loading tracks…";

        try
        {
            var detail = await _spotiDrome.GetAlbumAsync(album.BrowseId);
            if (YtAlbumDetail.Visibility != Visibility.Visible || YtAlbumTitle.Text != album.Title)
                return;

            _ytAlbum = detail;
            YtAlbumMeta.Text = $"{detail.Subtitle}  •  {detail.Tracks.Count} track{(detail.Tracks.Count == 1 ? "" : "s")}";
            YtAlbumTracksList.ItemsSource = detail.Tracks;
            YtAlbumDownloadButton.Content = "Download album";
            YtAlbumDownloadButton.IsEnabled = !string.IsNullOrEmpty(detail.Url);
        }
        catch (Exception ex)
        {
            YtAlbumDownloadButton.Content = "Download album";
            YtAlbumMeta.Text = $"Couldn't load the tracks: {ex.Message}";
        }
    }

    private void YtAlbumBack_Click(object sender, RoutedEventArgs e) => ShowYtResults();

    private async void YtTrackDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SpotiDromeTrack track } button || string.IsNullOrEmpty(track.Url))
            return;

        button.IsEnabled = false;
        button.Content = "Sending…";
        var ok = await StartDownloadAsync(track.Url, isAlbum: false, $"{track.Title} – {track.Artist}", track.Title, track.Artist, track.Album);
        button.Content = ok ? "Downloading" : "Download";
        button.IsEnabled = !ok;
    }

    private async void YtAlbumDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_ytAlbum is not { Url: { Length: > 0 } url } album)
            return;

        YtAlbumDownloadButton.IsEnabled = false;
        YtAlbumDownloadButton.Content = "Sending…";
        var ok = await StartDownloadAsync(url, isAlbum: true, $"{album.Title} – {album.Artist}", album.Title, album.Artist, album.Title);
        YtAlbumDownloadButton.Content = ok ? "Downloading…" : "Download album";
        YtAlbumDownloadButton.IsEnabled = !ok;
    }

    // ================= Downloads =================

    private async Task<bool> StartDownloadAsync(string url, bool isAlbum, string label, string title, string artist, string? albumName)
    {
        try
        {
            var jobId = await _spotiDrome.DownloadAsync(url, isAlbum, label, albumName);
            _downloads.Insert(0, new DownloadItem { JobId = jobId, Label = label, IsAlbum = isAlbum, Title = title, Artist = artist });
            UpdateDownloadsButton();
            _downloadPollTimer.Start();
            ShowToast($"Downloading {label}", "View", ShowDownloadsPage);
            return true;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Couldn't start the download: {ex.Message}");
            return false;
        }
    }

    private bool _polling;

    private async Task PollDownloadsAsync()
    {
        if (_polling)
            return;
        _polling = true;
        try
        {
            foreach (var item in _downloads.Where(d => !d.IsFinished).ToList())
            {
                try
                {
                    var job = await _spotiDrome.GetJobAsync(item.JobId);
                    if (job == null)
                    {
                        item.Update(null, "SpotiDrome lost track of this download (was it restarted?).");
                        continue;
                    }

                    item.Update(job);
                    if (item.IsFinished)
                        OnDownloadFinished(item);
                }
                catch
                {
                    // SpotiDrome briefly unreachable - try again on the next tick.
                }
            }

            UpdateDownloadsButton();
            if (_downloads.All(d => d.IsFinished))
                _downloadPollTimer.Stop();
        }
        finally
        {
            _polling = false;
        }
    }

    private async void OnDownloadFinished(DownloadItem item)
    {
        if (!item.Succeeded)
        {
            ShowToast($"{item.Label}: {item.StatusText}", "View", ShowDownloadsPage);
            return;
        }

        // Library pages were loaded once - make them reload with the new songs.
        _albumsPageLoaded = false;
        _songsPageLoaded = false;

        ShowToast($"{item.Label} is now in your library", item.ActionText, () => PlayOrOpenDownload(item));

        // Still looking at the search it came from: show it under the library results now.
        if (SearchPage.Visibility == Visibility.Visible && _searchQuery.Length > 0)
            await RunLibrarySearchAsync(_searchQuery, _searchToken);
    }

    private void UpdateDownloadsButton()
    {
        if (_downloads.Count == 0)
        {
            DownloadsButton.Visibility = Visibility.Collapsed;
            return;
        }

        var active = _downloads.Count(d => !d.IsFinished);
        DownloadsButton.Content = active > 0 ? $"Downloading ({active})…" : "Downloads";
        DownloadsButton.Visibility = Visibility.Visible;
    }

    private void DownloadsButton_Click(object sender, RoutedEventArgs e) => ShowDownloadsPage();

    private void ShowDownloadsPage()
    {
        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();
        ClearSidebarSelection();
        HideAllPages();
        ShowPage(DownloadsPage);
    }

    private void DownloadAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DownloadItem item })
            PlayOrOpenDownload(item);
    }

    private async void DownloadCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DownloadItem item })
            return;
        try { await _spotiDrome.CancelJobAsync(item.JobId); }
        catch (Exception ex) { await ShowErrorAsync($"Couldn't cancel: {ex.Message}"); }
    }

    // A finished download: find it in the library (it's tagged with the YouTube title/artist)
    // and play the song / open the album.
    private async void PlayOrOpenDownload(DownloadItem item)
    {
        try
        {
            var (_, albums, songs) = await _client.SearchAsync(item.Title, artistCount: 0, albumCount: 10, songCount: 20);
            if (item.IsAlbum)
            {
                var album = albums.FirstOrDefault(a => string.Equals(a.Name, item.Title, StringComparison.OrdinalIgnoreCase))
                            ?? albums.FirstOrDefault();
                if (album != null)
                {
                    album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, 240);
                    await OpenAlbumAsync(album);
                    return;
                }
            }
            else
            {
                var song = songs.FirstOrDefault(s => string.Equals(s.Title, item.Title, StringComparison.OrdinalIgnoreCase)
                                                     && SharesWord(s.ArtistName, item.Artist))
                           ?? songs.FirstOrDefault(s => string.Equals(s.Title, item.Title, StringComparison.OrdinalIgnoreCase))
                           ?? songs.FirstOrDefault();
                if (song != null)
                {
                    PopulateSongArt(new[] { song });
                    PlayQueueFrom(new List<Song> { song }, 0);
                    return;
                }
            }
            ShowToast("Not in the library yet - Navidrome may still be scanning. Try again in a moment.");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Couldn't find it in the library: {ex.Message}");
        }
    }

    private static bool SharesWord(string a, string b)
    {
        static HashSet<string> Words(string s) =>
            s.ToLowerInvariant().Split(new[] { ' ', ',', '&', '-', '/' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return Words(a).Overlaps(Words(b));
    }

    // ================= Toast =================

    private void ShowToast(string text, string? actionText = null, Action? action = null)
    {
        ToastText.Text = text;
        _toastAction = action;
        ToastActionButton.Content = actionText;
        ToastActionButton.Visibility = action != null && actionText != null ? Visibility.Visible : Visibility.Collapsed;
        ToastBar.Visibility = Visibility.Visible;
        Motion.FadeIn(ToastBar);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastBar.Visibility = Visibility.Collapsed;
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        HideToast();
        action?.Invoke();
    }

    // ================= Settings: SpotiDrome =================

    private void RefreshSpotiDromeSettings()
    {
        SpotiDromeUrlBox.Text = _prefs.SpotiDromeUrl;
        if (string.IsNullOrEmpty(SpotiDromeTestText.Text))
            SpotiDromeTestText.Text = _spotiDrome.IsConfigured ? $"Using {_spotiDrome.BaseUrl}" : "Not connected.";
    }

    private async void SpotiDromeSave_Click(object sender, RoutedEventArgs e)
    {
        _prefs.SpotiDromeUrl = SpotiDromeUrlBox.Text.Trim();
        PreferencesService.Save(_prefs);
        _spotiDrome.Configure(_prefs.SpotiDromeUrl);

        if (!_spotiDrome.IsConfigured)
        {
            SpotiDromeTestText.Text = "Turned off - search shows your library only.";
            return;
        }

        SpotiDromeSaveButton.IsEnabled = false;
        SpotiDromeTestText.Text = $"Testing {_spotiDrome.BaseUrl}…";
        var error = await _spotiDrome.TestConnectionAsync();
        SpotiDromeSaveButton.IsEnabled = true;
        SpotiDromeTestText.Text = error ?? $"Connected to {_spotiDrome.BaseUrl}. \"Download via SpotiDrome\" now shows in search.";
    }
}
