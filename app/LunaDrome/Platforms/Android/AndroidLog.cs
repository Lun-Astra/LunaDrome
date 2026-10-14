namespace LunaDrome.Services;

/// <summary>Logcat + playback.log (what "Share playback log" sends) for the Android side:
/// the background player, Android Auto's commands, audio focus, the car connection.</summary>
internal static class AndroidLog
{
    public static void Write(string message)
    {
        Console.Error.WriteLine($"[LunaDrome] {message}");
        PlaybackLog.Write(message);
    }
}
