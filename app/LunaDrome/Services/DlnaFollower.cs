using System.Runtime.CompilerServices;
using LunaDrome.Core.Dlna;
using Windows.Media.Core;

namespace LunaDrome.Services;

/// <summary>
/// DLNA output: a renderer (TV, receiver, network speaker) fetches and plays the song itself,
/// so - like <see cref="SendspinFollower"/> on phones - the player keeps running muted as the
/// clock (queue, position bar, lock screen work as before) and this keeps the renderer on the
/// same song, play/pause state and position. The renderer buffers on its own and may drift a
/// little, so every few seconds the muted player is moved to where the renderer really is
/// (inaudible) rather than the other way round; pausing or skipping on the renderer's own
/// remote comes back as <see cref="RemoteCommand"/>.
/// </summary>
internal sealed class DlnaFollower : IDisposable
{
    private static readonly ConditionalWeakTable<Windows.Media.Playback.MediaPlayer, DlnaFollower> Followers = new();
    private static readonly TimeSpan JumpThreshold = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DriftThreshold = TimeSpan.FromSeconds(2);
    // Further off than buffering explains: a seek got lost, so the renderer is sent there again.
    private static readonly TimeSpan LostSeekThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
    // After a command the renderer needs a moment (buffering, TRANSITIONING) before its state means anything.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(4);
    private const int FailuresBeforeGivingUp = 3;

    private readonly Windows.Media.Playback.MediaPlayer _player;
    private readonly Func<Uri, TimeSpan, DlnaTrack?> _describe;
    private readonly Action<string> _log;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    private Uri? _trackUri;            // the player's song the renderer has
    private DlnaTrack? _track;         // what it was given (a converted stream starts at StartsAt)
    private bool _rendererShouldPlay;  // what we last told it
    private TimeSpan? _seekOnPlay;     // loaded while paused: where to start once it plays
    private bool _busy;
    private int _failures;
    private DateTime _lastCommand = DateTime.MinValue, _lastPoll = DateTime.MinValue;
    private TimeSpan _lastPosition;
    private DateTime _lastTick;
    private bool _wasPlaying;
    private int? _volumeSent;
    private int _volumeWanted = -1;

    private DlnaFollower(Windows.Media.Playback.MediaPlayer player, DlnaRenderer renderer, Func<Uri, TimeSpan, DlnaTrack?> describe,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action<string> log)
    {
        _player = player;
        Renderer = renderer;
        _describe = describe;
        _log = log;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Follow();
        _timer.Start();
    }

    public DlnaRenderer Renderer { get; }

    /// <summary>The renderer stopped answering (string = why); the caller moves playback back here.</summary>
    public event Action<string>? Failed;
    /// <summary>Paused or resumed on the renderer itself.</summary>
    public event Action<MediaCommand>? RemoteCommand;

    public static DlnaFollower? For(Windows.Media.Playback.MediaPlayer player) =>
        Followers.TryGetValue(player, out var follower) ? follower : null;

    public static bool IsAttached(Windows.Media.Playback.MediaPlayer player) => Followers.TryGetValue(player, out _);

    public static DlnaFollower Attach(Windows.Media.Playback.MediaPlayer player, DlnaRenderer renderer, Func<Uri, TimeSpan, DlnaTrack?> describe,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action<string> log)
    {
        Detach(player);
        var follower = new DlnaFollower(player, renderer, describe, dispatcher, log);
        Followers.Add(player, follower);
        player.Volume = 0;   // the renderer plays it; this device stays quiet
        return follower;
    }

    public static void Detach(Windows.Media.Playback.MediaPlayer player)
    {
        if (!Followers.TryGetValue(player, out var follower)) return;
        Followers.Remove(player);
        follower.Dispose();
    }

    /// <summary>LunaDrome's volume (0-1) as the renderer's volume. The first value only sets the
    /// starting point: switching to a TV mustn't jump it to whatever the slider here was at.</summary>
    public static void SetVolume(Windows.Media.Playback.MediaPlayer player, double volume)
    {
        if (!Followers.TryGetValue(player, out var follower) || !follower.Renderer.CanSetVolume) return;
        var wanted = (int)Math.Round(Math.Clamp(volume, 0, 1) * 100);
        if (follower._volumeWanted < 0)
            follower._volumeSent = wanted;
        follower._volumeWanted = wanted;
    }

