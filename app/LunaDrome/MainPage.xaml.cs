using System.Collections.ObjectModel;
using LunaDrome.Controls;
using LunaDrome.Core.Models;
using LunaDrome.Core.Services;
using LunaDrome.Services;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace LunaDrome;

// Behavior mirrors the WPF app's MainWindow.xaml.cs page for page (nav, Home rows + hero,
// artist page with album drill-in, sort menus, context menus, full-screen player), so the two
// feel the same to use - see MainPage.xaml for the layout side of the port.
public sealed partial class MainPage : Page
{
    private enum RepeatMode { Off, All, One }

    /// <summary>Per-track-list state: raw songs + search/sort, for album, library and playlist views.</summary>
    private sealed class TrackView
    {
        public required ListView List { get; init; }
        public required TextBox Search { get; init; }
        public required Button SortButton { get; init; }
        public required Button DirectionButton { get; init; }
        public List<Song> Raw { get; set; } = new();
        public string? SortKey { get; set; }
        public bool Ascending { get; set; } = true;
    }

    private static readonly (string Tag, string Label)[] SortOptions =
    {
        ("Album", "Album"), ("AlbumArtist", "Album Artist"), ("Artist", "Artist"), ("Duration", "Duration"),
        ("Favorited", "Is favorited"), ("Genre", "Genre"), ("Name", "Name"), ("PlayCount", "Play count"),
        ("Random", "Random"), ("RecentlyAdded", "Recently added"), ("RecentlyPlayed", "Recently played"),
        ("ReleaseYear", "Release year"), ("Year", "Year"),
    };

    private static readonly Random SortRandom = new();

    private readonly NavidromeClient _client = new();
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _progressTimer;

    private readonly ObservableCollection<Song> _queue = new();
    private int _queueIndex = -1;
    private bool _isShuffled;
    private RepeatMode _repeatMode = RepeatMode.Off;
    private readonly Random _random = new();
    private bool _isPlaying;
    private bool _isUserSeeking;
    private bool _lyricsAreSynced;

    private readonly Dictionary<string, TrackView> _trackViews;

    private bool _initialized;
    private bool _suppressNavEvents;
    private bool _albumsPageLoaded;
    private bool _songsPageLoaded;

    private List<Playlist> _playlists = new();
    private Playlist? _currentPlaylist;
    private Artist? _currentArtist;
    private Album? _currentAlbum;
    private List<Album> _heroAlbums = new();
    private int _heroIndex;
    private int _heroLoadToken;
    private List<Artist> _artistsRaw = new();
    private List<Album> _allAlbumsRaw = new();

    // Visualizer: loopback capture feeds the analyzer on its own thread; the render loop pulls
    // the latest bands once per display frame (see OnVisualizerRenderFrame).
    private readonly SpectrumAnalyzer _spectrum = new();
    private readonly LoopbackCapture _loopback;
    private readonly float[] _visualizerLatestBands = new float[SpectrumAnalyzer.BandCount];
    private readonly float[] _visualizerSmoothed = new float[SpectrumAnalyzer.BandCount];
    private bool _visualizerRenderHooked;

