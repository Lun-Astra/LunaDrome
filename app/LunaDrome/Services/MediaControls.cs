using LunaDrome.Core.Models;

namespace LunaDrome.Services;

public enum MediaCommand { Play, Pause, PlayPause, Stop, Next, Previous }

/// <summary>
/// The OS media integration: Windows' media flyout (SystemMediaTransportControls, which also
/// routes the keyboard's media keys to us) or Linux's MPRIS (KDE/GNOME media widgets, media
/// keys, playerctl). Updates come from the UI thread; CommandReceived fires on any thread.
/// </summary>
public interface IMediaControls : IDisposable
{
    event Action<MediaCommand>? CommandReceived;

    /// <summary>null = nothing loaded (hides the OS widget where possible).</summary>
    void SetTrack(Song? song, string? artUrl = null);

    /// <summary>true = playing, false = paused, null = stopped / nothing to play.</summary>
    void SetPlaybackState(bool? playing);

    void SetCanGoPreviousNext(bool canGoPrevious, bool canGoNext);

    void SetPosition(TimeSpan position, TimeSpan duration);
}

public static class MediaControls
{
    public static IMediaControls? Create()
    {
        try
        {
#if ANDROID
            // Notification / lock screen / Android Auto; MPRIS (D-Bus) isn't compiled in there.
            return AndroidMediaControls.Create();
#else
            if (OperatingSystem.IsWindows())
                return SmtcMediaControls.TryCreate();
            if (OperatingSystem.IsLinux())
                return MprisMediaControls.TryCreate();
#endif
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LunaDrome] media controls unavailable: {ex.Message}");
        }
        return null;
    }
}
