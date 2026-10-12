using Android.Content;
using Android.Media;
using Android.OS;
using LunaDrome.Core.Models;
using LunaDrome.Core.Services;
using AndroidPlayer = Android.Media.MediaPlayer;

namespace LunaDrome.Services;

/// <summary>
/// Plays music for Android Auto while the app's page isn't running (the car started LunaDrome
/// on its own, the phone stays in a pocket). A plain queue on Android's MediaPlayer, driven by
/// the media session. When the app is opened later, the page takes the queue over
/// (<see cref="TakeOver"/>). Everything runs on the main looper, like the session callbacks.
/// </summary>
internal sealed class AutoPlayer : Java.Lang.Object, AndroidPlayer.IOnPreparedListener,
    AndroidPlayer.IOnCompletionListener, AndroidPlayer.IOnErrorListener, AudioManager.IOnAudioFocusChangeListener
{
    private static readonly Handler s_main = new(Looper.MainLooper!);
    private static AutoPlayer? s_current;

    private readonly Context _context = Android.App.Application.Context;
    private readonly AudioManager? _audio;
    private AudioFocusRequestClass? _focusRequest;
    private NavidromeClient? _client;
    private AndroidPlayer? _player;
    private List<Song> _songs = new();
    private int _index = -1;
    private bool _prepared, _playWhenReady, _resumeOnFocusGain, _ticking;
    private int _failuresInARow;
    private TimeSpan _startAt;
    private DateTime _lastSave;

    private AutoPlayer() => _audio = (AudioManager?)_context.GetSystemService(Context.AudioService);

    /// <summary>The player, while it has something loaded.</summary>
    public static bool IsActive => s_current?._index >= 0;

    private static AutoPlayer Current => s_current ??= new AutoPlayer();
    private static AndroidMediaControls Controls => AndroidMediaControls.Create();

    public static void Post(Action action) => s_main.Post(action);

    // --- Session commands (from AndroidMediaControls while the page isn't connected) ---

    public static void Play()
    {
        var player = Current;
        if (player._index >= 0)
            player.Resume();
        else
            _ = player.RestoreSavedQueueAsync();
    }

    public static void Pause() => s_current?.PauseInternal();
    public static void Next() => s_current?.Skip(+1);
    public static void Previous() => s_current?.Skip(-1);
    public static void SeekTo(TimeSpan position) => s_current?.Seek(position);

    public static async void PlayFromMediaId(string mediaId)
    {
        var target = await AutoLibrary.ResolveAsync(mediaId);
        if (target is { } t)
            Post(() => Current.Start(t.Songs, t.Index, TimeSpan.Zero, play: true));
    }

    public static async void PlayFromSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query) && s_current?._index >= 0)
        {
            Post(() => s_current?.Resume());
            return;
        }
        var songs = await AutoLibrary.SearchAsync(query);
        if (songs.Count > 0)
            Post(() => Current.Start(songs, 0, TimeSpan.Zero, play: true));
    }

    /// <summary>Stops this player and hands its queue to the page (null when nothing's loaded).</summary>
    public static (List<Song> Songs, int Index, TimeSpan Position, bool Playing)? TakeOver()
    {
        var player = s_current;
        if (player == null || player._index < 0)
            return null;
        var state = (player._songs, player._index, player.Position, player._playWhenReady);
        player.SaveQueue();
        player.Release();
        player.AbandonFocus();
        player._index = -1;
        s_current = null;
        return state;
    }

    // --- Playback ---

    private TimeSpan Position
    {
        get
        {
            try { return _prepared && _player != null ? TimeSpan.FromMilliseconds(_player.CurrentPosition) : _startAt; }
            catch { return _startAt; }
        }
    }

    // "Play" with nothing loaded: pick up the queue the app saved (the same one it resumes).
    private async Task RestoreSavedQueueAsync()
    {
        var client = await AutoLibrary.GetClientAsync();
        var state = QueueStateService.Load();
        if (client != null && state is { Songs.Count: > 0 }
            && string.Equals(state.ServerUrl, client.ServerUrl, StringComparison.OrdinalIgnoreCase)
            && state.Username == client.Username)
        {
            var index = Math.Clamp(state.CurrentIndex, 0, state.Songs.Count - 1);
            Post(() => Start(state.Songs, index, TimeSpan.FromSeconds(Math.Max(0, state.PositionSeconds)), play: true));
        }
        else
        {
            PlayFromSearch(""); // nothing saved: a random album
        }
    }

    private void Start(List<Song> songs, int index, TimeSpan startAt, bool play)
    {
        _songs = songs;
        _failuresInARow = 0;
        Load(index, startAt, play);
    }

    private async void Load(int index, TimeSpan startAt, bool play)
    {
        if (index < 0 || index >= _songs.Count)
            return;
        var client = await AutoLibrary.GetClientAsync();
        if (client != null)
            Post(() => LoadWith(client, index, startAt, play));
    }

    private void LoadWith(NavidromeClient client, int index, TimeSpan startAt, bool play)
    {
        if (s_current != this || index < 0 || index >= _songs.Count)
            return;
        _client = client;
        Release();
        _index = index;
        _startAt = startAt;
        _prepared = false;
        _playWhenReady = play;
        var song = _songs[index];
        Console.Error.WriteLine($"[LunaDrome] auto player: {song.Title} ({index + 1}/{_songs.Count}), play={play}");

        Controls.SetTrack(song, _client.GetCoverArtUrl(song.AlbumId, 480));
        Controls.SetCanGoPreviousNext(true, index < _songs.Count - 1);
        Controls.SetPlaybackState(play);
        Controls.SetPosition(startAt, TimeSpan.FromSeconds(song.DurationSeconds));

        try
        {
            _player = new AndroidPlayer();
            _player.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Music)!
                .Build()!);
            _player.SetWakeMode(_context, WakeLockFlags.Partial);
            _player.SetOnPreparedListener(this);
            _player.SetOnCompletionListener(this);
            _player.SetOnErrorListener(this);
            _player.SetDataSource(_client.GetStreamUrl(song.Id));
            _player.PrepareAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LunaDrome] auto player load: {ex.Message}");
            Failed();
        }
        SaveQueue();
    }

    public void OnPrepared(AndroidPlayer? mp)
    {
        if (mp != _player)
            return;
        _prepared = true;
        _failuresInARow = 0;
        if (_startAt > TimeSpan.Zero)
            mp!.SeekTo((int)_startAt.TotalMilliseconds);
        if (_playWhenReady)
            Resume();
        var song = _songs[_index];
        _ = ScrobbleAsync(song.Id);
    }

    public void OnCompletion(AndroidPlayer? mp)
    {
        if (mp != _player)
            return;
        if (_index < _songs.Count - 1)
            Load(_index + 1, TimeSpan.Zero, play: true);
        else
        {
            _playWhenReady = false;
            _startAt = TimeSpan.Zero;
            Release();
            Controls.SetPlaybackState(false);
            AbandonFocus();
        }
    }

    public bool OnError(AndroidPlayer? mp, MediaError what, int extra)
    {
        if (mp == _player)
        {
            Console.Error.WriteLine($"[LunaDrome] auto player error {what} {extra}");
            Failed();
        }
        return true;
    }

    // A song that won't play: the next one, but stop after a few in a row (offline).
    private void Failed()
    {
        Release();
        if (++_failuresInARow < 3 && _index < _songs.Count - 1)
            Load(_index + 1, TimeSpan.Zero, _playWhenReady);
        else
        {
            _playWhenReady = false;
            Controls.SetPlaybackState(false);
        }
    }

    private void Resume()
    {
        if (_index < 0)
            return;
        _playWhenReady = true;
        if (_player == null)
        {
            Load(_index, _startAt, play: true);
            return;
        }
        if (!RequestFocus())
            return;
        if (_prepared)
        {
            _player.Start();
            Controls.SetPosition(Position, TimeSpan.FromSeconds(_songs[_index].DurationSeconds));
            StartTicking();
        }
        Controls.SetPlaybackState(true);
    }

    private void PauseInternal()
    {
        _playWhenReady = false;
        _resumeOnFocusGain = false;
        if (_prepared && _player?.IsPlaying == true)
            _player.Pause();
        _startAt = Position;
        Controls.SetPlaybackState(false);
        Controls.SetPosition(_startAt, TimeSpan.FromSeconds(_songs.ElementAtOrDefault(_index)?.DurationSeconds ?? 0));
        SaveQueue();
    }

    private void Skip(int direction)
    {
        if (_index < 0)
            return;
        // Previous within the first seconds goes back a song, later it restarts this one.
        if (direction < 0 && Position > TimeSpan.FromSeconds(3))
        {
            Seek(TimeSpan.Zero);
            return;
        }
        var target = _index + direction;
        if (target >= 0 && target < _songs.Count)
            Load(target, TimeSpan.Zero, play: true);
    }

    private void Seek(TimeSpan position)
    {
        if (_prepared && _player != null)
            _player.SeekTo((int)position.TotalMilliseconds);
        else
            _startAt = position;
        Controls.SetPosition(position, TimeSpan.FromSeconds(_songs.ElementAtOrDefault(_index)?.DurationSeconds ?? 0));
    }

    private void Release()
    {
        var player = _player;
        _player = null;
        _prepared = false;
        if (player == null)
            return;
        try
        {
            player.Reset();
            player.Release();
        }
        catch { }
    }

    // While playing: keep the car's position bar honest and save the queue now and then.
    private void StartTicking()
    {
        if (_ticking)
            return;
        _ticking = true;
        s_main.PostDelayed(Tick, 1000);
    }

    private void Tick()
    {
        if (s_current != this || !_prepared || _player?.IsPlaying != true)
        {
            _ticking = false;
            return;
        }
        Controls.SetPosition(Position, TimeSpan.FromSeconds(_songs[_index].DurationSeconds));
        if (DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(15))
            SaveQueue();
        s_main.PostDelayed(Tick, 1000);
    }

    // The queue the app resumes on its next start, so it carries on where the car stopped.
    private void SaveQueue()
    {
        if (_client == null || _index < 0)
            return;
        _lastSave = DateTime.UtcNow;
        try
        {
            QueueStateService.SaveInBackground(new QueueState
            {
                ServerUrl = _client.ServerUrl,
                Username = _client.Username,
                Songs = _songs,
                CurrentIndex = _index,
                PositionSeconds = Position.TotalSeconds,
            }, ex => Console.Error.WriteLine($"[LunaDrome] auto player save: {ex.Message}"));
        }
        catch { }
    }

    private async Task ScrobbleAsync(string songId)
    {
        try { if (_client != null) await _client.ScrobbleAsync(songId); }
        catch { /* best-effort */ }
    }

    // --- Audio focus: pause for calls and navigation prompts, like any music app ---

    private bool RequestFocus()
    {
        if (_audio == null)
            return true;
        AudioFocusRequest result;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            _focusRequest ??= new AudioFocusRequestClass.Builder(AudioFocus.Gain)
                .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!)!
                .SetOnAudioFocusChangeListener(this)!
                .Build();
            result = _audio.RequestAudioFocus(_focusRequest!);
        }
        else
        {
#pragma warning disable CA1422, CS0618 // the pre-Android 8 API
            result = _audio.RequestAudioFocus(this, Android.Media.Stream.Music, AudioFocus.Gain);
#pragma warning restore CA1422, CS0618
        }
        return result == AudioFocusRequest.Granted;
    }

    private void AbandonFocus()
    {
        if (_audio == null)
            return;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            if (_focusRequest != null)
                _audio.AbandonAudioFocusRequest(_focusRequest);
        }
        else
        {
#pragma warning disable CA1422, CS0618
            _audio.AbandonAudioFocus(this);
#pragma warning restore CA1422, CS0618
        }
    }

    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        switch (focusChange)
        {
            case AudioFocus.Gain:
                _player?.SetVolume(1f, 1f);
                if (_resumeOnFocusGain)
                {
                    _resumeOnFocusGain = false;
                    Resume();
                }
                break;
            case AudioFocus.LossTransientCanDuck:
                _player?.SetVolume(0.2f, 0.2f);
                break;
            case AudioFocus.LossTransient:
                var wasPlaying = _playWhenReady;
                PauseInternal();
                _resumeOnFocusGain = wasPlaying;
                break;
            case AudioFocus.Loss:
                PauseInternal();
                break;
        }
    }
}
