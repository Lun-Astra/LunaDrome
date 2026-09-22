using LunaDrome.Core.Models;
using LunaDrome.Core.Services;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace LunaDrome;

public sealed partial class MainPage : Page
{
    private readonly NavidromeClient _client = new();
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _progressTimer;
    private Song? _currentSong;

    private List<Song> _queue = new();
    private int _queueIndex = -1;

    private bool _homeLoaded;
    private bool _allAlbumsLoaded;

    // Which top-level panel Album Detail should return to on "Back".
    private string _albumDetailReturnPanel = "Albums";

    public MainPage()
    {
        this.InitializeComponent();

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _progressTimer.Tick += ProgressTimer_Tick;
        _progressTimer.Start();

        _player.Volume = VolumeSlider.Value / 100.0;
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(OnMediaEnded);

        var saved = SettingsService.Load();
        if (!string.IsNullOrWhiteSpace(saved.ServerUrl))
            ServerUrlBox.Text = saved.ServerUrl;
        if (!string.IsNullOrWhiteSpace(saved.Username))
            UsernameBox.Text = saved.Username;

        var savedPassword = CredentialProtector.Unprotect(saved.ProtectedPassword);
        if (!string.IsNullOrEmpty(savedPassword))
        {
            PasswordBox.Password = savedPassword;
            _ = TryLoginAsync();
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e) => await TryLoginAsync();

    private async Task TryLoginAsync()
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

        if (RememberMeBox.IsChecked == true)
        {
            SettingsService.Save(new AppSettings
            {
                ServerUrl = serverUrl,
                Username = username,
                ProtectedPassword = CredentialProtector.Protect(password)
            });
        }
        else
        {
            SettingsService.Save(new AppSettings());
        }

        var artists = await _client.GetArtistsAsync();
        ArtistsList.ItemsSource = artists;

        var tracks = await _client.GetAllSongsAsync(limit: 200);
        TracksList.ItemsSource = tracks;

        LoginPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;

        await LoadHomeAsync();
    }

