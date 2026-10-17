using LunaDrome.Core.Sendspin;

namespace LunaDrome.Services;

/// <summary>Phones play through the platform's media player, which has no PCM tap yet, so
/// Sendspin output is desktop-only for now (the output picker isn't shown there).</summary>
internal static class SendspinTap
{
    public static bool IsAttached(Windows.Media.Playback.MediaPlayer player) => false;
    public static bool TryAttach(Windows.Media.Playback.MediaPlayer player, SendspinSource source) => false;
    public static void SetGain(Windows.Media.Playback.MediaPlayer player, double volume) { }
    public static void Detach(Windows.Media.Playback.MediaPlayer player) { }
}
