using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

namespace LunaDrome.Services;

/// <summary>
/// The audio devices of this computer and which one a desktop player uses (LibVLC behind Uno's
/// MediaPlayer, reached through <see cref="VlcAudioOutput"/>). Devices belong to an output module:
/// DirectSound on Windows, PulseAudio (also PipeWire) on Linux - the ones LunaDrome sets.
/// </summary>
internal static class AudioDevices
{
    public sealed record Device(string Id, string Name);

    public static bool Supported => true;

    private static LibVLC? _lister;

    /// <summary>The devices of <paramref name="module"/>: from the player's own output when it
    /// can tell (PulseAudio only lists them there), else from a small LibVLC instance of its own
    /// (works without a song playing). Empty when there are none or LibVLC can't list them.</summary>
    public static IReadOnlyList<Device> List(string module, Windows.Media.Playback.MediaPlayer? player = null, Action<string>? log = null)
    {
        try
        {
            AudioOutputDevice[] found = [];
            if (player != null && VlcAudioOutput.GetVlcPlayer(player) is MediaPlayer vlc)
                found = vlc.AudioOutputDeviceEnum;
            if (found.Length == 0)
            {
                _lister ??= new LibVLC("--quiet", "--no-video");
                found = _lister.AudioOutputDevices(module);
            }
            return found
                .Where(d => !string.IsNullOrEmpty(d.DeviceIdentifier))
                .Select(d => new Device(d.DeviceIdentifier, string.IsNullOrWhiteSpace(d.Description) ? d.DeviceIdentifier : d.Description))
                .ToList();
        }
        catch (Exception ex)
        {
            log?.Invoke($"audio: listing devices failed - {ex.Message}");
            return [];
        }
    }

    /// <summary>Moves the audio of a playing player to <paramref name="deviceId"/>. LibVLC 3 can
    /// only switch an output that's running (the module-specific setting is ignored by
    /// PulseAudio), so this is also called whenever a player starts playing; false when there's
    /// no output yet or the player can't be reached. Already there = true, nothing done.</summary>
    public static bool TrySet(Windows.Media.Playback.MediaPlayer player, string deviceId)
    {
        try
        {
            if (VlcAudioOutput.GetVlcPlayer(player) is not MediaPlayer vlc)
                return false;
            if (vlc.OutputDevice == deviceId)
                return true;
            vlc.SetOutputDevice(deviceId);
            return vlc.OutputDevice == deviceId;
        }
        catch
        {
            return false;
        }
    }
}
