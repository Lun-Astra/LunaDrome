using System.Reflection;
using Windows.Media.Playback;

namespace LunaDrome.Services;

/// <summary>
/// Picks the audio output of the LibVLC player behind Uno's desktop MediaPlayer. Uno doesn't
/// expose it, so this goes through its (internal) SharedMediaPlayerExtension by reflection.
/// </summary>
/// <remarks>
/// Why: on Windows VLC's default output (mmdevice) sets the volume of the whole app's audio
/// session, so two players in LunaDrome can't have different volumes - a crossfade made them
/// fight over one volume. DirectSound has a volume per stream (with the same volume curve).
/// </remarks>
internal static class VlcAudioOutput
{
    private const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Takes effect when the player next starts audio, so call it before the first song.
    /// False when the VLC player can't be reached (Android, or a changed Uno).</summary>
    public static bool TrySet(Windows.Media.Playback.MediaPlayer player, string module)
    {
        try
        {
            var vlcPlayer = GetVlcPlayer(player);
            var setAudioOutput = vlcPlayer?.GetType().GetMethod("SetAudioOutput", new[] { typeof(string) });
            return setAudioOutput?.Invoke(vlcPlayer, new object[] { module }) is true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The LibVLCSharp.Shared.MediaPlayer behind a desktop player (null on phones).</summary>
    public static object? GetVlcPlayer(Windows.Media.Playback.MediaPlayer player)
    {
        try
        {
            var extension = FindExtension(player);
            return extension?.GetType().GetProperty("VlcPlayer", AnyMember | BindingFlags.Instance)?.GetValue(extension);
        }
        catch
        {
            return null;
        }
    }

    private static object? FindExtension(Windows.Media.Playback.MediaPlayer player)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType("Uno.UI.MediaPlayer.Skia.Win32.SharedMediaPlayerExtension")
                ?? assembly.GetType("Uno.UI.MediaPlayer.Skia.X11.SharedMediaPlayerExtension");
            if (type?.GetMethod("GetByMediaPlayer", AnyMember | BindingFlags.Static) is { } get)
                return get.Invoke(null, new object[] { player });
        }
        return null;
    }
}