    private void Follow()
    {
        if (_busy)
            return;
        Uri? uri = null;
        try { uri = (_player.Source as MediaSource)?.Uri; } catch { /* source being swapped */ }
        var session = _player.PlaybackSession;
        var state = session.PlaybackState;
        var playing = state is Windows.Media.Playback.MediaPlaybackState.Playing or Windows.Media.Playback.MediaPlaybackState.Buffering;
        var position = session.Position;
        var now = DateTime.UtcNow;
        var expected = _wasPlaying ? _lastPosition + (now - _lastTick) : _lastPosition;
        _lastPosition = position;
        _lastTick = now;
        _wasPlaying = playing;

        if (uri == null)
        {
            if (_trackUri != null)
            {
                _trackUri = null;
                _track = null;
                _rendererShouldPlay = false;
                Run("stop", () => Renderer.StopAsync());
            }
            return;
        }
        if (uri != _trackUri)
        {
            if (state == Windows.Media.Playback.MediaPlaybackState.Opening)
                return;   // the position isn't known yet
            _trackUri = uri;
            var from = position > TimeSpan.FromSeconds(2) ? position : TimeSpan.Zero;
            if (_describe(uri, from) is not { } track)
            {
                _track = null;
                _log($"dlna: can't hand '{uri}' to {Renderer.Name}");
                return;
            }
            _track = track;
            _rendererShouldPlay = playing;
            // A converted stream already starts where the player is; the original file seeks there.
            var seek = track.StartsAt == TimeSpan.Zero && from > TimeSpan.Zero ? from : (TimeSpan?)null;
            _seekOnPlay = playing ? null : seek;
            _log($"dlna: {Renderer.Name} plays '{track.Title}' from {position:mm\\:ss}{(playing ? "" : " (paused)")}");
            Run("load", async () =>
            {
                await Renderer.LoadAsync(track);
                if (playing)
                    await PlayFromAsync(seek);
            });
            return;
        }
        if (_track == null)
            return;

        if (playing != _rendererShouldPlay && state != Windows.Media.Playback.MediaPlaybackState.Opening)
        {
            _rendererShouldPlay = playing;
            if (playing)
            {
                var seek = _seekOnPlay;
                _seekOnPlay = null;
                Run("play", async () =>
                {
                    if (seek is { } at && _track.SeekByReload)
                        await ReloadFromAsync(uri, at, play: true);
                    else
                        await PlayFromAsync(seek is { } point && point > TimeSpan.FromSeconds(2) ? point : null);
                });
            }
            else
            {
                Run("pause", () => Renderer.PauseAsync());
            }
            return;
        }

        // A seek here (or a restart): the position jumped away from where time took it.
        if (Math.Abs((position - expected).TotalMilliseconds) > JumpThreshold.TotalMilliseconds)
        {
            if (!playing)
                _seekOnPlay = position;
            else if (_track.SeekByReload)
                Run("seek", () => ReloadFromAsync(uri, position, play: true));
            else
                Run("seek", () => Renderer.SeekAsync(position));
            return;
        }

        if (_volumeWanted >= 0 && _volumeWanted != _volumeSent)
        {
            var volume = _volumeWanted;
            _volumeSent = volume;
            Run("volume", () => Renderer.SetVolumeAsync(volume), countsAsCommand: false);
            return;
        }

        if (now - _lastPoll > PollEvery && now - _lastCommand > SettleTime)
        {
            _lastPoll = now;
            Run("status", async () =>
            {
                if (Reconcile(await Renderer.GetStatusAsync()) is { } followUp)
                    await followUp();
            }, countsAsCommand: false);
        }
    }

    /// <summary>A converted stream can't be seeked in: fetch it again from that point.</summary>
    private async Task ReloadFromAsync(Uri uri, TimeSpan position, bool play)
    {
        if (_describe(uri, position) is not { } track)
            return;
        _track = track;
        await Renderer.LoadAsync(track);
        if (play)
            await Renderer.PlayAsync();
    }

