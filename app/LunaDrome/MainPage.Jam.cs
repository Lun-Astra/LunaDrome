using LunaDrome.Controls;
using LunaDrome.Core.Models;
using LunaDrome.Core.Services;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Point = Windows.Foundation.Point;

namespace LunaDrome;

// JamiDrome: Luna's shared "jam" server (next to SpotiDrome). The jam - queue, Auto DJ,
// downloads - lives on the server; this PC can be its one speaker ("Play here"), in which
// case the player follows the server's timeline instead of the local queue: play/pause/next
// act on the jam for everyone, and the local queue waits (paused) until the jam is left.
// Friends join from a phone browser through an invite link (Share link).
public sealed partial class MainPage
{
    private JamSession _jam = null!;
    // UI-thread mirror of "this PC is the jam's speaker and _player belongs to the jam".
    private bool _jamOwnsPlayer;
    // The jam track loaded in _player, and whether it has reported its end already.
    private string? _jamLoadedItemId;
    private string? _jamEndedItemId;
    private bool _jamMediaOpened;
    // Where to seek once the jam track opens (joining mid-song). Separate from _pendingSeek,
    // which keeps the local queue's position for when the jam is left.
    private TimeSpan _jamPendingSeek;
    private string _jamQueueSignature = "";
    private string _jamHistorySignature = "";
    private string? _jamArtShown;
    private List<JamSearchResult> _jamSearchResults = new();

    // #31: starting to play an empty jam takes the local queue along (the playing song at its
    // position + what's next). These jam items stay linked to their local queue entries: removing
    // one on either side removes the other, and leaving the jam continues from where it got.
    private const int MaxJamTakeover = 25;
    private readonly Dictionary<string, QueueEntry> _jamTakenOver = new();
    // Taken-over items seen in a state from the server: until then a state can predate them.
    private readonly HashSet<string> _jamTakenOverSeen = new();

    private bool JamOwnsPlayback => _jamOwnsPlayer;

    private void InitJam()
    {
        if (string.IsNullOrEmpty(_prefs.JamDeviceId))
        {
            _prefs.JamDeviceId = Guid.NewGuid().ToString("N");
            PreferencesService.Save(_prefs);
        }
        _jam = new JamSession(_prefs.JamDeviceId, $"LunaDrome on {Environment.MachineName}");
        _jam.StateChanged += () => DispatcherQueue.TryEnqueue(OnJamStateChanged);
        _jam.SpeakerLost += speaker => DispatcherQueue.TryEnqueue(() => OnJamSpeakerLost(speaker));
        _jam.AuthLost += () => DispatcherQueue.TryEnqueue(OnJamAuthLost);
        _jam.ReactionsReceived += emojis => DispatcherQueue.TryEnqueue(() => ShowJamReactions(emojis));
        _jam.Client.Configure(_prefs.JamiDromeUrl, CredentialProtector.Unprotect(_prefs.JamiDromeApiKey));
        UpdateJamNav();
    }

