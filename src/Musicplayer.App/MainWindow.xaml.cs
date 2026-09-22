using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Musicplayer.App.Models;
using Musicplayer.App.Services;
using Musicplayer.App.Views;

namespace Musicplayer.App
{
    public partial class MainWindow : Window
    {
        private readonly NavidromeClient _client;
        private bool _isPlaying;
        private bool _isUserSeeking;

        private readonly ObservableCollection<Song> _queue = new();
        private int _queueIndex = -1;
        private bool _isShuffled;
        private RepeatMode _repeatMode = RepeatMode.Off;
        private readonly Random _shuffleRandom = new();

        private enum RepeatMode { Off, All, One }

        private readonly DispatcherTimer _progressTimer;

        private bool _homeLoaded;
        private bool _artistsLoaded;
        private bool _albumsPageLoaded;
        private bool _songsPageLoaded;

        private List<Song> _albumSongsRaw = new();
        private List<Song> _allSongsRaw = new();
        private string? _albumTrackSortKey;
        private string? _allTracksSortKey;
        private bool _albumTrackSortAscending = true;
        private bool _allTracksSortAscending = true;
        private List<Artist> _artistsRaw = new();
        private Artist? _currentArtist;
        private List<Album> _artistAlbumsRaw = new();
        private int _artistAlbumsColumns = 3;
        private List<Album> _allAlbumsRaw = new();
        private int _allAlbumsColumns = 4;

        private List<Album> _genreAlbumsRaw = new();
        private int _genreAlbumsColumns = 4;

        private List<Album> _heroAlbums = new();
        private int _heroIndex;

        private List<Playlist> _playlistsRaw = new();
        private List<Song> _playlistSongsRaw = new();

        private string? _selectedPlaylistId;
        private string? _playlistTrackSortKey;
        private bool _playlistTrackSortAscending = true;

        private List<LyricLine> _currentLyrics = new();
        private bool _lyricsAreSynced;
        private LyricLine? _currentLyricLine;

        private readonly AudioVisualizer _audioVisualizer = new();
        private readonly float[] _visualizerLatestBands = new float[AudioVisualizer.BandCount];
        private readonly float[] _visualizerSmoothed = new float[AudioVisualizer.BandCount];
        private PathFigure? _visualizerFigure;
        private bool _visualizerRenderHooked;

        // Parameterless constructor kept only so the XAML designer doesn't choke;
        // the app always uses the constructor below with an authenticated client.
        public MainWindow() : this(new NavidromeClient())
        {
        }

        public MainWindow(NavidromeClient client)
        {
            InitializeComponent();
            DarkTitleBar.Apply(this);
            WindowPlacement.CenterOnPrimaryScreen(this);
            _client = client;
            QueueList.ItemsSource = _queue;

            // Restores the last-used volume; VolumeSlider_ValueChanged (already null-guards Player
            // for the XAML-declared initial Value firing before InitializeComponent finishes) keeps
            // Player.Volume in sync with whatever this ends up setting.
            VolumeSlider.Value = SettingsService.Load().Volume;
            Player.Volume = VolumeSlider.Value;

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _progressTimer.Tick += ProgressTimer_Tick;

            Closed += (_, _) =>
            {
                CompositionTarget.Rendering -= OnVisualizerRenderFrame;
                _audioVisualizer.Dispose();
            };

            // Set after everything above is wired up (fields, ItemsSource, timer) since
            // checking NavHome fires Nav_Checked synchronously, which loads the Home page.
            NavHome.IsChecked = true;

            // Selects "Queue" as the default content shown when the full-screen player opens.
            QueueTabButton.IsChecked = true;

            // Playlists live directly in the sidebar (not behind a nav tab), so they load
            // unconditionally at startup rather than lazily on first visit to a "Playlists" page.
            _ = LoadPlaylistsAsync();
        }

