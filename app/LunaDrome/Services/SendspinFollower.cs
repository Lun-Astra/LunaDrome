using System.Runtime.CompilerServices;
using LunaDrome.Core.Sendspin;
using LunaDrome.Core.Sendspin.Decoding;
using Windows.Media.Core;

namespace LunaDrome.Services;

/// <summary>
/// Sendspin output where the platform player can't hand over its audio (phones): the player
/// keeps running, muted, as the clock - queue, position bar, lock screen, background playback all
/// work as before - and this follows it, decoding the same song itself
/// (<see cref="SendspinTrackPlayer"/>) into the Sendspin source: same address, same position,
/// pausing and seeking when it does.
/// </summary>
internal sealed class SendspinFollower : IDisposable
{
    private static readonly ConditionalWeakTable<Windows.Media.Playback.MediaPlayer, SendspinFollower> Followers = new();
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan JumpThreshold = TimeSpan.FromSeconds(1.5);

    private readonly Windows.Media.Playback.MediaPlayer _player;
    private readonly SendspinSource _source;
    private readonly SendspinTrackPlayer _track;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly Action<string> _log;
    private TimeSpan _lastPosition;
    private DateTime _lastTick;
    private bool _wasPlaying;

    private SendspinFollower(Windows.Media.Playback.MediaPlayer player, SendspinSource source,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action<string> log)
    {
        _player = player;
        _source = source;
        _log = log;
        _track = new SendspinTrackPlayer(source, Http) { Log = log };
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) => Follow();
        _timer.Start();
    }

    public static bool IsAttached(Windows.Media.Playback.MediaPlayer player) => Followers.TryGetValue(player, out _);

    public static bool TryAttach(Windows.Media.Playback.MediaPlayer player, SendspinSource source,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action<string> log)
    {
        if (Followers.TryGetValue(player, out _)) return true;
        Followers.Add(player, new SendspinFollower(player, source, dispatcher, log));
        player.Volume = 0;   // the speakers play it; the phone stays quiet
        return true;
    }

    public static void Detach(Windows.Media.Playback.MediaPlayer player)
    {
        if (!Followers.TryGetValue(player, out var follower)) return;
        Followers.Remove(player);
        follower.Dispose();
    }

    public static void SetGain(Windows.Media.Playback.MediaPlayer player, double volume)
    {
        if (Followers.TryGetValue(player, out var follower))
            follower._source.Gain = (float)Math.Pow(Math.Clamp(volume, 0, 1), 3);   // the same curve as desktop
    }

    /// <summary>Every 200 ms: same song, same play/pause state, same position (give or take).</summary>
    private void Follow()
    {
        Uri? uri = null;
        try { uri = (_player.Source as MediaSource)?.Uri; } catch { /* source being swapped */ }
        var session = _player.PlaybackSession;
        var state = session.PlaybackState;
        var playing = state is Windows.Media.Playback.MediaPlaybackState.Playing or Windows.Media.Playback.MediaPlaybackState.Buffering;
        var position = session.Position;
        var now = DateTime.UtcNow;

        if (uri == null)
        {
            if (_track.Uri != null) _track.Stop();
        }
        else if (uri != _track.Uri)
        {
            _log($"sendspin: following '{uri.AbsolutePath}' from {position:mm\\:ss}");
            _track.Play(uri, position, paused: !playing);
        }
        else
        {
            if (playing && _track.Paused) _track.Resume();
            else if (!playing && !_track.Paused && state != Windows.Media.Playback.MediaPlaybackState.Opening) _track.Pause();
            // A seek (or a restart): the player's position jumped away from where time took it.
            var expected = _wasPlaying ? _lastPosition + (now - _lastTick) : _lastPosition;
            if (Math.Abs((position - expected).TotalMilliseconds) > JumpThreshold.TotalMilliseconds && !_track.Ended)
            {
                _log($"sendspin: player jumped to {position:mm\\:ss} - seeking along");
                _track.Play(uri, position, paused: !playing);
            }
        }
        _lastPosition = position;
        _lastTick = now;
        _wasPlaying = playing;
    }

    public void Dispose()
    {
        _timer.Stop();
        _track.Stop();
        _ = _track.DisposeAsync().AsTask();
    }
}
