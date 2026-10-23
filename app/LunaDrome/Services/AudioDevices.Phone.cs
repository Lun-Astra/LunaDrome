namespace LunaDrome.Services;

/// <summary>Phones play through the system's own routing (Bluetooth, headphones), so there's no
/// device choice; see AudioDevices.cs for the desktop version.</summary>
internal static class AudioDevices
{
    public sealed record Device(string Id, string Name);

    public static bool Supported => false;

    public static IReadOnlyList<Device> List(string module, Windows.Media.Playback.MediaPlayer? player = null, Action<string>? log = null) => [];

    public static bool TrySet(Windows.Media.Playback.MediaPlayer player, string deviceId) => false;
}
