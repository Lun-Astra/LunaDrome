using LunaDrome.Core.Models;
using LunaDrome.Core.Sendspin;

namespace LunaDrome.Services;

/// <summary>
/// Sendspin speakers as one more "media controls" target: their displays get the song and
/// progress (metadata role), and their buttons (controller role) arrive as media commands -
/// the same path as the media keys. Only active while LunaDrome streams to them.
/// </summary>
public sealed class SendspinMediaControls : IMediaControls
{
    private SendspinOutput? _output;
    private Song? _song;
    private bool? _playing;
    private TimeSpan _position, _duration;
    private TimeSpan _sentPosition;
    private DateTime _sentAt;
    private DateTime _trackSetAt;

    public event Action<MediaCommand>? CommandReceived;

    /// <summary>The output to report to (null = not streaming).</summary>
    public SendspinOutput? Output
    {
        get => _output;
        set
        {
            if (_output != null) _output.ControllerCommand -= OnControllerCommand;
            _output = value;
            if (_output != null)
            {
                _output.ControllerCommand += OnControllerCommand;
                Push();
            }
        }
    }

    public void SetTrack(Song? song, string? artUrl = null)
    {
        _trackSetAt = DateTime.UtcNow;
        _song = song;
        _position = TimeSpan.Zero;
        _duration = song != null ? TimeSpan.FromSeconds(song.DurationSeconds) : TimeSpan.Zero;
        Push();
    }

    public void SetPlaybackState(bool? playing)
    {
        if (_playing == playing) return;
        _playing = playing;
        Push();
    }

    public void SetCanGoPreviousNext(bool canGoPrevious, bool canGoNext) { }

    /// <summary>Called on every progress tick; only a jump (seek) is worth a message, since the
    /// speakers advance the position themselves from the last one.</summary>
    public void SetPosition(TimeSpan position, TimeSpan duration)
    {
        // Right after a track change the first ticks can still carry the old song's position.
        if (DateTime.UtcNow - _trackSetAt < TimeSpan.FromSeconds(1) && position > TimeSpan.FromSeconds(2))
            return;
        _position = position;
        if (duration > TimeSpan.Zero) _duration = duration;
        var expected = _playing == true ? _sentPosition + (DateTime.UtcNow - _sentAt) : _sentPosition;
        if (Math.Abs((position - expected).TotalSeconds) > 1.5)
            Push();
    }

    private void Push()
    {
        _sentPosition = _position;
        _sentAt = DateTime.UtcNow;
        // No artwork_url: LunaDrome's cover links carry the Navidrome login token, which
        // shouldn't be handed to every device on the network.
        _output?.SetMetadata(_song?.Title, _song?.ArtistName, _song?.AlbumName,
            string.IsNullOrEmpty(_song?.AlbumArtistName) ? null : _song.AlbumArtistName, null,
            _song?.Year, _song is { Track: > 0 } ? _song.Track : null,
            (long)_position.TotalMilliseconds, (long)_duration.TotalMilliseconds, _playing == true);
    }

    private void OnControllerCommand(string command)
    {
        MediaCommand? mapped = command switch
        {
            "play" => MediaCommand.Play,
            "pause" => MediaCommand.Pause,
            "stop" => MediaCommand.Stop,
            "next" => MediaCommand.Next,
            "previous" => MediaCommand.Previous,
            _ => null,
        };
        if (mapped is { } c) CommandReceived?.Invoke(c);
    }

    public void Dispose() => Output = null;
}
