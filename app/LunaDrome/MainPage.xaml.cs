using System.Collections.ObjectModel;
using LunaDrome.Core.Models;
using LunaDrome.Core.Services;
using Microsoft.UI.Xaml.Controls.Primitives;
using LunaDrome.Services;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace LunaDrome;

public sealed partial class MainPage : Page
{
    private enum RepeatMode { Off, All, One }

    private readonly NavidromeClient _client = new();
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _progressTimer;
    private Song? _currentSong;

    private ObservableCollection<Song> _queue = new();
    private int _queueIndex = -1;
    private bool _isShuffled;
    private RepeatMode _repeatMode = RepeatMode.Off;
    private readonly Random _random = new();

    private bool _homeLoaded;
    private bool _allAlbumsLoaded;
    private bool _genresLoaded;
    private Playlist? _currentPlaylist;
    private Artist? _currentArtist;

    // Raw (unfiltered) lists cached for the client-side search boxes.
    private List<Artist> _artistsRaw = new();
    private List<Album> _allAlbumsRaw = new();
    private List<Song> _allSongsRaw = new();

    // Visualizer: loopback capture feeds the analyzer on its own thread; the render loop pulls
    // the latest bands once per display frame (see OnVisualizerRenderFrame).
    private readonly SpectrumAnalyzer _spectrum = new();
    private readonly LoopbackCapture _loopback;
    private readonly float[] _visualizerLatestBands = new float[SpectrumAnalyzer.BandCount];
    private readonly float[] _visualizerSmoothed = new float[SpectrumAnalyzer.BandCount];
    private bool _visualizerRenderHooked;

    public MainPage()
    {
        _loopback = new LoopbackCapture(_spectrum);
        this.InitializeComponent();
        Unloaded += (_, _) => { StopVisualizer(); _loopback.Dispose(); };

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _progressTimer.Tick += ProgressTimer_Tick;
        _progressTimer.Start();

        _player.Volume = VolumeSlider.Value / 100.0;
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(OnMediaEnded);

        QueueList.ItemsSource = _queue;

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

        _artistsRaw = await _client.GetArtistsAsync();
        ArtistsList.ItemsSource = _artistsRaw;

        _allSongsRaw = await _client.GetAllSongsAsync(limit: 200);
        RefreshAllTracks();

        LoginPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;

        await LoadHomeAsync();
        await LoadPlaylistsAsync();
    }

