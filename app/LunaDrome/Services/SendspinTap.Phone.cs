using LunaDrome.Core.Sendspin;

namespace LunaDrome.Services;

/// <summary>Phones: the platform player gives no access to its decoded audio, so Sendspin output
/// follows it instead - the player plays muted and SendspinFollower decodes the same song.</summary>
internal static class SendspinTap
{
    public static bool IsAttached(Windows.Media.Playback.MediaPlayer player) => SendspinFollower.IsAttached(player);
    public static bool MutesPlayer(Windows.Media.Playback.MediaPlayer player) => SendspinFollower.IsAttached(player);

    public static bool TryAttach(Windows.Media.Playback.MediaPlayer player, SendspinSource source,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action<string> log) =>
        SendspinFollower.TryAttach(player, source, dispatcher, log);

    public static void SetGain(Windows.Media.Playback.MediaPlayer player, double volume) => SendspinFollower.SetGain(player, volume);
    public static void Detach(Windows.Media.Playback.MediaPlayer player) => SendspinFollower.Detach(player);
}