    /// <summary>The renderer's own state against the player's; returns a command to send when
    /// the renderer has to be put right.</summary>
    private Func<Task>? Reconcile(DlnaStatus reported)
    {
        if (DateTime.UtcNow - _lastCommand < SettleTime || _trackUri == null || _track == null)
            return null;   // a command went out while the status was on its way
        // Positions in the song, also for a stream that starts part-way in.
        var status = reported with { Position = reported.Position + _track.StartsAt, Duration = reported.Duration > TimeSpan.Zero ? reported.Duration + _track.StartsAt : TimeSpan.Zero };
        var session = _player.PlaybackSession;
        var playing = session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
        if (_rendererShouldPlay && playing && status.TransportState == "PAUSED_PLAYBACK")
        {
            _log($"dlna: paused on {Renderer.Name}");
            _rendererShouldPlay = false;
            RemoteCommand?.Invoke(MediaCommand.Pause);
        }
        else if (!_rendererShouldPlay && !playing && status.Playing)
        {
            _log($"dlna: resumed on {Renderer.Name}");
            _rendererShouldPlay = true;
            RemoteCommand?.Invoke(MediaCommand.Play);
        }
        else if (_rendererShouldPlay && playing && status.TransportState is "STOPPED" or "NO_MEDIA_PRESENT"
                 && session.NaturalDuration - session.Position > TimeSpan.FromSeconds(5))
        {
            // Stopped on the renderer, or another app took it over.
            _log($"dlna: {Renderer.Name} stopped ({status.TransportState})");
            _rendererShouldPlay = false;
            RemoteCommand?.Invoke(MediaCommand.Pause);
        }
        else if (status.Playing && playing && status.Position > TimeSpan.Zero
                 && (status.Position - session.Position).Duration() > LostSeekThreshold && _trackUri is { } uri)
        {
            var target = session.Position;
            var reload = _track.SeekByReload;
            _log($"dlna: {Renderer.Name} is at {status.Position:mm\\:ss}, the player at {target:mm\\:ss} - sending it there again");
            _lastCommand = DateTime.UtcNow;
            return () => reload ? ReloadFromAsync(uri, target, play: true) : PlayFromAsync(target);
        }
        else if (status.Playing && playing && status.Position > TimeSpan.Zero
                 && (status.Position - session.Position).Duration() > DriftThreshold)
        {
            // Follow the renderer: the player is muted, so moving it is inaudible.
            _log($"dlna: {Renderer.Name} is at {status.Position:mm\\:ss}, the player at {session.Position:mm\\:ss} - moving the player");
            session.Position = status.Position;
            _lastPosition = status.Position;
            _lastTick = DateTime.UtcNow;
        }
        return null;
    }

    /// <summary>Play, then seek once the renderer has the stream open: a seek sent before that is
    /// accepted but ignored by some (gmediarender does), and the song starts from the top.</summary>
    private async Task PlayFromAsync(TimeSpan? position)
    {
        await Renderer.PlayAsync();
        if (position is not { } at)
            return;
        for (var waited = 0; waited < 6000; waited += 300)
        {
            if ((await Renderer.GetStatusAsync()).TransportState == "PLAYING")
                break;
            await Task.Delay(300);
        }
        // Even "PLAYING" can come a moment before it can seek: check, and try again.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await SeekQuietlyAsync(at);
            await Task.Delay(800);
            var now = (await Renderer.GetStatusAsync()).Position;
            if ((now - at).Duration() < TimeSpan.FromSeconds(4))
                return;
            at += TimeSpan.FromSeconds(1);   // the player moved on meanwhile
        }
    }

    // A failed seek isn't worth giving up over.
    private async Task SeekQuietlyAsync(TimeSpan position)
    {
        try { await Renderer.SeekAsync(position); }
        catch (Exception ex) when (ex is DlnaException or HttpRequestException or TaskCanceledException)
        {
            _log($"dlna: {Renderer.Name} didn't seek to {position:mm\\:ss}: {ex.Message}");
        }
    }

    private async void Run(string what, Func<Task> action, bool countsAsCommand = true)
    {
        _busy = true;
        if (countsAsCommand) _lastCommand = DateTime.UtcNow;
        try
        {
            await action();
            _failures = 0;
        }
        catch (Exception ex) when (ex is DlnaException or HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            _log($"dlna: {what} on {Renderer.Name} failed: {ex.Message}{(ex.InnerException is { } inner ? $" ({inner.Message})" : "")}");
            // A refused command (DlnaException) still means it's there; silence three times means it's gone.
            if (ex is not DlnaException && ++_failures >= FailuresBeforeGivingUp && Followers.TryGetValue(_player, out var self) && self == this)
                Failed?.Invoke(ex is TaskCanceledException ? "no answer" : ex.Message);
        }
        finally
        {
            if (countsAsCommand) _lastCommand = DateTime.UtcNow;
            _busy = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_trackUri != null)
            _ = Renderer.StopAsync().ContinueWith(t => _log($"dlna: stopping {Renderer.Name} failed: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
    }
}