    private async Task LoadPlaylistsAsync()
    {
        var playlists = await _client.GetPlaylistsAsync();
        PlaylistsList.ItemsSource = playlists;
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
        PlaylistDetailPanel.Visibility = name == "PlaylistDetail" ? Visibility.Visible : Visibility.Collapsed;
        GenresPanel.Visibility = name == "Genres" ? Visibility.Visible : Visibility.Collapsed;
        GenreDetailPanel.Visibility = name == "GenreDetail" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string panel })
            return;

        if (panel == "Home")
            await LoadHomeAsync();
        else if (panel == "Albums")
            await LoadAllAlbumsAsync();
        else if (panel == "Genres")
            await LoadGenresAsync();

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

        _allAlbumsRaw = await _client.GetAlbumListAsync("alphabeticalByName", size: 200);
        PopulateCoverArt(_allAlbumsRaw);
        AllAlbumsList.ItemsSource = _allAlbumsRaw;
        _allAlbumsLoaded = true;
    }

    private async Task LoadGenresAsync()
    {
        if (_genresLoaded)
            return;

        var genres = await _client.GetGenresAsync();
        GenresList.ItemsSource = genres;
        _genresLoaded = true;
    }

    private async void GenreRow_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Genre genre)
            return;

        GenreDetailHeader.Text = genre.Name;
        var albums = await _client.GetAlbumsByGenreAsync(genre.Name);
        PopulateCoverArt(albums);
        GenreAlbumsList.ItemsSource = albums;

        ShowPanel("GenreDetail");
    }

    private void GenreBack_Click(object sender, RoutedEventArgs e) => ShowPanel("Genres");

    // --- Search filters (client-side, over the cached raw lists) ---

    private void ArtistSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = ArtistSearchBox.Text.Trim();
        ArtistsList.ItemsSource = string.IsNullOrEmpty(q)
            ? _artistsRaw
            : _artistsRaw.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void AllAlbumsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = AllAlbumsSearchBox.Text.Trim();
        AllAlbumsList.ItemsSource = string.IsNullOrEmpty(q)
            ? _allAlbumsRaw
            : _allAlbumsRaw.Where(a =>
                a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.ArtistName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void AllTracksSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAllTracks();

    private void TracksSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshAllTracks();

    private void RefreshAllTracks()
    {
        // Guard: TextChanged/SelectionChanged can fire during InitializeComponent, before
        // the raw list or other named controls exist yet.
        if (AllTracksSearchBox == null || TracksSortCombo == null)
            return;

        var q = AllTracksSearchBox.Text?.Trim() ?? "";
        IEnumerable<Song> filtered = string.IsNullOrEmpty(q)
            ? _allSongsRaw
            : _allSongsRaw.Where(s =>
                s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.ArtistName.Contains(q, StringComparison.OrdinalIgnoreCase));

        filtered = ((TracksSortCombo.SelectedItem as ComboBoxItem)?.Content as string) switch
        {
            "Artist" => filtered.OrderBy(s => s.ArtistName).ThenBy(s => s.Title),
            "Longest first" => filtered.OrderByDescending(s => s.DurationSeconds),
            "Shortest first" => filtered.OrderBy(s => s.DurationSeconds),
            _ => filtered.OrderBy(s => s.Title)
        };

        TracksList.ItemsSource = filtered.ToList();
    }

    // --- Artist / album drill-down ---

    private async void ArtistRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement fe)
            return;

        var current = fe;
        while (current != null && current.Tag is not Artist)
            current = current.Parent as FrameworkElement;

        if (current?.Tag is not Artist artist)
            return;

        _currentArtist = artist;
        ArtistDetailHeader.Text = artist.Name;
        var (detail, albums) = await _client.GetArtistDetailAsync(artist.Id);
        _currentArtist = detail;
        ArtistFavoriteButton.Content = detail.IsFavorited ? "★" : "☆";
        PopulateCoverArt(albums);
        ArtistAlbumsList.ItemsSource = albums;

        ShowPanel("ArtistDetail");
    }

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
            ArtistFavoriteButton.Content = _currentArtist.IsFavorited ? "★" : "☆";
        }
        catch { /* best-effort */ }
    }

    private string _albumDetailReturnPanel = "Albums";

    private async void AlbumCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Album album })
            return;

        // A tap on the hover play button should only play the album (AlbumPlayOverlay_Click),
        // not also open its track list.
        for (var el = e.OriginalSource as DependencyObject; el != null && el != sender; el = VisualTreeHelper.GetParent(el))
        {
            if (el is Button)
                return;
        }

        _albumDetailReturnPanel = HomePanel.Visibility == Visibility.Visible ? "Home"
            : ArtistDetailPanel.Visibility == Visibility.Visible ? "ArtistDetail"
            : GenreDetailPanel.Visibility == Visibility.Visible ? "GenreDetail"
            : "Albums";

        AlbumDetailHeader.Text = album.Name;
        var songs = await _client.GetSongsForAlbumAsync(album.Id);
        AlbumTracksList.ItemsSource = songs;

        ShowPanel("AlbumDetail");
    }

    private async void AlbumPlayOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Album album })
            return;

        var songs = await _client.GetSongsForAlbumAsync(album.Id);
        if (songs.Count > 0)
            PlayQueueFrom(songs, 0);
    }

    private void ArtistBack_Click(object sender, RoutedEventArgs e) => ShowPanel("Artists");

    private void AlbumBack_Click(object sender, RoutedEventArgs e) => ShowPanel(_albumDetailReturnPanel);

    // --- Playlists ---

    private async void PlaylistRow_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Playlist playlist)
            return;

        _currentPlaylist = playlist;
        PlaylistDetailHeader.Text = playlist.Name;
        var songs = await _client.GetPlaylistSongsAsync(playlist.Id);
        PlaylistTracksList.ItemsSource = songs;

        ShowPanel("PlaylistDetail");
    }

    private async void NewPlaylist_Click(object sender, RoutedEventArgs e)
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

    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist == null)
            return;

        var confirm = new ContentDialog
        {
            Title = "Delete playlist",
            Content = $"Delete \"{_currentPlaylist.Name}\"? This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        await _client.DeletePlaylistAsync(_currentPlaylist.Id);
        _currentPlaylist = null;
        await LoadPlaylistsAsync();
        await LoadHomeAsync();
        ShowPanel("Home");
    }

    private async void AddToPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSong == null)
            return;

        var playlists = await _client.GetPlaylistsAsync();

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single };
        list.ItemsSource = playlists;
        list.DisplayMemberPath = nameof(Playlist.Name);

        var dialog = new ContentDialog
        {
            Title = $"Add \"{_currentSong.Title}\" to playlist",
            Content = list,
            PrimaryButtonText = "Add",
            SecondaryButtonText = "New playlist...",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            if (list.SelectedItem is Playlist selected)
                await _client.AddSongToPlaylistAsync(selected.Id, _currentSong.Id);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            var name = await PromptForPlaylistNameAsync();
            if (string.IsNullOrWhiteSpace(name))
                return;

            var newId = await _client.CreatePlaylistAsync(name);
            if (newId != null)
                await _client.AddSongToPlaylistAsync(newId, _currentSong.Id);

            await LoadPlaylistsAsync();
        }
    }

    // --- Lyrics ---

    private async void LyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (LyricsPanel.Visibility == Visibility.Visible)
        {
            LyricsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        QueuePanel.Visibility = Visibility.Collapsed;
        CloseVisualizer();
        LyricsPanel.Visibility = Visibility.Visible;
        await LoadLyricsForCurrentSongAsync();
    }

    private void CloseLyrics_Click(object sender, RoutedEventArgs e) => LyricsPanel.Visibility = Visibility.Collapsed;

    private void QueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueuePanel.Visibility == Visibility.Visible)
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        LyricsPanel.Visibility = Visibility.Collapsed;
        CloseVisualizer();
        QueuePanel.Visibility = Visibility.Visible;
    }

    private void CloseQueue_Click(object sender, RoutedEventArgs e) => QueuePanel.Visibility = Visibility.Collapsed;

    private void QueueList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Song song)
            return;

        var index = _queue.IndexOf(song);
        if (index < 0)
            return;

        _queueIndex = index;
        PlaySong(song);
    }

    private void RemoveFromQueue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Song song })
            return;

        var index = _queue.IndexOf(song);
        if (index < 0)
            return;

        _queue.RemoveAt(index);

        // Keep _queueIndex pointing at the same still-playing track after the shift.
        if (index < _queueIndex)
            _queueIndex--;
        else if (index == _queueIndex)
            _queueIndex = Math.Min(_queueIndex, _queue.Count - 1);
    }

    private async Task LoadLyricsForCurrentSongAsync()
    {
        if (_currentSong == null)
        {
            LyricsList.ItemsSource = new List<LyricLine> { new() { Text = "Nothing playing." } };
            return;
        }

        var (lines, _) = await _client.GetLyricsAsync(_currentSong.Id, _currentSong.ArtistName, _currentSong.Title);
        LyricsList.ItemsSource = lines.Count > 0
            ? lines
            : new List<LyricLine> { new() { Text = "No lyrics found for this track." } };
    }

    // --- Visualizer ---

    private void VisualizerButton_Click(object sender, RoutedEventArgs e)
    {
        if (VisualizerPanel.Visibility == Visibility.Visible)
        {
            CloseVisualizer();
            return;
        }

        LyricsPanel.Visibility = Visibility.Collapsed;
        QueuePanel.Visibility = Visibility.Collapsed;
        VisualizerPanel.Visibility = Visibility.Visible;
        StartVisualizer();
    }

    private void CloseVisualizer_Click(object sender, RoutedEventArgs e) => CloseVisualizer();

    private void CloseVisualizer()
    {
        VisualizerPanel.Visibility = Visibility.Collapsed;
        StopVisualizer();
    }

    // Capture + per-frame rendering only run while the panel is open - no point capturing
    // audio or redrawing every frame for a panel nobody can see.
    private void StartVisualizer()
    {
        if (_visualizerRenderHooked)
            return;

        _visualizerRenderHooked = true;
        Array.Clear(_visualizerSmoothed);
        _loopback.Start();
        CompositionTarget.Rendering += OnVisualizerRenderFrame;
    }

    private void StopVisualizer()
    {
        if (!_visualizerRenderHooked)
            return;

        _visualizerRenderHooked = false;
        CompositionTarget.Rendering -= OnVisualizerRenderFrame;
        _loopback.Stop();
    }

    // Ported from the WPF app's OnVisualizerRenderFrame/UpdateSmoothCurve, where every constant
    // below was tuned with Luna (see PROJECT_MEMORY.md's visualizer entries): temporal smoothing
    // 0.88/0.12, a 5-tap spatial average, smoothstep fade over 22 bands at each end, fixed flat
    // "anchor" zones at both edges, baseline at mid-height, and Catmull-Rom-to-Bezier curves with
    // horizontal-tangent connectors into the flat zones. Rebuilds the geometry each frame rather
    // than mutating segments in place, since in-place segment edits aren't guaranteed to
    // invalidate rendering on every Uno backend.
    private void OnVisualizerRenderFrame(object? sender, object e)
    {
        _spectrum.CopyLatestBandsTo(_visualizerLatestBands);

        var width = VisualizerContainer.ActualWidth > 0 ? VisualizerContainer.ActualWidth : 280;
        var height = VisualizerContainer.ActualHeight > 0 ? VisualizerContainer.ActualHeight : 160;
        VisualizerPath.Width = width;
        VisualizerPath.Height = height;

        const double padding = 6;
        var baselineY = height / 2;
        var drawableHeight = Math.Max(1, baselineY - padding);

        const double anchorWidth = 38;
        var moveZoneWidth = Math.Max(1, width - padding * 2 - anchorWidth * 2);
        var leftMoveX = padding + anchorWidth;
        var rightMoveX = width - padding - anchorWidth;

        const int fadeBands = 22;
        var bandCount = _visualizerLatestBands.Length;

        for (var i = 0; i < bandCount; i++)
            _visualizerSmoothed[i] = _visualizerSmoothed[i] * 0.88f + _visualizerLatestBands[i] * 0.12f;

        var points = new Windows.Foundation.Point[bandCount + 4];
        points[0] = new(padding, baselineY);
        points[1] = new(leftMoveX, baselineY);

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
            var level = sum / count;

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

            var x = leftMoveX + i * moveZoneWidth / (bandCount - 1);
            points[i + 2] = new(x, baselineY - level * fade * drawableHeight);
        }

        points[^2] = new(rightMoveX, baselineY);
        points[^1] = new(width - padding, baselineY);

        VisualizerPath.Data = BuildSmoothCurve(points, bandCount, padding, baselineY);
    }

    // Segments by index: [0]=flat line, [1]=entry connector, [2..bandCount]=interior Catmull-Rom,
    // [bandCount+1]=exit connector, [last]=flat line. Control points clamped to [minY, maxY] so a
    // loud band can't overshoot past the baseline or the top edge.
    private static PathGeometry BuildSmoothCurve(Windows.Foundation.Point[] points, int bandCount, double minY, double maxY)
    {
        var figure = new PathFigure { StartPoint = points[0], IsFilled = true, IsClosed = false };
        var segmentCount = points.Length - 1;

        for (var i = 0; i < segmentCount; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];

            if (i == 0 || i == segmentCount - 1)
            {
                figure.Segments.Add(new LineSegment { Point = p2 });
                continue;
            }

            Windows.Foundation.Point c1, c2;
            if (i == 1)
            {
                var next = points[i + 2];
                c1 = new(p1.X + (p2.X - p1.X) / 3.0, p1.Y);
                c2 = new(p2.X - (next.X - p1.X) / 6.0, Math.Clamp(p2.Y - (next.Y - p1.Y) / 6.0, minY, maxY));
            }
            else if (i == bandCount + 1)
            {
                var prev = points[i - 1];
                c1 = new(p1.X + (p2.X - prev.X) / 6.0, Math.Clamp(p1.Y + (p2.Y - prev.Y) / 6.0, minY, maxY));
                c2 = new(p2.X - (p2.X - p1.X) / 3.0, p2.Y);
            }
            else
            {
                var p0 = points[i - 1];
                var p3 = points[i + 2];
                c1 = new(p1.X + (p2.X - p0.X) / 6.0, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6.0, minY, maxY));
                c2 = new(p2.X - (p3.X - p1.X) / 6.0, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6.0, minY, maxY));
            }

            figure.Segments.Add(new BezierSegment { Point1 = c1, Point2 = c2, Point3 = p2 });
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    // --- Queue / playback ---

    private void TrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (sender is not ListView { ItemsSource: List<Song> source } || e.ClickedItem is not Song clicked)
            return;

        var index = source.IndexOf(clicked);
        if (index < 0)
            return;

        // Matches the WPF app's behavior: clicking a track queues the whole list it came
        // from (album, playlist, or library-wide), starting from that track.
        PlayQueueFrom(source, index);
    }

    private void PlayQueueFrom(List<Song> source, int startIndex)
    {
        _queue.Clear();
        foreach (var song in source)
            _queue.Add(song);

        _queueIndex = startIndex;
        PlaySong(_queue[_queueIndex]);
    }

    private void TrackRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Song song } fe)
            return;

        var flyout = new MenuFlyout();

        var playNext = new MenuFlyoutItem { Text = "Play next" };
        playNext.Click += (_, _) => QueuePlayNext(song);
        flyout.Items.Add(playNext);

        var addToQueue = new MenuFlyoutItem { Text = "Add to queue" };
        addToQueue.Click += (_, _) => QueueAppend(song);
        flyout.Items.Add(addToQueue);

        flyout.ShowAt(fe, e.GetPosition(fe));
    }

    // Inserts right after the currently playing track without interrupting playback - if
    // nothing is playing yet, this just starts the queue with this one song.
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

    private async void PlaySong(Song song)
    {
        _currentSong = song;
        _player.Source = MediaSource.CreateFromUri(new Uri(_client.GetStreamUrl(song.Id)));
        _player.Play();
        PlayPauseButton.Content = "Pause";
        NowPlayingText.Text = $"{song.Title} — {song.ArtistName}";
        FavoriteButton.Content = song.IsFavorited ? "★ Fav" : "☆ Fav";

        if (LyricsPanel.Visibility == Visibility.Visible)
            await LoadLyricsForCurrentSongAsync();
    }

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        _isShuffled = !_isShuffled;
        ShuffleButton.Content = _isShuffled ? "Shuffle: On" : "Shuffle";
    }

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        _repeatMode = _repeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        };
        RepeatButton.Content = _repeatMode switch
        {
            RepeatMode.All => "Repeat: All",
            RepeatMode.One => "Repeat: One",
            _ => "Repeat: Off"
        };
    }

    // Centralizes "what plays next" so both the Next button and auto-advance-on-completion
    // use the same rule: random-excluding-current when shuffled, else sequential, wrapping
    // to 0 only when RepeatMode.All. Returns -1 when there's nothing next to play.
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
        if (_queueIndex <= 0 || _queueIndex >= _queue.Count)
            return;

        _queueIndex--;
        PlaySong(_queue[_queueIndex]);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e) => PlayNext();

    private void PlayNext()
    {
        var next = GetNextQueueIndex();
        if (next < 0)
            return;

        _queueIndex = next;
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

        if (_repeatMode == RepeatMode.One && _queueIndex >= 0 && _queueIndex < _queue.Count)
        {
            PlaySong(_queue[_queueIndex]);
            return;
        }

        var next = GetNextQueueIndex();
        if (next >= 0)
        {
            _queueIndex = next;
            PlaySong(_queue[_queueIndex]);
        }
        else
        {
            PlayPauseButton.Content = "Play";
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
            FavoriteButton.Content = _currentSong.IsFavorited ? "★ Fav" : "☆ Fav";
        }
        catch { /* best-effort */ }
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
