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
    private readonly Func<Song, Task<string?>>? _findCover;
    private Song? _song;
    private string? _coverUrl;    // the current song's public cover, once found
    private bool? _playing;
    private TimeSpan _position;
    private TimeSpan _duration;

    /// <param name="findCover">Finds a song's public album cover (null: the logo).</param>
    public DiscordMediaControls(Action<string>? log, Func<Song, Task<string?>>? findCover = null)
    {
        _presence.Log = log;
        _findCover = findCover;
    }

    /// <summary>Album covers instead of the LunaDrome logo (when the server has public links).</summary>
    public bool ShowAlbumArt { get; set; } = true;

    public bool Enabled
    {
        get => _presence.Enabled;
        set => _presence.Enabled = value;
    }

    public event Action<MediaCommand>? CommandReceived { add { } remove { } }

    public void SetTrack(Song? song, string? artUrl = null)
    {
        // artUrl is ignored on purpose: Navidrome's cover URL contains the login token.
        var changed = song?.Id != _song?.Id;
        if (changed)
        {
            _position = TimeSpan.Zero;
            _duration = song == null ? TimeSpan.Zero : TimeSpan.FromSeconds(song.DurationSeconds);
            _coverUrl = null;
        }
        _song = song;
        Update();
        if (changed && song != null)
            _ = FindCoverAsync(song);
    }

    // The status goes out right away with the logo; the cover follows when it's found.
    private async Task FindCoverAsync(Song song)
    {
        if (_findCover == null || !Enabled || !ShowAlbumArt)
            return;
        string? cover;
        try { cover = await _findCover(song); }
        catch { return; }
        if (cover == null || song.Id != _song?.Id)
            return;
        _coverUrl = cover;
        Update();
    }

    /// <summary>The album-art setting changed: show or drop the cover now.</summary>
    public void Refresh()
    {
        if (!ShowAlbumArt)
            _coverUrl = null;
        else if (_song != null && _coverUrl == null)
            _ = FindCoverAsync(_song);
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
        _presence.SetPlaying(_song.Title, _song.ArtistName, _song.AlbumName, _position, _duration, ShowAlbumArt ? _coverUrl : null);
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
