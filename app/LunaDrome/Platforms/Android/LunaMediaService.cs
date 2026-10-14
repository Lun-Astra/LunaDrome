using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Support.V4.Media;
using AndroidX.Core.Content;
using AndroidX.Media;

namespace LunaDrome.Services;

/// <summary>
/// The media browser service Android Auto connects to (#26): it browses the library through
/// AndroidAutoBridge and controls playback through AndroidMediaControls' session. While a song
/// plays it also runs as a foreground service with the media notification, so Android doesn't
/// kill the app in the background.
/// </summary>
[Service(Exported = true, Label = "LunaDrome", ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
[IntentFilter(new[] { "android.media.browse.MediaBrowserService" })]
[IntentFilter(new[] { "android.intent.action.MEDIA_BUTTON" })]
public class LunaMediaService : MediaBrowserServiceCompat
{
    private const string RootId = "root";
    private const string ActionShow = "lunadrome.SHOW_PLAYBACK";
    private const string ExtraPlaying = "playing";

    private static LunaMediaService? s_running;
    private bool _foreground;

    /// <summary>Music is playing (the service is in the foreground).</summary>
    public static bool IsPlaying => s_running?._foreground == true;

    public LunaMediaService() { }
    protected LunaMediaService(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    /// <summary>Shows/updates the media notification; foreground while playing.</summary>
    internal static void ShowPlayback(Context context, bool playing)
    {
        if (s_running != null)
        {
            s_running.UpdateForeground(playing);
            return;
        }
        if (!playing)
            return; // nothing to keep alive; the service starts on the next Play
        var intent = new Intent(context, typeof(LunaMediaService)).SetAction(ActionShow)!.PutExtra(ExtraPlaying, true);
        ContextCompat.StartForegroundService(context, intent);
    }

    internal static void StopPlayback(Context context)
    {
        s_running?.UpdateForeground(null);
    }

    public override void OnCreate()
    {
        base.OnCreate();
        s_running = this;
        // Android Auto can start the service before the app UI; the session exists either way.
        SessionToken = AndroidMediaControls.Create().Session.SessionToken;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var controls = AndroidMediaControls.Create();
        // Headset / notification buttons arrive here via MediaButtonReceiver.
        AndroidX.Media.Session.MediaButtonReceiver.HandleIntent(controls.Session, intent);

        // Started with startForegroundService (ours or MediaButtonReceiver's), so Android wants
        // StartForeground within seconds even if we're about to drop it again.
        if (intent?.Action == ActionShow || !_foreground)
        {
            StartForegroundCompat(controls.BuildNotification());
            _foreground = true;
            UpdateForeground(controls.HasTrack ? controls.IsPlaying : (bool?)null);
        }
        return StartCommandResult.NotSticky;
    }

    private void StartForegroundCompat(Notification notification)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(AndroidMediaControls.NotificationId, notification, ForegroundService.TypeMediaPlayback);
        else
            StartForeground(AndroidMediaControls.NotificationId, notification);
    }

    // playing: true = foreground + ongoing notification, false = notification stays but can be
    // swiped away (and Android may stop the app), null = nothing loaded, notification removed.
    private void UpdateForeground(bool? playing)
    {
        var controls = AndroidMediaControls.Current;
        if (controls == null || playing == null)
        {
            if (_foreground || playing == null)
                StopForegroundCompat(removeNotification: true);
            _foreground = false;
            return;
        }

        var notification = controls.BuildNotification();
        if (playing == true)
        {
            StartForegroundCompat(notification);
            _foreground = true;
        }
        else
        {
            if (_foreground)
                StopForegroundCompat(removeNotification: false);
            _foreground = false;
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.Notify(AndroidMediaControls.NotificationId, notification);
        }
    }

    private void StopForegroundCompat(bool removeNotification)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
            StopForeground(removeNotification ? StopForegroundFlags.Remove : StopForegroundFlags.Detach);
        else
            StopForeground(removeNotification);
        if (removeNotification)
            ((NotificationManager?)GetSystemService(NotificationService))?.Cancel(AndroidMediaControls.NotificationId);
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        // Swiped out of recents while paused: nothing left to keep running for, and the
        // prebuffered tracks go too. (While playing, the music keeps going.)
        if (!_foreground)
        {
            LunaDrome.Services.TrackPrefetcher.DeleteCacheOnExit();
            StopSelf();
        }
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        if (s_running == this)
            s_running = null;
        base.OnDestroy();
    }

    public override BrowserRoot? OnGetRoot(string clientPackageName, int clientUid, Bundle? rootHints)
    {
        // Android Auto lays out the root's children as tabs when they're browsable.
        var extras = new Bundle();
        extras.PutBoolean("android.media.browse.CONTENT_STYLE_SUPPORTED", true);
        extras.PutInt("android.media.browse.CONTENT_STYLE_BROWSABLE_HINT", 2); // grid
        extras.PutInt("android.media.browse.CONTENT_STYLE_PLAYABLE_HINT", 1);  // list
        return new BrowserRoot(RootId, extras);
    }

    public override void OnLoadChildren(string parentId, Result result)
    {
        result.Detach();
        _ = LoadChildrenAsync(parentId, result);
    }

    private static async Task LoadChildrenAsync(string parentId, Result result)
    {
        var items = new List<MediaBrowserCompat.MediaItem>();
        try
        {
            foreach (var item in await AutoLibrary.BrowseAsync(parentId == RootId ? "" : parentId))
                items.Add(Item(item));
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"browse {parentId}: {ex.Message}");
        }
        result.SendResult(new JavaList<MediaBrowserCompat.MediaItem>(items));
    }

    private static MediaBrowserCompat.MediaItem Item(BrowseItem item)
    {
        var description = new MediaDescriptionCompat.Builder()
            .SetMediaId(item.MediaId)!
            .SetTitle(item.Title)!
            .SetSubtitle(item.Subtitle)!;
        if (!string.IsNullOrEmpty(item.ArtUrl))
            description.SetIconUri(Android.Net.Uri.Parse(item.ArtUrl));
        int flags = (item.Browsable ? MediaBrowserCompat.MediaItem.FlagBrowsable : 0)
            | (item.Playable ? MediaBrowserCompat.MediaItem.FlagPlayable : 0);
        return new MediaBrowserCompat.MediaItem(description.Build()!, flags);
    }
}
