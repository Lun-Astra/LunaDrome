using LunaDrome.Controls;
using LunaDrome.Core.Models;
using LunaDrome.Core.Services;

namespace LunaDrome;

// Cassette player (Settings > Appearance, phones): with it on, a cassette button in the phone's
// now-playing screen switches between the normal player and Controls/CassetteDeck; the deck's ⇄
// switches back. The choice is remembered. The deck copies its state from the playback bar like
// the phone player does (SyncCompactPlayer), and its keys call the same playback methods.
public sealed partial class MainPage
{
    private CassetteDeck? _cassette;

    /// <summary>Phones only (the setting is only on phones); in the phone layout, or the wider
    /// one an unfolded foldable gets.</summary>
    private bool CassetteAvailable => _prefs.CassettePlayer && (IsCompact || OperatingSystem.IsAndroid() || OperatingSystem.IsIOS());
    private bool CassetteShown => CassetteAvailable && _prefs.CassetteMode;

    private void InitCassettePlayer()
    {
        CompactCassetteButton.Visibility = _prefs.CassettePlayer ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Built the first time it's needed, on top of the full-screen player.</summary>
    private CassetteDeck Cassette()
    {
        if (_cassette != null)
            return _cassette;
        var deck = _cassette = new CassetteDeck { Visibility = Visibility.Collapsed };
        deck.PlayPauseRequested += TogglePlayPause;
        deck.NextRequested += () => { if (!JamOwnsPlayback) PlayNext(); };
        deck.PreviousRequested += () => { if (!JamOwnsPlayback) PlayPrevious(); };
        deck.StopRequested += () => { if (_isPlaying) TogglePlayPause(); };   // STOP pauses; the cassette ejects
        deck.SeekByRequested += CassetteSeekBy;
        deck.VolumeChanged += volume => VolumeSlider.Value = volume;
        deck.FavoriteRequested += () =>
        {
            if (_current?.Song is { } song)
                _ = ToggleNowPlayingFavoriteAsync(song);
        };
        deck.CloseRequested += () => CloseFullScreenPlayer_Click(deck, new RoutedEventArgs());
        deck.ExitRequested += () => SetCassetteMode(false);
        deck.TapeChosen += playlist => _ = PlayTapeAsync(playlist);
        FullScreenPlayerOverlay.Children.Add(deck);
        return deck;
    }

    private void CassettePlayerCheck_Click(object sender, RoutedEventArgs e)
    {
        _prefs.CassettePlayer = CassettePlayerCheck.IsChecked == true;
        _prefs.CassetteMode = true;   // turning it on = you want to see it
        PreferencesService.Save(_prefs);
        InitCassettePlayer();
        UpdateCompactPlayerMode();
    }

    private void CompactCassetteButton_Click(object sender, RoutedEventArgs e) => SetCassetteMode(true);

    private void SetCassetteMode(bool on)
    {
        _prefs.CassetteMode = on;
        PreferencesService.Save(_prefs);
        UpdateCompactPlayerMode();
    }

    private void ShowCassetteDeck(bool show)
    {
        if (!show && _cassette == null)
            return;
        var deck = Cassette();
        deck.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        deck.SetActive(show);
        WatchHinge(show);
        if (show)
        {
            deck.SetTapes(_playlists);
            SyncCassetteDeck();
        }
    }

    /// <summary>Copies the playback bar's state into the deck (cheap; every progress tick).</summary>
    private void SyncCassetteDeck()
    {
        if (_cassette is not { Visibility: Visibility.Visible } deck)
            return;
        var song = _current?.Song;
        var playing = song != null || JamOwnsPlayback;
        deck.SetSong(song?.Id,
                     playing ? NowPlayingText.Text : "No tape",
                     song?.ArtistName is { Length: > 0 } artist ? artist : playing ? NowPlayingSubText.Text : "Pick one from the rack",
                     song?.AlbumName,
                     playing ? FullScreenArt.Background ?? NowPlayingArt.Background : null);
        deck.SetState(PauseIcon.Visibility == Visibility.Visible, ProgressSlider.Value, ProgressSlider.Maximum);
        deck.SetFavorite(NowPlayingFavorite.Visibility == Visibility.Visible ? song?.IsFavorited : null);
        deck.SetVolume(VolumeSlider.Value);
    }

    /// <summary>Holding FF / REW winds through the song a few seconds per step.</summary>
    private void CassetteSeekBy(double seconds)
    {
        if (_current == null || JamOwnsPlayback || ProgressSlider.Maximum <= 1)
            return;
        ProgressSlider.Value = Math.Clamp(ProgressSlider.Value + seconds, 0, Math.Max(0, ProgressSlider.Maximum - 1));
        _isUserSeeking = true;
        CommitSeek();
        SyncCassetteDeck();
    }

    /// <summary>A tape from the rack: plays the playlist from the start.</summary>
    private async Task PlayTapeAsync(Playlist playlist)
    {
        try
        {
            var songs = await _client.GetPlaylistSongsAsync(playlist.Id);
            if (songs.Count == 0)
                return;
            PopulateSongArt(songs);
            PlayQueueFrom(songs, 0);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Failed to load playlist: {ex.Message}");
        }
    }

#if ANDROID
    // A foldable standing half-open: the hinge angle sensor (Android 11+). The deck uses it, with
    // a landscape window, for the tabletop layout. Only listened to while the deck is shown.
    private HingeListener? _hinge;

    private void WatchHinge(bool watch)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return;
        var sensors = Android.App.Application.Context.GetSystemService(Android.Content.Context.SensorService) as Android.Hardware.SensorManager;
        if (sensors == null)
            return;
        if (watch && _hinge == null)
        {
            var sensor = sensors.GetDefaultSensor(Android.Hardware.SensorType.HingeAngle);
            if (sensor == null)
                return;   // not a foldable
            _hinge = new HingeListener(angle => DispatcherQueue.TryEnqueue(() =>
            {
                if (_cassette != null)
                    _cassette.HalfOpen = angle is > 30 and < 150;
            }));
            sensors.RegisterListener(_hinge, sensor, Android.Hardware.SensorDelay.Ui);
        }
        else if (!watch && _hinge != null)
        {
            sensors.UnregisterListener(_hinge);
            _hinge.Dispose();
            _hinge = null;
            if (_cassette != null)
                _cassette.HalfOpen = false;
        }
    }

    private sealed class HingeListener(Action<float> changed) : Java.Lang.Object, Android.Hardware.ISensorEventListener
    {
        public void OnAccuracyChanged(Android.Hardware.Sensor? sensor, Android.Hardware.SensorStatus accuracy) { }

        public void OnSensorChanged(Android.Hardware.SensorEvent? e)
        {
            if (e?.Values is { Count: > 0 } values)
                changed(values[0]);
        }
    }
#else
    private void WatchHinge(bool watch) { }
#endif
}