    // Drives the visualizer at ~60fps. Deliberately not CompositionTarget.Rendering: on Uno's Skia
    // desktop host, subscribing to Rendering keeps the whole window re-rendering at the monitor's
    // refresh rate (180Hz here) - ~1 CPU core - even on frames the handler skips. A timer only
    // causes a redraw when the canvas actually invalidates.
    private readonly DispatcherTimer _visualizerTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public MainPage()
    {
        _loopback = new LoopbackCapture(_spectrum);
        this.InitializeComponent();
        Unloaded += (_, _) => { StopVisualizer(); _loopback.Dispose(); };
        _visualizerTimer.Tick += OnVisualizerRenderFrame;

        _trackViews = new()
        {
            ["AllTracks"] = new() { List = AllSongsList, Search = AllTracksSearchBox, SortButton = AllTracksSortButton, DirectionButton = AllTracksSortDirectionButton },
            ["PlaylistTracks"] = new() { List = PlaylistSongsList, Search = PlaylistTrackSearchBox, SortButton = PlaylistTrackSortButton, DirectionButton = PlaylistTrackSortDirectionButton },
        };

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _progressTimer.Tick += ProgressTimer_Tick;
        _progressTimer.Start();

        _player.Volume = VolumeSlider.Value;
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(OnMediaEnded);

        // Slider marks its own pointer events handled, so listen with handledEventsToo to know
        // when the user is dragging (the progress timer mustn't fight the drag) and when to seek.
        ProgressSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _isUserSeeking = true), true);
        ProgressSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => CommitSeek()), true);
        ProgressSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => CommitSeek()), true);

        QueueList.ItemsSource = _queue;

        // Home cards resize so a whole number of them fills the row width exactly (HomeLayout).
        var homeLayout = (HomeLayout)Resources["HomeLayout"];
        HomeContent.SizeChanged += (_, e) => homeLayout.Update(e.NewSize.Width);
        // Genres: always exactly three full rows of tiles, whatever the row width (like Feishin).
        homeLayout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeLayout.GenreTilesPerRow))
                BindHomeGenres();
        };
        QueueTabButton.IsChecked = true;

        _initialized = true;

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

    // ================= Login =================

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

        SettingsService.Save(RememberMeBox.IsChecked == true
            ? new AppSettings { ServerUrl = serverUrl, Username = username, ProtectedPassword = CredentialProtector.Protect(password) }
            : new AppSettings());

        LoginPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;

        NavHome.IsChecked = true;
        _ = LoadPlaylistsAsync();

        try
        {
            _artistsRaw = await _client.GetArtistsAsync();
            RefreshArtists();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Failed to load artists: {ex.Message}");
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        try
        {
            await new ContentDialog
            {
                Title = "LunaDrome",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            }.ShowAsync();
        }
        catch { /* another dialog already open */ }
    }

    // ================= Navigation =================

    private void HideAllPages()
    {
        HomePage.Visibility = Visibility.Collapsed;
        AlbumPage.Visibility = Visibility.Collapsed;
        ArtistsPage.Visibility = Visibility.Collapsed;
        ArtistDetailPage.Visibility = Visibility.Collapsed;
        AlbumsPage.Visibility = Visibility.Collapsed;
        SongsPage.Visibility = Visibility.Collapsed;
        GenreAlbumsPage.Visibility = Visibility.Collapsed;
        PlaylistsPage.Visibility = Visibility.Collapsed;
    }

    private static void ShowPage(UIElement page)
    {
        page.Visibility = Visibility.Visible;
        Motion.FadeIn(page);
    }

    // Opening an artist/genre/playlist acts like its own page: exactly one thing in the sidebar
    // (a nav item OR a playlist) should look active, so clear the rest.
    private void ClearSidebarSelection(bool keepPlaylist = false)
    {
        _suppressNavEvents = true;
        NavHome.IsChecked = false;
        NavArtists.IsChecked = false;
        NavAlbums.IsChecked = false;
        NavSongs.IsChecked = false;
        if (!keepPlaylist)
            PlaylistsList.SelectedItem = null;
        _suppressNavEvents = false;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressNavEvents || sender is not RadioButton { Tag: string tag })
            return;

        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();

        _suppressNavEvents = true;
        PlaylistsList.SelectedItem = null;
        _suppressNavEvents = false;

        HideAllPages();
        switch (tag)
        {
            case "Home":
                ShowPage(HomePage);
                _ = LoadHomeAsync();
                break;
            case "Artists":
                ArtistsList.SelectedItem = null;
                ShowPage(ArtistsPage);
                break;
            case "Albums":
                ShowPage(AlbumsPage);
                if (!_albumsPageLoaded)
                {
                    _albumsPageLoaded = true;
                    _ = LoadAllAlbumsAsync();
                }
                break;
            case "Songs":
                ShowPage(SongsPage);
                if (!_songsPageLoaded)
                {
                    _songsPageLoaded = true;
                    _ = LoadAllSongsAsync();
                }
                break;
        }
    }

    // ================= Home =================

    private bool _homeLoaded;

    private async Task LoadHomeAsync()
    {
        if (_homeLoaded)
            return;
        _homeLoaded = true;

        try
        {
            var heroTask = _client.GetAlbumListAsync("random", 10);
            var recentlyPlayedTask = _client.GetAlbumListAsync("recent", 20);
            var mostPlayedTask = _client.GetAlbumListAsync("frequent", 20);
            var recentlyAddedTask = _client.GetAlbumListAsync("newest", 20);
            var exploreTask = _client.GetAlbumListAsync("random", 20);
            var genresTask = _client.GetGenresAsync();
            await Task.WhenAll(heroTask, recentlyPlayedTask, mostPlayedTask, recentlyAddedTask, exploreTask, genresTask);

            _heroAlbums = heroTask.Result;
            foreach (var album in _heroAlbums)
                album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, 480);

            _heroIndex = 0;
            if (_heroAlbums.Count > 0)
            {
                ShowHeroAlbum(0);
                HomeHeroCard.Visibility = Visibility.Visible;
            }

            BindHomeRow(HomeRecentlyPlayedSection, HomeRecentlyPlayedList, recentlyPlayedTask.Result);
            BindHomeRow(HomeMostPlayedSection, HomeMostPlayedList, mostPlayedTask.Result);
            BindHomeRow(HomeRecentlyAddedSection, HomeAlbumsList, recentlyAddedTask.Result);
            BindHomeRow(HomeExploreSection, HomeExploreList, exploreTask.Result);

            _homeGenres = genresTask.Result;
            BindHomeGenres();
        }
        catch (Exception ex)
        {
            _homeLoaded = false;
            await ShowErrorAsync($"Failed to load home page: {ex.Message}");
        }
    }

    private List<Genre> _homeGenres = new();

    private void BindHomeGenres()
    {
        var perRow = ((HomeLayout)Resources["HomeLayout"]).GenreTilesPerRow;
        var genres = _homeGenres.Take(perRow * 3).ToList();
        HomeGenresList.ItemsSource = genres;
        HomeGenresSection.Visibility = genres.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Most/recently-played rows can legitimately be empty (no play history yet) - collapse the
    // whole row rather than showing an empty header.
    private void BindHomeRow(FrameworkElement section, ItemsControl list, List<Album> albums)
    {
        foreach (var album in albums)
            album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, 240);

        list.ItemsSource = albums;
        section.Visibility = albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ShowHeroAlbum(int index)
    {
        var album = _heroAlbums[index];
        SetImage(HeroArt, album.CoverArtUrl);
        HeroTitleText.Text = album.Name;
        HeroArtistText.Text = album.ArtistName;

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(album.Genre))
            meta.Add(album.Genre);
        if (album.Year is > 0)
            meta.Add(album.Year.Value.ToString());
        HeroMetaText.Text = string.Join(" · ", meta);

        // Blur renders off-thread; drop the result if the user already clicked on to another album.
        var token = ++_heroLoadToken;
        var blurred = await BlurredArt.LoadAsync(album.CoverArtUrl);
        if (token == _heroLoadToken)
            HeroBackdrop.Source = blurred;
    }

    private void HeroPrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_heroAlbums.Count == 0)
            return;
        _heroIndex = (_heroIndex - 1 + _heroAlbums.Count) % _heroAlbums.Count;
        ShowHeroAlbum(_heroIndex);
    }

    private void HeroNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_heroAlbums.Count == 0)
            return;
        _heroIndex = (_heroIndex + 1) % _heroAlbums.Count;
        ShowHeroAlbum(_heroIndex);
    }

    private async void HeroTitleText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_heroAlbums.Count > 0)
            await OpenAlbumAsync(_heroAlbums[_heroIndex]);
    }

    private async void HeroArtistText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_heroAlbums.Count == 0)
            return;
        var album = _heroAlbums[_heroIndex];
        await NavigateToArtistAsync(album.ArtistId, album.ArtistName);
    }

    private async void HeroPlayOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_heroAlbums.Count > 0)
            await PlayAlbumAsync(_heroAlbums[_heroIndex]);
    }

    private void HomeMoreAlbumsButton_Click(object sender, RoutedEventArgs e) => NavAlbums.IsChecked = true;

    private void HomePlaylistCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Playlist playlist })
            PlaylistsList.SelectedItem = _playlists.FirstOrDefault(p => p.Id == playlist.Id) ?? playlist;
    }

    private async void GenreCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Genre genre })
            return;

        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        ClearSidebarSelection();
        HideAllPages();
        GenreAlbumsHeader.Text = genre.Name.ToUpperInvariant();
        GenreAlbumsList.ItemsSource = null;
        ShowPage(GenreAlbumsPage);

        try
        {
            var albums = await _client.GetAlbumsByGenreAsync(genre.Name, 500);
            foreach (var album in albums)
                album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, 240);
            GenreAlbumsList.ItemsSource = albums;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Failed to load albums: {ex.Message}");
        }
    }

    // ================= Albums =================

    private async Task LoadAllAlbumsAsync()
    {
        try
        {
            _allAlbumsRaw = await _client.GetAlbumListAsync("alphabeticalByName", 500);
            foreach (var album in _allAlbumsRaw)
                album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, 240);
            RefreshAllAlbums();
        }
        catch (Exception ex)
        {
            _albumsPageLoaded = false;
            await ShowErrorAsync($"Failed to load albums: {ex.Message}");
        }
    }

    private void AllAlbumsSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAllAlbums();

    private void RefreshAllAlbums()
    {
        if (AllAlbumsSearchBox == null)
            return;

        var q = AllAlbumsSearchBox.Text.Trim();
        AllAlbumsList.ItemsSource = string.IsNullOrEmpty(q)
            ? _allAlbumsRaw
            : _allAlbumsRaw.Where(a =>
                a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.ArtistName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // Clicking the art area plays (the hover overlay covers it); anywhere else on the card opens.
    private async void AlbumCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Album album })
            await OpenAlbumAsync(album);
    }

    private async void AlbumPlayOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: Album album })
            await PlayAlbumAsync(album);
    }

    private async void CardArtistText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: Album album })
            await NavigateToArtistAsync(album.ArtistId, album.ArtistName);
    }

    private async Task PlayAlbumAsync(Album album)
    {
        try
        {
            var songs = await _client.GetSongsForAlbumAsync(album.Id);
            PopulateSongArt(songs);
            if (songs.Count > 0)
                PlayQueueFrom(songs, 0);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Failed to play album: {ex.Message}");
        }
    }

    private async void CardAlbumText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: Album album })
            await OpenAlbumAsync(album);
    }

    // ================= Artists =================

    private void ArtistSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshArtists();

    private void RefreshArtists()
    {
        if (ArtistSearchBox == null)
            return;

        var q = ArtistSearchBox.Text.Trim();
        ArtistsList.ItemsSource = string.IsNullOrEmpty(q)
            ? _artistsRaw
            : _artistsRaw.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async void ArtistsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ArtistsList.SelectedItem is Artist artist)
            await NavigateToArtistAsync(artist.Id, artist.Name);
    }

    private int _artistLoadToken;

    // Feishin-style artist page: header (blurred art, cover, name, counts, Play/Next/Last,
    // favorite) + the artist's album grid.
    private async Task NavigateToArtistAsync(string artistId, string fallbackName)
    {
        if (string.IsNullOrEmpty(artistId))
            return;

        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();
        HideAllPages();
        ShowPage(ArtistDetailPage);
        ClearSidebarSelection();
        ArtistDetailPage.ChangeView(null, 0, null, true);

        var token = ++_artistLoadToken;
        ArtistAlbumsList.ItemsSource = null;
        ArtistHeaderNameText.Text = fallbackName;
        ArtistHeaderStatsText.Text = "";
        ArtistAlbumCountText.Text = "";
        ArtistHeaderBackdrop.Source = null;
        ArtistHeaderArt.Background = null;
        SetArtistFavoriteIcon(false);

        try
        {
            var (artist, albums) = await _client.GetArtistDetailAsync(artistId);
            if (token != _artistLoadToken)
                return;

            _currentArtist = artist;
            foreach (var album in albums)
                album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, 300);

            // Newest first, like Feishin's default "Release date" sort on the artist page.
            albums = albums.OrderByDescending(a => a.Year ?? 0).ToList();
            ArtistAlbumsList.ItemsSource = albums;
            ArtistAlbumCountText.Text = albums.Count.ToString();

            var totalSongs = albums.Sum(a => a.SongCount);
            ArtistHeaderNameText.Text = artist.Name;
            ArtistHeaderStatsText.Text = $"{albums.Count} album{(albums.Count == 1 ? "" : "s")}  •  {totalSongs} track{(totalSongs == 1 ? "" : "s")}";
            SetImage(ArtistHeaderArt, artist.CoverArtUrl);
            SetArtistFavoriteIcon(artist.IsFavorited);

            var backdrop = await BlurredArt.LoadAsync(artist.CoverArtUrl);
            if (token == _artistLoadToken)
                ArtistHeaderBackdrop.Source = backdrop;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Failed to load artist: {ex.Message}");
        }
    }

    private void SetArtistFavoriteIcon(bool favorited) =>
        ArtistFavoriteIcon.Fill = favorited
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private async void ArtistFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentArtist == null)
            return;

        try
        {
            if (_currentArtist.IsFavorited)
                await _client.UnstarArtistAsync(_currentArtist.Id);
            else
                await _client.StarArtistAsync(_currentArtist.Id);

            _currentArtist.IsFavorited = !_currentArtist.IsFavorited;
            SetArtistFavoriteIcon(_currentArtist.IsFavorited);
        }
        catch { /* best-effort */ }
    }

    private async Task<List<Song>> GetArtistSongsAsync()
    {
        var songs = new List<Song>();
        if (ArtistAlbumsList.ItemsSource is not List<Album> albums)
            return songs;

        try
        {
            // Oldest album first so "Play" runs through the discography in release order.
            foreach (var album in albums.OrderBy(a => a.Year ?? 0))
                songs.AddRange(await _client.GetSongsForAlbumAsync(album.Id));
            PopulateSongArt(songs);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Failed to load artist tracks: {ex.Message}");
        }
        return songs;
    }

    private async void ArtistHeaderPlayButton_Click(object sender, RoutedEventArgs e)
    {
        var songs = await GetArtistSongsAsync();
        if (songs.Count > 0)
            PlayQueueFrom(songs, 0);
    }

    private async void ArtistHeaderNextButton_Click(object sender, RoutedEventArgs e) => QueueSongsNext(await GetArtistSongsAsync());

    private async void ArtistHeaderLastButton_Click(object sender, RoutedEventArgs e) => QueueSongsLast(await GetArtistSongsAsync());

    // Feishin's "Next" = insert right after the current track (keeping their order);
    // "Last" = append to the end of the queue.
    private void QueueSongsNext(List<Song> songs)
    {
        if (songs.Count == 0)
            return;

        if (_queueIndex < 0)
        {
            PlayQueueFrom(songs, 0);
            return;
        }

        for (var i = 0; i < songs.Count; i++)
            _queue.Insert(_queueIndex + 1 + i, songs[i]);
    }

    private void QueueSongsLast(List<Song> songs)
    {
        if (songs.Count == 0)
            return;

        if (_queueIndex < 0)
        {
            PlayQueueFrom(songs, 0);
            return;
        }

        foreach (var song in songs)
            _queue.Add(song);
    }

    // ================= Album page =================

    private List<Song> _albumSongsRaw = new();
    private int _albumLoadToken;

    private async Task OpenAlbumAsync(Album album)
    {
        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();
        HideAllPages();
        ShowPage(AlbumPage);
        ClearSidebarSelection();
        AlbumPage.ChangeView(null, 0, null, true);

        var token = ++_albumLoadToken;
        _currentAlbum = album;
        _albumSongsRaw = new();
        AlbumSongsList.ItemsSource = null;
        AlbumTrackSearchBox.Text = "";
        AlbumHeaderTitleText.Text = album.Name;
        AlbumHeaderArtistText.Text = album.ArtistName;
        AlbumHeaderMetaText.Text = album.Year is > 0 ? album.Year.Value.ToString() : "";
        AlbumGenreText.Text = album.Genre ?? "";
        AlbumGenrePanel.Visibility = string.IsNullOrWhiteSpace(album.Genre) ? Visibility.Collapsed : Visibility.Visible;
        var artUrl = _client.GetCoverArtUrl(album.Id, 480);
        SetImage(AlbumHeaderArt, artUrl);
        AlbumHeaderTint.Background = (Brush)Application.Current.Resources["RaisedBrush"];

        try
        {
            var songs = await _client.GetSongsForAlbumAsync(album.Id);
            if (token != _albumLoadToken)
                return;

            PopulateSongArt(songs);
            _albumSongsRaw = songs;
            RefreshAlbumTracks();

            // "2013 • 11 tracks • 43m 13s • 94 plays", like Feishin's album meta line.
            var totalSeconds = songs.Sum(s => s.DurationSeconds);
            var duration = totalSeconds >= 3600
                ? $"{totalSeconds / 3600}h {totalSeconds % 3600 / 60}m {totalSeconds % 60}s"
                : $"{totalSeconds / 60}m {totalSeconds % 60}s";
            var meta = new List<string>();
            if (album.Year is > 0)
                meta.Add(album.Year.Value.ToString());
            meta.Add($"{songs.Count} track{(songs.Count == 1 ? "" : "s")}");
            meta.Add(duration);
            var plays = songs.Sum(s => s.PlayCount);
            if (plays > 0)
                meta.Add($"{plays} play{(plays == 1 ? "" : "s")}");
            AlbumHeaderMetaText.Text = string.Join("  •  ", meta);

            if (string.IsNullOrWhiteSpace(album.Genre) && songs.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Genre)) is { } withGenre)
            {
                AlbumGenreText.Text = withGenre.Genre;
                AlbumGenrePanel.Visibility = Visibility.Visible;
            }

            // Header tint: the cover's average color, darkened so white text stays readable,
            // fading out toward the bottom (Feishin colors its album header from the art too).
            if (await BlurredArt.AverageColorAsync(artUrl) is { } avg && token == _albumLoadToken)
            {
                var dark = Windows.UI.Color.FromArgb(255, (byte)(avg.R * 0.55), (byte)(avg.G * 0.55), (byte)(avg.B * 0.55));
                AlbumHeaderTint.Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = dark, Offset = 0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, (byte)(dark.R * 0.7 + 26 * 0.3), (byte)(dark.G * 0.7 + 27 * 0.3), (byte)(dark.B * 0.7 + 30 * 0.3)), Offset = 1 }
                    }
                };
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Failed to load album: {ex.Message}");
        }
    }

    private void AlbumTrackSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAlbumTracks();

    private void RefreshAlbumTracks()
    {
        if (AlbumSongsList == null)
            return;
        AlbumSongsList.ItemsSource = FilterSongs(_albumSongsRaw, AlbumTrackSearchBox.Text?.Trim() ?? "").ToList();
    }

    private async void AlbumHeaderArtistText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_currentAlbum != null)
            await NavigateToArtistAsync(_currentAlbum.ArtistId, _currentAlbum.ArtistName);
    }

    private void AlbumPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_albumSongsRaw.Count > 0)
            PlayQueueFrom(_albumSongsRaw, 0);
    }

    private void AlbumNextButton_Click(object sender, RoutedEventArgs e) => QueueSongsNext(_albumSongsRaw);

    private void AlbumLastButton_Click(object sender, RoutedEventArgs e) => QueueSongsLast(_albumSongsRaw);

    private void AlbumTrackRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Song song } || AlbumSongsList.ItemsSource is not List<Song> shown)
            return;

        var index = shown.IndexOf(song);
        if (index >= 0)
            PlayQueueFrom(shown, index);
    }

    private void AlbumTrackRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Song song } row || AlbumSongsList.ItemsSource is not List<Song> shown)
            return;

        var flyout = BuildSongMenu(song, shown);
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "Add album to queue", () => QueueSongsLast(_albumSongsRaw));
        flyout.ShowAt(row, e.GetPosition(row));
        e.Handled = true;
    }

    private async void TrackFavorite_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Panel { Tag: Song song } cell)
            return;

        try
        {
            if (song.IsFavorited)
                await _client.UnstarSongAsync(song.Id);
            else
                await _client.StarSongAsync(song.Id);
            song.IsFavorited = !song.IsFavorited;

            // Song isn't observable, so update this row's heart directly.
            if (cell.Children.FirstOrDefault() is Microsoft.UI.Xaml.Shapes.Path heart)
                heart.Fill = (Brush)new FavoriteFillConverter().Convert(song.IsFavorited, typeof(Brush), null!, "");
        }
        catch { /* best-effort */ }
    }
    private async void SongArtistText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: Song song })
            await NavigateToArtistAsync(song.ArtistId, song.ArtistName);
    }

    // ================= Tracks (library-wide) =================

    private async Task LoadAllSongsAsync()
    {
        try
        {
            var songs = await _client.GetAllSongsAsync(500);
            PopulateSongArt(songs);
            _trackViews["AllTracks"].Raw = songs;
            RefreshTrackView("AllTracks");
        }
        catch (Exception ex)
        {
            _songsPageLoaded = false;
            await ShowErrorAsync($"Failed to load songs: {ex.Message}");
        }
    }

    private void PopulateSongArt(IEnumerable<Song> songs)
    {
        foreach (var song in songs)
            song.CoverArtUrl = _client.GetCoverArtUrl(song.AlbumId, 240);
    }

    // ================= Track views: search / sort =================

    private void TrackSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_trackViews != null && sender is FrameworkElement { Tag: string key })
            RefreshTrackView(key);
    }

    private void RefreshTrackView(string key)
    {
        var view = _trackViews[key];
        var filtered = FilterSongs(view.Raw, view.Search.Text?.Trim() ?? "");
        view.List.ItemsSource = SortSongs(filtered, view.SortKey, view.Ascending);
    }

    private static IEnumerable<Song> FilterSongs(IEnumerable<Song> source, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return source;

        return source.Where(s =>
            s.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            s.ArtistName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            s.AlbumName.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    // sortKey is one of SortOptions' tags; null keeps the source order. Subsonic only has one
    // year per track, so ReleaseYear and Year sort the same field (same as the WPF app).
    private static List<Song> SortSongs(IEnumerable<Song> source, string? sortKey, bool ascending) => sortKey switch
    {
        "Album" => Order(source, s => s.AlbumName, ascending, StringComparer.OrdinalIgnoreCase),
        "AlbumArtist" => Order(source, s => s.AlbumArtistName, ascending, StringComparer.OrdinalIgnoreCase),
        "Artist" => Order(source, s => s.ArtistName, ascending, StringComparer.OrdinalIgnoreCase),
        "Duration" => Order(source, s => s.DurationSeconds, ascending),
        "Favorited" => Order(source, s => s.IsFavorited, ascending),
        "Genre" => Order(source, s => s.Genre, ascending, StringComparer.OrdinalIgnoreCase),
        "Name" => Order(source, s => s.Title, ascending, StringComparer.OrdinalIgnoreCase),
        "PlayCount" => Order(source, s => s.PlayCount, ascending),
        "Random" => source.OrderBy(_ => SortRandom.Next()).ToList(),
        "RecentlyAdded" => Order(source, s => s.CreatedAt ?? DateTime.MinValue, ascending),
        "RecentlyPlayed" => Order(source, s => s.PlayedAt ?? DateTime.MinValue, ascending),
        "ReleaseYear" or "Year" => Order(source, s => s.Year ?? 0, ascending),
        _ => source.ToList()
    };

    private static List<Song> Order<TKey>(IEnumerable<Song> source, Func<Song, TKey> keySelector, bool ascending, IComparer<TKey>? comparer = null)
    {
        comparer ??= Comparer<TKey>.Default;
        return (ascending ? source.OrderBy(keySelector, comparer) : source.OrderByDescending(keySelector, comparer)).ToList();
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } button)
            return;

        var view = _trackViews[key];
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var (tag, label) in SortOptions)
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = view.SortKey == tag };
            item.Click += (_, _) =>
            {
                view.SortKey = view.SortKey == tag ? null : tag; // clicking the active sort clears it
                RefreshTrackView(key);
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(button);
    }

    private void SortDirectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;

        var view = _trackViews[key];
        view.Ascending = !view.Ascending;
        view.DirectionButton.Content = view.Ascending ? "↑" : "↓";
        RefreshTrackView(key);
    }

    // The small ▶ next to each list header: pops out Play / Shuffle.
    private void PlayMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } button)
            return;

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var play = new MenuFlyoutItem { Text = "Play" };
        play.Click += (_, _) => PlayTrackView(key, shuffle: false);
        var shuffle = new MenuFlyoutItem { Text = "Shuffle" };
        shuffle.Click += (_, _) => PlayTrackView(key, shuffle: true);
        flyout.Items.Add(play);
        flyout.Items.Add(shuffle);
        flyout.ShowAt(button);
    }

    private void PlayTrackView(string key, bool shuffle)
    {
        if (_trackViews[key].List.ItemsSource is not List<Song> { Count: > 0 } songs)
            return;

        PlayQueueFrom(shuffle ? songs.OrderBy(_ => _random.Next()).ToList() : songs, 0);
    }

    // Double-click a row to play: queues the whole list as shown (search + sort), from that track.
    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not ListView { ItemsSource: List<Song> source } || (e.OriginalSource as FrameworkElement)?.DataContext is not Song song)
            return;

        var index = source.IndexOf(song);
        if (index >= 0)
            PlayQueueFrom(source, index);
    }

    // Right-click: select the row under the cursor, then the WPF app's song context menu.
    private void TrackList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not ListView { Tag: string key } list || (e.OriginalSource as FrameworkElement)?.DataContext is not Song song)
            return;

        list.SelectedItem = song;
        var flyout = BuildSongMenu(song, list.ItemsSource as List<Song>);

        if (key == "PlaylistTracks")
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            AddMenuItem(flyout, "Remove from playlist", async () => await RemoveFromPlaylistAsync(song));
        }

        flyout.ShowAt(list, e.GetPosition(list));
        e.Handled = true;
    }

    // The shared part of every song context menu; callers append list-specific items.
    private MenuFlyout BuildSongMenu(Song song, List<Song>? source)
    {
        var flyout = new MenuFlyout();
        AddMenuItem(flyout, "Play now", () =>
        {
            if (source != null && source.IndexOf(song) is var i and >= 0)
                PlayQueueFrom(source, i);
        });
        AddMenuItem(flyout, "Play next", () => QueuePlayNext(song));
        AddMenuItem(flyout, "Add to queue", () => QueueAppend(song));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(BuildAddToPlaylistSubmenu(song));
        AddFavoriteToggle(flyout, song);
        return flyout;
    }

    private static void AddMenuItem(MenuFlyout flyout, string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => onClick();
        flyout.Items.Add(item);
    }

    private void AddFavoriteToggle(MenuFlyout flyout, Song song) =>
        AddMenuItem(flyout, song.IsFavorited ? "Remove from favorites" : "Add to favorites", async () =>
        {
            try
            {
                if (song.IsFavorited)
                    await _client.UnstarSongAsync(song.Id);
                else
                    await _client.StarSongAsync(song.Id);
                song.IsFavorited = !song.IsFavorited;
            }
            catch { /* best-effort */ }
        });

    private MenuFlyoutSubItem BuildAddToPlaylistSubmenu(Song song)
    {
        var sub = new MenuFlyoutSubItem { Text = "Add to playlist" };
        foreach (var playlist in _playlists)
        {
            var item = new MenuFlyoutItem { Text = playlist.Name };
            item.Click += async (_, _) =>
            {
                try
                {
                    await _client.AddSongToPlaylistAsync(playlist.Id, song.Id);
                    await LoadPlaylistsAsync();
                }
                catch (Exception ex) { await ShowErrorAsync($"Failed to add to playlist: {ex.Message}"); }
            };
            sub.Items.Add(item);
        }

        if (_playlists.Count > 0)
            sub.Items.Add(new MenuFlyoutSeparator());

        var create = new MenuFlyoutItem { Text = "New playlist..." };
        create.Click += async (_, _) =>
        {
            var name = await PromptForPlaylistNameAsync();
            if (string.IsNullOrWhiteSpace(name))
                return;
            var id = await _client.CreatePlaylistAsync(name);
            if (id != null)
                await _client.AddSongToPlaylistAsync(id, song.Id);
            await LoadPlaylistsAsync();
        };
        sub.Items.Add(create);
        return sub;
    }

    // ================= Playlists =================

    private async Task LoadPlaylistsAsync()
    {
        try
        {
            var selectedId = (PlaylistsList.SelectedItem as Playlist)?.Id;
            _playlists = await _client.GetPlaylistsAsync();

            _suppressNavEvents = true;
            PlaylistsList.ItemsSource = _playlists;
            PlaylistsList.SelectedItem = _playlists.FirstOrDefault(p => p.Id == selectedId);
            _suppressNavEvents = false;

            HomePlaylistsList.ItemsSource = _playlists;
            HomePlaylistsSection.Visibility = _playlists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _suppressNavEvents = false;
            await ShowErrorAsync($"Failed to load playlists: {ex.Message}");
        }
    }

    private async void PlaylistsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavEvents || PlaylistsList.SelectedItem is not Playlist playlist)
            return;

        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();
        ClearSidebarSelection(keepPlaylist: true);
        HideAllPages();
        ShowPage(PlaylistsPage);

        _currentPlaylist = playlist;
        PlaylistTracksHeader.Text = playlist.Name.ToUpperInvariant();
        SetImage(PlaylistHeaderArt, playlist.CoverArtUrl);
        PlaylistSongsList.ItemsSource = null;

        try
        {
            var songs = await _client.GetPlaylistSongsAsync(playlist.Id);
            PopulateSongArt(songs);
            _trackViews["PlaylistTracks"].Raw = songs;
            RefreshTrackView("PlaylistTracks");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Failed to load playlist: {ex.Message}");
        }
    }

    private void PlaylistsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Playlist playlist)
            return;

        var flyout = new MenuFlyout();
        AddMenuItem(flyout, "Delete playlist", async () => await DeletePlaylistAsync(playlist));
        flyout.ShowAt(PlaylistsList, e.GetPosition(PlaylistsList));
        e.Handled = true;
    }

    private async void NewPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptForPlaylistNameAsync();
        if (string.IsNullOrWhiteSpace(name))
            return;

        await _client.CreatePlaylistAsync(name);
        await LoadPlaylistsAsync();
    }

    private async Task<string?> PromptForPlaylistNameAsync()
    {
        var nameBox = new TextBox { PlaceholderText = "Playlist name" };
        var dialog = new ContentDialog
        {
            Title = "New playlist",
            Content = nameBox,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? nameBox.Text.Trim() : null;
    }

    private async Task DeletePlaylistAsync(Playlist playlist)
    {
        var confirm = new ContentDialog
        {
            Title = "Delete playlist",
            Content = $"Delete \"{playlist.Name}\"? This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        await _client.DeletePlaylistAsync(playlist.Id);
        if (_currentPlaylist?.Id == playlist.Id)
        {
            _currentPlaylist = null;
            NavHome.IsChecked = true;
        }
        await LoadPlaylistsAsync();
    }

    private async Task RemoveFromPlaylistAsync(Song song)
    {
        if (_currentPlaylist == null || song.PlaylistIndex < 0)
            return;

        try
        {
            await _client.RemoveSongFromPlaylistAsync(_currentPlaylist.Id, song.PlaylistIndex);
            var songs = await _client.GetPlaylistSongsAsync(_currentPlaylist.Id);
            PopulateSongArt(songs);
            _trackViews["PlaylistTracks"].Raw = songs;
            RefreshTrackView("PlaylistTracks");
            await LoadPlaylistsAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Failed to remove from playlist: {ex.Message}");
        }
    }

    // ================= Full-screen player: Queue / Lyrics / Visualizer =================

    private void NowPlayingText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        FullScreenPlayerOverlay.Visibility = FullScreenPlayerOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (FullScreenPlayerOverlay.Visibility == Visibility.Visible)
            Motion.FadeIn(FullScreenPlayerOverlay);
        UpdateVisualizerCaptureState();
    }

    private void QueueToggleButton_Click(object sender, RoutedEventArgs e)
    {
        QueueTabButton.IsChecked = true;
        FullScreenPlayerOverlay.Visibility = Visibility.Visible;
        Motion.FadeIn(FullScreenPlayerOverlay);
        UpdateVisualizerCaptureState();
    }

    private void CloseFullScreenPlayer_Click(object sender, RoutedEventArgs e)
    {
        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();
    }

    private async void NowPlayingArtist_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_queueIndex < 0 || _queueIndex >= _queue.Count)
            return;

        var song = _queue[_queueIndex];
        await NavigateToArtistAsync(song.ArtistId, song.ArtistName);
    }

    private async void SidePanelTabButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string panel } || QueueList == null)
            return;

        QueueList.Visibility = panel == "Queue" ? Visibility.Visible : Visibility.Collapsed;
        LyricsPanelView.Visibility = panel == "Lyrics" ? Visibility.Visible : Visibility.Collapsed;
        VisualizerPanelView.Visibility = panel == "Visualizer" ? Visibility.Visible : Visibility.Collapsed;
        UpdateVisualizerCaptureState();

        if (panel == "Lyrics")
            await LoadLyricsForCurrentSongAsync();
    }

    private void QueueList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Song song)
            return;

        var index = _queue.IndexOf(song);
        if (index >= 0)
            PlayQueueIndex(index);
    }

    private void QueueList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Song song)
            return;

        QueueList.SelectedItem = song;
        var flyout = new MenuFlyout();
        flyout.Items.Add(BuildAddToPlaylistSubmenu(song));
        AddFavoriteToggle(flyout, song);
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "Remove from queue", () => RemoveFromQueue(song));
        flyout.ShowAt(QueueList, e.GetPosition(QueueList));
        e.Handled = true;
    }

    private void RemoveFromQueue(Song song)
    {
        var index = _queue.IndexOf(song);
        if (index < 0)
            return;

        _queue.RemoveAt(index);

        // Keep _queueIndex pointing at the same still-playing track after the shift.
        if (index < _queueIndex)
            _queueIndex--;
        else if (index == _queueIndex)
            _queueIndex = Math.Min(_queueIndex, _queue.Count - 1);

        QueueList.SelectedIndex = _queueIndex;
    }

    private async Task LoadLyricsForCurrentSongAsync()
    {
        var song = _queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex] : null;
        if (song == null)
        {
            LyricsList.ItemsSource = null;
            LyricsPlaceholder.Text = "Nothing playing.";
            LyricsPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var (lines, synced) = await _client.GetLyricsAsync(song.Id, song.ArtistName, song.Title);
            _lyricsAreSynced = synced;
            LyricsList.ItemsSource = lines;
            LyricsPlaceholder.Text = "No lyrics found for this track.";
            LyricsPlaceholder.Visibility = lines.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch
        {
            LyricsList.ItemsSource = null;
            LyricsPlaceholder.Text = "No lyrics found for this track.";
            LyricsPlaceholder.Visibility = Visibility.Visible;
        }
    }

    // Synced lyrics: double-click a line to jump there.
    private void LyricsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!_lyricsAreSynced || (e.OriginalSource as FrameworkElement)?.DataContext is not LyricLine line)
            return;

        _player.PlaybackSession.Position = line.Start;
    }

    // ================= Visualizer =================

    // Capture + per-frame rendering only run while the Visualizer tab of the (open) full-screen
    // player is showing - no point capturing audio or redrawing for a panel nobody can see.
    private void UpdateVisualizerCaptureState()
    {
        var showing = FullScreenPlayerOverlay.Visibility == Visibility.Visible
                      && VisualizerPanelView.Visibility == Visibility.Visible;
        if (showing)
            StartVisualizer();
        else
            StopVisualizer();
    }

    private void StartVisualizer()
    {
        if (_visualizerRenderHooked)
            return;

        _visualizerRenderHooked = true;
        _visualizerLastTicks = 0;
        _visualizerForceDraw = true;
        Array.Clear(_visualizerSmoothed);
        _loopback.Start();
        _visualizerTimer.Start();
    }

    private void StopVisualizer()
    {
        if (!_visualizerRenderHooked)
            return;

        _visualizerRenderHooked = false;
        _visualizerTimer.Stop();
        _loopback.Stop();
    }

    // Every ~16ms: pull the latest bands, smooth them over time, hand them to the Skia
    // canvas (which does the WPF app's spatial smoothing + curve drawing). Smoothing is
    // time-based - the WPF constant was 0.88 old / 0.12 new *per 60fps frame* - so the glide
    // speed stays the same even if the frame rate isn't a steady 60.
    private long _visualizerLastTicks;

    private void OnVisualizerRenderFrame(object? sender, object e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var dt = _visualizerLastTicks == 0 ? 1.0 / 60 : (now - _visualizerLastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        _visualizerLastTicks = now;
        var keep = (float)Math.Pow(0.88, Math.Clamp(dt, 0, 0.25) * 60);

        _spectrum.CopyLatestBandsTo(_visualizerLatestBands);
        float maxChange = 0;
        for (var i = 0; i < _visualizerSmoothed.Length; i++)
        {
            _visualizerSmoothed[i] = _visualizerSmoothed[i] * keep + _visualizerLatestBands[i] * (1 - keep);
            maxChange = Math.Max(maxChange, Math.Abs(_visualizerSmoothed[i] - _visualizerDrawn[i]));
        }

        // Only redraw when the curve would visibly move: paused/silent (a flat line) otherwise
        // still cost a full window re-render 60 times a second. 0.001 of the half-height is well
        // under a pixel, so skipped frames are indistinguishable.
        if (maxChange < 0.001f && !_visualizerForceDraw)
            return;

        _visualizerForceDraw = false;
        Array.Copy(_visualizerSmoothed, _visualizerDrawn, _visualizerDrawn.Length);
        VisualizerCanvas.SetLevels(_visualizerSmoothed);
    }

    private readonly float[] _visualizerDrawn = new float[SpectrumAnalyzer.BandCount];
    private bool _visualizerForceDraw = true;

    // ================= Queue / playback =================

    private void PlayQueueFrom(List<Song> source, int startIndex)
    {
        _queue.Clear();
        foreach (var song in source)
            _queue.Add(song);

        PlayQueueIndex(startIndex);
    }

    // Inserts right after the current track without interrupting playback; if nothing is
    // playing yet, starts the queue with this one song.
    private void QueuePlayNext(Song song)
    {
        if (_queueIndex < 0)
        {
            PlayQueueFrom(new List<Song> { song }, 0);
            return;
        }
        _queue.Insert(_queueIndex + 1, song);
    }

    private void QueueAppend(Song song)
    {
        if (_queueIndex < 0)
        {
            PlayQueueFrom(new List<Song> { song }, 0);
            return;
        }
        _queue.Add(song);
    }

    private async void PlayQueueIndex(int index)
    {
        if (index < 0 || index >= _queue.Count)
            return;

        _queueIndex = index;
        var song = _queue[index];

        _player.Source = MediaSource.CreateFromUri(new Uri(_client.GetStreamUrl(song.Id)));
        _player.Play();
        SetPlaying(true);

        NowPlayingText.Text = song.Title;
        NowPlayingSubText.Text = song.ArtistName;
        SetImage(NowPlayingArt, song.CoverArtUrl ?? _client.GetCoverArtUrl(song.AlbumId, 240));
        FullScreenTitleText.Text = song.Title;
        FullScreenArtistText.Text = song.ArtistName;
        // The full-screen art is 360x360 - fetch a bigger rendition than the 240px list thumbnails.
        SetImage(FullScreenArt, _client.GetCoverArtUrl(song.AlbumId, 720));
        QueueList.SelectedIndex = index;

        if (LyricsPanelView.Visibility == Visibility.Visible)
            await LoadLyricsForCurrentSongAsync();
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        PlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        _isShuffled = !_isShuffled;
        ShuffleIconPath.Stroke = (Brush)Application.Current.Resources[_isShuffled ? "AccentBrush" : "TextSecondaryBrush"];
    }

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        _repeatMode = _repeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        };

        var brush = (Brush)Application.Current.Resources[_repeatMode == RepeatMode.Off ? "TextSecondaryBrush" : "AccentBrush"];
        RepeatIconPath.Stroke = brush;
        RepeatArrowHead.Fill = brush;
        RepeatOneBadge.Foreground = brush;
        RepeatOneBadge.Visibility = _repeatMode == RepeatMode.One ? Visibility.Visible : Visibility.Collapsed;
    }

    // One rule for both the Next button and auto-advance: random-excluding-current when
    // shuffled, else sequential, wrapping to 0 only with RepeatMode.All. -1 = nothing next.
    private int GetNextQueueIndex()
    {
        if (_queue.Count == 0 || _queueIndex < 0)
            return -1;

        if (_isShuffled && _queue.Count > 1)
        {
            int next;
            do { next = _random.Next(_queue.Count); } while (next == _queueIndex);
            return next;
        }

        if (_queueIndex + 1 < _queue.Count)
            return _queueIndex + 1;

        return _repeatMode == RepeatMode.All ? 0 : -1;
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_queueIndex > 0)
            PlayQueueIndex(_queueIndex - 1);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e) => PlayQueueIndex(GetNextQueueIndex());

    // Natural completion only (not Stop/skip) scrobbles - skipping shouldn't inflate play counts.
    private async void OnMediaEnded()
    {
        if (_queueIndex >= 0 && _queueIndex < _queue.Count)
        {
            try { await _client.ScrobbleAsync(_queue[_queueIndex].Id); }
            catch { /* best-effort */ }
        }

        if (_repeatMode == RepeatMode.One && _queueIndex >= 0 && _queueIndex < _queue.Count)
        {
            PlayQueueIndex(_queueIndex);
            return;
        }

        var next = GetNextQueueIndex();
        if (next >= 0)
            PlayQueueIndex(next);
        else
            SetPlaying(false);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_queueIndex < 0)
            return;

        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
            SetPlaying(false);
        }
        else
        {
            _player.Play();
            SetPlaying(true);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _player.Pause();
        _player.PlaybackSession.Position = TimeSpan.Zero;
        SetPlaying(false);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _player.Volume = e.NewValue;
    }

    private void CommitSeek()
    {
        if (!_isUserSeeking)
            return;

        _isUserSeeking = false;
        if (_player.PlaybackSession.NaturalDuration > TimeSpan.Zero)
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
    }

    private void ProgressTimer_Tick(object? sender, object e)
    {
        var session = _player.PlaybackSession;
        if (session == null || session.NaturalDuration <= TimeSpan.Zero)
            return;

        var position = session.Position;
        var duration = session.NaturalDuration;

        if (!_isUserSeeking)
        {
            ProgressSlider.Maximum = duration.TotalSeconds;
            ProgressSlider.Value = position.TotalSeconds;
        }
        ElapsedText.Text = $"{(int)position.TotalMinutes}:{position.Seconds:D2}";
        DurationText.Text = $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}";
    }

    // ================= Image helpers =================

    // Single named art elements (hero, now playing, full-screen, playlist header) use an
    // ImageBrush background on a rounded Border, like the WPF app does.
    private static void SetImage(Border target, string? url) => target.Background = MakeImageBrush(url);

    private static ImageBrush? MakeImageBrush(string? url) =>
        string.IsNullOrEmpty(url)
            ? null
            : new ImageBrush { ImageSource = new BitmapImage(new Uri(url)), Stretch = Stretch.UniformToFill };
}