        // Quick fade-in whenever a page/section becomes visible, so switching nav items (or
        // opening a playlist/genre) feels like a transition rather than an instant hard cut.
        private static void FadeIn(UIElement element)
        {
            element.Opacity = 0;
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.18)));
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string tag })
                return;

            HomePage.Visibility = Visibility.Collapsed;
            ArtistsPage.Visibility = Visibility.Collapsed;
            ArtistDetailPage.Visibility = Visibility.Collapsed;
            AlbumsPage.Visibility = Visibility.Collapsed;
            SongsPage.Visibility = Visibility.Collapsed;
            PlaylistsPage.Visibility = Visibility.Collapsed;
            GenreAlbumsPage.Visibility = Visibility.Collapsed;
            // Keep the sidebar single-selection: picking a top-level nav page deselects any playlist.
            PlaylistsList.SelectedItem = null;

            switch (tag)
            {
                case "Home":
                    HomePage.Visibility = Visibility.Visible;
                    FadeIn(HomePage);
                    if (!_homeLoaded)
                    {
                        _homeLoaded = true;
                        _ = LoadHomeAsync();
                    }
                    break;

                case "Artists":
                    ArtistsPage.Visibility = Visibility.Visible;
                    FadeIn(ArtistsPage);
                    if (!_artistsLoaded)
                    {
                        _artistsLoaded = true;
                        _ = LoadArtistsAsync();
                    }
                    break;

                case "Albums":
                    AlbumsPage.Visibility = Visibility.Visible;
                    FadeIn(AlbumsPage);
                    if (!_albumsPageLoaded)
                    {
                        _albumsPageLoaded = true;
                        _ = LoadAllAlbumsAsync();
                    }
                    break;

                case "Songs":
                    SongsPage.Visibility = Visibility.Visible;
                    FadeIn(SongsPage);
                    if (!_songsPageLoaded)
                    {
                        _songsPageLoaded = true;
                        _ = LoadAllSongsAsync();
                    }
                    break;
            }
        }

        private async Task LoadHomeAsync()
        {
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

                var genres = genresTask.Result.Take(10).ToList();
                HomeGenresList.ItemsSource = genres;
                HomeGenresSection.Visibility = genres.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load home page: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowHeroAlbum(int index)
        {
            var album = _heroAlbums[index];
            HeroBackdropBrush.ImageSource = new BitmapImage(new Uri(album.CoverArtUrl!));
            HeroArtBrush.ImageSource = new BitmapImage(new Uri(album.CoverArtUrl!));
            HeroTitleText.Text = album.Name;
            HeroArtistText.Text = album.ArtistName;

            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(album.Genre))
                meta.Add(album.Genre);
            if (album.Year is > 0)
                meta.Add(album.Year.Value.ToString());
            HeroMetaText.Text = string.Join(" · ", meta);
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

        private async void HeroArtistText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_heroAlbums.Count == 0)
                return;

            var album = _heroAlbums[_heroIndex];
            await NavigateToArtistAsync(album.ArtistId, album.ArtistName);
        }

        private async void HeroTitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_heroAlbums.Count == 0)
                return;

            var album = _heroAlbums[_heroIndex];
            await NavigateToArtistAsync(album.ArtistId, album.ArtistName);
            await ShowAlbumTracksAsync(album);
        }

        private async void HeroPlayOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_heroAlbums.Count == 0)
                return;

            await PlayAlbumAsync(_heroAlbums[_heroIndex]);
        }

        // Most/recently-played rows can legitimately come back empty (fresh server, no play
        // history yet) - collapse the whole row rather than showing an empty header.
        private void BindHomeRow(FrameworkElement section, ItemsControl list, List<Album> albums)
        {
            foreach (var album in albums)
                album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, 240);

            list.ItemsSource = albums;
            section.Visibility = albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HomeMoreAlbumsButton_Click(object sender, RoutedEventArgs e) => NavAlbums.IsChecked = true;

        // A horizontal-only ScrollViewer doesn't consume the plain (vertical) mouse wheel by
        // default, so it bubbles up to HomePage's own vertical ScrollViewer instead - scrolling
        // the whole page instead of just this row. Redirect the wheel delta to horizontal scroll
        // and mark it handled so it stops here.
        private void HorizontalCarousel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer)
                return;

            scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private void HomePlaylistCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: Playlist playlist })
                PlaylistsList.SelectedItem = playlist;
        }

        private async Task LoadAllAlbumsAsync()
        {
            try
            {
                var albums = await _client.GetAlbumListAsync("alphabeticalByName", 500);
                foreach (var album in albums)
                    album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, 240);

                _allAlbumsRaw = albums;
                RefreshAllAlbums();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load albums: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadAllSongsAsync()
        {
            try
            {
                var songs = await _client.GetAllSongsAsync(500);
                foreach (var song in songs)
                    song.CoverArtUrl = _client.GetCoverArtUrl(song.AlbumId, 240);

                _allSongsRaw = songs;
                RefreshAllTracks();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load songs: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static readonly Random SortRandom = new();

        // sortKey matches a MenuItem.Tag from one of the "Sort" popout menus; null means
        // whatever order the source list already came in (album track order / artist-then-title).
        // Subsonic only exposes one "year" tag per track, so "ReleaseYear" and "Year" both
        // sort that same field - the ascending toggle is what actually differentiates them.
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

        private static IEnumerable<Song> FilterSongs(IEnumerable<Song> source, string search)
        {
            if (string.IsNullOrWhiteSpace(search))
                return source;

            return source.Where(s =>
                s.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                s.ArtistName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<Album> FilterAlbums(IEnumerable<Album> source, string search)
        {
            if (string.IsNullOrWhiteSpace(search))
                return source;

            return source.Where(a =>
                a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.ArtistName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<Artist> FilterArtists(IEnumerable<Artist> source, string search)
        {
            if (string.IsNullOrWhiteSpace(search))
                return source;

            return source.Where(a => a.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        private void RefreshAlbumTracks()
        {
            // These controls fire before InitializeComponent() finishes building the whole tree
            // (default SelectedIndex/empty Text), so guard against being called too early.
            if (SongsList == null || AlbumTrackSearchBox == null)
                return;

            var filtered = FilterSongs(_albumSongsRaw, AlbumTrackSearchBox.Text);
            SongsList.ItemsSource = SortSongs(filtered, _albumTrackSortKey, _albumTrackSortAscending);
        }

        private void RefreshAllTracks()
        {
            if (AllSongsList == null || AllTracksSearchBox == null)
                return;

            var filtered = FilterSongs(_allSongsRaw, AllTracksSearchBox.Text);
            AllSongsList.ItemsSource = SortSongs(filtered, _allTracksSortKey, _allTracksSortAscending);
        }

        private void RefreshAllAlbums()
        {
            if (AllAlbumsList == null || AllAlbumsSearchBox == null)
                return;

            var filtered = FilterAlbums(_allAlbumsRaw, AllAlbumsSearchBox.Text);
            AllAlbumsList.ItemsSource = filtered.Chunk(_allAlbumsColumns).ToList();
        }

        // AllAlbumsList's rows are pre-chunked (see RefreshAllAlbums) rather than laid out by a
        // WrapPanel, so the row count per line has to be recomputed whenever the available width
        // changes - 152 matches LargeAlbumCardTemplate's outer StackPanel Width.
        private void AllAlbumsList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var columns = Math.Max(1, (int)(e.NewSize.Width / 152));
            if (columns == _allAlbumsColumns)
                return;

            _allAlbumsColumns = columns;
            RefreshAllAlbums();
        }

        private void RefreshGenreAlbums()
        {
            if (GenreAlbumsList == null)
                return;

            GenreAlbumsList.ItemsSource = _genreAlbumsRaw.Chunk(_genreAlbumsColumns).ToList();
        }

        // Same row-chunking approach as AllAlbumsList_SizeChanged, for the same reason
        // (LargeAlbumCardTemplate, so 152 again).
        private void GenreAlbumsList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var columns = Math.Max(1, (int)(e.NewSize.Width / 152));
            if (columns == _genreAlbumsColumns)
                return;

            _genreAlbumsColumns = columns;
            RefreshGenreAlbums();
        }

        private void RefreshArtists()
        {
            if (ArtistsList == null || ArtistSearchBox == null)
                return;

            ArtistsList.ItemsSource = FilterArtists(_artistsRaw, ArtistSearchBox.Text).ToList();
        }

        private void RefreshPlaylistTracks()
        {
            if (PlaylistSongsList == null || PlaylistTrackSearchBox == null)
                return;

            var filtered = FilterSongs(_playlistSongsRaw, PlaylistTrackSearchBox.Text);
            PlaylistSongsList.ItemsSource = SortSongs(filtered, _playlistTrackSortKey, _playlistTrackSortAscending);
        }

        private void SortButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { ContextMenu: { } menu } button)
                return;

            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }

        private void SortDirectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string pageTag } button)
                return;

            switch (pageTag)
            {
                case "AlbumTracks":
                    _albumTrackSortAscending = !_albumTrackSortAscending;
                    button.Content = _albumTrackSortAscending ? "↑" : "↓";
                    RefreshAlbumTracks();
                    break;
                case "AllTracks":
                    _allTracksSortAscending = !_allTracksSortAscending;
                    button.Content = _allTracksSortAscending ? "↑" : "↓";
                    RefreshAllTracks();
                    break;
                case "PlaylistTracks":
                    _playlistTrackSortAscending = !_playlistTrackSortAscending;
                    button.Content = _playlistTrackSortAscending ? "↑" : "↓";
                    RefreshPlaylistTracks();
                    break;
            }
        }

        private void SortMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // MenuItem has no built-in GroupName on net6.0-windows, so enforce radio-style
            // checking manually: only the clicked item in this popout stays checked.
            if (sender is not MenuItem { Tag: string sortKey, Parent: ContextMenu { PlacementTarget: Button { Tag: string pageTag } } menu })
                return;

            foreach (var sibling in menu.Items.OfType<MenuItem>())
                sibling.IsChecked = ReferenceEquals(sibling, sender);

            switch (pageTag)
            {
                case "AlbumTracks":
                    _albumTrackSortKey = sortKey;
                    RefreshAlbumTracks();
                    break;
                case "AllTracks":
                    _allTracksSortKey = sortKey;
                    RefreshAllTracks();
                    break;
                case "PlaylistTracks":
                    _playlistTrackSortKey = sortKey;
                    RefreshPlaylistTracks();
                    break;
            }
        }

        private void AlbumTrackSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAlbumTracks();

        private void AllTracksSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAllTracks();

        private void AllAlbumsSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAllAlbums();

        private void ArtistSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshArtists();

        private void PlaylistTrackSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshPlaylistTracks();

        private async Task LoadArtistsAsync()
        {
            try
            {
                var artists = await _client.GetArtistsAsync();
                _artistsRaw = artists;
                RefreshArtists();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load artists: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ArtistsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ArtistsList.SelectedItem is not Artist selected)
                return;

            await NavigateToArtistAsync(selected.Id, selected.Name);
        }

        // Shared by clicking an artist in the Artists list and clicking an artist name anywhere
        // else in the app (now-playing bar, full-screen player) - both just need an id/fallback
        // name, not an actual selection in ArtistsList (which may not even have that artist loaded
        // yet, e.g. if it's still filtered out by a search box).
        private async Task NavigateToArtistAsync(string artistId, string fallbackName)
        {
            if (string.IsNullOrEmpty(artistId))
                return;

            FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;

            // Opening an artist acts like its own page, same as a playlist/genre: hide every
            // other page/nav selection so exactly one thing in the sidebar+content is active.
            HomePage.Visibility = Visibility.Collapsed;
            ArtistsPage.Visibility = Visibility.Collapsed;
            AlbumsPage.Visibility = Visibility.Collapsed;
            SongsPage.Visibility = Visibility.Collapsed;
            PlaylistsPage.Visibility = Visibility.Collapsed;
            GenreAlbumsPage.Visibility = Visibility.Collapsed;
            ArtistDetailPage.Visibility = Visibility.Visible;
            FadeIn(ArtistDetailPage);
            NavHome.IsChecked = false;
            NavArtists.IsChecked = false;
            NavAlbums.IsChecked = false;
            NavSongs.IsChecked = false;
            PlaylistsList.SelectedItem = null;

            ArtistAlbumTracksView.Visibility = Visibility.Collapsed;
            ArtistDiscographyView.Visibility = Visibility.Visible;
            AlbumsList.ItemsSource = null;
            SongsList.ItemsSource = null;
            _albumSongsRaw = new List<Song>();
            _artistAlbumsRaw = new List<Album>();

            ArtistHeaderNameText.Text = fallbackName;
            ArtistHeaderStatsText.Text = "";
            SetImageBrushSource(ArtistHeaderBackdropBrush, null);
            SetImageBrushSource(ArtistHeaderArtBrush, null);
            SetArtistFavoriteIcon(false);

            try
            {
                var (artist, albums) = await _client.GetArtistDetailAsync(artistId);
                _currentArtist = artist;
                foreach (var album in albums)
                    album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, 240);

                _artistAlbumsRaw = albums;
                RefreshArtistAlbums();

                var totalSongs = albums.Sum(a => a.SongCount);
                ArtistHeaderNameText.Text = artist.Name;
                ArtistHeaderStatsText.Text = $"{albums.Count} album{(albums.Count == 1 ? "" : "s")} · {totalSongs} track{(totalSongs == 1 ? "" : "s")}";
                SetImageBrushSource(ArtistHeaderBackdropBrush, artist.CoverArtUrl);
                SetImageBrushSource(ArtistHeaderArtBrush, artist.CoverArtUrl);
                SetArtistFavoriteIcon(artist.IsFavorited);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load albums: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void NowPlayingArtist_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_queueIndex < 0 || _queueIndex >= _queue.Count)
                return;

            var song = _queue[_queueIndex];
            await NavigateToArtistAsync(song.ArtistId, song.ArtistName);
        }

        private void SetArtistFavoriteIcon(bool favorited)
        {
            var brush = favorited ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
            ArtistFavoriteIcon.Fill = brush;
        }

        private void ArtistHeaderPlayButton_Click(object sender, RoutedEventArgs e) => TriggerPlayFromTop("Artist", shuffle: false);

        private void ArtistHeaderShuffleButton_Click(object sender, RoutedEventArgs e) => TriggerPlayFromTop("Artist", shuffle: true);

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
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to update favorite: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BackToDiscographyButton_Click(object sender, RoutedEventArgs e)
        {
            ArtistAlbumTracksView.Visibility = Visibility.Collapsed;
            ArtistDiscographyView.Visibility = Visibility.Visible;
            FadeIn(ArtistDiscographyView);
        }

        private void RefreshArtistAlbums()
        {
            if (AlbumsList == null)
                return;

            AlbumsList.ItemsSource = _artistAlbumsRaw.Chunk(_artistAlbumsColumns).ToList();
        }

        // AlbumsList's rows are pre-chunked (see RefreshArtistAlbums), same reasoning as
        // AllAlbumsList_SizeChanged: 112 matches AlbumCardTemplate's outer StackPanel Width.
        private void AlbumsList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var columns = Math.Max(1, (int)(e.NewSize.Width / 112));
            if (columns == _artistAlbumsColumns)
                return;

            _artistAlbumsColumns = columns;
            RefreshArtistAlbums();
        }

        // Cards now live inside a per-row ItemsControl rather than being selectable ListBoxItems
        // (see AlbumsList in XAML), so a click on one no longer raises ListBox.SelectionChanged -
        // this replaces that as the "view this album's tracks" trigger.
        private async void AlbumCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not Album album)
                return;

            // Don't double-handle a click on the hover-play overlay (AlbumPlayOverlay_MouseLeftButtonDown
            // plays the album instead) or the artist-name sub-text (CardArtistText_MouseLeftButtonDown
            // goes to the artist only, without drilling into this specific album) - both fire later in
            // the bubble phase, this is the tunneling Preview handler so it runs first.
            if (e.OriginalSource is DependencyObject source &&
                (IsDescendantOf(source, "HoverOverlay") || IsDescendantOf(source, "CardArtistText")))
                return;

            // Opening an album from anywhere (Home, Albums, Genre pages, or another artist's
            // discography) always lands on its artist's page with this album's tracks pre-opened -
            // there's no separate standalone "album" page, so this is the one consistent destination.
            await NavigateToArtistAsync(album.ArtistId, album.ArtistName);
            await ShowAlbumTracksAsync(album);
        }

        private async void CardArtistText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement { DataContext: Album album })
                return;

            await NavigateToArtistAsync(album.ArtistId, album.ArtistName);
        }

        private async void SongArtistText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement { DataContext: Song song })
                return;

            await NavigateToArtistAsync(song.ArtistId, song.ArtistName);
        }

        private static bool IsDescendantOf(DependencyObject? element, string ancestorName)
        {
            while (element != null)
            {
                if (element is FrameworkElement { Name: var name } && name == ancestorName)
                    return true;

                element = VisualTreeHelper.GetParent(element);
            }

            return false;
        }

        private async void GenreCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: Genre genre })
                return;

            await ShowGenreAlbumsAsync(genre);
        }

        private async Task ShowGenreAlbumsAsync(Genre genre)
        {
            // Opening a genre acts like its own page, same as selecting a playlist: hide every
            // other page/nav selection so exactly one thing in the sidebar+content is active.
            HomePage.Visibility = Visibility.Collapsed;
            ArtistsPage.Visibility = Visibility.Collapsed;
            ArtistDetailPage.Visibility = Visibility.Collapsed;
            AlbumsPage.Visibility = Visibility.Collapsed;
            SongsPage.Visibility = Visibility.Collapsed;
            PlaylistsPage.Visibility = Visibility.Collapsed;
            GenreAlbumsPage.Visibility = Visibility.Visible;
            FadeIn(GenreAlbumsPage);
            NavHome.IsChecked = false;
            NavArtists.IsChecked = false;
            NavAlbums.IsChecked = false;
            NavSongs.IsChecked = false;
            PlaylistsList.SelectedItem = null;

            GenreAlbumsHeader.Text = genre.Name.ToUpperInvariant();
            GenreAlbumsList.ItemsSource = null;

            try
            {
                var albums = await _client.GetAlbumsByGenreAsync(genre.Name, 500);
                foreach (var album in albums)
                    album.CoverArtUrl = _client.GetCoverArtUrl(album.Id, 240);

                _genreAlbumsRaw = albums;
                RefreshGenreAlbums();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load {genre.Name} albums: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ShowAlbumTracksAsync(Album album)
        {
            ArtistDiscographyView.Visibility = Visibility.Collapsed;
            ArtistAlbumTracksView.Visibility = Visibility.Visible;
            FadeIn(ArtistAlbumTracksView);
            ArtistAlbumTracksHeader.Text = album.Name.ToUpperInvariant();

            SongsList.ItemsSource = null;

            try
            {
                var songs = await _client.GetSongsForAlbumAsync(album.Id);
                foreach (var song in songs)
                    song.CoverArtUrl = _client.GetCoverArtUrl(song.AlbumId, 240);

                _albumSongsRaw = songs;
                RefreshAlbumTracks();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load songs: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Right-clicking a track selects it first, so the context menu always acts on the track under the cursor.
        private void TrackItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListViewItem item)
                item.IsSelected = true;
        }

        private void PlaylistItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem item)
                item.IsSelected = true;
        }

        private async Task LoadPlaylistsAsync()
        {
            try
            {
                var playlists = await _client.GetPlaylistsAsync();
                _playlistsRaw = playlists;
                PlaylistsList.ItemsSource = playlists;
                HomePlaylistsList.ItemsSource = playlists;
                HomePlaylistsSection.Visibility = playlists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load playlists: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void PlaylistsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PlaylistSongsList.ItemsSource = null;
            _playlistSongsRaw = new List<Song>();

            if (PlaylistsList.SelectedItem is not Playlist playlist)
            {
                _selectedPlaylistId = null;
                return;
            }

            _selectedPlaylistId = playlist.Id;
            PlaylistTracksHeader.Text = playlist.Name.ToUpperInvariant();
            SetImageBrushSource(PlaylistHeaderArtBrush, playlist.CoverArtUrl);

            // Selecting a playlist acts like its own page: show it and deselect whichever
            // top-level nav item was active, so the sidebar has exactly one active item.
            HomePage.Visibility = Visibility.Collapsed;
            ArtistsPage.Visibility = Visibility.Collapsed;
            ArtistDetailPage.Visibility = Visibility.Collapsed;
            AlbumsPage.Visibility = Visibility.Collapsed;
            SongsPage.Visibility = Visibility.Collapsed;
            GenreAlbumsPage.Visibility = Visibility.Collapsed;
            PlaylistsPage.Visibility = Visibility.Visible;
            FadeIn(PlaylistsPage);
            NavHome.IsChecked = false;
            NavArtists.IsChecked = false;
            NavAlbums.IsChecked = false;
            NavSongs.IsChecked = false;

            await LoadPlaylistSongsAsync(playlist.Id);
        }

        private async Task LoadPlaylistSongsAsync(string playlistId)
        {
            try
            {
                var songs = await _client.GetPlaylistSongsAsync(playlistId);
                for (var i = 0; i < songs.Count; i++)
                {
                    songs[i].PlaylistIndex = i;
                    songs[i].CoverArtUrl = _client.GetCoverArtUrl(songs[i].AlbumId, 240);
                }

                _playlistSongsRaw = songs;
                RefreshPlaylistTracks();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load playlist: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void NewPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var name = InputDialog.Show(this, "New playlist", "Playlist name:", "Create");
            if (string.IsNullOrWhiteSpace(name))
                return;

            try
            {
                await _client.CreatePlaylistAsync(name);
                await LoadPlaylistsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to create playlist: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DeletePlaylistMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistsList.SelectedItem is not Playlist playlist)
                return;

            if (MessageBox.Show(this, $"Delete playlist \"{playlist.Name}\"?", "LunaDrome",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                await _client.DeletePlaylistAsync(playlist.Id);
                PlaylistSongsList.ItemsSource = null;
                _selectedPlaylistId = null;

                if (PlaylistsPage.Visibility == Visibility.Visible)
                    NavHome.IsChecked = true; // the deleted playlist's page can't stay shown

                await LoadPlaylistsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to delete playlist: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RemoveFromPlaylistMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistSongsList.SelectedItem is not Song song || _selectedPlaylistId is not { } playlistId || song.PlaylistIndex < 0)
                return;

            try
            {
                await _client.RemoveSongFromPlaylistAsync(playlistId, song.PlaylistIndex);
                // Re-fetch rather than patch indices locally: removing one song shifts every
                // later song's index, and Subsonic's songIndexToRemove needs the current index.
                await LoadPlaylistSongsAsync(playlistId);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to remove from playlist: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PlaylistSongsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PlaylistSongsList.SelectedItem is not Song song)
                return;

            if (PlaylistSongsList.ItemsSource is not IEnumerable<Song> playlistSongs)
                return;

            var songsList = playlistSongs.ToList();
            var startIndex = songsList.IndexOf(song);

            _queue.Clear();
            foreach (var s in songsList)
                _queue.Add(s);

            PlayQueueIndex(startIndex < 0 ? 0 : startIndex);
        }

        // Walks up from a (possibly nested, e.g. inside the "Add to playlist" submenu) MenuItem
        // to the ContextMenu that owns it, regardless of how deep the click came from.
        private static ContextMenu? FindOwningContextMenu(MenuItem item)
        {
            DependencyObject? current = item;
            while (current is MenuItem mi)
            {
                if (mi.Parent is ContextMenu menu)
                    return menu;

                current = mi.Parent as DependencyObject;
            }

            return null;
        }

        // The song a context menu action should apply to, resolved from whichever list
        // (ListView track list, or the Queue's ListBox) the menu's PlacementTarget is.
        private static Song? GetSelectedSong(ContextMenu? menu) => menu?.PlacementTarget switch
        {
            ListView listView => listView.SelectedItem as Song,
            ListBox listBox => listBox.SelectedItem as Song,
            _ => null
        };

        private static Song? GetContextMenuSong(MenuItem item) => GetSelectedSong(FindOwningContextMenu(item));

        // Every song context menu's "Favorite" entry shares this Tag so this handler can find
        // it generically regardless of which of the four context menus just opened.
        private void SongContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu)
                return;

            if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "FavoriteToggle") is not { } favoriteItem)
                return;

            var song = GetSelectedSong(menu);
            favoriteItem.IsEnabled = song != null;
            favoriteItem.Header = song is { IsFavorited: true } ? "Remove from favorites" : "Add to favorites";
        }

        private async void FavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || GetContextMenuSong(item) is not { } song)
                return;

            try
            {
                if (song.IsFavorited)
                    await _client.UnstarSongAsync(song.Id);
                else
                    await _client.StarSongAsync(song.Id);

                song.IsFavorited = !song.IsFavorited;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to update favorite: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void AddToPlaylistSubmenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem submenu)
                return;

            var song = GetContextMenuSong(submenu);

            submenu.Items.Clear();

            var newPlaylistItem = new MenuItem { Header = "New playlist..." };
            newPlaylistItem.Click += async (_, _) => await CreatePlaylistWithSongAsync(song);
            submenu.Items.Add(newPlaylistItem);

            List<Playlist> playlists;
            try
            {
                playlists = await _client.GetPlaylistsAsync();
            }
            catch
            {
                return; // leave just "New playlist..." if the fetch fails
            }

            if (playlists.Count == 0)
                return;

            submenu.Items.Add(new Separator());

            foreach (var playlist in playlists)
            {
                var item = new MenuItem { Header = playlist.Name };
                item.Click += async (_, _) => await AddSongToExistingPlaylistAsync(playlist.Id, song);
                submenu.Items.Add(item);
            }
        }

        private async Task AddSongToExistingPlaylistAsync(string playlistId, Song? song)
        {
            if (song == null)
                return;

            try
            {
                await _client.AddSongToPlaylistAsync(playlistId, song.Id);
                if (_selectedPlaylistId == playlistId)
                    await LoadPlaylistSongsAsync(playlistId);
                await LoadPlaylistsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to add to playlist: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task CreatePlaylistWithSongAsync(Song? song)
        {
            var name = InputDialog.Show(this, "New playlist", "Playlist name:", "Create");
            if (string.IsNullOrWhiteSpace(name))
                return;

            try
            {
                var playlistId = await _client.CreatePlaylistAsync(name);
                if (playlistId != null && song != null)
                    await _client.AddSongToPlaylistAsync(playlistId, song.Id);

                await LoadPlaylistsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to create playlist: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void AlbumPlayOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (sender is not FrameworkElement element || element.DataContext is not Album album)
                return;

            await PlayAlbumAsync(album);
        }

        private async Task PlayAlbumAsync(Album album)
        {
            try
            {
                var songs = await _client.GetSongsForAlbumAsync(album.Id);
                foreach (var song in songs)
                    song.CoverArtUrl = _client.GetCoverArtUrl(song.AlbumId, 240);

                _queue.Clear();
                foreach (var s in songs)
                    _queue.Add(s);

                PlayQueueIndex(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to play album: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SongsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SongsList.SelectedItem is not Song song)
                return;

            if (SongsList.ItemsSource is not IEnumerable<Song> albumSongs)
                return;

            // Replace the queue with the whole album, starting from the double-clicked track.
            var songsList = albumSongs.ToList();
            var startIndex = songsList.IndexOf(song);

            _queue.Clear();
            foreach (var s in songsList)
                _queue.Add(s);

            PlayQueueIndex(startIndex < 0 ? 0 : startIndex);
        }

        private void AllSongsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AllSongsList.SelectedItem is not Song song)
                return;

            // Library-wide list: play just this one song next, don't replace the queue with 500 tracks.
            var insertAt = _queueIndex + 1;
            _queue.Insert(insertAt, song);
            PlayQueueIndex(insertAt);
        }

        // The track context menus are shared across two different ListViews (per-album and
        // library-wide), so handlers resolve the actual source list via the menu's PlacementTarget
        // rather than hardcoding one control name.
        private static ListView? GetOwningTrackList(object sender) =>
            (sender as MenuItem)?.Parent is ContextMenu { PlacementTarget: ListView listView } ? listView : null;

        private void PlayNowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetOwningTrackList(sender)?.SelectedItem is not Song song)
                return;

            var insertAt = _queueIndex + 1;
            _queue.Insert(insertAt, song);
            PlayQueueIndex(insertAt);
        }

        private void PlayNextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetOwningTrackList(sender)?.SelectedItem is not Song song)
                return;

            _queue.Insert(_queueIndex + 1, song);
        }

        private void AddToQueueMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetOwningTrackList(sender)?.SelectedItem is not Song song)
                return;

            _queue.Add(song);
        }

        private void AddAlbumToQueueMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (SongsList.ItemsSource is not IEnumerable<Song> albumSongs)
                return;

            foreach (var song in albumSongs)
                _queue.Add(song);
        }

        private void RemoveFromQueueMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (QueueList.SelectedItem is not Song song)
                return;

            var index = _queue.IndexOf(song);
            if (index < 0)
                return;

            _queue.RemoveAt(index);

            if (index <= _queueIndex)
                _queueIndex--;
        }

        private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (QueueList.SelectedItem is not Song)
                return;

            var index = QueueList.SelectedIndex;
            if (index >= 0)
                PlayQueueIndex(index);
        }

        // The album/artist/playlist "Play" buttons pop out a menu (via the same generic
        // SortButton_Click used by the "Sort" buttons) rather than distinguishing click vs. hold,
        // since Luna found the hold-to-shuffle gesture undiscoverable.
        private void PlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: string context })
                TriggerPlayFromTop(context, shuffle: false);
        }

        private void ShuffleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: string context })
                TriggerPlayFromTop(context, shuffle: true);
        }

        private async void TriggerPlayFromTop(string? context, bool shuffle)
        {
            switch (context)
            {
                case "AlbumTracks":
                    if (SongsList.ItemsSource is IEnumerable<Song> albumTracks)
                        PlayFromList(albumTracks.ToList(), shuffle);
                    break;

                case "AllTracks":
                    if (AllSongsList.ItemsSource is IEnumerable<Song> allTracks)
                        PlayFromList(allTracks.ToList(), shuffle);
                    break;

                case "PlaylistTracks":
                    if (PlaylistSongsList.ItemsSource is IEnumerable<Song> playlistTracks)
                        PlayFromList(playlistTracks.ToList(), shuffle);
                    break;

                case "Artist":
                    if (_artistAlbumsRaw.Count == 0)
                        return;

                    try
                    {
                        var allSongs = new List<Song>();
                        foreach (var album in _artistAlbumsRaw)
                        {
                            var songs = await _client.GetSongsForAlbumAsync(album.Id);
                            foreach (var song in songs)
                                song.CoverArtUrl = _client.GetCoverArtUrl(song.AlbumId, 240);
                            allSongs.AddRange(songs);
                        }

                        PlayFromList(allSongs, shuffle);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"Failed to play artist: {ex.Message}", "LunaDrome", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    break;
            }
        }

        private void PlayFromList(List<Song> songs, bool shuffle)
        {
            if (songs.Count == 0)
                return;

            if (shuffle)
                songs = songs.OrderBy(_ => SortRandom.Next()).ToList();

            _queue.Clear();
            foreach (var song in songs)
                _queue.Add(song);

            PlayQueueIndex(0);
        }

        private void PlayQueueIndex(int index)
        {
            if (index < 0 || index >= _queue.Count)
                return;

            _queueIndex = index;
            var song = _queue[_queueIndex];
            var url = _client.GetStreamUrl(song.Id);

            Player.Source = new Uri(url);
            Player.Play();
            _isPlaying = true;
            SetPlayingIconState(true);
            UpdateVisualizerCaptureState();
            NowPlayingText.Text = song.Title;
            NowPlayingSubText.Text = song.ArtistName;
            FullScreenTitleText.Text = song.Title;
            FullScreenArtistText.Text = song.ArtistName;

            SetNowPlayingArt(song.CoverArtUrl);
            // The full-screen player shows art at 360x360 - song.CoverArtUrl is whatever size the
            // list it came from requested (usually 240, for small track-row thumbnails), which
            // looks soft/blurry stretched up that far. Fetch a dedicated larger size for this view.
            SetImageBrushSource(FullScreenArtBrush, _client.GetCoverArtUrl(song.AlbumId, 720));

            ProgressSlider.Value = 0;
            ElapsedText.Text = "0:00";
            DurationText.Text = "0:00";
            _progressTimer.Start();

            QueueList.SelectedIndex = _queueIndex;
            QueueList.ScrollIntoView(song);

            _ = LoadLyricsAsync(song);
        }

        private async Task LoadLyricsAsync(Song song)
        {
            _currentLyrics = new List<LyricLine>();
            _lyricsAreSynced = false;
            _currentLyricLine = null;
            LyricsList.ItemsSource = null;
            LyricsPlaceholder.Visibility = Visibility.Collapsed;

            var (lines, synced) = await _client.GetLyricsAsync(song.Id, song.ArtistName, song.Title);

            // The user may have skipped to a different track while this was in flight.
            if (_queueIndex < 0 || _queueIndex >= _queue.Count || !ReferenceEquals(_queue[_queueIndex], song))
                return;

            _currentLyrics = lines;
            _lyricsAreSynced = synced;
            LyricsList.ItemsSource = lines;
            LyricsPlaceholder.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetNowPlayingArt(string? url) => SetImageBrushSource(NowPlayingArtBrush, url);

        private static void SetImageBrushSource(ImageBrush brush, string? url)
        {
            if (string.IsNullOrEmpty(url))
            {
                brush.ImageSource = null;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(url);
                bitmap.EndInit();
                brush.ImageSource = bitmap;
            }
            catch
            {
                brush.ImageSource = null;
            }
        }

        private void SetPlayingIconState(bool playing)
        {
            PlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            PauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_queueIndex < 0)
                return;

            if (_isPlaying)
                Player.Pause();
            else
                Player.Play();

            _isPlaying = !_isPlaying;
            SetPlayingIconState(_isPlaying);
            UpdateVisualizerCaptureState();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            Player.Stop();
            _isPlaying = false;
            SetPlayingIconState(false);
            _progressTimer.Stop();
            ProgressSlider.Value = 0;
            ElapsedText.Text = "0:00";
            UpdateVisualizerCaptureState();
        }

        private void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            PlayQueueIndex(_queueIndex - 1);
        }

        // Shuffle picks a random track other than the current one; otherwise steps forward
        // sequentially, wrapping to the start when Repeat All is on.
        private int GetNextQueueIndex()
        {
            if (_queue.Count == 0)
                return -1;

            if (_isShuffled && _queue.Count > 1)
            {
                int next;
                do { next = _shuffleRandom.Next(_queue.Count); } while (next == _queueIndex);
                return next;
            }

            if (_queueIndex + 1 < _queue.Count)
                return _queueIndex + 1;

            return _repeatMode == RepeatMode.All ? 0 : _queue.Count;
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            PlayQueueIndex(GetNextQueueIndex());
        }

        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            _isShuffled = !_isShuffled;
            var brush = (Brush)FindResource(_isShuffled ? "AccentBrush" : "TextSecondaryBrush");
            ShuffleIconPath.Stroke = brush;
        }

        private void RepeatButton_Click(object sender, RoutedEventArgs e)
        {
            _repeatMode = _repeatMode switch
            {
                RepeatMode.Off => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.Off
            };

            var brush = (Brush)FindResource(_repeatMode == RepeatMode.Off ? "TextSecondaryBrush" : "AccentBrush");
            RepeatIconPath.Stroke = brush;
            RepeatArrowHead.Fill = brush;
            RepeatOneBadge.Foreground = brush;
            RepeatOneBadge.Visibility = _repeatMode == RepeatMode.One ? Visibility.Visible : Visibility.Collapsed;
        }

        private void QueueToggleButton_Click(object sender, RoutedEventArgs e)
        {
            QueueTabButton.IsChecked = true;
            FullScreenPlayerOverlay.Visibility = Visibility.Visible;
            UpdateVisualizerCaptureState();
        }

        private async void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            Player.Stop();

            // Only a track that actually finished counts as a play - skipping away early doesn't
            // scrobble. Best-effort: a failed scrobble shouldn't interrupt playback with a popup.
            if (_queueIndex >= 0 && _queueIndex < _queue.Count)
            {
                try { await _client.ScrobbleAsync(_queue[_queueIndex].Id); }
                catch { /* best-effort */ }
            }

            if (_repeatMode == RepeatMode.One)
            {
                PlayQueueIndex(_queueIndex);
                return;
            }

            var next = GetNextQueueIndex();
            if (next >= 0 && next < _queue.Count)
            {
                PlayQueueIndex(next);
            }
            else
            {
                _isPlaying = false;
                SetPlayingIconState(false);
                NowPlayingText.Text = "Nothing playing";
                NowPlayingSubText.Text = "";
                NowPlayingArtBrush.ImageSource = null;
                FullScreenTitleText.Text = "Nothing playing";
                FullScreenArtistText.Text = "";
                FullScreenArtBrush.ImageSource = null;
                _progressTimer.Stop();
                ProgressSlider.Value = 0;
                ElapsedText.Text = "0:00";
                DurationText.Text = "0:00";
                UpdateVisualizerCaptureState();
            }
        }

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (Player.NaturalDuration.HasTimeSpan)
            {
                ProgressSlider.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
                DurationText.Text = FormatTime(Player.NaturalDuration.TimeSpan);
            }
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (_isUserSeeking || Player.Source == null)
                return;

            var position = Player.Position;
            ProgressSlider.Value = position.TotalSeconds;
            ElapsedText.Text = FormatTime(position);

            if (!_lyricsAreSynced || _currentLyrics.Count == 0)
                return;

            LyricLine? current = null;
            foreach (var line in _currentLyrics)
            {
                if (line.Start > position)
                    break;
                current = line;
            }

            if (current == null || ReferenceEquals(current, _currentLyricLine))
                return;

            _currentLyricLine = current;
            LyricsList.SelectedItem = current;
            LyricsList.ScrollIntoView(current);
        }

        // Full-screen player (Feishin-style): clicking the now-playing title opens it directly.
        private void NowPlayingText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            FullScreenPlayerOverlay.Visibility = Visibility.Visible;
            UpdateVisualizerCaptureState();
        }

        private void CloseFullScreenPlayer_Click(object sender, RoutedEventArgs e)
        {
            FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
            UpdateVisualizerCaptureState();
        }

        // Right half of the full-screen player: Queue / Lyrics / Visualizer behind a tab bar.
        private void ShowSidePanelContent(string panel)
        {
            QueuePanelView.Visibility = panel == "Queue" ? Visibility.Visible : Visibility.Collapsed;
            LyricsPanelView.Visibility = panel == "Lyrics" ? Visibility.Visible : Visibility.Collapsed;
            VisualizerPanelView.Visibility = panel == "Visualizer" ? Visibility.Visible : Visibility.Collapsed;
            UpdateVisualizerCaptureState();
        }

        private void SidePanelTabButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string panel })
                ShowSidePanelContent(panel);
        }

        // Only runs the WASAPI loopback capture (and the per-frame render hook below) while the
        // panel is both visible and something is actually playing - no point capturing audio or
        // redrawing every frame for a panel nobody can see.
        private void UpdateVisualizerCaptureState()
        {
            var visualizerShowing = FullScreenPlayerOverlay.Visibility == Visibility.Visible
                                     && VisualizerPanelView.Visibility == Visibility.Visible;
            var shouldRun = _isPlaying && visualizerShowing;

            if (shouldRun == _visualizerRenderHooked)
                return;

            _visualizerRenderHooked = shouldRun;
            if (shouldRun)
            {
                _audioVisualizer.Start();
                CompositionTarget.Rendering += OnVisualizerRenderFrame;
            }
            else
            {
                CompositionTarget.Rendering -= OnVisualizerRenderFrame;
                _audioVisualizer.Stop();
            }
        }

        // Runs once per actual display frame (~60fps) rather than reacting to each WASAPI
        // DataAvailable callback directly - AudioVisualizer.CopyLatestBandsTo is a cheap,
        // thread-safe pull of whatever the capture thread has most recently computed, so this
        // stays smooth even though that delivery itself arrives in bursts (WasapiLoopbackCapture
        // doesn't expose a way to configure its ~100ms default buffer - see AudioVisualizer's
        // class comment). Renders a single smooth bending line through the spectrum (Feishin's
        // "line" visualizer mode): each band is exponentially smoothed frame-to-frame (a connected
        // line reveals raw jitter far more than independent bars would), then the 32 smoothed
        // points become a true smooth curve via Catmull-Rom-to-Bezier conversion - a Polyline
        // through the raw points would look jagged/angular, not "bending."
        private void OnVisualizerRenderFrame(object? sender, EventArgs e)
        {
            _audioVisualizer.CopyLatestBandsTo(_visualizerLatestBands);

            var width = VisualizerContainer.ActualWidth > 0 ? VisualizerContainer.ActualWidth : 280;
            var height = VisualizerContainer.ActualHeight > 0 ? VisualizerContainer.ActualHeight : 160;

            // A Path (a Shape) with the default Stretch="None" sizes its own layout slot to its
            // geometry's bounding box, and centers the geometry within any leftover space if that
            // slot ends up smaller than what the parent actually offers - which is what caused the
            // "off-center" look before. Pinning Width/Height explicitly makes the slot exactly
            // match the container every frame, removing that ambiguity; ClipToBounds on the parent
            // (in XAML) hard-clips any residual Bezier control-point overshoot past the edges.
            VisualizerPath.Width = width;
            VisualizerPath.Height = height;

            // Padding keeps a quiet (near-zero) reading from sitting exactly on the container's
            // edges, where half the line's stroke width (plus its round caps at the very first/
            // last point) would render past the visible area and look clipped/off-center.
            const double padding = 6;
            var drawableHeight = Math.Max(1, height - padding * 2);

            // Fixed "anchor" zone (~1cm at 96 DPI) at each end that never moves, rather than audio
            // data running edge-to-edge - Luna's explicit ask, and it also fixes the "big white
            // bar" bug: bass bands swing hard in value, and a Catmull-Rom segment between two very
            // different Y values can overshoot its control points well past either point - right at
            // the edge, that overshoot rendered as a dense tangle of white stroke passes.
            const double anchorWidth = 38;
            var moveZoneWidth = Math.Max(1, width - padding * 2 - anchorWidth * 2);
            var baselineY = height - padding;
            var leftMoveX = padding + anchorWidth;
            var rightMoveX = width - padding - anchorWidth;

            const int fadeBands = 22;
            var bandCount = _visualizerLatestBands.Length;

            for (var i = 0; i < bandCount; i++)
                _visualizerSmoothed[i] = _visualizerSmoothed[i] * 0.88f + _visualizerLatestBands[i] * 0.12f;

            // Spatial smoothing across neighboring bands - a screenshot showed sharp, jagged peaks
            // throughout the curve, not just at the flat-zone edge: adjacent frequency bands can
            // differ a lot in level, and the curve passes exactly through every one of them, so any
            // sharp band-to-band jump shows up as a sharp peak wherever it happens to fall, not
            // only at the boundary the earlier fade-window fix targeted. A 5-tap moving average
            // (standard technique for this in real spectrum analyzers) rounds those transitions off
            // everywhere at once, including - as a side effect - right where the flat zone ends.
            var spatialSmoothed = new float[bandCount];
            for (var i = 0; i < bandCount; i++)
            {
                float sum = 0;
                var count = 0;
                for (var k = -2; k <= 2; k++)
                {
                    var idx = i + k;
                    if (idx < 0 || idx >= bandCount)
                        continue;
                    sum += _visualizerSmoothed[idx];
                    count++;
                }
                spatialSmoothed[i] = sum / count;
            }

            var bandPoints = new Point[bandCount];
            for (var i = 0; i < bandCount; i++)
            {
                // Fading the first/last few bands' amplitude in from zero spreads the flat-to-data
                // transition across several band-widths of real horizontal space, rather than
                // forcing it all into the short connector segment right at the boundary. Smoothstep
                // (zero derivative at both t=0 and t=1), not a linear ramp - a linear ramp's slope
                // still starts abruptly at full strength from the very first faded band, which was
                // itself still reading as a subtle kink even with a wide fade zone.
                float fade = 1f;
                if (i < fadeBands)
                {
                    var t = (i + 1f) / (fadeBands + 1f);
                    fade = t * t * (3f - 2f * t);
                }
                else if (i >= bandCount - fadeBands)
                {
                    var t = (bandCount - i) / (float)(fadeBands + 1);
                    fade = t * t * (3f - 2f * t);
                }

                var x = bandCount <= 1 ? leftMoveX : leftMoveX + i * moveZoneWidth / (bandCount - 1);
                var y = baselineY - spatialSmoothed[i] * fade * drawableHeight;
                bandPoints[i] = new Point(x, y);
            }

            // [flat-zone start, flat-zone end] + [the band curve] + [flat-zone start, flat-zone end]
            var points = new Point[bandCount + 4];
            points[0] = new Point(padding, baselineY);
            points[1] = new Point(leftMoveX, baselineY);
            Array.Copy(bandPoints, 0, points, 2, bandCount);
            points[^2] = new Point(rightMoveX, baselineY);
            points[^1] = new Point(width - padding, baselineY);

            UpdateSmoothCurve(points, bandCount, padding, baselineY);
        }

        // Mutates a persistent PathFigure's existing segments in place instead of allocating a new
        // PathFigure/PathGeometry (and reassigning Path.Data) every single frame - the previous
        // version's main cost, and part of why it read as low-fps despite the FFT rate itself being
        // reasonable.
        //
        // Five kinds of segment, by index: [0]=flat line, [1]=entry connector, [2..bandCount]=
        // interior Catmull-Rom, [bandCount+1]=exit connector, [last]=flat line.
        //   - The two flat segments are plain LineSegments: mathematically incapable of curving at
        //     all, so they're guaranteed genuinely flat for their full width regardless of anything
        //     a spline's tangent math might otherwise bleed into them (Luna's "anchor zone isn't
        //     fully visible" report - a Bezier-based flat zone was already curving before it ended).
        //   - The two connector segments are still Bezier, not lines - a straight line there looked
        //     like an abrupt kink ("a straight line up") where the flat zone meets the curve. Their
        //     first control point is pinned to the *same Y* as the flat zone's end point, forcing a
        //     perfectly horizontal tangent at that junction - so the curve leaves the flat line
        //     seamlessly (no visible corner) and only starts actually bending a little past it.
        //   - The interior segments are the original Catmull-Rom-through-cubic-Bezier chain, built
        //     only from the real band points (never referencing the anchor/connector points), so a
        //     loud bass reading can't pull on or overshoot because of a neighboring fixed point.
        // Control points are clamped to [minY, maxY] throughout as a second line of defense.
        private void UpdateSmoothCurve(IReadOnlyList<Point> points, int bandCount, double minY, double maxY)
        {
            if (points.Count < 2)
                return;

            var segmentCount = points.Count - 1;

            if (_visualizerFigure == null)
            {
                // IsFilled=true (not IsClosed) fills the region bounded by the curve plus an
                // implicit straight edge from the last point back to the first - since both ends
                // sit at the same baseline Y, that implicit edge is exactly the flat bottom border
                // the fill should have. IsClosed stays false so that edge isn't also stroked.
                _visualizerFigure = new PathFigure { StartPoint = points[0], IsFilled = true };
                for (var i = 0; i < segmentCount; i++)
                {
                    var isFlat = i == 0 || i == segmentCount - 1;
                    _visualizerFigure.Segments.Add(isFlat ? new LineSegment() : new BezierSegment());
                }

                var geometry = new PathGeometry();
                geometry.Figures.Add(_visualizerFigure);
                VisualizerPath.Data = geometry;
            }

            _visualizerFigure.StartPoint = points[0];
            for (var i = 0; i < segmentCount; i++)
            {
                var p1 = points[i];
                var p2 = points[i + 1];

                if (_visualizerFigure.Segments[i] is LineSegment line)
                {
                    line.Point = p2;
                    continue;
                }

                var segment = (BezierSegment)_visualizerFigure.Segments[i];

                if (i == 1)
                {
                    // Entry connector: leaves p1 (the flat zone's end point) dead horizontal, then
                    // leans control2 toward the next real band point for a smooth handoff into it.
                    var next = points[i + 2];
                    var c2Y = Math.Clamp(p2.Y - (next.Y - p1.Y) / 6.0, minY, maxY);
                    segment.Point1 = new Point(p1.X + (p2.X - p1.X) / 3.0, p1.Y);
                    segment.Point2 = new Point(p2.X - (next.X - p1.X) / 6.0, c2Y);
                    segment.Point3 = p2;
                }
                else if (i == bandCount + 1)
                {
                    // Exit connector: mirror of the entry connector - arrives at p2 (the flat
                    // zone's start point) dead horizontal.
                    var prev = points[i - 1];
                    var c1Y = Math.Clamp(p1.Y + (p2.Y - prev.Y) / 6.0, minY, maxY);
                    segment.Point1 = new Point(p1.X + (p2.X - prev.X) / 6.0, c1Y);
                    segment.Point2 = new Point(p2.X - (p2.X - p1.X) / 3.0, p2.Y);
                    segment.Point3 = p2;
                }
                else
                {
                    // Interior: standard Catmull-Rom. p0/p3 are always the true neighboring points
                    // in the array - at the two ends of this range that's A1 (points[1]) and B0
                    // (points[bandCount+2]), the same points the connector segments above already
                    // use for their own tangent at R0/R_last. Using those real neighbors here too
                    // (rather than duplicating R0/R_last, which was the previous approach) is what
                    // makes the connector and the first/last interior segment agree on the tangent
                    // at their shared point - duplicating there instead of matching the connector's
                    // formula was the actual remaining source of the visible kink Luna kept
                    // reporting: the two segments meeting at R0 were computing its tangent two
                    // different ways.
                    var p0 = points[i - 1];
                    var p3 = points[i + 2];

                    var c1Y = Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6.0, minY, maxY);
                    var c2Y = Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6.0, minY, maxY);

                    segment.Point1 = new Point(p1.X + (p2.X - p0.X) / 6.0, c1Y);
                    segment.Point2 = new Point(p2.X - (p3.X - p1.X) / 6.0, c2Y);
                    segment.Point3 = p2;
                }
            }
        }

        private void LyricsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LyricsList.SelectedItem is not LyricLine line || !_lyricsAreSynced || _queueIndex < 0)
                return;

            Player.Position = line.Start;
        }

        private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = true;
        }

        private void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = false;
            Player.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Fires during InitializeComponent() as the XAML-declared Value is applied,
            // before the MediaElement further down the tree has been constructed yet.
            if (Player == null)
                return;

            Player.Volume = e.NewValue;

            // Load-modify-save rather than constructing a fresh AppSettings, so this doesn't
            // clobber the saved login info (server URL/username/password) alongside it.
            var settings = SettingsService.Load();
            settings.Volume = e.NewValue;
            SettingsService.Save(settings);
        }

        private static string FormatTime(TimeSpan ts) => $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }
}
