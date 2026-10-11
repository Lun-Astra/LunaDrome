using LunaDrome.Core.Models;
using LunaDrome.Core.Services;

namespace LunaDrome.Services;

/// <summary>
/// Feeds Discord's "Listening to LunaDrome" from the same updates the OS media controls get
/// (normal playback and jam playback alike), via <see cref="MediaControlsGroup"/>. Desktop only;
/// it never raises commands.
/// </summary>
public sealed class DiscordMediaControls : IMediaControls
{
    private readonly DiscordPresence _presence = new();
    private Song? _song;
    private bool? _playing;
    private TimeSpan _position;
    private TimeSpan _duration;

    public DiscordMediaControls(Action<string>? log) => _presence.Log = log;

    public bool Enabled
    {
        get => _presence.Enabled;
        set => _presence.Enabled = value;
    }

    public event Action<MediaCommand>? CommandReceived { add { } remove { } }

    public void SetTrack(Song? song, string? artUrl = null)
    {
        // artUrl is ignored on purpose: Navidrome's cover URL contains the login token.
        if (song?.Id != _song?.Id)
        {
            _position = TimeSpan.Zero;
            _duration = song == null ? TimeSpan.Zero : TimeSpan.FromSeconds(song.DurationSeconds);
        }
        _song = song;
        Update();
    }

    public void SetPlaybackState(bool? playing)
    {
        _playing = playing;
        Update();
    }

    public void SetCanGoPreviousNext(bool canGoPrevious, bool canGoNext) { }

    public void SetPosition(TimeSpan position, TimeSpan duration)
    {
        _position = position;
        if (duration > TimeSpan.Zero)
            _duration = duration;
        Update();
    }

    private void Update()
    {
        if (_song == null || _playing != true || _duration <= TimeSpan.Zero)
        {
            _presence.Clear();
            return;
        }
        _presence.SetPlaying(_song.Title, _song.ArtistName, _song.AlbumName, _position, _duration);
    }

    public void Dispose() => _presence.Dispose();
}

/// <summary>Sends every update to several media integrations (the OS one + Discord).</summary>
public sealed class MediaControlsGroup : IMediaControls
{
    private readonly IMediaControls[] _members;

    public MediaControlsGroup(params IMediaControls?[] members) =>
        _members = members.Where(m => m != null).Cast<IMediaControls>().ToArray();

    public event Action<MediaCommand>? CommandReceived
    {
        add { foreach (var m in _members) m.CommandReceived += value; }
        remove { foreach (var m in _members) m.CommandReceived -= value; }
    }

    public void SetTrack(Song? song, string? artUrl = null) { foreach (var m in _members) m.SetTrack(song, artUrl); }
    public void SetPlaybackState(bool? playing) { foreach (var m in _members) m.SetPlaybackState(playing); }
    public void SetCanGoPreviousNext(bool canGoPrevious, bool canGoNext) { foreach (var m in _members) m.SetCanGoPreviousNext(canGoPrevious, canGoNext); }
    public void SetPosition(TimeSpan position, TimeSpan duration) { foreach (var m in _members) m.SetPosition(position, duration); }
    public void Dispose() { foreach (var m in _members) m.Dispose(); }
}
