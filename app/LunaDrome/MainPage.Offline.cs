using LunaDrome.Controls;
using LunaDrome.Core.Models;
using LunaDrome.Core.Services;
using Microsoft.UI.Xaml.Input;

namespace LunaDrome;

// #28 offline downloads: songs, albums and playlists kept on this device (OfflineLibrary), in
// their own quality. Playback uses the file when there is one. The Downloaded page lists them,
// and when the server can't be reached at sign-in, "Play downloaded songs" opens the app with
// only that page (offline mode) until Reconnect works.
public sealed partial class MainPage
{
    private OfflineLibrary _offline = null!;
    private bool _offlineMode;
    private bool _offlineRefreshQueued;
    private int _offlineShownCount = -1;

    private void InitOffline()
    {
        // LocalApplicationData, not the roaming folder the settings use: these can be gigabytes.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LunaDrome-Uno", "offline");
        _offline = new OfflineLibrary(root, _client) { Quality = _prefs.DownloadQuality };
        // Progress comes many times a second from the download thread: one refresh per frame at most.
        _offline.Changed += () =>
        {
            if (_offlineRefreshQueued)
                return;
            _offlineRefreshQueued = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _offlineRefreshQueued = false;
                if (OfflinePage.Visibility == Visibility.Visible)
                    RefreshOfflinePage(listToo: _offline.Songs().Count != _offlineShownCount);
                if (SettingsPage.Visibility == Visibility.Visible)
                    UpdateOfflineSizeText();
            });
        };
    }

    /// <summary>Signed in (online): finish what's waiting and bring downloaded playlists up to date.</summary>
    private void OnlineForOffline()
    {
        SetOfflineMode(false);
        _offline.Resume();
        _ = _offline.SyncAsync(id => _client.GetPlaylistSongsAsync(id));
    }

    /// <summary>The file to play for this song, if it's downloaded.</summary>
    private string? OfflineFile(string songId) => _offline.TryGetFile(songId);

    /// <summary>The downloaded cover of the song's album as a file URI (offline mode has no
    /// server to ask), or null.</summary>
    private string? OfflineCover(Song song) =>
        _offline.CoverFile(song.AlbumId) is { } path ? new Uri(path).AbsoluteUri : null;

    // ================= Menus =================

    private void AddDownloadMenuItem(MenuFlyout flyout, List<Song> songs)
    {
        if (songs.Count == 0)
            return;
        if (songs.All(s => _offline.IsDownloaded(s.Id) || _offline.IsQueued(s.Id)))
        {
            AddMenuItem(flyout, songs.Count == 1 ? "Remove download" : $"Remove {songs.Count} downloads", () =>
            {
                _offline.RemoveSongs(songs.Select(s => s.Id));
                ShowToast(songs.Count == 1 ? $"Removed the download of {songs[0].Title}" : $"Removed {songs.Count} downloads");
            });
            return;
        }
        AddMenuItem(flyout, "Download for offline", () =>
        {
            _offline.DownloadSongs(songs);
            ShowToast(songs.Count == 1 ? $"Downloading {songs[0].Title}" : $"Downloading {songs.Count} songs",
                "Show", () => NavDownloaded.IsChecked = true);
        });
    }

    private void AddCollectionDownloadMenuItem(MenuFlyout flyout, string id, string kind, string name, string subtitle,
        Func<Task<List<Song>?>> getSongs)
    {
        if (_offline.HasCollection(id))
        {
            AddMenuItem(flyout, "Remove download", () =>
            {
                _offline.RemoveCollection(id);
                ShowToast($"Removed the download of {name}");
            });
            return;
        }
        AddMenuItem(flyout, kind == "album" ? "Download album" : "Download playlist", async () =>
        {
            if (await getSongs() is not { Count: > 0 } songs)
                return;
            _offline.DownloadCollection(new OfflineCollection { Id = id, Kind = kind, Name = name, Subtitle = subtitle }, songs);
            ShowToast($"Downloading {name} ({songs.Count} songs)" + (kind == "playlist" ? " - it stays in sync with the playlist" : ""),
                "Show", () => NavDownloaded.IsChecked = true);
        });
    }

    // ================= The Downloaded page =================

    private void ShowOfflinePage()
    {
        ShowPage(OfflinePage, () => Reselect(NavDownloaded));
        RefreshOfflinePage(listToo: true);
    }

    private void RefreshOfflinePage(bool listToo)
    {
        var stored = _offline.Songs();
        if (listToo)
        {
            _offlineShownCount = stored.Count;
            var songs = stored.Select(o =>
            {
                var song = o.Song.Clone();
                song.CoverArtUrl = OfflineCover(song);
                return song;
            }).ToList();
            OfflineSongsList.ItemsSource = songs;
            BuildOfflineCollections(songs);
        }

        OfflinePlayButton.IsEnabled = OfflineShuffleButton.IsEnabled = stored.Count > 0;
        OfflineReconnectButton.Visibility = _offlineMode ? Visibility.Visible : Visibility.Collapsed;

        var parts = new List<string>();
        if (_offlineMode)
            parts.Add("Offline - the server can't be reached, so only downloaded songs play.");
        parts.Add(stored.Count == 0
            ? "Nothing downloaded yet. Right-click (on a phone: hold) a song, album or playlist and pick Download."
            : $"{stored.Count} song{(stored.Count == 1 ? "" : "s")} · {FormatBytes(_offline.SizeBytes())} on this device.");
        if (_offline.Downloading is { } now)
            parts.Add($"Downloading {now.Title} ({FormatBytes(_offline.DownloadingBytes)}) - {_offline.PendingCount} to go.");
        else if (_offline.PendingCount > 0)
            parts.Add(_offlineMode
                ? $"{_offline.PendingCount} more waiting for the connection."
                : $"{_offline.PendingCount} more waiting" + (_offline.LastError is { } error ? $" - the last one failed: {error}" : "."));
        OfflineStatusText.Text = string.Join(" ", parts);
    }

    // A card per downloaded album/playlist: tap plays it, ✕ removes it.
    private void BuildOfflineCollections(List<Song> songs)
    {
        OfflineCollectionsPanel.Children.Clear();
        var byId = songs.ToDictionary(s => s.Id);
        var collections = _offline.Collections();
        OfflineCollectionsScroller.Visibility = collections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var collection in collections)
        {
            var have = collection.SongIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            var art = new Border { Width = 120, Height = 120, CornerRadius = new CornerRadius(6), Background = Res("RaisedBrush") };
            if (_offline.CoverFile(collection.CoverAlbumId) is { } cover)
                SetImage(art, new Uri(cover).AbsoluteUri);
            var remove = new Button
            {
                Width = 28, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(14),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0, 0, 0)), BorderThickness = new Thickness(0),
                Content = new Microsoft.UI.Xaml.Shapes.Path
                {
                    Width = 9, Height = 9, Stretch = Stretch.Uniform, Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 1.6,
                    Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), "M1,1 L11,11 M11,1 L1,11"),
                },
            };
            ToolTipService.SetToolTip(remove, "Remove the download");
            remove.Click += (_, _) =>
            {
                _offline.RemoveCollection(collection.Id);
                RefreshOfflinePage(listToo: true);
            };
            var artBox = new Grid();
            artBox.Children.Add(art);
            artBox.Children.Add(remove);

            var total = collection.SongIds.Count;
            var panel = new StackPanel { Width = 120, Spacing = 2 };
            panel.Children.Add(artBox);
            panel.Children.Add(new TextBlock { Text = collection.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new TextBlock
            {
                Text = (collection.Kind == "album" ? "Album" : "Playlist") + (have.Count < total ? $" · {have.Count} of {total}" : $" · {total} songs"),
                FontSize = 11, Foreground = Res("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var card = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = panel };
            Motion.SetHandCursor(card);
            card.Tapped += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject source && IsInside(source, remove))
                    return;
                e.Handled = true;
                if (have.Count > 0)
                    PlayQueueFrom(have, 0);
            };
            OfflineCollectionsPanel.Children.Add(card);
        }
    }

    private void OfflinePlay_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineSongsList.ItemsSource is List<Song> { Count: > 0 } songs)
            PlayQueueFrom(songs, 0);
    }

    private void OfflineShuffle_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineSongsList.ItemsSource is List<Song> { Count: > 0 } songs)
            PlayQueueFrom(songs.OrderBy(_ => _random.Next()).ToList(), 0);
    }

    private void OfflineRemove_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: Song song })
        {
            _offline.RemoveSongs(new[] { song.Id });
            RefreshOfflinePage(listToo: true);
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    // ================= Offline mode =================

    /// <summary>Login failed: with songs on this device, offer to play those without the server.</summary>
    private void OfferOfflineMode() =>
        OfflineModeButton.Visibility = _offline.Songs().Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OfflineMode_Click(object sender, RoutedEventArgs e)
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;
        SetOfflineMode(true);
        EnsureMediaControls();
        NavDownloaded.IsChecked = true;
    }

    private async void OfflineReconnect_Click(object sender, RoutedEventArgs e)
    {
        OfflineReconnectButton.IsEnabled = false;
        OfflineStatusText.Text = "Connecting…";
        var (success, error) = await _client.LoginAsync();
        OfflineReconnectButton.IsEnabled = true;
        if (!success)
        {
            RefreshOfflinePage(listToo: false);
            ShowToast($"Still no connection: {error}");
            return;
        }
        // The normal sign-in path from here (it also leaves offline mode).
        await TryLoginAsync();
    }

    // Offline: only the Downloaded page (the rest needs the server). The phone's tabs go there too.
    private void SetOfflineMode(bool offline)
    {
        _offlineMode = offline;
        var online = offline ? Visibility.Collapsed : Visibility.Visible;
        NavHome.Visibility = NavArtists.Visibility = NavAlbums.Visibility = NavSongs.Visibility =
            NavGenres.Visibility = NavStats.Visibility = PlaylistsHeader.Visibility = PlaylistsList.Visibility = online;
        if (offline)
        {
            NavDiscover.Visibility = NavJam.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateDiscoverNav();
            UpdateJamNav();
        }
        if (OfflinePage.Visibility == Visibility.Visible)
            RefreshOfflinePage(listToo: false);
    }

    // ================= Settings =================

    private void BuildDownloadQualityChoices()
    {
        DownloadQualityOptions.Children.Clear();
        StackPanel? row = null;
        foreach (var (quality, label, detail) in StreamQualityChoices)
        {
            if (row == null || row.Children.Count >= (IsCompact ? 3 : int.MaxValue))
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                DownloadQualityOptions.Children.Add(row);
            }
            var selected = _prefs.DownloadQuality == quality;
            var chip = new Border
            {
                Padding = new Thickness(14, 6, 14, 6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = Res(selected ? "AccentBrush" : "BorderBrush2"),
                Background = Res("RaisedBrush"),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = label },
                        new TextBlock { Text = detail, FontSize = 11, Foreground = Res("TextSecondaryBrush") },
                    },
                },
            };
            row.Children.Add(ChoiceButton(chip, $"Download {label} ({detail})", () =>
            {
                _prefs.DownloadQuality = quality;
                _offline.Quality = quality;
            }));
        }
        UpdateOfflineSizeText();
    }

    private void UpdateOfflineSizeText()
    {
        var count = _offline.Songs().Count;
        OfflineSizeText.Text = count == 0
            ? "Nothing downloaded"
            : $"Downloaded: {count} song{(count == 1 ? "" : "s")}, {FormatBytes(_offline.SizeBytes())}";
        OfflineRemoveAllButton.Visibility = count > 0 || _offline.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OfflineRemoveAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "Remove all downloads?",
            Content = $"{_offline.Songs().Count} songs ({FormatBytes(_offline.SizeBytes())}) are deleted from this device. They stay on your server.",
            PrimaryButtonText = "Remove all",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        try
        {
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;
        }
        catch { return; }
        _offline.RemoveAll();
        UpdateOfflineSizeText();
    }
}
