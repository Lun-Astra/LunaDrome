using AVFoundation;
using CoreGraphics;
using Foundation;
using LunaDrome.Core.Models;
using MediaPlayer;
using UIKit;

namespace LunaDrome.Services;

/// <summary>
/// iOS's lock screen / Control Center / headphone buttons: the now-playing info and the remote
/// commands (MPNowPlayingInfoCenter, MPRemoteCommandCenter). The audio session is set to
/// playback, so the music keeps going with the screen locked (Info.plist: UIBackgroundModes audio).
/// </summary>
public sealed class IosMediaControls : IMediaControls
{
    private static IosMediaControls? s_instance;
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private Song? _song;
    private MPMediaItemArtwork? _art;
    private bool? _playing;
    private TimeSpan _position, _duration;

    public event Action<MediaCommand>? CommandReceived;

    public static IosMediaControls Create() => s_instance ??= new IosMediaControls();

    private IosMediaControls()
    {
        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.Playback);
        session.SetActive(true, out _);

        var commands = MPRemoteCommandCenter.Shared;
        commands.PlayCommand.AddTarget(_ => Raise(MediaCommand.Play));
        commands.PauseCommand.AddTarget(_ => Raise(MediaCommand.Pause));
        commands.TogglePlayPauseCommand.AddTarget(_ => Raise(MediaCommand.PlayPause));
        commands.StopCommand.AddTarget(_ => Raise(MediaCommand.Stop));
        commands.NextTrackCommand.AddTarget(_ => Raise(MediaCommand.Next));
        commands.PreviousTrackCommand.AddTarget(_ => Raise(MediaCommand.Previous));
        commands.ChangePlaybackPositionCommand.Enabled = false;   // no seek command in IMediaControls
    }

    private MPRemoteCommandHandlerStatus Raise(MediaCommand command)
    {
        CommandReceived?.Invoke(command);
        return MPRemoteCommandHandlerStatus.Success;
    }

    public void SetTrack(Song? song, string? artUrl = null)
    {
        var changed = song?.Id != _song?.Id;
        _song = song;
        if (changed)
            _art = null;
        _duration = TimeSpan.FromSeconds(song?.DurationSeconds ?? 0);
        Publish();
        if (song != null && changed && !string.IsNullOrEmpty(artUrl))
            _ = LoadArtAsync(song.Id, artUrl);
    }

    public void SetPlaybackState(bool? playing)
    {
        _playing = playing;
        Publish();
    }

    public void SetCanGoPreviousNext(bool canGoPrevious, bool canGoNext)
    {
        var commands = MPRemoteCommandCenter.Shared;
        commands.PreviousTrackCommand.Enabled = canGoPrevious;
        commands.NextTrackCommand.Enabled = canGoNext;
    }

    public void SetPosition(TimeSpan position, TimeSpan duration)
    {
        // iOS extrapolates the position from the rate, so only a jump (seek, stall) needs a republish.
        var drift = Math.Abs((position - _position).TotalSeconds);
        _position = position;
        if (duration > TimeSpan.Zero)
            _duration = duration;
        if (drift > 1.5 || _playing != true)
            Publish();
    }

    private void Publish()
    {
        if (_song == null || _playing == null)
        {
            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo();
            return;
        }
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo
        {
            Title = _song.Title,
            Artist = _song.ArtistName,
            AlbumTitle = _song.AlbumName,
            PlaybackDuration = _duration.TotalSeconds,
            ElapsedPlaybackTime = _position.TotalSeconds,
            PlaybackRate = _playing == true ? 1 : 0,
            Artwork = _art,
        };
    }

    private async Task LoadArtAsync(string songId, string url)
    {
        try
        {
            var bytes = await s_http.GetByteArrayAsync(url);
            if (_song?.Id != songId || UIImage.LoadFromData(NSData.FromArray(bytes)) is not { } image)
                return;
            _art = new MPMediaItemArtwork(image.Size, (CGSize _) => image);
            Publish();
        }
        catch
        {
            // No art is fine - the controls work without it.
        }
    }

    public void Dispose()
    {
        // Lives as long as the app, like the Android session.
    }
}