    // Subsonic's album list endpoints don't include cover art URLs directly - Navidrome
    // resolves cover art from an id via a separate endpoint, so this is filled in after
    // the fact for whichever albums are about to be displayed.
    private void PopulateCoverArt(IEnumerable<Album> albums, int size = 240)
    {
        foreach (var album in albums)
            album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, size);
    }

    private void ShowPanel(string name)
    {
        HomePanel.Visibility = name == "Home" ? Visibility.Visible : Visibility.Collapsed;
        ArtistsPanel.Visibility = name == "Artists" ? Visibility.Visible : Visibility.Collapsed;
        ArtistDetailPanel.Visibility = name == "ArtistDetail" ? Visibility.Visible : Visibility.Collapsed;
        AlbumsPanel.Visibility = name == "Albums" ? Visibility.Visible : Visibility.Collapsed;
        AlbumDetailPanel.Visibility = name == "AlbumDetail" ? Visibility.Visible : Visibility.Collapsed;
        TracksPanel.Visibility = name == "Tracks" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string panel })
            return;

        if (panel == "Home")
            await LoadHomeAsync();
        else if (panel == "Albums")
            await LoadAllAlbumsAsync();

        ShowPanel(panel);
    }

    private async Task LoadHomeAsync()
    {
        if (_homeLoaded)
            return;

        var albums = await _client.GetAlbumListAsync("newest", size: 20);
        PopulateCoverArt(albums);
        HomeAlbumsList.ItemsSource = albums;
        _homeLoaded = true;
    }

    private async Task LoadAllAlbumsAsync()
    {
        if (_allAlbumsLoaded)
            return;

        var albums = await _client.GetAlbumListAsync("alphabeticalByName", size: 200);
        PopulateCoverArt(albums);
        AllAlbumsList.ItemsSource = albums;
        _allAlbumsLoaded = true;
    }

    private async void ArtistRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { } fe)
            return;

        // Walk up to the row container that carries the bound Artist in its Tag.
        var current = fe;
        while (current != null && current.Tag is not Artist)
            current = current.Parent as FrameworkElement;

        if (current?.Tag is not Artist artist)
            return;

        ArtistDetailHeader.Text = artist.Name;
        var albums = await _client.GetAlbumsForArtistAsync(artist.Id);
        PopulateCoverArt(albums);
        ArtistAlbumsList.ItemsSource = albums;

        ShowPanel("ArtistDetail");
    }

    private async void AlbumCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Album album })
            return;

        // Remember which page to return to on Back - whichever album grid this card came from.
        _albumDetailReturnPanel = HomePanel.Visibility == Visibility.Visible ? "Home"
            : ArtistDetailPanel.Visibility == Visibility.Visible ? "ArtistDetail"
            : "Albums";

        AlbumDetailHeader.Text = album.Name;
        var songs = await _client.GetSongsForAlbumAsync(album.Id);
        AlbumTracksList.ItemsSource = songs;

        ShowPanel("AlbumDetail");
    }

    private void ArtistBack_Click(object sender, RoutedEventArgs e) => ShowPanel("Artists");

    private void AlbumBack_Click(object sender, RoutedEventArgs e) => ShowPanel(_albumDetailReturnPanel);

    private void TrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (sender is not ListView { ItemsSource: List<Song> source } || e.ClickedItem is not Song clicked)
            return;

        var index = source.IndexOf(clicked);
        if (index < 0)
            return;

        // Matches the WPF app's behavior: clicking a track queues the whole list it came
        // from (album or library-wide), starting from that track, not just a single song.
        PlayQueueFrom(source, index);
    }

    private void PlayQueueFrom(List<Song> source, int startIndex)
    {
        _queue = new List<Song>(source);
        _queueIndex = startIndex;
        PlaySong(_queue[_queueIndex]);
    }

    private void PlaySong(Song song)
    {
        _currentSong = song;
        _player.Source = MediaSource.CreateFromUri(new Uri(_client.GetStreamUrl(song.Id)));
        _player.Play();
        PlayPauseButton.Content = "Pause";
        NowPlayingText.Text = $"{song.Title} — {song.ArtistName}";
        FavoriteButton.Content = song.IsFavorited ? "★ Favorited" : "☆ Favorite";
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_queueIndex <= 0 || _queueIndex >= _queue.Count)
            return;

        _queueIndex--;
        PlaySong(_queue[_queueIndex]);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e) => PlayNext();

    private void PlayNext()
    {
        if (_queueIndex < 0 || _queueIndex + 1 >= _queue.Count)
            return;

        _queueIndex++;
        PlaySong(_queue[_queueIndex]);
    }

    // Natural completion only (not on Stop/skip), matching the WPF app's scrobble policy -
    // skipping through tracks shouldn't inflate their play count.
    private async void OnMediaEnded()
    {
        if (_currentSong != null)
        {
            try { await _client.ScrobbleAsync(_currentSong.Id); }
            catch { /* best-effort */ }
        }

        if (_queueIndex >= 0 && _queueIndex + 1 < _queue.Count)
            PlayNext();
        else
            PlayPauseButton.Content = "Play";
    }

    private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSong == null)
            return;

        try
        {
            if (_currentSong.IsFavorited)
                await _client.UnstarSongAsync(_currentSong.Id);
            else
                await _client.StarSongAsync(_currentSong.Id);

            _currentSong.IsFavorited = !_currentSong.IsFavorited;
            FavoriteButton.Content = _currentSong.IsFavorited ? "★ Favorited" : "☆ Favorite";
        }
        catch
        {
            // best-effort - leave the button state unchanged if the request failed
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSong == null)
            return;

        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
            PlayPauseButton.Content = "Play";
        }
        else
        {
            _player.Play();
            PlayPauseButton.Content = "Pause";
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _player.Pause();
        _player.PlaybackSession.Position = TimeSpan.Zero;
        PlayPauseButton.Content = "Play";
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _player.Volume = e.NewValue / 100.0;
    }

    private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        var duration = _player.PlaybackSession.NaturalDuration;
        if (duration <= TimeSpan.Zero)
            return;

        _player.PlaybackSession.Position = TimeSpan.FromSeconds(ProgressSlider.Value * duration.TotalSeconds);
    }

    private void ProgressTimer_Tick(object? sender, object e)
    {
        var session = _player.PlaybackSession;
        if (session == null || session.NaturalDuration <= TimeSpan.Zero)
            return;

        var position = session.Position;
        var duration = session.NaturalDuration;

        ProgressSlider.Value = position.TotalSeconds / duration.TotalSeconds;
        ElapsedText.Text = $"{(int)position.TotalMinutes}:{position.Seconds:D2}";
        DurationText.Text = $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}";
    }
}
