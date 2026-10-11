using LunaDrome.Core.Services;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;

namespace LunaDrome;

/// <summary>
/// Sleep timer (pause after N minutes or at the end of the song, with a fade) and crossfade
/// (the next song starts on a second player while the current one fades out).
/// </summary>
public sealed partial class MainPage
{
    // ================= Volume =================

    /// <summary>The volume the player should have now: the slider, lowered while the sleep timer fades out.</summary>
    private double PlaybackVolume => VolumeSlider.Value * _sleepVolumeFactor;

    private void ApplyPlaybackVolume()
    {
        // During a crossfade the fade timer sets both players' volumes itself.
        if (_fadingOut == null)
            _player.Volume = PlaybackVolume;
    }

    // ================= Sleep timer =================

    private const double SleepFadeSeconds = 10;
    private static readonly int[] SleepTimerMinutes = { 15, 30, 45, 60, 90 };

    private DateTime? _sleepAt;        // timed: pause at this moment (UTC)
    private bool _sleepAtSongEnd;      // or: pause when the current song ends
    private double _sleepVolumeFactor = 1;

    private bool SleepTimerActive => _sleepAt != null || _sleepAtSongEnd;

    private void SleepTimerButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        if (SleepTimerActive)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = SleepTimerStatus(), IsEnabled = false });
            AddMenuItem(flyout, "Turn off sleep timer", () =>
            {
                CancelSleepTimer();
                ShowToast("Sleep timer off");
            });
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var minutes in SleepTimerMinutes)
            AddMenuItem(flyout, $"{minutes} minutes", () => StartSleepTimer(minutes));
        AddMenuItem(flyout, "End of this song", StartSleepAtSongEnd);
        flyout.ShowAt((FrameworkElement)sender);
    }

    private void StartSleepTimer(int minutes)
    {
        _sleepAtSongEnd = false;
        _sleepAt = DateTime.UtcNow.AddMinutes(minutes);
        UpdateSleepTimerButtons();
        ShowToast($"Sleep timer: pausing in {minutes} minutes");
    }

    private void StartSleepAtSongEnd()
    {
        _sleepAt = null;
        _sleepAtSongEnd = true;
        UpdateSleepTimerButtons();
        ShowToast("Sleep timer: pausing at the end of this song");
    }

    private void CancelSleepTimer()
    {
        _sleepAt = null;
        _sleepAtSongEnd = false;
        _sleepVolumeFactor = 1;
        ApplyPlaybackVolume();
        UpdateSleepTimerButtons();
    }

    private string SleepTimerStatus()
    {
        if (_sleepAtSongEnd)
            return "Pauses at the end of this song";
        if (_sleepAt is { } at)
        {
            var left = at - DateTime.UtcNow;
            return left.TotalMinutes >= 1
                ? $"Pauses in {(int)Math.Ceiling(left.TotalMinutes)} min"
                : $"Pauses in {Math.Max(0, (int)Math.Ceiling(left.TotalSeconds))} s";
        }
        return "Sleep timer";
    }

    private void UpdateSleepTimerButtons()
    {
        var active = SleepTimerActive;
        var brush = (Brush)Application.Current.Resources[active ? "AccentBrush" : "TextSecondaryBrush"];
        SleepTimerIcon.Stroke = brush;
        CompactSleepTimerIcon.Stroke = brush;
        SleepTimerIcon.Fill = active ? brush : null;
        CompactSleepTimerIcon.Fill = active ? brush : null;
        var tip = active ? SleepTimerStatus() : "Sleep timer";
        ToolTipService.SetToolTip(SleepTimerButton, tip);
        AutomationProperties.SetName(SleepTimerButton, tip);
        AutomationProperties.SetName(CompactSleepTimerButton, tip);
    }

    /// <summary>Every progress tick: fade out over the last seconds, pause when the time is up.</summary>
    private void UpdateSleepTimer(TimeSpan position, TimeSpan duration)
    {
        if (!SleepTimerActive)
            return;

        double secondsLeft;
        if (_sleepAt is { } at)
        {
            secondsLeft = (at - DateTime.UtcNow).TotalSeconds;
            if (secondsLeft <= 0)
            {
                CancelSleepTimer();
                if (_isPlaying)
                    SleepPause();
                return;
            }
        }
        else if (!JamOwnsPlayback && _loadedEntry != null && _loadedEntry == _current && duration > TimeSpan.Zero)
        {
            secondsLeft = (duration - position).TotalSeconds;   // OnMediaEnded does the pausing
        }
        else
        {
            secondsLeft = double.MaxValue;
        }

        var factor = Math.Clamp(secondsLeft / SleepFadeSeconds, 0, 1);
        if (Math.Abs(factor - _sleepVolumeFactor) > 0.001)
        {
            _sleepVolumeFactor = factor;
            ApplyPlaybackVolume();
        }
        UpdateSleepTimerButtons();   // the tooltip counts down
    }

    // Time's up. In a jam only this device stops playing it; the jam goes on for the others.
    private void SleepPause()
    {
        if (JamOwnsPlayback)
        {
            LeaveJamPlayback(release: true);
            SetPlaying(false);
        }
        else
        {
            Pause();
        }
        ShowToast("Sleep timer: paused");
    }

    /// <summary>OnMediaEnded with "end of this song": stop, with the next song ready to play.</summary>
    private bool HandleSleepAtSongEnd()
    {
        if (!_sleepAtSongEnd)
            return false;

        CancelSleepTimer();
        if (GetNextEntry() is { } next)
        {
            SetCurrent(next);
            _pendingSeek = TimeSpan.Zero;
        }
        SetPlaying(false);
        ShowToast("Sleep timer: paused");
        return true;
    }

    // ================= Crossfade =================

    private MediaPlayer? _fadingOut;
    private DateTime _fadeStart;
    private double _fadeSeconds;
    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    /// <summary>Hooks the events of a player; they're ignored once it's no longer the current one
    /// (the outgoing player of a crossfade keeps reporting until it's thrown away).</summary>
    private void HookPlayer(MediaPlayer player)
    {
        player.MediaEnded += (s, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(s, _player))
                OnMediaEnded();
        });

        // Playback failures used to be silent (e.g. Linux: libvlc missing/unusable -> the play
        // button just flipped to pause with no sound). Surface them in the now-playing line and
        // on the terminal, and log state changes for troubleshooting (LUNADROME_LOG=1).
        player.MediaFailed += (s, e) =>
        {
            Console.Error.WriteLine($"[LunaDrome] playback failed: {e.Error} {e.ErrorMessage} (0x{e.ExtendedErrorCode?.HResult:X8} {e.ExtendedErrorCode?.Message})");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(s, _player))
                    return;
                SetPlaying(false);
                // #24: the player sometimes reports a (message-less) failure and just keeps
                // playing; SyncWithPlayerState clears this again once it's really playing.
                _showingPlaybackError = true;
                NowPlayingSubText.Text = string.IsNullOrWhiteSpace(e.ErrorMessage)
                    ? "Playback failed" : $"Playback failed: {e.ErrorMessage}";
            });
        };
        player.MediaOpened += (s, _) =>
        {
            Log("media opened");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(s, _player))
                    ApplyPendingSeek();
            });
        };
        player.PlaybackSession.PlaybackStateChanged += (s, _) => Log($"playback state: {s.PlaybackState}");
    }

    /// <summary>Every progress tick: start crossfading into the next song when the current one
    /// is that many seconds from its end.</summary>
    private void MaybeStartCrossfade(TimeSpan position, TimeSpan duration)
    {
        var seconds = _prefs.CrossfadeSeconds;
        // Desktop only: on Android a second player can take the audio focus from the first.
        if (seconds <= 0 || OperatingSystem.IsAndroid() || _fadingOut != null || JamOwnsPlayback || !_isPlaying)
            return;
        if (_sleepAtSongEnd || _repeatMode == RepeatMode.One)
            return;
        if (_loadedEntry == null || _loadedEntry != _current || _endedEntry == _loadedEntry)
            return;
        // Short songs (intros, skits) play out fully.
        if (duration < TimeSpan.FromSeconds(seconds * 2 + 5) || duration - position > TimeSpan.FromSeconds(seconds))
            return;
        if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
            return;
        if (GetNextEntry() is not { } next)
            return;

        Log($"crossfade ({seconds}s) into '{next.Song.Title}'");
        _endedEntry = _loadedEntry;   // the old song counts as played
        _ = ScrobbleQuietlyAsync(_loadedEntry.Song.Id);

        _fadingOut = _player;
        _player = new MediaPlayer { Volume = 0 };
        HookPlayer(_player);
        _fadeStart = DateTime.UtcNow;
        _fadeSeconds = seconds;
        PlayEntry(next, crossfading: true);
        if (_fadingOut != null)   // PlayEntry may have ended it (playback failed to start)
            _fadeTimer.Start();
    }

    private void FadeTimer_Tick(object? sender, object e)
    {
        if (_fadingOut == null)
        {
            _fadeTimer.Stop();
            return;
        }

        // Equal-power curves: the sum sounds equally loud all the way through.
        var t = Math.Clamp((DateTime.UtcNow - _fadeStart).TotalSeconds / _fadeSeconds, 0, 1);
        var volume = PlaybackVolume;
        _player.Volume = volume * Math.Sin(t * Math.PI / 2);
        _fadingOut.Volume = volume * Math.Cos(t * Math.PI / 2);
        if (t >= 1)
            FinishCrossfade();
    }

    /// <summary>Ends a crossfade now (it ran out, or the user paused/skipped/started a jam):
    /// the outgoing song stops, the new one plays at full volume.</summary>
    private void FinishCrossfade()
    {
        _fadeTimer.Stop();
        var old = _fadingOut;
        if (old == null)
            return;
        _fadingOut = null;
        try
        {
            old.Pause();
            old.Source = null;
            (old as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Log($"crossfade: disposing the old player failed: {ex.Message}");
        }
        ApplyPlaybackVolume();
    }
}
