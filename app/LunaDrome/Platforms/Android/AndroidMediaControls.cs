using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Support.V4.Media;
using Android.Support.V4.Media.Session;
using LunaDrome.Core.Models;
using AndroidX.Core.App;
using MediaStyle = AndroidX.Media.App.NotificationCompat.MediaStyle;

namespace LunaDrome.Services;

/// <summary>
/// Android's media session (#26): the notification + lock-screen controls, headset/Bluetooth
/// buttons and Android Auto all talk to this one MediaSessionCompat. LunaMediaService hands its
/// token to Android Auto and keeps the app alive as a foreground service while music plays.
/// Called on the UI thread (the Android main looper), like the session callbacks.
/// </summary>
public sealed class AndroidMediaControls : IMediaControls
{
    private const string ChannelId = "playback";
    private const string SongMixAction = "lunadrome.SONG_MIX";
    internal const int NotificationId = 1;

    private static AndroidMediaControls? s_instance;
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Context _context;
    private Song? _song;
    private Bitmap? _art;
    private bool? _playing;
    private bool _canPrevious, _canNext;
    private TimeSpan _position, _duration;
    private DateTime _positionReportedAt;

    public event Action<MediaCommand>? CommandReceived;

    internal MediaSessionCompat Session { get; }
    internal bool HasTrack => _song != null && _playing != null;
    internal bool IsPlaying => _playing == true;

    /// <summary>The session the service hands out, or null when the app UI never started.</summary>
    internal static AndroidMediaControls? Current => s_instance;