    private void UpdateJamNav()
    {
        NavJam.Visibility = _jam.Client.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        NavJamLiveDot.Visibility = JamOwnsPlayback ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowJamPage()
    {
        ShowPage(JamPage, ShowJamPage);
        _jam.SetWatching(true);
        RenderJamPage();
        _jam.PollSoon();
    }

    /// <summary>Window closing: hand the jam back (it pauses right away instead of in ~20s).</summary>
    public void ReleaseJamOnExit()
    {
        Log($"jam: window closing (speaker: {JamOwnsPlayback})");
        if (!JamOwnsPlayback)
            return;
        _jamOwnsPlayer = false;
        try
        {
            // Blocks closing for at most ~3s (a fresh TLS connection to the server can take a moment).
            var released = Task.Run(() => _jam.ReleaseSpeakerAsync(TimeSpan.FromSeconds(3))).Wait(3500);
            Log($"jam: released on exit: {released}");
        }
        catch (Exception ex) { Log($"jam: release on exit failed: {ex.Message}"); }
        _jam.Dispose();
    }

    // ================= State from the server =================

    private void OnJamStateChanged()
    {
        FollowTakenOverQueue();
        if (JamOwnsPlayback)
            SyncJamPlayback();
        if (JamPage.Visibility == Visibility.Visible)
            RenderJamPage();
    }

    private void OnJamSpeakerLost(JamSpeaker? speaker)
    {
        if (!JamOwnsPlayback)
            return;
        LeaveJamPlayback(release: false);
        ShowToast($"The jam moved to {speaker?.Name ?? "another device"}.", "Play here", () => _ = JamPlayHereAsync());
    }

    private void OnJamAuthLost()
    {
        if (JamOwnsPlayback)
            LeaveJamPlayback(release: false);
        _jam.Client.Configure(_prefs.JamiDromeUrl, "");
        _jam.SetWatching(false);
        // Keys don't expire, so a 401 means it was revoked - forget it.
        _prefs.JamiDromeApiKey = "";
        PreferencesService.Save(_prefs);
        UpdateJamNav();
        JamiDromeTestText.Text = "JamiDrome rejected the API key (revoked?) - paste a new one and press Connect.";
        if (JamPage.Visibility == Visibility.Visible)
            NavHome.IsChecked = true;
        ShowToast("JamiDrome rejected the API key. Set a new one in Settings.");
    }

    // ================= Reactions (#34) =================

    // The guests' emoji reactions float up like on the JamiDrome player page: over the jam's cover
    // on the Jam page, otherwise over the now-playing cover while this device plays the jam.
    private void ShowJamReactions(IReadOnlyList<string> emojis)
    {
        FrameworkElement? anchor = JamPage.Visibility == Visibility.Visible ? JamArt
            : JamOwnsPlayback ? NowPlayingArtBox : null;
        if (anchor == null || anchor.ActualWidth <= 0)
            return;
        Point origin;
        try { origin = anchor.TransformToVisual(ReactionLayer).TransformPoint(new Point(0, 0)); }
        catch { return; }
        for (var i = 0; i < Math.Min(emojis.Count, 8); i++)
            SpawnReaction(emojis[i], origin, anchor, delay: TimeSpan.FromMilliseconds(i * 180));
    }

    // JamiDrome's reactions are drawn from Assets/Reactions (colour on every platform); anything
    // else falls back to text.
    private static readonly HashSet<string> ReactionImages = new() { "1f525", "2764", "1f602", "1f44f", "1f389", "1f483" };

    private static FrameworkElement ReactionVisual(string emoji, double size)
    {
        var code = string.Join("_", emoji.EnumerateRunes().Where(r => r.Value != 0xFE0F).Select(r => r.Value.ToString("x")));
        if (ReactionImages.Contains(code))
            return new Image { Width = size, Height = size,
                Source = new BitmapImage(new Uri($"ms-appx:///Assets/Reactions/emoji_u{code}.png")) };
        return new TextBlock { Text = emoji, FontSize = size };
    }

    private void SpawnReaction(string emoji, Point origin, FrameworkElement anchor, TimeSpan delay)
    {
        var size = anchor.ActualWidth < 100 ? 22.0 : 34.0;
        var transform = new TranslateTransform();
        var text = ReactionVisual(emoji, size);
        text.Opacity = 0;
        text.RenderTransform = transform;
        Canvas.SetLeft(text, origin.X + Random.Shared.NextDouble() * Math.Max(0, anchor.ActualWidth - size));
        Canvas.SetTop(text, origin.Y + anchor.ActualHeight - size);
        ReactionLayer.Children.Add(text);

        var duration = TimeSpan.FromSeconds(2.6);
        var rise = Math.Max(120, anchor.ActualHeight * 1.2);
        var storyboard = new Storyboard { BeginTime = delay };
        var move = new DoubleAnimation { From = 0, To = -rise, Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(move, transform);
        Storyboard.SetTargetProperty(move, "Y");
        var fade = new DoubleAnimationUsingKeyFrames { Duration = duration };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.25)), Value = 1 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6)), Value = 1 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(duration), Value = 0 });
        Storyboard.SetTarget(fade, text);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(move);
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => ReactionLayer.Children.Remove(text);
        storyboard.Begin();
    }

    // ================= Being the speaker =================

    private async Task JamPlayHereAsync()
    {
        try
        {
            await _jam.ClaimSpeakerAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Couldn't start playing the jam here: {ex.Message}");
            return;
        }

        // Hand the player over from the local queue, remembering where it was.
        if (_loadedEntry != null && _loadedEntry == _current && _endedEntry != _loadedEntry)
            _pendingSeek = _player.PlaybackSession.Position;
        await TakeQueueIntoJamAsync();
        FinishCrossfade();
        _player.Pause();
        _loadedEntry = null;
        _jamOwnsPlayer = true;
        _jamLoadedItemId = null;
        _jamEndedItemId = null;
        _mediaControls?.SetCanGoPreviousNext(false, true);
        UpdateJamNav();
        OnJamStateChanged();
    }

    /// <summary>The local queue goes into the jam, up to <see cref="MaxJamTakeover"/> songs, so
    /// guests see it (#33). An empty jam starts with the song that's playing here (at its
    /// position); a jam with songs of its own keeps them and gets what's next here after them.
    /// Requests then play before these.</summary>
    private async Task TakeQueueIntoJamAsync()
    {
        _jamTakenOver.Clear();
        _jamTakenOverSeen.Clear();
        if (_current == null)
            return;
        try
        {
            var state = await _jam.Client.GetStateAsync();
            var jamHasSongs = state.NowPlaying != null || state.Queue.Count > 0;
            var entries = _queue.Skip(jamHasSongs ? QueueIndex + 1 : QueueIndex).Take(MaxJamTakeover).ToList();
            if (entries.Count == 0)
                return;
            var added = await _jam.Client.AddLibraryBatchAsync(entries.Select(q => q.Song.Id).ToList(),
                JamRequesterName, jamHasSongs ? 0 : _pendingSeek.TotalSeconds);
            // Link each jam item to its entry (a song can be in the queue twice; the jam adds it once).
            var unlinked = entries.ToList();
            foreach (var (itemId, songId) in added)
            {
                var entry = unlinked.FirstOrDefault(q => q.Song.Id == songId);
                if (entry == null)
                    continue;
                unlinked.Remove(entry);
                _jamTakenOver[itemId] = entry;
            }
            Log($"jam: took over {added.Count} of {entries.Count} queued songs (jam had songs: {jamHasSongs})");
            var songs = $"{added.Count} song{(added.Count == 1 ? "" : "s")}";
            if (added.Count > 0)
                ShowToast(jamHasSongs ? $"Your next {songs} were added to the jam, after its own." : $"Your queue is now the jam's queue ({songs}).");
            _jam.PollSoon();
        }
        catch (Exception ex)
        {
            Log($"jam: taking over the queue failed: {ex.Message}");
            ShowToast($"Couldn't add your queue to the jam: {ex.Message}");
        }
    }

    // Keeps the local queue in step with the taken-over jam items: one that starts playing becomes
    // the current one (so after the jam the queue continues from there); one that's gone without
    // being played was removed (here, on the JamiDrome dashboard) and leaves the local queue too.
    private void FollowTakenOverQueue()
    {
        var state = _jam.State;
        if (state == null || _jamTakenOver.Count == 0)
            return;
        if (state.NowPlaying != null && _jamTakenOver.TryGetValue(state.NowPlaying.Id, out var playing)
            && playing != _current && _queue.Contains(playing))
        {
            _loadedEntry = null;
            _pendingSeek = TimeSpan.Zero;
            SetCurrent(playing);
        }
        var inJam = state.Queue.Select(i => i.Id).ToHashSet();
        if (state.NowPlaying != null)
            inJam.Add(state.NowPlaying.Id);
        var played = state.History.Select(i => i.Id).ToHashSet();
        var removed = new List<QueueEntry>();
        foreach (var (itemId, entry) in _jamTakenOver.ToList())
        {
            if (inJam.Contains(itemId))
            {
                _jamTakenOverSeen.Add(itemId);
                continue;
            }
            if (!_jamTakenOverSeen.Contains(itemId) && !played.Contains(itemId))
                continue; // this state is older than the takeover
            _jamTakenOver.Remove(itemId);
            if (!played.Contains(itemId) && entry != _current && _queue.Contains(entry))
                removed.Add(entry);
        }
        if (removed.Count > 0)
            RemoveFromQueue(removed);
    }

    /// <summary>Removing taken-over songs from the local queue removes them from the jam too.</summary>
    private void RemoveTakenOverFromJam(IEnumerable<QueueEntry> entries)
    {
        var removing = entries.ToHashSet();
        foreach (var (itemId, entry) in _jamTakenOver.ToList())
        {
            if (!removing.Contains(entry))
                continue;
            _jamTakenOver.Remove(itemId);
            var id = itemId;
            _ = RunJamCommandAsync(() => _jam.Client.RemoveAsync(id));
        }
    }

    /// <summary>Gives the player back to the local queue (paused where it was).</summary>
    private void LeaveJamPlayback(bool release)
    {
        if (!JamOwnsPlayback)
            return;
        _jamOwnsPlayer = false;
        _jamLoadedItemId = null;
        _player.Pause();
        _player.Source = null;
        if (release)
            _ = _jam.ReleaseSpeakerAsync();

        // Still on a song taken over from the local queue: continue it at the jam's position.
        var np = _jam.State?.NowPlaying;
        if (np != null && _jamTakenOver.TryGetValue(np.Id, out var playing) && playing == _current)
        {
            _loadedEntry = null;
            _pendingSeek = TimeSpan.FromSeconds(_jam.ElapsedSeconds);
        }
        _jamTakenOver.Clear();
        _jamTakenOverSeen.Clear();
        SetCurrent(_current);
        SetPlaying(false);
        if (_current != null)
        {
            var duration = TimeSpan.FromSeconds(_current.Song.DurationSeconds);
            ProgressSlider.Maximum = Math.Max(1, duration.TotalSeconds);
            ProgressSlider.Value = Math.Min(_pendingSeek.TotalSeconds, ProgressSlider.Maximum);
            ElapsedText.Text = FormatTime(_pendingSeek);
            DurationText.Text = FormatTime(duration);
        }
        UpdateJamNav();
        if (JamPage.Visibility == Visibility.Visible)
            RenderJamPage();
    }

    // Makes _player follow the server: the right track, at the right second, paused or not.
    private void SyncJamPlayback()
    {
        var state = _jam.State;
        if (state == null)
            return;

        var np = state.NowPlaying;
        if (np == null || string.IsNullOrEmpty(np.StreamUrl))
        {
            if (_jamLoadedItemId != null)
            {
                _player.Pause();
                _jamLoadedItemId = null;
            }
            ShowJamNowPlaying(null);
            SetPlaying(false);
            return;
        }

        var elapsed = _jam.ElapsedSeconds;
        if (np.Id != _jamLoadedItemId)
        {
            _jamLoadedItemId = np.Id;
            _jamEndedItemId = null;
            _jamMediaOpened = false;
            _jamPendingSeek = elapsed > 1 ? TimeSpan.FromSeconds(elapsed) : TimeSpan.Zero;
            Log($"jam: loading '{np.Title}' at {elapsed:F1}s");
            try
            {
                _player.Source = MediaSource.CreateFromUri(new Uri(_jam.Client.AuthorizedUrl(np.StreamUrl)!));
                if (!state.Paused)
                    _player.Play();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LunaDrome] jam playback start failed: {ex}");
                NowPlayingSubText.Text = $"Playback failed: {ex.Message}";
            }
            ShowJamNowPlaying(np);
            SetPlaying(!state.Paused);
            return;
        }

        var playbackState = _player.PlaybackSession.PlaybackState;
        var running = playbackState is MediaPlaybackState.Playing or MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
        if (state.Paused && running)
            _player.Pause();
        else if (!state.Paused && !running && _jamEndedItemId != np.Id)
            _player.Play();
        else if (!state.Paused && _jamMediaOpened && playbackState == MediaPlaybackState.Playing)
        {
            // Stay on the shared timeline (buffering, a slow start, the laptop dozing off).
            var position = _player.PlaybackSession.Position.TotalSeconds;
            if (Math.Abs(position - elapsed) > 4 && elapsed < (np.Duration ?? double.MaxValue) - 2)
            {
                Log($"jam: drifted {position - elapsed:F1}s - seeking to {elapsed:F1}s");
                _player.PlaybackSession.Position = TimeSpan.FromSeconds(elapsed);
            }
        }
        SetPlaying(!state.Paused);
    }

    /// <summary>Called when the jam track opens: jump to where the jam is.</summary>
    private void ApplyJamPendingSeek()
    {
        _jamMediaOpened = true;
        if (_jamPendingSeek <= TimeSpan.Zero)
            return;
        // A second or two may have passed while opening - recompute from the live timeline.
        var target = Math.Max(_jamPendingSeek.TotalSeconds, _jam.ElapsedSeconds);
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(target);
        _jamPendingSeek = TimeSpan.Zero;
    }

    private void OnJamMediaEnded()
    {
        if (_jamLoadedItemId == null || _jamEndedItemId == _jamLoadedItemId)
            return;
        _jamEndedItemId = _jamLoadedItemId;
        var itemId = _jamLoadedItemId;
        _ = RunJamCommandAsync(() => _jam.Client.AdvanceAsync(itemId));
    }

    // Progress bar while the jam plays here: the player's position once it runs, else the jam's
    // timeline; the server's duration (some streams don't report one to the player).
    private void ShowJamPlaybackPosition(TimeSpan position, TimeSpan duration)
    {
        var np = _jam.State?.NowPlaying;
        if (np == null || _jamLoadedItemId != np.Id)
            return;
        var jamDuration = np.Duration is > 0 ? TimeSpan.FromSeconds(np.Duration.Value) : duration;
        var jamPosition = _jamMediaOpened && position > TimeSpan.Zero ? position : TimeSpan.FromSeconds(_jam.ElapsedSeconds);
        if (jamDuration > TimeSpan.Zero)
            ShowPlaybackPosition(jamPosition < jamDuration ? jamPosition : jamDuration, jamDuration);
    }

    // The now-playing bar / full-screen player / OS media widget show the jam track.
    private void ShowJamNowPlaying(JamItem? item)
    {
        if (item == null)
        {
            NowPlayingText.Text = "Jam";
            NowPlayingSubText.Text = _jam.State?.AutofillInProgress == true ? "Auto DJ is picking a song…" : "Waiting for songs…";
            NowPlayingArt.Background = null;
            FullScreenTitleText.Text = "Jam";
            FullScreenArtistText.Text = NowPlayingSubText.Text;
            FullScreenArt.Background = null;
            ProgressSlider.Value = 0;
            ElapsedText.Text = "0:00";
            DurationText.Text = "0:00";
            _mediaControls?.SetTrack(null);
            return;
        }

        NowPlayingText.Text = item.Title;
        NowPlayingSubText.Text = string.IsNullOrWhiteSpace(item.RequestedBy) ? item.Artist : $"{item.Artist} · Jam, {item.RequestedByDisplay}";
        SetImage(NowPlayingArt, item.ArtUrl);
        FullScreenTitleText.Text = item.Title;
        FullScreenArtistText.Text = item.Artist;
        SetImage(FullScreenArt, item.ArtUrl);
        _mediaControls?.SetTrack(new Song { Id = item.Id, Title = item.Title, ArtistName = item.Artist, AlbumName = item.Album ?? "" }, item.ArtUrl);
    }

    // ----- Player controls while the jam owns the player (hooked from MainPage.xaml.cs) -----

    private void JamTogglePause() => _ = RunJamCommandAsync(() =>
        _jam.State?.Paused == true ? _jam.Client.ResumeAsync() : _jam.Client.PauseAsync());

    private void JamPause() => _ = RunJamCommandAsync(() => _jam.Client.PauseAsync());

    private void JamResume() => _ = RunJamCommandAsync(() => _jam.Client.ResumeAsync());

    private void JamSkip()
    {
        var current = _jam.State?.NowPlaying?.Id;
        if (current != null)
            _ = RunJamCommandAsync(() => _jam.Client.AdvanceAsync(current));
    }

    /// <summary>Runs a jam command, then refreshes right away so the UI (and player) follow.</summary>
    private async Task RunJamCommandAsync(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception ex)
        {
            ShowToast($"JamiDrome: {ex.Message}");
        }
        _jam.PollSoon();
    }

    // ================= Jam page =================

    private void RenderJamPage()
    {
        var state = _jam.State;
        if (state == null)
        {
            JamStatusText.Text = _jam.LastError != null ? $"Can't reach JamiDrome: {_jam.LastError}" : "Connecting…";
            return;
        }

        var np = state.NowPlaying;
        JamStatusText.Text = JamOwnsPlayback
            ? state.Paused ? "Playing on this PC · paused" : "Playing on this PC"
            : state.Speaker != null
                ? $"Playing on {state.Speaker.Name}"
                : np != null
                    ? "Nobody is playing the jam, so it's paused. Press Play here to listen."
                    : "Nobody is playing the jam.";
        if (_jam.LastError != null)
            JamStatusText.Text += $" (connection trouble: {_jam.LastError})";
        JamSpeakerButton.Content = JamOwnsPlayback ? "Stop playing here" : "Play here";

        JamTitleText.Text = np?.Title ?? (state.AutofillInProgress ? "Auto DJ is picking a song…" : "Nothing playing");
        JamArtistText.Text = np?.Artist ?? "";
        JamRequestedText.Text = np?.RequestedByDisplay ?? "";
        if (np?.ArtUrl != _jamArtShown)
        {
            _jamArtShown = np?.ArtUrl;
            JamArt.Source = string.IsNullOrEmpty(_jamArtShown) ? null : new BitmapImage(new Uri(_jamArtShown));
        }
        UpdateJamProgress();

        var pausedForNoSpeaker = state.Paused && state.PauseReason == "no_speaker";
        JamPauseButton.Content = state.Paused && !pausedForNoSpeaker ? "Resume" : "Pause";
        JamPauseButton.IsEnabled = np != null && !pausedForNoSpeaker;
        JamSkipButton.IsEnabled = np != null;
        JamAutoDjCheck.IsChecked = state.AutoplayEnabled;

        // Only rebuild the lists when something in them changed (keeps images from flickering).
        var queueSignature = string.Join("|", state.Queue.Select(i => $"{i.Id}:{i.Status}:{Math.Round(i.Progress ?? 0)}"));
        if (queueSignature != _jamQueueSignature)
        {
            _jamQueueSignature = queueSignature;
            JamQueueList.ItemsSource = state.Queue;
        }
        JamQueueEmptyText.Visibility = state.Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var history = Enumerable.Reverse(state.History).ToList();
        var historySignature = string.Join("|", history.Select(i => i.Id));
        if (historySignature != _jamHistorySignature)
        {
            _jamHistorySignature = historySignature;
            JamHistoryList.ItemsSource = history;
        }
        JamHistorySection.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Between polls, from the progress timer: the jam's progress bar keeps moving.
    private void UpdateJamProgress()
    {
        if (JamPage.Visibility != Visibility.Visible)
            return;
        var np = _jam.State?.NowPlaying;
        var duration = np?.Duration ?? 0;
        var elapsed = np == null ? 0 : Math.Min(_jam.ElapsedSeconds, duration > 0 ? duration : double.MaxValue);
        JamProgress.Value = duration > 0 ? elapsed / duration : 0;
        JamElapsedText.Text = FormatTime(TimeSpan.FromSeconds(elapsed));
        JamDurationText.Text = FormatTime(TimeSpan.FromSeconds(duration));
    }

    private async void JamSpeaker_Click(object sender, RoutedEventArgs e)
    {
        if (JamOwnsPlayback)
            LeaveJamPlayback(release: true);
        else
            await JamPlayHereAsync();
    }

    private void JamPause_Click(object sender, RoutedEventArgs e) => JamTogglePause();

    private void JamSkip_Click(object sender, RoutedEventArgs e) => JamSkip();

    private void JamAutoDj_Click(object sender, RoutedEventArgs e)
    {
        var enabled = JamAutoDjCheck.IsChecked == true;
        _ = RunJamCommandAsync(() => _jam.Client.SetAutoplayAsync(enabled));
    }

    private void JamRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: JamItem item })
            _ = RunJamCommandAsync(() => _jam.Client.RemoveAsync(item.Id));
    }

    private void JamRequeue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: JamItem item })
            _ = RunJamCommandAsync(() => _jam.Client.RequeueAsync(item.Id, JamRequesterName));
    }

    private async void JamEnd_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "End the jam?",
            Content = "Clears the jam's queue, stops it playing and turns off every invite link. Recently played stays.",
            PrimaryButtonText = "End jam",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (JamOwnsPlayback)
            LeaveJamPlayback(release: false);
        await RunJamCommandAsync(() => _jam.Client.EndJamAsync());
    }

    // ----- Adding songs -----

    private string JamRequesterName => string.IsNullOrWhiteSpace(_client.Username) ? "Host" : _client.Username;

    private async void JamSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        await RunJamSearchAsync();
    }

    private async void JamSearch_Click(object sender, RoutedEventArgs e) => await RunJamSearchAsync();

    private async Task RunJamSearchAsync()
    {
        var query = JamSearchBox.Text.Trim();
        if (query.Length == 0)
            return;
        JamSearchStatusText.Text = "Searching…";
        JamSearchResultsList.ItemsSource = null;
        try
        {
            _jamSearchResults = await _jam.Client.SearchAsync(query);
            JamSearchResultsList.ItemsSource = _jamSearchResults;
            JamSearchStatusText.Text = _jamSearchResults.Count == 0 ? "Nothing found." : "";
        }
        catch (Exception ex)
        {
            JamSearchStatusText.Text = $"Search failed: {ex.Message}";
        }
    }

    private async void JamSearchAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: JamSearchResult result } button)
            return;
        button.IsEnabled = false;
        try
        {
            await _jam.Client.AddYouTubeAsync(result, JamRequesterName);
            button.Content = "Added";
            _jam.PollSoon();
        }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            ShowToast($"Couldn't add it: {ex.Message}");
        }
    }

    /// <summary>"Add to Jam" in the song / album menus (library songs stream from Navidrome).</summary>
    private void AddJamMenuItem(MenuFlyout flyout, Func<Task<List<Song>>> getSongs)
    {
        if (!_jam.Client.IsLoggedIn)
            return;
        AddMenuItem(flyout, "Add to Jam", async () =>
        {
            try
            {
                var songs = await getSongs();
                var added = 0;
                string? lastError = null;
                foreach (var song in songs)
                {
                    try
                    {
                        await _jam.Client.AddLibrarySongAsync(song.Id, JamRequesterName);
                        added++;
                    }
                    catch (JamiDromeException ex)
                    {
                        lastError = ex.Message; // e.g. already in the queue - keep going
                    }
                }
                _jam.PollSoon();
                ShowToast(added switch
                {
                    0 => $"Nothing added: {lastError}",
                    1 when songs.Count == 1 => $"Added \"{songs[0].Title}\" to the jam.",
                    _ => $"Added {added} song{(added == 1 ? "" : "s")} to the jam."
                }, "Open Jam", () => NavJam.IsChecked = true);
            }
            catch (Exception ex)
            {
                ShowToast($"Couldn't add to the jam: {ex.Message}");
            }
        });
    }

    // ----- Share link -----

    private async void JamShare_Click(object sender, RoutedEventArgs e)
    {
        var durations = new (string Label, TimeSpan Ttl)[]
        {
            ("1 hour", TimeSpan.FromHours(1)), ("3 hours", TimeSpan.FromHours(3)), ("12 hours", TimeSpan.FromHours(12)),
            ("1 day", TimeSpan.FromDays(1)), ("7 days", TimeSpan.FromDays(7)),
        };
        var ttlBox = new ComboBox { ItemsSource = durations.Select(d => d.Label).ToList(), SelectedIndex = 1, MinWidth = 140 };
        var createButton = new Button { Content = "Create link", Style = (Style)Application.Current.Resources["PrimaryButtonStyle"], Padding = new Thickness(16, 6, 16, 6) };
        var linkBox = new TextBox { IsReadOnly = true, Visibility = Visibility.Collapsed };
        var copyButton = new Button { Content = "Copy", Style = (Style)Application.Current.Resources["TextButtonStyle"], Visibility = Visibility.Collapsed };
        var qr = new Image { Width = 200, Height = 200, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
        var status = new TextBlock { Style = (Style)Resources["SettingsHintStyle"], Margin = new Thickness(0), TextWrapping = TextWrapping.Wrap,
            Text = "Anyone with the link can add songs and vote from their phone's browser - no app needed. Links stop working when they expire or you end the jam." };

        var linkRow = new Grid { ColumnSpacing = 8 };
        linkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        linkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        linkRow.Children.Add(linkBox);
        Grid.SetColumn(copyButton, 1);
        linkRow.Children.Add(copyButton);

        var ttlRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ttlRow.Children.Add(new TextBlock { Text = "Valid for", VerticalAlignment = VerticalAlignment.Center });
        ttlRow.Children.Add(ttlBox);
        ttlRow.Children.Add(createButton);

        // #19: the links that are still valid, each with a Delete button.
        var invitesHeader = new TextBlock { Text = "Active links", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        var invitesList = new StackPanel { Spacing = 6 };

        void ShowLink(string link)
        {
            linkBox.Text = link;
            linkBox.Visibility = Visibility.Visible;
            copyButton.Visibility = Visibility.Visible;
            qr.Source = new BitmapImage(new Uri(_jam.Client.InviteQrUrl(link)));
            qr.Visibility = Visibility.Visible;
            CopyToClipboard(link);
        }

        async Task RefreshInvitesAsync()
        {
            invitesList.Children.Clear();
            List<JamInvite> invites;
            try { invites = await _jam.Client.ListInvitesAsync(); }
            catch (Exception ex)
            {
                invitesList.Children.Add(new TextBlock { Text = $"Couldn't load the links: {ex.Message}", Style = (Style)Resources["SettingsHintStyle"], TextWrapping = TextWrapping.Wrap });
                return;
            }
            if (invites.Count == 0)
            {
                invitesList.Children.Add(new TextBlock { Text = "No active links.", Style = (Style)Resources["SettingsHintStyle"] });
                return;
            }
            foreach (var invite in invites.OrderBy(i => i.ExpiresAt))
            {
                var expires = DateTimeOffset.FromUnixTimeSeconds((long)invite.ExpiresAt).ToLocalTime();
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    Text = $"…{invite.Token[Math.Max(0, invite.Token.Length - 6)..]}  ·  expires {expires:ddd HH:mm}",
                });
                // #36: an existing link's address + QR code again (e.g. for someone who arrives later).
                var show = new Button { Content = "Show", Style = (Style)Application.Current.Resources["TextButtonStyle"] };
                show.Click += (_, _) =>
                {
                    var link = _jam.Client.InviteLink(invite.Token);
                    ShowLink(link);
                    status.Text = $"Link copied (expires {expires:ddd HH:mm}). Send it, or let them scan the code.";
                };
                Grid.SetColumn(show, 1);
                row.Children.Add(show);
                var delete = new Button { Content = "Delete", Style = (Style)Application.Current.Resources["TextButtonStyle"] };
                var armed = false;
                delete.Click += async (_, _) =>
                {
                    // Click twice: the first click arms it (no native confirm dialogs).
                    if (!armed)
                    {
                        armed = true;
                        delete.Content = "Click again to delete";
                        return;
                    }
                    delete.IsEnabled = false;
                    try
                    {
                        await _jam.Client.RevokeInviteAsync(invite.Token);
                        status.Text = "Link deleted - it no longer works.";
                    }
                    catch (Exception ex)
                    {
                        status.Text = $"Couldn't delete the link: {ex.Message}";
                    }
                    await RefreshInvitesAsync();
                };
                Grid.SetColumn(delete, 2);
                row.Children.Add(delete);
                invitesList.Children.Add(row);
            }
        }

        var content = new StackPanel { Spacing = 12, MaxWidth = 420 };
        content.Children.Add(status);
        content.Children.Add(ttlRow);
        content.Children.Add(linkRow);
        content.Children.Add(qr);
        content.Children.Add(invitesHeader);
        content.Children.Add(invitesList);
        _ = RefreshInvitesAsync();

        createButton.Click += async (_, _) =>
        {
            createButton.IsEnabled = false;
            try
            {
                var link = await _jam.Client.CreateInviteAsync(durations[Math.Max(0, ttlBox.SelectedIndex)].Ttl);
                ShowLink(link);
                status.Text = "Link copied. Send it to your friends, or let them scan the code.";
                await RefreshInvitesAsync();
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
            Title = "Share the jam",
            // Scrolls: with a QR code shown, the list of links no longer fit (#36).
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Done",
            XamlRoot = this.XamlRoot
        };
        try { await dialog.ShowAsync(); }
        catch { /* another dialog already open */ }
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch { /* no clipboard (e.g. a bare Linux session) - the link is still shown */ }
    }

    // ================= Settings: JamiDrome =================

    private void RefreshJamiDromeSettings()
    {
        JamiDromeUrlBox.Text = _prefs.JamiDromeUrl;
        JamiDromeKeyBox.Password = CredentialProtector.Unprotect(_prefs.JamiDromeApiKey) ?? "";
        if (string.IsNullOrEmpty(JamiDromeTestText.Text))
            JamiDromeTestText.Text = _jam.Client.IsLoggedIn ? $"Connected to {_jam.Client.SiteRoot}." : "Not connected.";
    }

    private async void JamiDromeSave_Click(object sender, RoutedEventArgs e)
    {
        var url = JamiDromeUrlBox.Text.Trim();
        var apiKey = JamiDromeKeyBox.Password.Trim();
        if (JamOwnsPlayback)
            LeaveJamPlayback(release: true);

        // Nothing counts as connected until the key has been tested below.
        _prefs.JamiDromeUrl = url;
        _prefs.JamiDromeApiKey = "";
        PreferencesService.Save(_prefs);
        _jam.Client.Configure(url, apiKey);
        _jam.SetWatching(false);

        if (!_jam.Client.IsConfigured)
        {
            _jam.Client.Configure(url, "");
            UpdateJamNav();
            JamiDromeTestText.Text = "Turned off - no Jam page.";
            return;
        }

        JamiDromeSaveButton.IsEnabled = false;
        JamiDromeTestText.Text = $"Testing {_jam.Client.SiteRoot}…";
        var error = await _jam.Client.TestConnectionAsync();
        if (error == null)
        {
            // Stored encrypted, like the SpotiDrome key.
            _prefs.JamiDromeApiKey = CredentialProtector.Protect(apiKey);
            PreferencesService.Save(_prefs);
            JamiDromeTestText.Text = $"Connected to {_jam.Client.SiteRoot}. The Jam page is in the sidebar.";
        }
        else
        {
            _jam.Client.Configure(url, "");
            JamiDromeTestText.Text = error;
        }
        JamiDromeSaveButton.IsEnabled = true;
        UpdateJamNav();
    }
}
