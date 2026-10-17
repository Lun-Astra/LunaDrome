using System.Diagnostics;

namespace LunaDrome.Core.Sendspin;

/// <summary>The server clock every Sendspin timestamp is in: monotonic microseconds. Speakers
/// sync to it with client/time ↔ server/time, so it only has to be steady, not wall time.</summary>
public static class SendspinClock
{
    private static readonly double TicksToUs = 1_000_000.0 / Stopwatch.Frequency;

    public static long NowUs => (long)(Stopwatch.GetTimestamp() * TicksToUs);
}

/// <summary>One audio format a speaker accepts, or the one a stream uses.</summary>
public sealed record SendspinFormat(string Codec, int SampleRate, int Channels, int BitDepth)
{
    public override string ToString() => $"{Codec} {SampleRate} Hz {BitDepth}-bit {(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : Channels + " ch")}";
}
