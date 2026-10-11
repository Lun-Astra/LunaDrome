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
    // Not readonly: a crossfade swaps in a fresh player for the next song (MainPage.SleepCrossfade.cs).
    private MediaPlayer _player = new();
    private readonly DispatcherTimer _progressTimer;

    private readonly ObservableCollection<QueueEntry> _queue = new();
    // The playing (or, after a restore, paused-but-not-loaded) entry. Its position is looked up
    // on demand (QueueIndex) so drag-reordering the queue can't leave a stale index behind.
    private QueueEntry? _current;
    // The entry whose media is actually loaded in _player (null after a queue restore, until Play).
    private QueueEntry? _loadedEntry;
    // Queue order from before shuffle was switched on, put back when it's switched off.
    private List<QueueEntry>? _unshuffledOrder;
    // Where to seek once the loaded media opens (resuming a restored queue, stall recovery).
    private TimeSpan _pendingSeek;
    private readonly TrackPrefetcher _prefetcher;
    // Settings page choices (preferences.json).
    private readonly Preferences _prefs = PreferencesService.Load();
    // Signed in to a server; false again after Sign out (so the emptied queue isn't saved).
    private bool _signedIn;
    // Windows media flyout / Linux MPRIS: OS media widgets + keyboard media keys (#3, #13).
    private IMediaControls? _mediaControls;
    // The OS one (media flyout / MPRIS / Android notification) and Discord, both behind _mediaControls.
    private IMediaControls? _osMediaControls;
    private DiscordMediaControls? _discord;
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
        _prefetcher = new TrackPrefetcher(_client);
        this.InitializeComponent();
        InitCompactLayout();
        Loaded += (_, _) => EnsureMediaControls();
        Unloaded += (_, _) =>
        {
            SaveQueueState();
            StopVisualizer();
            _loopback.Dispose();
            _prefetcher.Dispose();
            _mediaControls?.Dispose();
        };
        _visualizerTimer.Tick += OnVisualizerRenderFrame;

        _trackViews = new()
        {
            ["AllTracks"] = new() { List = AllSongsList, Search = AllTracksSearchBox, SortButton = AllTracksSortButton, DirectionButton = AllTracksSortDirectionButton },
            ["PlaylistTracks"] = new() { List = PlaylistSongsList, Search = PlaylistTrackSearchBox, SortButton = PlaylistTrackSortButton, DirectionButton = PlaylistTrackSortDirectionButton },
        };

        _lyricsTimer.Tick += LyricsTimer_Tick;
        // #29, Android: swiping a song row to the right adds it to the queue (Controls/SwipeToQueue).
        Controls.SwipeToQueue.Swiped += song =>
        {
            AddToQueue(new List<Song> { song });
            ShowToast($"Added to queue: {song.Title}");
        };
        // #30, Android: holding an album-page track starts selecting (that list gets no RightTapped).
        Controls.SwipeToQueue.Held += row =>
        {
            Controls.SwipeToQueue.Trace($"HELD row {row.DataContext?.GetType().Name} inAlbum={IsInside(row, AlbumSongsList)}");
            if (row.DataContext is Song song && IsInside(row, AlbumSongsList))
                EnterAlbumSelection(song);
        };
        // #30, Android: swiping a queue row to the left removes it from the queue.
        Controls.SwipeToQueue.SwipedToRemove += item =>
        {
            if (item is QueueEntry entry && _queue.Contains(entry))
            {
                RemoveFromQueue(new List<QueueEntry> { entry });
                ShowToast($"Removed from queue: {entry.Song.Title}");
            }
        };
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _progressTimer.Tick += ProgressTimer_Tick;
        _progressTimer.Start();

        Themes.Apply(_prefs.Theme, _prefs.Accent);
        InitSearch();
        InitJam();
        _client.StreamQuality = _prefs.StreamQuality;
        VolumeSlider.Value = Math.Clamp(_prefs.Volume, 0, 1);
        _player.Volume = VolumeSlider.Value;
        // handledEventsToo: the Slider may handle the wheel itself without moving.
        VolumeSlider.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(VolumeSlider_PointerWheelChanged), true);
        // Player events (MediaEnded, failures, seek-on-open) live in HookPlayer: a crossfade
        // swaps in a second player, which gets the same handlers.
        HookPlayer(_player);
        _fadeTimer.Tick += FadeTimer_Tick;

        // Slider marks its own pointer events handled, so listen with handledEventsToo to know
        // when the user is dragging (the progress timer mustn't fight the drag) and when to seek.
        ProgressSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _isUserSeeking = true), true);
        ProgressSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => CommitSeek()), true);
        ProgressSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => CommitSeek()), true);

        QueueList.ItemsSource = _queue;
        SideQueueList.ItemsSource = _queue;
        _queue.CollectionChanged += (_, _) => RenumberQueue();

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
        EnsureMediaControls();
        RestoreQueueState();
        _signedIn = true;
        if (_prefs.CheckForUpdates)
            _ = CheckForUpdateAsync();

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

    // ================= Settings (#15) =================

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();
        ClearSidebarSelection();
        HideAllPages();
        RefreshSettingsPage();
        ShowPage(SettingsPage, () => SettingsButton_Click(this, new RoutedEventArgs()));
        SettingsPage.ChangeView(null, 0, null, true);
    }

    private void RefreshSettingsPage()
    {
        BuildSettingsChoices();
        if (_prefs.Accent.StartsWith('#'))
            ShowAccentColorPicker();
        else
            AccentColorPicker.Visibility = Visibility.Collapsed;
        PrefetchCheck.IsChecked = _prefs.PrefetchNextTrack;
        VolumeStepBox.Value = Math.Clamp(_prefs.VolumeScrollStep, 1, 25);
        CrossfadeBox.Value = Math.Clamp(_prefs.CrossfadeSeconds, 0, 12);
        CrossfadeSetting.Visibility = OperatingSystem.IsAndroid() ? Visibility.Collapsed : Visibility.Visible;
        ResumeQueueCheck.IsChecked = _prefs.ResumeQueueOnStartup;
        OnlineLyricsCheck.IsChecked = _prefs.OnlineLyrics;
        AutoUpdateCheck.IsChecked = _prefs.CheckForUpdates;
        DiscordPresenceCheck.IsChecked = _prefs.DiscordPresence;
        DiscordPresenceSetting.Visibility = OperatingSystem.IsAndroid() ? Visibility.Collapsed : Visibility.Visible;
        RefreshSpotiDromeSettings();
        RefreshJamiDromeSettings();
        UpdateCacheSizeText();
        AccountUserText.Text = $"Signed in as {_client.Username}";
        AccountServerText.Text = _client.ServerUrl;
        AboutVersionText.Text = $"LunaDrome v{UpdateChecker.CurrentVersion}";
        if (string.IsNullOrEmpty(UpdateStatusText.Text))
            UpdateStatusText.Text = _update != null ? $"v{_update.Version} is available." : "";
    }

    // Theme chips + accent swatches; rebuilt after every pick so the selection ring (and the
    // neutral swatch, which follows the theme) stay right.
    private void BuildSettingsChoices()
    {
        ThemeOptions.Children.Clear();
        // Dark themes on one row, light on the next; on a phone at most 3 per row so they wrap.
        var perRow = IsCompact ? 3 : int.MaxValue;
        var rows = new Dictionary<bool, StackPanel>();
        StackPanel RowFor(bool light)
        {
            if (!rows.TryGetValue(light, out var row) || row.Children.Count >= perRow)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                rows[light] = row;
                ThemeOptions.Children.Add(row);
            }
            return row;
        }
        foreach (var theme in Themes.ThemeNames.OrderBy(Themes.IsLight))
        {
            var chip = new Border
            {
                Padding = new Thickness(16, 7, 16, 7),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(_prefs.Theme == theme ? 2 : 1),
                BorderBrush = (Brush)Application.Current.Resources[_prefs.Theme == theme ? "AccentBrush" : "BorderBrush2"],
                Background = (Brush)Application.Current.Resources["RaisedBrush"],
                Child = new TextBlock { Text = theme },
            };
            RowFor(Themes.IsLight(theme)).Children.Add(ChoiceButton(chip, theme, () => _prefs.Theme = theme));
        }

        // Streaming quality chips (#28), same look as the theme chips.
        QualityOptions.Children.Clear();
        StackPanel? qualityRow = null;
        foreach (var (quality, label, detail) in StreamQualityChoices)
        {
            if (qualityRow == null || qualityRow.Children.Count >= (IsCompact ? 3 : int.MaxValue))
            {
                qualityRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                QualityOptions.Children.Add(qualityRow);
            }
            var selected = _prefs.StreamQuality == quality;
            var chip = new Border
            {
                Padding = new Thickness(14, 6, 14, 6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = (Brush)Application.Current.Resources[selected ? "AccentBrush" : "BorderBrush2"],
                Background = (Brush)Application.Current.Resources["RaisedBrush"],
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = label },
                        new TextBlock { Text = detail, FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] },
                    },
                },
            };
            qualityRow.Children.Add(ChoiceButton(chip, $"{label} ({detail})", () => SetStreamQuality(quality)));
        }

        AccentOptions.Children.Clear();
        foreach (var accent in Themes.AccentNames)
        {
            var ring = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                BorderThickness = new Thickness(2),
                BorderBrush = _prefs.Accent == accent
                    ? (Brush)Application.Current.Resources["TextPrimaryBrush"]
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 24,
                    Height = 24,
                    Fill = new SolidColorBrush(Themes.AccentSwatch(accent, _prefs.Theme)),
                    Stroke = (Brush)Application.Current.Resources["BorderBrush2"],
                    StrokeThickness = 1,
                },
            };
            AccentOptions.Children.Add(ChoiceButton(ring, accent, () =>
            {
                _prefs.Accent = accent;
                AccentColorPicker.Visibility = Visibility.Collapsed;
            }));
        }

        // "Custom": the color-wheel pick (a rainbow until one is made); opens the wheel.
        var isCustom = _prefs.Accent.StartsWith('#');
        _customAccentSwatch = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 24,
            Height = 24,
            Stroke = (Brush)Application.Current.Resources["BorderBrush2"],
            StrokeThickness = 1,
            Fill = Themes.TryAccentColor(_prefs.CustomAccent, out var custom) ? new SolidColorBrush(custom) : RainbowBrush(),
        };
        var customRing = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            BorderThickness = new Thickness(2),
            BorderBrush = isCustom
                ? (Brush)Application.Current.Resources["TextPrimaryBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = _customAccentSwatch,
        };
        AccentOptions.Children.Add(ChoiceButton(customRing, "Custom", () =>
        {
            if (!Themes.TryAccentColor(_prefs.CustomAccent, out _))
                _prefs.CustomAccent = Themes.ToHex(Themes.AccentSwatch(_prefs.Accent, _prefs.Theme));
            _prefs.Accent = _prefs.CustomAccent;
            ShowAccentColorPicker();
        }));
    }

    private Microsoft.UI.Xaml.Shapes.Ellipse? _customAccentSwatch;
    private bool _settingAccentPicker;
    private DispatcherTimer? _accentSaveTimer;

    private static LinearGradientBrush RainbowBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };
        uint[] colors = { 0xFFFF5A5A, 0xFFFFC23D, 0xFF3DD68C, 0xFF4C8DFF, 0xFFB15CFF };
        for (var i = 0; i < colors.Length; i++)
        {
            var c = colors[i];
            brush.GradientStops.Add(new GradientStop
            {
                Color = Windows.UI.Color.FromArgb(0xFF, (byte)(c >> 16), (byte)(c >> 8), (byte)c),
                Offset = i / (double)(colors.Length - 1),
            });
        }
        return brush;
    }

    private void ShowAccentColorPicker()
    {
        if (!Themes.TryAccentColor(_prefs.CustomAccent, out var color))
            return;
        _settingAccentPicker = true;
        AccentColorPicker.Color = color;
        _settingAccentPicker = false;
        AccentColorPicker.Visibility = Visibility.Visible;
    }

    // Dragging on the wheel recolors the app live; preferences.json is written once it settles.
    private void AccentColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_settingAccentPicker)
            return;

        var hex = Themes.ToHex(args.NewColor);
        _prefs.CustomAccent = hex;
        _prefs.Accent = hex;
        Themes.Apply(_prefs.Theme, _prefs.Accent);
        if (_customAccentSwatch != null)
            _customAccentSwatch.Fill = new SolidColorBrush(args.NewColor);

        if (_accentSaveTimer == null)
        {
            _accentSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _accentSaveTimer.Tick += (_, _) =>
            {
                _accentSaveTimer.Stop();
                PreferencesService.Save(_prefs);
                BuildSettingsChoices();
            };
        }
        _accentSaveTimer.Stop();
        _accentSaveTimer.Start();
    }

    private Button ChoiceButton(UIElement content, string name, Action choose)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) =>
        {
            choose();
            Themes.Apply(_prefs.Theme, _prefs.Accent);
            PreferencesService.Save(_prefs);
            BuildSettingsChoices();
        };
        return button;
    }

    private async void PreferenceCheck_Click(object sender, RoutedEventArgs e)
    {
        var onlineLyricsBefore = _prefs.OnlineLyrics;
        _prefs.PrefetchNextTrack = PrefetchCheck.IsChecked == true;
        _prefs.ResumeQueueOnStartup = ResumeQueueCheck.IsChecked == true;
        _prefs.OnlineLyrics = OnlineLyricsCheck.IsChecked == true;
        _prefs.CheckForUpdates = AutoUpdateCheck.IsChecked == true;
        _prefs.DiscordPresence = DiscordPresenceCheck.IsChecked == true;
        PreferencesService.Save(_prefs);
        if (_discord != null)
            _discord.Enabled = _prefs.DiscordPresence;

        if (!_prefs.PrefetchNextTrack)
        {
            _prefetcher.Clear(_loadedEntry?.Song.Id);
            UpdateCacheSizeText();
        }

        // Re-fetch the current song's lyrics with/without the online lookup.
        if (onlineLyricsBefore != _prefs.OnlineLyrics)
        {
            _lyricsSongId = null;
            if (LyricsPanelView.Visibility == Visibility.Visible || QueueSidePanel.Visibility == Visibility.Visible)
                await LoadLyricsForCurrentSongAsync();
        }
    }

    private void UpdateCacheSizeText()
    {
        var mb = TrackPrefetcher.CacheSizeBytes() / (1024.0 * 1024.0);
        CacheSizeText.Text = $"Pre-buffered songs: {mb:0.#} MB";
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        _prefetcher.Clear(_loadedEntry?.Song.Id);
        UpdateCacheSizeText();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking...";
        var (update, error) = await UpdateChecker.CheckWithErrorAsync();
        CheckUpdatesButton.IsEnabled = true;

        if (update != null)
        {
            ShowUpdateButton(update);
            UpdateStatusText.Text = $"v{update.Version} is available - use the Update button at the bottom of the sidebar.";
        }
        else
        {
            UpdateStatusText.Text = error == null ? "You're on the latest version." : $"Couldn't check for updates: {error}.";
        }
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) =>
        UpdateChecker.OpenUrl("https://github.com/Lun-Astra/LunaDrome");

    private void ReportProblem_Click(object sender, RoutedEventArgs e) =>
        UpdateChecker.OpenUrl("https://github.com/Lun-Astra/LunaDrome/issues/new");

    // Back to the login screen: forgets the saved password (server + username stay filled in),
    // stops playback. The queue is saved first, so signing back in resumes it.
    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "Sign out",
            Content = "Sign out of this server? Playback stops; your queue is kept for next time.",
            PrimaryButtonText = "Sign out",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        LeaveJamPlayback(release: true);
        SaveQueueState();
        _signedIn = false;
        SettingsService.Save(new AppSettings { ServerUrl = _client.ServerUrl, Username = _client.Username });

        _player.Pause();
        SetPlaying(false);
        _loadedEntry = null;
        _pendingSeek = TimeSpan.Zero;
        _unshuffledOrder = null;
        _queue.Clear();
        SetCurrent(null);
        QueueSidePanel.Visibility = Visibility.Collapsed;
        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();

        // Everything reloads for whichever server is signed into next.
        _homeLoaded = false;
        _albumsPageLoaded = false;
        _songsPageLoaded = false;
        _genresPageLoaded = false;
        _lyricsSongId = null;

        PasswordBox.Password = "";
        StatusText.Text = "";
        LoginButton.IsEnabled = true;
        LibraryPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
    }

    // ================= Updates =================

    private UpdateInfo? _update;
    private bool _updateInProgress;

    private async Task CheckForUpdateAsync()
    {
        if (_update == null && await UpdateChecker.CheckAsync() is { } update)
            ShowUpdateButton(update);
    }

    private void ShowUpdateButton(UpdateInfo update)
    {
        _update = update;
        UpdateButton.Content = $"Update to v{update.Version}";
        ToolTipService.SetToolTip(UpdateButton, UpdateChecker.CanInstallInPlace
            ? $"Download and install LunaDrome v{update.Version} (you're on v{UpdateChecker.CurrentVersion})"
            : $"Open the LunaDrome v{update.Version} download page (you're on v{UpdateChecker.CurrentVersion})");
        UpdateButton.Visibility = Visibility.Visible;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } update || _updateInProgress)
            return;

        if (!UpdateChecker.CanInstallInPlace || update.InstallerUrl == null)
        {
            UpdateChecker.OpenReleasePage(update);
            return;
        }

        var confirm = new ContentDialog
        {
            Title = $"Update to LunaDrome v{update.Version}",
            Content = "LunaDrome will download the update, close, install it and start again. Your queue is kept.",
            PrimaryButtonText = "Update",
            CloseButtonText = "Not now",
            XamlRoot = this.XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        _updateInProgress = true;
        try
        {
            var progress = new Progress<double>(p => UpdateButton.Content = $"Downloading... {p:P0}");
            var installer = await UpdateChecker.DownloadInstallerAsync(update, progress);
            UpdateButton.Content = "Installing...";
            SaveQueueState();
            UpdateChecker.RunInstaller(installer);
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            _updateInProgress = false;
            UpdateButton.Content = $"Update to v{update.Version}";
            await ShowErrorAsync($"Update failed: {ex.Message}");
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
        GenresPage.Visibility = Visibility.Collapsed;
        PlaylistsPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        SearchPage.Visibility = Visibility.Collapsed;
        DownloadsPage.Visibility = Visibility.Collapsed;
        JamPage.Visibility = Visibility.Collapsed;
        _jam?.SetWatching(false);
    }

    private void ShowPage(UIElement page, Action? reopen = null)
    {
        RecordPage(reopen);
        ExitSelection();         // a selection belongs to the page it was made on
        CloseCompactSidebar();   // any page opened from the phone menu closes it
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
        NavGenres.IsChecked = false;
        NavJam.IsChecked = false;
        if (!keepPlaylist)
            PlaylistsList.SelectedItem = null;
        _suppressNavEvents = false;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        CloseCompactSidebar();
        if (!_initialized || _suppressNavEvents || sender is not RadioButton { Tag: string tag })
            return;
        OnCompactNavigated(tag);

        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();

        _suppressNavEvents = true;
        PlaylistsList.SelectedItem = null;
        _suppressNavEvents = false;

        HideAllPages();
        switch (tag)
        {
            case "Home":
                ShowPage(HomePage, () => Reselect(NavHome));
                _ = LoadHomeAsync();
                break;
            case "Artists":
                ArtistsList.SelectedItem = null;
                ShowPage(ArtistsPage, () => Reselect(NavArtists));
                break;
            case "Albums":
                ShowPage(AlbumsPage, () => Reselect(NavAlbums));
                if (!_albumsPageLoaded)
                {
                    _albumsPageLoaded = true;
                    _ = LoadAllAlbumsAsync();
                }
                break;
            case "Songs":
                ShowPage(SongsPage, () => Reselect(NavSongs));
                if (!_songsPageLoaded)
                {
                    _songsPageLoaded = true;
                    _ = LoadAllSongsAsync();
                }
                break;
            case "Genres":
                ShowPage(GenresPage, () => Reselect(NavGenres));
                if (!_genresPageLoaded)
                {
                    _genresPageLoaded = true;
                    _ = LoadAllGenresAsync();
                }
                break;
            case "Jam":
                ShowJamPage();
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

    private void HomeMoreGenresButton_Click(object sender, RoutedEventArgs e) => NavGenres.IsChecked = true;

    private void HomePlaylistCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Playlist playlist })
            PlaylistsList.SelectedItem = _playlists.FirstOrDefault(p => p.Id == playlist.Id) ?? playlist;
    }

    private async void GenreCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Genre genre })
            await OpenGenreAsync(genre);
    }

    private async Task OpenGenreAsync(Genre genre)
    {
        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        ClearSidebarSelection();
        HideAllPages();
        GenreAlbumsHeader.Text = genre.Name.ToUpperInvariant();
        GenreAlbumsList.ItemsSource = null;
        ShowPage(GenreAlbumsPage, () => _ = OpenGenreAsync(genre));

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

    // ================= Genres =================

    private bool _genresPageLoaded;
    private List<Genre> _allGenresRaw = new();
    private bool _genresSortByName;

    private async Task LoadAllGenresAsync()
    {
        try
        {
            _allGenresRaw = await _client.GetGenresAsync();
            RefreshAllGenres();
        }
        catch (Exception ex)
        {
            _genresPageLoaded = false;
            await ShowErrorAsync($"Failed to load genres: {ex.Message}");
        }
    }

    private void AllGenresSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshAllGenres();

    // Most albums first (as the server list comes) or A-Z.
    private void GenresSortButton_Click(object sender, RoutedEventArgs e)
    {
        _genresSortByName = !_genresSortByName;
        GenresSortButton.Content = _genresSortByName ? "Sort: A–Z" : "Sort: Most albums";
        RefreshAllGenres();
    }

    private void RefreshAllGenres()
    {
        if (AllGenresSearchBox == null)
            return;

        var q = AllGenresSearchBox.Text.Trim();
        IEnumerable<Genre> genres = string.IsNullOrEmpty(q)
            ? _allGenresRaw
            : _allGenresRaw.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (_genresSortByName)
            genres = genres.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);
        AllGenresList.ItemsSource = genres.ToList();
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

    // The album's songs for the card menu; empty (after an error message) if loading fails.
    private async Task<List<Song>> GetAlbumSongsAsync(Album album)
    {
        try
        {
            var songs = await _client.GetSongsForAlbumAsync(album.Id);
            PopulateSongArt(songs);
            return songs;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Failed to load album: {ex.Message}");
            return new();
        }
    }

    // Right-click an album card: the same queue options as a song, for the whole album.
    private void AlbumCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Album album } card)
            return;

        var flyout = new MenuFlyout();
        AddMenuItem(flyout, "Play", async () => await PlayAlbumAsync(album));
        AddMenuItem(flyout, "Shuffle", async () =>
        {
            var songs = await GetAlbumSongsAsync(album);
            if (songs.Count > 0)
                PlayQueueFrom(songs.OrderBy(_ => _random.Next()).ToList(), 0);
        });
        AddMenuItem(flyout, "Play next", async () => QueueSongsNext(await GetAlbumSongsAsync(album)));
        AddMenuItem(flyout, "Add to queue", async () => AddToQueue(await GetAlbumSongsAsync(album)));
        AddMenuItem(flyout, "Play last", async () => QueueSongsLast(await GetAlbumSongsAsync(album)));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(BuildAddToPlaylistSubmenu(() => GetAlbumSongsAsync(album)));
        AddJamMenuItem(flyout, () => GetAlbumSongsAsync(album));
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "Go to album", async () => await OpenAlbumAsync(album));
        AddMenuItem(flyout, "Go to artist", async () => await NavigateToArtistAsync(album.ArtistId, album.ArtistName));
        flyout.ShowAt(card, e.GetPosition(card));
        e.Handled = true;
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
        ShowPage(ArtistDetailPage, () => _ = NavigateToArtistAsync(artistId, fallbackName));
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
    // "Last" = append to the end of the queue. Neither interrupts what's playing; with nothing
    // in the queue they start it.
    private void QueueSongsNext(List<Song> songs)
    {
        if (songs.Count == 0)
            return;

        if (_current == null)
        {
            PlayQueueFrom(songs, 0);
            return;
        }

        var entries = songs.Select(s => new QueueEntry(s) { IsUserQueued = true }).ToList();
        InsertIntoQueue(QueueIndex + 1, entries);
        // Keep them "next" in the pre-shuffle order too, so un-shuffling doesn't bury them.
        _unshuffledOrder?.InsertRange(_unshuffledOrder.IndexOf(_current) + 1, entries);
    }

    // "Add to queue" (#16), like Spotify: after the playing song AND after anything queued
    // before (Play next / Add to queue), but ahead of the rest of the list that was started.
    // It used to append to the very end - after a whole album or the Tracks page, so it
    // effectively never came up.
    private void AddToQueue(List<Song> songs)
    {
        if (songs.Count == 0)
            return;

        if (_current == null)
        {
            PlayQueueFrom(songs, 0);
            return;
        }

        var entries = songs.Select(s => new QueueEntry(s) { IsUserQueued = true }).ToList();
        InsertIntoQueue(AfterUserQueued(_queue, _current), entries);
        if (_unshuffledOrder != null)
            _unshuffledOrder.InsertRange(AfterUserQueued(_unshuffledOrder, _current), entries);
    }

    // Position right after the playing entry and the queued-by-you entries that follow it.
    private static int AfterUserQueued(IList<QueueEntry> list, QueueEntry current)
    {
        var index = list.IndexOf(current) + 1;
        while (index < list.Count && list[index].IsUserQueued)
            index++;
        return index;
    }

    // Shuffle what's still to come - except songs you queued yourself, which stay up front
    // in the order you added them.
    private List<QueueEntry> WithUpcomingShuffled(int index)
    {
        var upcoming = _queue.Skip(index + 1).ToList();
        var queuedByYou = upcoming.TakeWhile(q => q.IsUserQueued).ToList();
        return _queue.Take(index + 1).Concat(queuedByYou).Concat(Shuffled(upcoming.Skip(queuedByYou.Count))).ToList();
    }

    // "Last" (album / artist header buttons): the very end of the queue, like Feishin.
    private void QueueSongsLast(List<Song> songs)
    {
        if (songs.Count == 0)
            return;

        if (_current == null)
        {
            PlayQueueFrom(songs, 0);
            return;
        }

        var entries = songs.Select(s => new QueueEntry(s)).ToList();
        InsertIntoQueue(_queue.Count, entries);
        _unshuffledOrder?.AddRange(entries);
    }

    private void InsertIntoQueue(int index, List<QueueEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
            _queue.Insert(index + i, entries[i]);
    }

    // ================= Album page =================

    private List<Song> _albumSongsRaw = new();
    private int _albumLoadToken;

    private async Task OpenAlbumAsync(Album album)
    {
        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();
        HideAllPages();
        ShowPage(AlbumPage, () => _ = OpenAlbumAsync(album));
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
        if (InSelectionMode)
            return;   // tapping toggles songs while selecting
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

        if (IsTouchHold(e))   // #30: hold starts selecting on Android
        {
            EnterAlbumSelection(song);
            e.Handled = true;
            return;
        }

        var flyout = BuildSongMenu(song, shown);
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "Play album next", () => QueueSongsNext(_albumSongsRaw));
        AddMenuItem(flyout, "Add album to queue", () => AddToQueue(_albumSongsRaw));
        AddMenuItem(flyout, "Play album last", () => QueueSongsLast(_albumSongsRaw));
        flyout.ShowAt(row, e.GetPosition(row));
        e.Handled = true;
    }

    private void UpdateNowPlayingFavorite(Song song)
    {
        NowPlayingFavoriteHeart.Fill = (Brush)new FavoriteFillConverter().Convert(song.IsFavorited, typeof(Brush), null!, "");
        ToolTipService.SetToolTip(NowPlayingFavorite, song.IsFavorited ? "Remove from favorites" : "Add to favorites");
        SyncCompactPlayer();
    }

    private async void NowPlayingFavorite_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_current?.Song is not { } song)
            return;
        try
        {
            if (song.IsFavorited)
                await _client.UnstarSongAsync(song.Id);
            else
                await _client.StarSongAsync(song.Id);
            song.IsFavorited = !song.IsFavorited;
            if (_current?.Song == song)
                UpdateNowPlayingFavorite(song);
        }
        catch { /* best-effort, like the list hearts */ }
    }

    private async void TrackFavorite_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: Song song })
            return;

        try
        {
            if (song.IsFavorited)
                await _client.UnstarSongAsync(song.Id);
            else
                await _client.StarSongAsync(song.Id);
            song.IsFavorited = !song.IsFavorited;   // the row's heart follows through its binding
            if (_current?.Song == song)
                UpdateNowPlayingFavorite(song);
        }
        catch { /* best-effort */ }
    }
    // Tapping the heart twice quickly mustn't also play the song (the list's DoubleTapped).
    private void TrackFavorite_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;

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
            var songs = await _client.GetAllSongsAsync();
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
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "Play next", () => { if (TrackViewSongs(key) is { } songs) QueueSongsNext(songs); });
        AddMenuItem(flyout, "Add to queue", () => { if (TrackViewSongs(key) is { } songs) AddToQueue(songs); });
        flyout.ShowAt(button);
    }

    // The list as shown (search + sort applied).
    private List<Song>? TrackViewSongs(string key) =>
        _trackViews[key].List.ItemsSource is List<Song> { Count: > 0 } songs ? songs.ToList() : null;

    private void PlayTrackView(string key, bool shuffle)
    {
        if (_trackViews[key].List.ItemsSource is not List<Song> { Count: > 0 } songs)
            return;

        PlayQueueFrom(shuffle ? songs.OrderBy(_ => _random.Next()).ToList() : songs, 0);
    }

    // Double-click a row to play: queues the whole list as shown (search + sort), from that track.
    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (InSelectionMode)
            return;   // tapping toggles songs while selecting
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

        if (IsTouchHold(e))   // #30: hold starts selecting on Android
        {
            EnterSelection(list, song);
            e.Handled = true;
            return;
        }

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
        AddMenuItem(flyout, "Play next", () => QueueSongsNext(new() { song }));
        AddMenuItem(flyout, "Add to queue", () => AddToQueue(new() { song }));
        AddMenuItem(flyout, "Play last", () => QueueSongsLast(new() { song }));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(BuildAddToPlaylistSubmenu(new List<Song> { song }));
        AddJamMenuItem(flyout, () => Task.FromResult(new List<Song> { song }));
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

    private MenuFlyoutSubItem BuildAddToPlaylistSubmenu(List<Song> songs) =>
        BuildAddToPlaylistSubmenu(() => Task.FromResult(songs));

    // Songs are fetched only once a playlist is picked (album cards load them on demand).
    private MenuFlyoutSubItem BuildAddToPlaylistSubmenu(Func<Task<List<Song>>> getSongs)
    {
        var sub = new MenuFlyoutSubItem { Text = "Add to playlist" };
        foreach (var playlist in _playlists)
        {
            var item = new MenuFlyoutItem { Text = playlist.Name };
            item.Click += async (_, _) =>
            {
                try
                {
                    foreach (var song in await getSongs())
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
            {
                foreach (var song in await getSongs())
                    await _client.AddSongToPlaylistAsync(id, song.Id);
            }
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
        ShowPage(PlaylistsPage, () =>
        {
            PlaylistsList.SelectedItem = null;
            PlaylistsList.SelectedItem = _playlists.FirstOrDefault(p => p.Id == playlist.Id) ?? playlist;
        });

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

        var flyout = PlaylistQueueMenu(playlist);
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "Delete playlist", async () => await DeletePlaylistAsync(playlist));
        flyout.ShowAt(PlaylistsList, e.GetPosition(PlaylistsList));
        e.Handled = true;
    }

    // Right-click (long-press on a phone) on a Home playlist card.
    private void HomePlaylistCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Playlist playlist } card)
            return;

        PlaylistQueueMenu(playlist).ShowAt(card, e.GetPosition(card));
        e.Handled = true;
    }

    // Play / Shuffle / Play next / Add to queue for a whole playlist, without opening it.
    private MenuFlyout PlaylistQueueMenu(Playlist playlist)
    {
        var flyout = new MenuFlyout();
        AddMenuItem(flyout, "Play", async () => { if (await LoadPlaylistSongsAsync(playlist) is { } songs) PlayQueueFrom(songs, 0); });
        AddMenuItem(flyout, "Shuffle", async () =>
        {
            if (await LoadPlaylistSongsAsync(playlist) is { } songs)
                PlayQueueFrom(songs.OrderBy(_ => _random.Next()).ToList(), 0);
        });
        AddMenuItem(flyout, "Play next", async () => { if (await LoadPlaylistSongsAsync(playlist) is { } songs) QueueSongsNext(songs); });
        AddMenuItem(flyout, "Add to queue", async () =>
        {
            if (await LoadPlaylistSongsAsync(playlist) is { } songs)
            {
                AddToQueue(songs);
                ShowToast($"Added {songs.Count} songs from \"{playlist.Name}\" to the queue");
            }
        });
        return flyout;
    }

    private async Task<List<Song>?> LoadPlaylistSongsAsync(Playlist playlist)
    {
        try
        {
            var songs = await _client.GetPlaylistSongsAsync(playlist.Id);
            PopulateSongArt(songs);
            return songs.Count > 0 ? songs : null;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Failed to load playlist: {ex.Message}");
            return null;
        }
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

    private Task RemoveFromPlaylistAsync(Song song) => RemoveFromPlaylistAsync(new List<Song> { song });

    private async Task RemoveFromPlaylistAsync(List<Song> toRemove)
    {
        if (_currentPlaylist == null || toRemove.All(s => s.PlaylistIndex < 0))
            return;

        try
        {
            // By position: highest first, so removing one doesn't shift the next one's index.
            foreach (var index in toRemove.Select(s => s.PlaylistIndex).Where(i => i >= 0).Distinct().OrderByDescending(i => i))
                await _client.RemoveSongFromPlaylistAsync(_currentPlaylist.Id, index);
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
        if (IsCompact)
            return;   // the whole bar opens the player on a phone (PlaybackBar_Tapped)
        FullScreenPlayerOverlay.Visibility = FullScreenPlayerOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (FullScreenPlayerOverlay.Visibility == Visibility.Visible)
        {
            Motion.FadeIn(FullScreenPlayerOverlay);
            ScrollLyricsToActive(animate: false);
        }
        UpdateVisualizerCaptureState();
    }

    // The Queue button toggles the docked side panel (Feishin's right sidebar); the full-screen
    // Queue / Lyrics / Visualizer player stays on the now-playing title. Two separate views of
    // the same queue.
    private async void QueueToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var show = QueueSidePanel.Visibility != Visibility.Visible || FullScreenPlayerOverlay.Visibility == Visibility.Visible;
        if (FullScreenPlayerOverlay.Visibility == Visibility.Visible)
            CloseFullScreenPlayer_Click(sender, e);

        QueueSidePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;

        Motion.FadeIn(QueueSidePanel);
        // After the just-shown list has been laid out, or the scroll is ignored.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_current != null)
                SideQueueList.ScrollIntoView(_current, ScrollIntoViewAlignment.Leading);
        });
        await LoadLyricsForCurrentSongAsync();
    }

    // ----- Side panel toolbar (acts on the side list's selection) -----

    // One-off shuffle of what's still to come (the shuffle mode button is separate).
    private void SideQueueShuffle_Click(object sender, RoutedEventArgs e)
    {
        var index = QueueIndex;
        ReplaceQueue(WithUpcomingShuffled(index));
        if (_current != null)
            SideQueueList.ScrollIntoView(_current);
    }

    private void SideQueuePlayNext_Click(object sender, RoutedEventArgs e) =>
        MoveToPlayNext(SideQueueList, SelectedQueueEntries(SideQueueList));

    private void SideQueueMoveToEnd_Click(object sender, RoutedEventArgs e) =>
        MoveToEnd(SideQueueList, SelectedQueueEntries(SideQueueList));

    private void SideQueueRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueueEntries(SideQueueList) is { Count: > 0 } selected)
            RemoveFromQueue(selected);
    }

    private void SideQueueClear_Click(object sender, RoutedEventArgs e) =>
        RemoveFromQueue(_queue.Where(q => q != _current).ToList());

    // Keeps the side panel's # column in step with the queue order.
    private void RenumberQueue()
    {
        for (var i = 0; i < _queue.Count; i++)
            _queue[i].Number = i + 1;
    }

    private void ScrollQueuesTo(QueueEntry entry)
    {
        QueueList.ScrollIntoView(entry);
        if (QueueSidePanel.Visibility == Visibility.Visible)
            SideQueueList.ScrollIntoView(entry);
    }

    private void CloseFullScreenPlayer_Click(object sender, RoutedEventArgs e)
    {
        // #29: on a phone, ✕ on the queue / lyrics tabs goes back to the player view first.
        if (IsCompact && _compactPlayerTabs)
        {
            _compactPlayerTabs = false;
            UpdateCompactPlayerMode();
            return;
        }
        FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
        UpdateVisualizerCaptureState();
    }

    private async void NowPlayingArtist_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsCompact && sender == NowPlayingArtistLink)
            return;   // phone: tapping the bar opens the player instead (PlaybackBar_Tapped)
        e.Handled = true;
        if (_current?.Song is { } song)
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
        {
            await LoadLyricsForCurrentSongAsync();
            ScrollLyricsToActive(animate: false);
        }
    }

    private void QueueList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (InSelectionMode)
            return;   // tapping toggles songs while selecting
        if ((e.OriginalSource as FrameworkElement)?.DataContext is QueueEntry entry && _queue.Contains(entry))
            PlayEntry(entry);
    }

    // Selected queue rows in queue order (the ListView reports them in click order).
    private List<QueueEntry> SelectedQueueEntries(ListView list) =>
        list.SelectedItems.OfType<QueueEntry>().OrderBy(_queue.IndexOf).ToList();

    // Shared by the full-screen Queue tab and the side panel (sender = which list).
    // Acts on the whole selection (#7); right-clicking a row outside it selects just that row.
    private void QueueList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not ListView list || (e.OriginalSource as FrameworkElement)?.DataContext is not QueueEntry clicked)
            return;

        if (IsTouchHold(e))   // #30: hold starts selecting on Android
        {
            EnterSelection(list, clicked);
            e.Handled = true;
            return;
        }

        if (!list.SelectedItems.Contains(clicked))
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(clicked);
        }

        BuildQueueMenu(list, SelectedQueueEntries(list), clicked).ShowAt(list, e.GetPosition(list));
        e.Handled = true;
    }

    private MenuFlyout BuildQueueMenu(ListView list, List<QueueEntry> selected, QueueEntry? clicked)
    {
        var flyout = new MenuFlyout();
        if (selected.Count == 1 && clicked != null)
            AddMenuItem(flyout, "Play", () => PlayEntry(clicked));
        AddMenuItem(flyout, "Play next", () => MoveToPlayNext(list, selected));
        AddMenuItem(flyout, "Move up", () => MoveInQueue(list, selected, -1));
        AddMenuItem(flyout, "Move down", () => MoveInQueue(list, selected, +1));
        AddMenuItem(flyout, "Move to end", () => MoveToEnd(list, selected));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(BuildAddToPlaylistSubmenu(selected.Select(q => q.Song).ToList()));
        if (selected.Count == 1)
            AddFavoriteToggle(flyout, selected[0].Song);
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, selected.Count == 1 ? "Remove from queue" : $"Remove {selected.Count} songs from queue",
            () => RemoveFromQueue(selected));
        return flyout;
    }

    private void QueueList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is ListView list && e.Key == Windows.System.VirtualKey.Delete && SelectedQueueEntries(list) is { Count: > 0 } selected)
        {
            RemoveFromQueue(selected);
            e.Handled = true;
        }
    }

    // Shifts the selected rows one place up (-1) or down (+1) as a block (#8).
    private void MoveInQueue(ListView list, List<QueueEntry> entries, int delta)
    {
        var indices = entries.Select(_queue.IndexOf).Where(i => i >= 0).OrderBy(i => i).ToList();
        if (indices.Count == 0 || (delta < 0 && indices[0] == 0) || (delta > 0 && indices[^1] == _queue.Count - 1))
            return;

        if (delta > 0)
            indices.Reverse();
        foreach (var i in indices)
            _queue.Move(i, i + delta);

        ReselectQueue(list, entries);
    }

    // "Play next" from inside the queue: move the rows to right after the playing track.
    private void MoveToPlayNext(ListView list, List<QueueEntry> entries)
    {
        var moving = entries.Where(q => q != _current && _queue.Contains(q)).ToList();
        if (moving.Count == 0)
            return;

        foreach (var entry in moving)
            _queue.Remove(entry);
        InsertIntoQueue(QueueIndex + 1, moving);
        ReselectQueue(list, moving);
    }

    private void MoveToEnd(ListView list, List<QueueEntry> entries)
    {
        var moving = entries.Where(_queue.Contains).ToList();
        foreach (var entry in moving)
            _queue.Remove(entry);
        InsertIntoQueue(_queue.Count, moving);
        ReselectQueue(list, moving);
    }

    private static void ReselectQueue(ListView list, List<QueueEntry> entries)
    {
        list.SelectedItems.Clear();
        foreach (var entry in entries)
            list.SelectedItems.Add(entry);
    }

    private void RemoveFromQueue(List<QueueEntry> entries)
    {
        var removing = entries.ToHashSet();
        RemoveTakenOverFromJam(removing);

        // Removing the playing track moves on to the next remaining one (playing it only if
        // something was playing), or stops if nothing is left after it.
        QueueEntry? replacement = null;
        var removingCurrent = _current != null && removing.Contains(_current);
        if (removingCurrent)
            replacement = _queue.Skip(QueueIndex + 1).FirstOrDefault(q => !removing.Contains(q));

        foreach (var entry in removing)
            _queue.Remove(entry);
        _unshuffledOrder?.RemoveAll(removing.Contains);

        if (!removingCurrent)
            return;

        if (replacement != null && _isPlaying)
        {
            PlayEntry(replacement);
            return;
        }

        _player.Pause();
        SetPlaying(false);
        _loadedEntry = null;
        _pendingSeek = TimeSpan.Zero;
        SetCurrent(replacement);
    }

    // Lyrics of the current song, fetched once and shared by the full-screen Lyrics tab and the
    // side panel. _lyricsSongId = the song they belong to (null = not fetched yet).
    private string? _lyricsSongId;
    private List<LyricLine> _lyricsLines = new();

    private async Task LoadLyricsForCurrentSongAsync()
    {
        var song = _current?.Song;
        if (song == null)
        {
            _lyricsSongId = null;
            _lyricsLines = new();
            LyricsList.ItemsSource = null;
            LyricsPlaceholder.Text = "Nothing playing.";
            LyricsPlaceholder.Visibility = Visibility.Visible;
            _lyricsTimer.Stop();
            UpdateLyricsOffsetBar();
            ShowSideLyrics();
            return;
        }

        if (_lyricsSongId != song.Id)
        {
            List<LyricLine> lines;
            bool synced;
            try
            {
                (lines, synced) = await _client.GetLyricsAsync(song, _prefs.OnlineLyrics);
            }
            catch
            {
                (lines, synced) = (new(), false);
            }

            // Skipped on to another song while this was loading.
            if (_current?.Song.Id != song.Id)
                return;

            _lyricsSongId = song.Id;
            _lyricsLines = lines;
            _lyricsAreSynced = synced;
        }

        foreach (var line in _lyricsLines)
            line.IsDimmed = _lyricsAreSynced;
        _lyricsOffset = _prefs.LyricsOffsets.TryGetValue(song.Id, out var offset) ? offset : 0;
        UpdateLyricsOffsetBar();
        if (_lyricsAreSynced && _lyricsLines.Count > 0)
            _lyricsTimer.Start();
        else
            _lyricsTimer.Stop();
        LyricsList.ItemsSource = _lyricsLines;
        LyricsPlaceholder.Text = "No lyrics found for this track.";
        LyricsPlaceholder.Visibility = _lyricsLines.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowSideLyrics();
        SyncCompactPlayer();   // #29: show / fill the phone player's lyrics card
    }

    private int _sideLyricIndex = int.MinValue;

    // Side panel lyrics area: hidden without lyrics; synced = previous / current / next line
    // (advanced by UpdateSideLyrics from the progress timer), unsynced = scrollable text.
    private void ShowSideLyrics()
    {
        var has = _lyricsLines.Count > 0 && _lyricsSongId == _current?.Song.Id;
        SideLyricsPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
            return;

        SideLyricsSynced.Visibility = _lyricsAreSynced ? Visibility.Visible : Visibility.Collapsed;
        SideLyricsPlain.Visibility = _lyricsAreSynced ? Visibility.Collapsed : Visibility.Visible;
        if (!_lyricsAreSynced)
        {
            SideLyricsPlainText.Text = string.Join("\n", _lyricsLines.Select(l => l.Text));
            SideLyricsPlain.ChangeView(null, 0, null, true);
        }

        _sideLyricIndex = int.MinValue;
        foreach (var line in _lyricsLines)
            line.IsActive = false;
        UpdateSideLyrics(_loadedEntry == _current ? _player.PlaybackSession.Position : _pendingSeek);
        ScrollLyricsToActive(animate: false);
    }

    // Synced lyrics: follows playback in both the side panel (previous / current / next) and
    // the full-screen Lyrics tab (the current line highlighted and kept in view).
    private void UpdateSideLyrics(TimeSpan position)
    {
        if (!_lyricsAreSynced || _lyricsLines.Count == 0 || _lyricsSongId != _current?.Song.Id)
            return;

        position -= TimeSpan.FromSeconds(_lyricsOffset);
        // Last line that has started (-1 = before the first one).
        var index = -1;
        for (var i = 0; i < _lyricsLines.Count && _lyricsLines[i].Start <= position; i++)
            index = i;
        if (index == _sideLyricIndex)
            return;

        if (_sideLyricIndex >= 0 && _sideLyricIndex < _lyricsLines.Count)
            _lyricsLines[_sideLyricIndex].IsActive = false;
        if (index >= 0)
            _lyricsLines[index].IsActive = true;
        _sideLyricIndex = index;
        ScrollLyricsToActive(animate: true);
        SideLyricPrev.Text = index >= 1 ? _lyricsLines[index - 1].Text : "";
        SideLyricCurrent.Text = index >= 0 ? _lyricsLines[index].Text : "";
        SideLyricNext.Text = index + 1 < _lyricsLines.Count ? _lyricsLines[index + 1].Text : "";
        SyncCompactPlayer();   // #29: the phone player's lyrics card
    }

    private DateTime _lyricsUserScrolledAt = DateTime.MinValue;

    // The progress timer (400ms) also moves the highlight, but a line could light up up to
    // 0.4s late. While synced lyrics are on screen this keeps it within ~0.1s; it does nothing
    // otherwise, and only runs while the song has synced lyrics.
    private readonly DispatcherTimer _lyricsTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void LyricsTimer_Tick(object? sender, object e)
    {
        var onScreen = (FullScreenPlayerOverlay.Visibility == Visibility.Visible && LyricsPanelView.Visibility == Visibility.Visible)
                       || (QueueSidePanel.Visibility == Visibility.Visible && SideLyricsPanel.Visibility == Visibility.Visible)
                       || CompactPlayerView.Visibility == Visibility.Visible;
        if (!onScreen || !_isPlaying || JamOwnsPlayback || _loadedEntry == null || _loadedEntry != _current)
            return;
        UpdateSideLyrics(_player.PlaybackSession.Position);
    }

    // Per-song lyrics offset (seconds, positive = lyrics later): the file is often not the exact
    // recording the synced lyrics were timed to (a music video's intro, another edit).
    private double _lyricsOffset;
    private const double LyricsOffsetStep = 0.5;

    private void LyricsEarlier_Click(object sender, RoutedEventArgs e) => SetLyricsOffset(_lyricsOffset - LyricsOffsetStep);

    private void LyricsLater_Click(object sender, RoutedEventArgs e) => SetLyricsOffset(_lyricsOffset + LyricsOffsetStep);

    private void LyricsOffsetReset_Click(object sender, RoutedEventArgs e) => SetLyricsOffset(0);

    private void SetLyricsOffset(double seconds)
    {
        if (_lyricsSongId == null)
            return;
        _lyricsOffset = Math.Round(Math.Clamp(seconds, -30, 30), 1);
        if (_lyricsOffset == 0)
            _prefs.LyricsOffsets.Remove(_lyricsSongId);
        else
            _prefs.LyricsOffsets[_lyricsSongId] = _lyricsOffset;
        PreferencesService.Save(_prefs);
        UpdateLyricsOffsetBar();
        UpdateSideLyrics(_loadedEntry == _current ? _player.PlaybackSession.Position : _pendingSeek);
        ScrollLyricsToActive(animate: true);
    }

    private void UpdateLyricsOffsetBar()
    {
        LyricsOffsetBar.Visibility = _lyricsAreSynced && _lyricsLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LyricsOffsetText.Text = _lyricsOffset == 0 ? "In sync" : $"{_lyricsOffset:+0.0;-0.0} s";
        LyricsOffsetReset.Visibility = _lyricsOffset == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // Scrolling or touching the list yourself pauses the follow-along for a few seconds.
    private void LyricsList_UserScrolled(object sender, PointerRoutedEventArgs e) => _lyricsUserScrolledAt = DateTime.UtcNow;

    /// <summary>Keeps the full-screen Lyrics tab's active line in the middle of the list.</summary>
    private void ScrollLyricsToActive(bool animate)
    {
        if (!_lyricsAreSynced || _sideLyricIndex < 0 || _sideLyricIndex >= _lyricsLines.Count
            || FullScreenPlayerOverlay.Visibility != Visibility.Visible || LyricsPanelView.Visibility != Visibility.Visible
            || DateTime.UtcNow - _lyricsUserScrolledAt < TimeSpan.FromSeconds(4))
            return;

        var line = _lyricsLines[_sideLyricIndex];
        // After layout: a row that's far off-screen has no container yet until it's scrolled to.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (LyricsList.ContainerFromItem(line) is not FrameworkElement row)
            {
                LyricsList.ScrollIntoView(line, ScrollIntoViewAlignment.Leading);
                LyricsList.UpdateLayout();
                if (LyricsList.ContainerFromItem(line) is not FrameworkElement laidOut)
                    return;
                row = laidOut;
                animate = false;
            }
            if (FindDescendant<ScrollViewer>(LyricsList) is not { } scroller)
                return;
            var top = row.TransformToVisual(scroller).TransformPoint(new Windows.Foundation.Point(0, 0)).Y + scroller.VerticalOffset;
            var target = Math.Max(0, top - (scroller.ViewportHeight - row.ActualHeight) / 2);
            scroller.ChangeView(null, target, null, !animate);
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    // Synced lyrics: double-click a line to jump there.
    private void LyricsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!_lyricsAreSynced || (e.OriginalSource as FrameworkElement)?.DataContext is not LyricLine line)
            return;

        var target = line.Start + TimeSpan.FromSeconds(_lyricsOffset);
        _player.PlaybackSession.Position = target < TimeSpan.Zero ? TimeSpan.Zero : target;
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

    // Every ~16ms: pull the latest bands, shape them, smooth them over time, hand them to the
    // Skia canvas (which does the spatial smoothing + curve drawing). Smoothing is time-based
    // (constants are per 60fps frame), so the speed stays the same at any frame rate.
    //
    // "More active, like Feishin": auto-gain puts the loudest band near the top and music sits
    // within ~30 dB of it, so with the full 60 dB window every band hovered at 50-90% - a high,
    // flat plateau. The shaping drops the bottom quarter (a 45 dB window) and bends the rest
    // (power 1.5), so quiet bands fall into valleys and peaks stand out. Fast rise, slower fall
    // (instead of the old 0.88/0.12 both ways) lets beats punch up without the line jittering.
    private const float VisualizerFloor = 0.25f;
    private const float VisualizerContrast = 1.5f;
    private const double VisualizerRiseKeep = 0.55;
    private const double VisualizerFallKeep = 0.86;
    private long _visualizerLastTicks;

    private void OnVisualizerRenderFrame(object? sender, object e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var dt = _visualizerLastTicks == 0 ? 1.0 / 60 : (now - _visualizerLastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        _visualizerLastTicks = now;
        var frames = Math.Clamp(dt, 0, 0.25) * 60;
        var riseKeep = (float)Math.Pow(VisualizerRiseKeep, frames);
        var fallKeep = (float)Math.Pow(VisualizerFallKeep, frames);

        _spectrum.CopyLatestBandsTo(_visualizerLatestBands);
        float maxChange = 0;
        for (var i = 0; i < _visualizerSmoothed.Length; i++)
        {
            var target = MathF.Pow(Math.Clamp((_visualizerLatestBands[i] - VisualizerFloor) / (1 - VisualizerFloor), 0f, 1f), VisualizerContrast);
            var keep = target > _visualizerSmoothed[i] ? riseKeep : fallKeep;
            _visualizerSmoothed[i] = _visualizerSmoothed[i] * keep + target * (1 - keep);
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

    private int QueueIndex => _current == null ? -1 : _queue.IndexOf(_current);

    private void ReplaceQueue(List<QueueEntry> entries)
    {
        _queue.Clear();
        foreach (var entry in entries)
            _queue.Add(entry);
    }

    private List<QueueEntry> Shuffled(IEnumerable<QueueEntry> entries) => entries.OrderBy(_ => _random.Next()).ToList();

    // Replaces the queue with source and plays source[startIndex]. With shuffle on, that track
    // plays first and the rest follow in random order (the list order is kept for un-shuffle).
    private void PlayQueueFrom(List<Song> source, int startIndex)
    {
        if (startIndex < 0 || startIndex >= source.Count)
            return;

        var entries = source.Select(s => new QueueEntry(s)).ToList();
        var start = entries[startIndex];
        if (_isShuffled)
        {
            _unshuffledOrder = entries;
            ReplaceQueue(new List<QueueEntry> { start }.Concat(Shuffled(entries.Where(q => q != start))).ToList());
        }
        else
        {
            _unshuffledOrder = null;
            ReplaceQueue(entries);
        }

        PlayEntry(start);
    }

    private void PlayEntry(QueueEntry entry, TimeSpan startAt = default, bool crossfading = false)
    {
        // Anything else that starts a song ends a running crossfade first.
        if (!crossfading)
            FinishCrossfade();
        // Playing something from the library while this PC plays the jam = leave the jam.
        LeaveJamPlayback(release: true);
        if (entry != _loadedEntry)
            _stallRecoveries = 0;

        SetCurrent(entry);
        var song = entry.Song;

        // Already pre-buffered by TrackPrefetcher? Then play it from disk: starts instantly (#4).
        var file = _prefetcher.TryGetFile(song.Id);
        Log($"play '{song.Title}' ({song.Id}){(file != null ? " from prefetch" : "")}");
        try
        {
            _loadedEntry = entry;
            _endedEntry = null;
            _pendingSeek = startAt;
            ResetStallWatch();
            _player.Source = MediaSource.CreateFromUri(file != null ? new Uri(file) : new Uri(_client.GetStreamUrl(song.Id)));
            _player.Play();
        }
        catch (Exception ex)
        {
            // e.g. the native media engine failing to load on Linux
            Console.Error.WriteLine($"[LunaDrome] playback start failed: {ex}");
            FinishCrossfade();
            NowPlayingSubText.Text = $"Playback failed: {ex.Message}";
            SetPlaying(false);
            return;
        }
        SetPlaying(true);
    }

    // Seeks once the media has opened - seeking before that is ignored by the player.
    private void ApplyPendingSeek()
    {
        if (JamOwnsPlayback)
        {
            ApplyJamPendingSeek();
            return;
        }
        if (_pendingSeek <= TimeSpan.Zero || _loadedEntry == null)
            return;

        _player.PlaybackSession.Position = _pendingSeek;
        _pendingSeek = TimeSpan.Zero;
    }

    // Marks the playing row and fills the now-playing bar / full-screen player (null = clear).
    private async void SetCurrent(QueueEntry? entry)
    {
        if (_current != null)
            _current.IsCurrent = false;
        _current = entry;
        // While this PC plays the jam, the bar shows the jam track - only mark the queue row.
        if (JamOwnsPlayback)
        {
            if (entry != null)
                entry.IsCurrent = true;
            return;
        }
        _mediaControls?.SetCanGoPreviousNext(entry != null, entry != null);

        if (entry == null)
        {
            NowPlayingText.Text = "Nothing playing";
            NowPlayingSubText.Text = "";
            NowPlayingFavorite.Visibility = Visibility.Collapsed;
            NowPlayingArt.Background = null;
            FullScreenTitleText.Text = "";
            FullScreenArtistText.Text = "";
            FullScreenArt.Background = null;
            ProgressSlider.Value = 0;
            ElapsedText.Text = "0:00";
            DurationText.Text = "0:00";
            _mediaControls?.SetTrack(null);
            ShowSideLyrics();
            return;
        }

        entry.IsCurrent = true;
        var song = entry.Song;
        NowPlayingText.Text = song.Title;
        NowPlayingSubText.Text = song.ArtistName;
        _showingPlaybackError = false;
        NowPlayingFavorite.Visibility = Visibility.Visible;
        UpdateNowPlayingFavorite(song);
        SetImage(NowPlayingArt, song.CoverArtUrl ?? _client.GetCoverArtUrl(song.AlbumId, 240));
        FullScreenTitleText.Text = song.Title;
        FullScreenArtistText.Text = song.ArtistName;
        // The full-screen art is 360x360 - fetch a bigger rendition than the 240px list thumbnails.
        SetImage(FullScreenArt, _client.GetCoverArtUrl(song.AlbumId, 720));
        ScrollQueuesTo(entry);
        ShowSideLyrics(); // hides the previous song's lyrics until the new ones load
        SyncCompactPlayer();
        _mediaControls?.SetTrack(song, _client.GetCoverArtUrl(song.AlbumId, 480));

        if (LyricsPanelView.Visibility == Visibility.Visible || QueueSidePanel.Visibility == Visibility.Visible
            || CompactPlayerView.Visibility == Visibility.Visible)
            await LoadLyricsForCurrentSongAsync();
    }

    private static readonly bool DiagnosticsEnabled = Environment.GetEnvironmentVariable("LUNADROME_LOG") == "1";

    private static void Log(string message)
    {
        if (DiagnosticsEnabled)
            Console.Error.WriteLine($"[LunaDrome] {DateTime.Now:HH:mm:ss.fff} {message}");
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        PlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        _mediaControls?.SetPlaybackState(_current == null && !JamOwnsPlayback ? null : playing);
        SyncCompactPlayer();
    }

    // Shuffle reorders the queue itself instead of picking a random entry on every Next: the
    // queue then shows the real play order, Play next really plays next (#10) and Back goes to
    // the track that actually played before (#6). Tracks already played stay where they are;
    // switching shuffle off restores the original order.
    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        _isShuffled = !_isShuffled;
        UpdateShuffleIcon();

        if (_isShuffled)
        {
            _unshuffledOrder = _queue.ToList();
            var index = QueueIndex;
            ReplaceQueue(WithUpcomingShuffled(index));
        }
        else if (_unshuffledOrder != null)
        {
            var inQueue = _queue.ToHashSet();
            var restored = _unshuffledOrder.Where(inQueue.Contains).ToList();
            var restoredSet = restored.ToHashSet();
            restored.AddRange(_queue.Where(q => !restoredSet.Contains(q)));
            _unshuffledOrder = null;
            ReplaceQueue(restored);
        }

        if (_current != null)
            ScrollQueuesTo(_current);
    }

    private void UpdateShuffleIcon()
    {
        ShuffleIconPath.Stroke = (Brush)Application.Current.Resources[_isShuffled ? "AccentBrush" : "TextSecondaryBrush"];
        SyncCompactPlayer();
    }

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        _repeatMode = _repeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        };
        UpdateRepeatIcon();
    }

    private void UpdateRepeatIcon()
    {
        var brush = (Brush)Application.Current.Resources[_repeatMode == RepeatMode.Off ? "TextSecondaryBrush" : "AccentBrush"];
        RepeatIconPath.Stroke = brush;
        RepeatArrowHead.Fill = brush;
        RepeatOneBadge.Foreground = brush;
        RepeatOneBadge.Visibility = _repeatMode == RepeatMode.One ? Visibility.Visible : Visibility.Collapsed;
        SyncCompactPlayer();
    }

    // One rule for the Next button and auto-advance: the following queue entry, wrapping to the
    // first only with RepeatMode.All. Null = nothing next.
    private QueueEntry? GetNextEntry()
    {
        var index = QueueIndex;
        if (index < 0)
            return null;
        if (index + 1 < _queue.Count)
            return _queue[index + 1];
        return _repeatMode == RepeatMode.All ? _queue[0] : null;
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => PlayPrevious();

    // Like other players (#6): a few seconds into a track, Back restarts it; right at the start
    // it goes to the previous queue entry. On the first entry it always restarts.
    private void PlayPrevious()
    {
        // The jam has no "previous" - its timeline only moves forward.
        if (JamOwnsPlayback)
            return;
        if (_current == null)
            return;

        var index = QueueIndex;
        var loadedAndRunning = _loadedEntry == _current && _endedEntry != _loadedEntry;
        if (loadedAndRunning && (_player.PlaybackSession.Position > TimeSpan.FromSeconds(3) || index <= 0))
        {
            _player.PlaybackSession.Position = TimeSpan.Zero;
            ResetStallWatch();
            return;
        }

        PlayEntry(index > 0 ? _queue[index - 1] : _current);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e) => PlayNext();

    private void PlayNext()
    {
        if (JamOwnsPlayback)
        {
            JamSkip();
            return;
        }
        if (GetNextEntry() is { } next)
            PlayEntry(next);
    }

    // Natural completion only (not Stop/skip) scrobbles - skipping shouldn't inflate play counts.
    private void OnMediaEnded()
    {
        if (JamOwnsPlayback)
        {
            OnJamMediaEnded();
            return;
        }
        // The player's MediaEnded and the stall watchdog can both report the same ending.
        if (_loadedEntry == null || _endedEntry == _loadedEntry)
            return;
        _endedEntry = _loadedEntry;

        // Not awaited: waiting on the server here used to hold up starting the next track (#11).
        _ = ScrobbleQuietlyAsync(_loadedEntry.Song.Id);

        if (HandleSleepAtSongEnd())
            return;

        if (_repeatMode == RepeatMode.One && _current != null)
        {
            PlayEntry(_current);
            return;
        }

        if (GetNextEntry() is { } next)
            PlayEntry(next);
        else
            SetPlaying(false);
    }

    private async Task ScrobbleQuietlyAsync(string songId)
    {
        try { await _client.ScrobbleAsync(songId); }
        catch { /* best-effort */ }
    }

    // Needs the window to exist (Windows ties the flyout to its handle), so it's tried on Loaded
    // and again after login.
    private void EnsureMediaControls()
    {
        if (_osMediaControls != null)
            return;

#if !ANDROID
        // Discord status (desktop), fed by the same updates; on/off from Settings.
        _discord ??= new DiscordMediaControls(Log) { Enabled = _prefs.DiscordPresence };
#endif
        _osMediaControls = MediaControls.Create();
        Log($"media controls: {_osMediaControls?.GetType().Name ?? "not available yet"}");
        _mediaControls = new MediaControlsGroup(_osMediaControls, _discord);
        if (_osMediaControls != null)
        {
            _osMediaControls.CommandReceived += command => DispatcherQueue.TryEnqueue(() => HandleMediaCommand(command));
#if ANDROID
            ConnectAndroidAuto();
#endif
        }
        if (_current != null)
        {
            SetCurrent(_current);
            SetPlaying(_isPlaying);
        }
    }

    private void HandleMediaCommand(MediaCommand command)
    {
        switch (command)
        {
            case MediaCommand.Play: Play(); break;
            case MediaCommand.Pause: Pause(); break;
            case MediaCommand.PlayPause: TogglePlayPause(); break;
            case MediaCommand.Stop: Stop(); break;
            case MediaCommand.Next: PlayNext(); break;
            case MediaCommand.Previous: PlayPrevious(); break;
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        // While this PC plays the jam, pause/resume is for everyone in it.
        if (JamOwnsPlayback)
        {
            JamTogglePause();
            return;
        }
        if (_current == null)
            return;

        if (_isPlaying)
            Pause();
        else
            Play();
    }

    private void Play()
    {
        if (JamOwnsPlayback)
        {
            JamResume();
            return;
        }
        if (_current == null)
            return;

        // A restored queue (or one whose playing track was removed) has nothing loaded yet;
        // a finished track needs loading again too.
        if (_loadedEntry != _current || _endedEntry == _loadedEntry)
        {
            PlayEntry(_current, _loadedEntry != _current ? _pendingSeek : TimeSpan.Zero);
            return;
        }

        _player.Play();
        ResetStallWatch();
        SetPlaying(true);
    }

    private void Pause()
    {
        FinishCrossfade();
        if (JamOwnsPlayback)
        {
            JamPause();
            return;
        }
        _player.Pause();
        SetPlaying(false);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => Stop();

    private void Stop()
    {
        FinishCrossfade();
        if (JamOwnsPlayback)
        {
            JamPause();
            return;
        }
        _player.Pause();
        _player.PlaybackSession.Position = TimeSpan.Zero;
        SetPlaying(false);
    }

    // #27: the mouse wheel over the volume slider changes the volume by VolumeScrollStep percent
    // per notch. Touchpads send small deltas; they add up to whole notches (120 = one notch).
    private int _volumeWheelRemainder;

    private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(VolumeSlider).Properties;
        e.Handled = true;   // don't scroll the page behind it
        if (props.IsHorizontalMouseWheel)
            return;

        _volumeWheelRemainder += props.MouseWheelDelta;
        var notches = _volumeWheelRemainder / 120;
        if (notches == 0)
            return;
        _volumeWheelRemainder -= notches * 120;

        var step = Math.Clamp(_prefs.VolumeScrollStep, 1, 25) / 100.0;
        // Snap to the step grid, so 5% steps land on 45%, 50%... even from 47%.
        var target = Math.Round((VolumeSlider.Value + notches * step) / step) * step;
        VolumeSlider.Value = Math.Clamp(target, 0, 1);
    }

    private static readonly (StreamQuality Quality, string Label, string Detail)[] StreamQualityChoices =
    {
        (StreamQuality.Original, "Original", "as on the server"),
        (StreamQuality.VeryHigh, "Very high", "MP3 320 kbit/s"),
        (StreamQuality.High, "High", "Opus 160 kbit/s"),
        (StreamQuality.Normal, "Normal", "Opus 96 kbit/s"),
        (StreamQuality.Low, "Low", "Opus 24 kbit/s"),
    };

    // Saved by ChoiceButton afterwards.
    private void SetStreamQuality(StreamQuality quality)
    {
        if (quality == _prefs.StreamQuality)
            return;
        _prefs.StreamQuality = quality;
        _client.StreamQuality = quality;
        // A prefetched next song is in the old quality; fetch it again in the new one.
        _prefetcher.Clear(_loadedEntry?.Song.Id);
        UpdateCacheSizeText();
    }

    private void VolumeStepBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_initialized || double.IsNaN(args.NewValue))
            return;
        _prefs.VolumeScrollStep = (int)Math.Clamp(Math.Round(args.NewValue), 1, 25);
        PreferencesService.Save(_prefs);
    }

    private void CrossfadeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_initialized || double.IsNaN(args.NewValue))
            return;
        _prefs.CrossfadeSeconds = (int)Math.Clamp(Math.Round(args.NewValue), 0, 12);
        PreferencesService.Save(_prefs);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ApplyPlaybackVolume();
        // Not while the XAML default is being applied at startup (that would overwrite the saved volume).
        if (_initialized)
            _prefs.Volume = e.NewValue;
    }

    private void CommitSeek()
    {
        if (!_isUserSeeking)
            return;

        _isUserSeeking = false;
        // The jam's timeline is shared - no seeking; the slider snaps back on the next tick.
        if (JamOwnsPlayback)
            return;
        if (_current != null && _loadedEntry != _current)
        {
            // Restored queue, not loaded yet: remember the spot for when Play is pressed.
            _pendingSeek = TimeSpan.FromSeconds(ProgressSlider.Value);
            ElapsedText.Text = FormatTime(_pendingSeek);
            return;
        }

        if (_player.PlaybackSession.NaturalDuration > TimeSpan.Zero)
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
            ResetStallWatch();
        }
    }

    private static string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

    private int _saveTickCounter;

    private void ProgressTimer_Tick(object? sender, object e)
    {
        var session = _player.PlaybackSession;
        if (session == null)
            return;

        var position = session.Position;
        var duration = session.NaturalDuration;

        SyncWithPlayerState(session, position, duration);
        CheckForStall(position, duration);
        UpdateSleepTimer(position, duration);
        MaybeStartCrossfade(position, duration);
        SyncCompactPlayer();   // #29: progress + state of the phone player (no-op when hidden)
        PrefetchNext();

        // Keep queue.json fresh while playing (~every 10s) so even a crash resumes close by.
        if (++_saveTickCounter >= 25)
        {
            _saveTickCounter = 0;
            if (_isPlaying)
                SaveQueueState(background: true);
        }

        UpdateJamProgress();
        if (JamOwnsPlayback)
        {
            ShowJamPlaybackPosition(position, duration);
            return;
        }

        if (duration <= TimeSpan.Zero || _loadedEntry == null || _loadedEntry != _current)
            return;

        UpdateSideLyrics(position);
        ShowPlaybackPosition(position, duration);
    }

    private void ShowPlaybackPosition(TimeSpan position, TimeSpan duration)
    {
        _mediaControls?.SetPosition(position, duration);
        if (!_isUserSeeking)
        {
            ProgressSlider.Maximum = duration.TotalSeconds;
            ProgressSlider.Value = position.TotalSeconds;
        }
        ElapsedText.Text = FormatTime(position);
        DurationText.Text = FormatTime(duration);
    }

    // Starts buffering whatever will play next (follows queue edits, shuffle, repeat).
    /// <summary>Window closing: the prebuffered tracks are deleted (not left until the next launch).</summary>
    public void ClearCacheOnExit()
    {
        FinishCrossfade();
        _prefetcher.Dispose();
        try { _player.Source = null; } catch { /* closing anyway */ }   // let go of a cached file
        TrackPrefetcher.DeleteCacheOnExit();
    }

    private void PrefetchNext()
    {
        // Not during a crossfade: that would delete the fading-out song's file under its player.
        if (JamOwnsPlayback || _loadedEntry == null || !_isPlaying || !_prefs.PrefetchNextTrack || _fadingOut != null)
            return;

        if (GetNextEntry() is { } next && next.Song.Id != _loadedEntry.Song.Id)
            _prefetcher.Prefetch(next.Song.Id, keepSongId: _loadedEntry.Song.Id);
    }

    // ----- Keeping the icon in sync with the player (#21, #18) -----
    // The play/pause icon and _isPlaying used to follow only our own Play/Pause calls; the
    // player's real state was just logged. When the player changed state by itself (media keys
    // or the OS while the window wasn't focused, or reaching the end without raising
    // MediaEnded) the icon showed "paused" while it played (#21), or playback stopped at the end
    // of a track instead of moving on (#18). Checked on every progress tick.
    private int _pausedTicks;
    private bool _showingPlaybackError;

    private void SyncWithPlayerState(MediaPlaybackSession session, TimeSpan position, TimeSpan duration)
    {
        if (JamOwnsPlayback || _current == null || _loadedEntry != _current || _endedEntry == _loadedEntry)
        {
            _pausedTicks = 0;
            return;
        }
        var state = session.PlaybackState;
        if (state == MediaPlaybackState.Playing)
        {
            _pausedTicks = 0;
            if (_showingPlaybackError)
            {
                Log("player recovered after a reported failure - clearing the error");
                _showingPlaybackError = false;
                NowPlayingSubText.Text = _current.Song.ArtistName;
            }
            if (!_isPlaying)
            {
                Log("player is playing while we showed paused - syncing the icon");
                SetPlaying(true);
            }
            return;
        }
        if (!_isPlaying || state is MediaPlaybackState.Opening or MediaPlaybackState.Buffering)
        {
            _pausedTicks = 0;
            return;
        }
        // We think it's playing, but the player is paused/stopped.
        if (duration > TimeSpan.Zero && position >= duration - TimeSpan.FromSeconds(1))
        {
            Log("track reached its end without MediaEnded - moving on");
            OnMediaEnded();
        }
        else if (++_pausedTicks >= 3)   // ~1.2s: not just the moment between Play() and playing
        {
            _pausedTicks = 0;
            Log($"player paused by itself at {position} - syncing the icon");
            SetPlaying(false);
        }
    }

    // ----- Stall watchdog (#11) -----
    // Now and then a track never starts (stuck opening/buffering the server stream) or never
    // reports its end, and only Stop + Play got it going again. If playback should be running
    // but the position hasn't moved for StallTimeout, do that automatically: at the very end of
    // the track treat it as finished, otherwise reload it at the same spot (twice, then skip).
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);
    private TimeSpan _stallLastPosition;
    private long _stallSinceTicks;
    private int _stallRecoveries;
    private QueueEntry? _endedEntry;

    private void ResetStallWatch()
    {
        _stallLastPosition = TimeSpan.MinValue;
        _stallSinceTicks = Environment.TickCount64;
    }

    private void CheckForStall(TimeSpan position, TimeSpan duration)
    {
        if (JamOwnsPlayback || !_isPlaying || _isUserSeeking || _current == null || _loadedEntry != _current || _endedEntry == _loadedEntry)
        {
            ResetStallWatch();
            return;
        }

        if (position != _stallLastPosition)
        {
            _stallLastPosition = position;
            _stallSinceTicks = Environment.TickCount64;
            return;
        }

        if (Environment.TickCount64 - _stallSinceTicks < StallTimeout.TotalMilliseconds)
            return;

        ResetStallWatch();
        if (duration > TimeSpan.Zero && position >= duration - TimeSpan.FromSeconds(2))
        {
            Log("stalled at the end of the track - treating it as finished");
            OnMediaEnded();
        }
        else if (_stallRecoveries < 2)
        {
            _stallRecoveries++;
            Log($"playback stalled at {position} - reloading (attempt {_stallRecoveries})");
            PlayEntry(_current, position);
        }
        else
        {
            Log("playback keeps stalling - skipping to the next track");
            if (GetNextEntry() is { } next)
                PlayEntry(next);
            else
                Pause();
        }
    }

    // ----- Queue persistence (#12) -----

    // Also saves preferences (the volume changes without a Settings click).
    // background: the periodic save while playing; the JSON + disk write happen off the UI thread.
    public void SaveQueueState(bool background = false)
    {
        if (background)
            PreferencesService.SaveInBackground(_prefs);
        else
            PreferencesService.Save(_prefs);
        if (!_signedIn)
            return;

        try
        {
            var entries = _queue.ToList();
            var indexOf = entries.Select((q, i) => (q, i)).ToDictionary(p => p.q, p => p.i);
            var position = _loadedEntry != null && _loadedEntry == _current && _endedEntry != _loadedEntry
                ? _player.PlaybackSession.Position
                : _pendingSeek;

            var state = new QueueState
            {
                ServerUrl = _client.ServerUrl,
                Username = _client.Username,
                Songs = entries.Select(q => q.Song).ToList(),
                CurrentIndex = QueueIndex,
                PositionSeconds = position.TotalSeconds,
                Shuffled = _isShuffled,
                RepeatMode = (int)_repeatMode,
                UnshuffledOrder = _unshuffledOrder?.Where(indexOf.ContainsKey).Select(q => indexOf[q]).ToList(),
            };
            if (background)
                QueueStateService.SaveInBackground(state, ex => Log($"saving the queue failed: {ex.Message}"));
            else
                QueueStateService.Save(state);
        }
        catch (Exception ex)
        {
            Log($"saving the queue failed: {ex.Message}");
        }
    }

    // After login: put back the queue from last time, paused at the same spot. Nothing is
    // loaded until Play (so the app doesn't start making noise on launch).
    private void RestoreQueueState()
    {
        if (!_prefs.ResumeQueueOnStartup)
            return;
        var state = QueueStateService.Load();
        if (_current != null || state == null || state.Songs.Count == 0
            || !string.Equals(state.ServerUrl, _client.ServerUrl, StringComparison.OrdinalIgnoreCase)
            || state.Username != _client.Username)
            return;

        PopulateSongArt(state.Songs);
        var entries = state.Songs.Select(s => new QueueEntry(s)).ToList();
        ReplaceQueue(entries);

        _isShuffled = state.Shuffled;
        UpdateShuffleIcon();
        _unshuffledOrder = _isShuffled && state.UnshuffledOrder != null
            ? state.UnshuffledOrder.Where(i => i >= 0 && i < entries.Count).Distinct().Select(i => entries[i]).ToList()
            : null;
        _repeatMode = Enum.IsDefined(typeof(RepeatMode), state.RepeatMode) ? (RepeatMode)state.RepeatMode : RepeatMode.Off;
        UpdateRepeatIcon();

        var current = entries[Math.Clamp(state.CurrentIndex, 0, entries.Count - 1)];
        SetCurrent(current);
        SetPlaying(false);

        var duration = TimeSpan.FromSeconds(current.Song.DurationSeconds);
        _pendingSeek = TimeSpan.FromSeconds(Math.Clamp(state.PositionSeconds, 0, Math.Max(0, duration.TotalSeconds - 1)));
        ProgressSlider.Maximum = Math.Max(1, duration.TotalSeconds);
        ProgressSlider.Value = _pendingSeek.TotalSeconds;
        ElapsedText.Text = FormatTime(_pendingSeek);
        DurationText.Text = FormatTime(duration);
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