    private AndroidMediaControls(Context context)
    {
        _context = context;
        Session = new MediaSessionCompat(context, "LunaDrome");
        Session.SetCallback(new SessionCallback(this));
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch != null)
            Session.SetSessionActivity(PendingIntent.GetActivity(context, 0, launch, PendingIntentFlags.Immutable));
        Session.Active = true;
        PublishState();
    }

    public static AndroidMediaControls Create()
    {
        s_instance ??= new AndroidMediaControls(Android.App.Application.Context);
        return s_instance;
    }

    public void SetTrack(Song? song, string? artUrl = null)
    {
        var changed = song?.Id != _song?.Id;
        _song = song;
        if (changed)
            _art = null;
        _duration = TimeSpan.FromSeconds(song?.DurationSeconds ?? 0);
        PublishMetadata();
        UpdateNotification();
        if (song != null && changed && !string.IsNullOrEmpty(artUrl))
            _ = LoadArtAsync(song.Id, artUrl);
    }

    public void SetPlaybackState(bool? playing)
    {
        // Also when unchanged: after taking over from AutoPlayer the state is already "playing",
        // but the focus was AutoPlayer's.
        if (AndroidAutoBridge.PlayFromMediaId != null)   // the page is playing, not AutoPlayer
            PageAudioFocus.Update(playing, Raise);
        if (_playing == playing)
            return;
        _playing = playing;
        PublishState();
        UpdateNotification();
    }

    public void SetCanGoPreviousNext(bool canGoPrevious, bool canGoNext)
    {
        if (_canPrevious == canGoPrevious && _canNext == canGoNext)
            return;
        _canPrevious = canGoPrevious;
        _canNext = canGoNext;
        PublishState();
    }

    public void SetPosition(TimeSpan position, TimeSpan duration)
    {
        if (duration > TimeSpan.Zero && Math.Abs((duration - _duration).TotalSeconds) > 1)
        {
            _duration = duration;
            PublishMetadata();
        }
        // The session extrapolates the position from the last state while playing, so only
        // republish when the real position drifted from that (a seek, a stall).
        var expected = _playing == true ? _position + (DateTime.UtcNow - _positionReportedAt) : _position;
        if (Math.Abs((position - expected).TotalSeconds) < 1.5)
            return;
        _position = position;
        PublishState();
    }

    private void PublishMetadata()
    {
        if (_song == null)
        {
            Session.SetMetadata(null);
            return;
        }
        var builder = new MediaMetadataCompat.Builder()
            .PutString(MediaMetadataCompat.MetadataKeyMediaId, _song.Id)!
            .PutString(MediaMetadataCompat.MetadataKeyTitle, _song.Title)!
            .PutString(MediaMetadataCompat.MetadataKeyArtist, _song.ArtistName)!
            .PutString(MediaMetadataCompat.MetadataKeyAlbum, _song.AlbumName)!
            .PutString(MediaMetadataCompat.MetadataKeyAlbumArtist, string.IsNullOrEmpty(_song.AlbumArtistName) ? _song.ArtistName : _song.AlbumArtistName)!
            .PutLong(MediaMetadataCompat.MetadataKeyDuration, (long)_duration.TotalMilliseconds)!;
        if (_art != null)
            builder.PutBitmap(MediaMetadataCompat.MetadataKeyAlbumArt, _art);
        Session.SetMetadata(builder.Build());
    }

    private void PublishState()
    {
        long actions = PlaybackStateCompat.ActionPlay | PlaybackStateCompat.ActionPause | PlaybackStateCompat.ActionPlayPause
            | PlaybackStateCompat.ActionStop | PlaybackStateCompat.ActionSeekTo
            | PlaybackStateCompat.ActionPlayFromMediaId | PlaybackStateCompat.ActionPlayFromSearch;
        if (_canPrevious)
            actions |= PlaybackStateCompat.ActionSkipToPrevious;
        if (_canNext)
            actions |= PlaybackStateCompat.ActionSkipToNext;

        int state = _playing switch
        {
            true => PlaybackStateCompat.StatePlaying,
            false => PlaybackStateCompat.StatePaused,
            null => PlaybackStateCompat.StateStopped,
        };
        _positionReportedAt = DateTime.UtcNow;
        Session.SetPlaybackState(new PlaybackStateCompat.Builder()
            .SetActions(actions)!
            // A Song mix button on Android Auto's player screen.
            .AddCustomAction(new PlaybackStateCompat.CustomAction.Builder(SongMixAction, "Song mix", Resource.Drawable.lunadrome_shuffle).Build())!
            .SetState(state, (long)_position.TotalMilliseconds, _playing == true ? 1f : 0f)!
            .Build());
    }

    private async Task LoadArtAsync(string songId, string url)
    {
        try
        {
            var bytes = await s_http.GetByteArrayAsync(url);
            var bitmap = await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length);
            if (bitmap == null || _song?.Id != songId)
                return;
            _art = bitmap;
            PublishMetadata();
            UpdateNotification();
        }
        catch
        {
            // No art is fine - the controls work without it.
        }
    }

    // --- Notification (shown by LunaMediaService while there's something to play) ---

    private void UpdateNotification()
    {
        try
        {
            if (_song == null || _playing == null)
            {
                LunaMediaService.StopPlayback(_context);
                return;
            }
            LunaMediaService.ShowPlayback(_context, _playing == true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LunaDrome] media notification: {ex.Message}");
        }
    }

    internal Notification BuildNotification()
    {
        EnsureChannel();
        var playing = _playing == true;
        var builder = new NotificationCompat.Builder(_context, ChannelId)
            .SetSmallIcon(Resource.Mipmap.icon)!
            .SetContentTitle(_song?.Title ?? "LunaDrome")!
            .SetContentText(_song?.ArtistName ?? "")!
            .SetSubText(_song?.AlbumName ?? "")!
            .SetLargeIcon(_art)!
            .SetContentIntent(Session.Controller?.SessionActivity)!
            .SetDeleteIntent(ButtonIntent(PlaybackStateCompat.ActionStop))!
            .SetVisibility(NotificationCompat.VisibilityPublic)!
            .SetOnlyAlertOnce(true)!
            .SetOngoing(playing)!
            .AddAction(Resource.Drawable.lunadrome_previous, "Previous", ButtonIntent(PlaybackStateCompat.ActionSkipToPrevious))!
            .AddAction(playing ? Resource.Drawable.lunadrome_pause : Resource.Drawable.lunadrome_play,
                playing ? "Pause" : "Play", ButtonIntent(PlaybackStateCompat.ActionPlayPause))!
            .AddAction(Resource.Drawable.lunadrome_next, "Next", ButtonIntent(PlaybackStateCompat.ActionSkipToNext))!
            .SetStyle(new MediaStyle()
                .SetMediaSession(Session.SessionToken)!
                .SetShowActionsInCompactView(0, 1, 2))!;
        return builder.Build()!;
    }

    private PendingIntent? ButtonIntent(long action) =>
        AndroidX.Media.Session.MediaButtonReceiver.BuildMediaButtonPendingIntent(_context, action);

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return;
        var manager = (NotificationManager?)_context.GetSystemService(Context.NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) != null)
            return;
        var channel = new NotificationChannel(ChannelId, "Playback", NotificationImportance.Low)
        {
            Description = "The song that's playing, with play/pause and skip",
        };
        channel.SetShowBadge(false);
        manager?.CreateNotificationChannel(channel);
    }

    public void Dispose()
    {
        // The session lives as long as the process: Android Auto and the notification keep
        // talking to it after the page is gone.
    }

    private void Raise(MediaCommand command) => CommandReceived?.Invoke(command);

    private sealed class SessionCallback : MediaSessionCompat.Callback
    {
        private readonly AndroidMediaControls _owner;

        public SessionCallback(AndroidMediaControls owner) => _owner = owner;

        // The page plays when it's running; otherwise (Android Auto started the app on its own)
        // AutoPlayer does.
        private static bool PageConnected => AndroidAutoBridge.PlayFromMediaId != null;

        public override void OnPlay() => Command(MediaCommand.Play, AutoPlayer.Play);
        public override void OnPause() => Command(MediaCommand.Pause, AutoPlayer.Pause);
        public override void OnStop() => Command(MediaCommand.Pause, AutoPlayer.Pause);
        public override void OnSkipToNext() => Command(MediaCommand.Next, AutoPlayer.Next);
        public override void OnSkipToPrevious() => Command(MediaCommand.Previous, AutoPlayer.Previous);

        private void Command(MediaCommand command, Action withoutPage)
        {
            if (PageConnected)
                _owner.Raise(command);
            else
                withoutPage();
        }

        public override void OnSeekTo(long pos)
        {
            var position = TimeSpan.FromMilliseconds(pos);
            if (PageConnected)
                AndroidAutoBridge.Seek?.Invoke(position);
            else
                AutoPlayer.SeekTo(position);
        }

        public override void OnPlayFromMediaId(string? mediaId, Bundle? extras)
        {
            if (string.IsNullOrEmpty(mediaId))
                return;
            if (PageConnected)
                AndroidAutoBridge.PlayFromMediaId?.Invoke(mediaId);
            else
                AutoPlayer.PlayFromMediaId(mediaId);
        }

        public override void OnCustomAction(string? action, Bundle? extras)
        {
            if (action == SongMixAction)
                OnPlayFromMediaId(AutoLibrary.SongMixId, extras);
        }

        public override void OnPlayFromSearch(string? query, Bundle? extras)
        {
            if (PageConnected)
                AndroidAutoBridge.PlayFromSearch?.Invoke(query ?? "");
            else
                AutoPlayer.PlayFromSearch(query ?? "");
        }
    }
}

/// <summary>A row in Android Auto's browse tree.</summary>
public sealed record BrowseItem(string MediaId, string Title, string? Subtitle, string? ArtUrl, bool Browsable, bool Playable);

/// <summary>
/// What the Android side needs from the page: ways to start playback. MainPage fills these in
/// (Platforms/Android/MainPage.Android.cs) once it's logged in; until then AutoPlayer plays.
/// </summary>
public static class AndroidAutoBridge
{
    public static Action<string>? PlayFromMediaId;
    public static Action<string>? PlayFromSearch;
    public static Action<TimeSpan>? Seek;
}
