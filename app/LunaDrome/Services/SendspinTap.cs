using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using LunaDrome.Core.Sendspin;

namespace LunaDrome.Services;

/// <summary>
/// Sends a desktop player's audio to Sendspin instead of the sound card: LibVLC hands over the
/// decoded samples (16-bit stereo at <see cref="SendspinOutput.SampleRate"/>, resampled by VLC)
/// through its audio callbacks, at playback pace, and reports pause / seek / end and its volume.
/// Everything else - queue, seeking, crossfade, the position bar - works as before, because the
/// player itself keeps running; only where its sound goes changes.
/// </summary>
/// <remarks>Like the audio output, this takes effect when the player next opens a song.</remarks>
internal sealed class SendspinTap
{
    private static readonly ConditionalWeakTable<Windows.Media.Playback.MediaPlayer, SendspinTap> Taps = new();

    private readonly SendspinSource _source;
    // The delegates must outlive the native callbacks, so the tap keeps them.
    private readonly LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPlayCb _play;
    private readonly LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPauseCb _pause;
    private readonly LibVLCSharp.Shared.MediaPlayer.LibVLCAudioResumeCb _resume;
    private readonly LibVLCSharp.Shared.MediaPlayer.LibVLCAudioFlushCb _flush;
    private readonly LibVLCSharp.Shared.MediaPlayer.LibVLCAudioDrainCb _drain;
    private readonly LibVLCSharp.Shared.MediaPlayer.LibVLCVolumeCb _volume;

    private SendspinTap(SendspinSource source)
    {
        _source = source;
        _play = OnPlay;
        _pause = (_, _) => _source.Pause();
        _resume = (_, _) => _source.Resume();
        _flush = (_, _) => _source.Flush();
        _drain = _ => _source.Drain();
        // Registered so VLC doesn't scale the samples itself, but the level comes from SetGain:
        // what VLC reports here under audio callbacks isn't reliable on every platform.
        _volume = (_, _, _) => { };
    }

    public static bool IsAttached(Windows.Media.Playback.MediaPlayer player) => Taps.TryGetValue(player, out _);

    /// <summary>Routes the player's audio into <paramref name="source"/>.</summary>
    public static bool TryAttach(Windows.Media.Playback.MediaPlayer player, SendspinSource source)
    {
        if (VlcAudioOutput.GetVlcPlayer(player) is not LibVLCSharp.Shared.MediaPlayer vlc)
            return false;
        var tap = new SendspinTap(source);
        vlc.SetAudioFormat("S16N", SendspinOutput.SampleRate, SendspinOutput.Channels);
        vlc.SetAudioCallbacks(tap._play, tap._pause, tap._resume, tap._flush, tap._drain);
        vlc.SetVolumeCallback(tap._volume);
        Taps.AddOrUpdate(player, tap);
        return true;
    }

    /// <summary>The player's volume (0-1) as the stream's level; LunaDrome calls this wherever it
    /// sets a player's volume (slider, crossfade, sleep timer).</summary>
    public static void SetGain(Windows.Media.Playback.MediaPlayer player, double volume)
    {
        // Same curve as VLC's own output (DirectSound: 60·log10(v) dB, i.e. amplitude v³), so the
        // slider sounds the same on the speakers as on this computer; linear made 10% still loud.
        if (Taps.TryGetValue(player, out var tap))
            tap._source.Gain = (float)Math.Pow(Math.Clamp(volume, 0, 1), 3);
    }

    /// <summary>Stops taking the player's audio; the caller then picks a sound-card output
    /// (<see cref="VlcAudioOutput.TrySet"/>), which replaces the callbacks.</summary>
    public static void Detach(Windows.Media.Playback.MediaPlayer player)
    {
        if (!Taps.TryGetValue(player, out var tap))
            return;
        Taps.Remove(player);
        tap._source.Stop();
    }

    private short[] _buffer = new short[8192];

    private void OnPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        // count = frames; S16N stereo = 2 shorts per frame. VLC calls this from one thread.
        var length = checked((int)count * SendspinOutput.Channels);
        if (_buffer.Length < length) _buffer = new short[length];
        Marshal.Copy(samples, _buffer, 0, length);
        _source.Write(_buffer.AsSpan(0, length));
    }
}
