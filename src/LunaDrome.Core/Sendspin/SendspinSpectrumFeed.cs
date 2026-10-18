using LunaDrome.Core.Services;

namespace LunaDrome.Core.Sendspin;

/// <summary>
/// The visualizer's input while playing on Sendspin speakers: this device's own output is then
/// silent, so instead of listening to it, the mix the speakers get goes into the
/// <see cref="SpectrumAnalyzer"/> - each chunk when its timestamp comes up, i.e. when the
/// speakers play it, so the curve moves with what's heard rather than a few hundred ms early.
/// </summary>
public sealed class SendspinSpectrumFeed : IDisposable
{
    private const int MaxQueuedChunks = 400;   // 8 s at 20 ms: more than the largest lead
    private const long SilenceAfterUs = 60_000;

    private readonly SendspinOutput _output;
    private readonly SpectrumAnalyzer _analyzer;
    private readonly Queue<(long TimestampUs, float[] Mono)> _queue = new();
    private readonly float[] _silence = new float[SpectrumAnalyzer.FftLength];
    private long _fedUntilUs;

    public SendspinSpectrumFeed(SendspinOutput output, SpectrumAnalyzer analyzer)
    {
        _output = output;
        _analyzer = analyzer;
        _analyzer.SampleRate = SendspinOutput.SampleRate;
        _analyzer.Reset();
        _fedUntilUs = SendspinClock.NowUs;
        _output.Mixed += OnMixed;
    }

    private void OnMixed(long timestampUs, ReadOnlySpan<short> samples)
    {
        var mono = new float[samples.Length / SendspinOutput.Channels];
        for (var i = 0; i < mono.Length; i++)
            mono[i] = (samples[2 * i] + samples[2 * i + 1]) / 65536f;
        lock (_queue)
        {
            _queue.Enqueue((timestampUs, mono));
            while (_queue.Count > MaxQueuedChunks) _queue.Dequeue();
        }
    }

    /// <summary>Hands the analyzer everything the speakers have started playing by now; call it
    /// before reading the bands (the visualizer's frame timer). While nothing plays, silence
    /// goes in, so the curve settles like it does on this device's own output.</summary>
    public void Pump()
    {
        var now = SendspinClock.NowUs;
        while (true)
        {
            float[] mono;
            lock (_queue)
            {
                if (_queue.Count == 0 || _queue.Peek().TimestampUs > now) break;
                var chunk = _queue.Dequeue();
                mono = chunk.Mono;
                _fedUntilUs = chunk.TimestampUs + mono.Length * 1_000_000L / SendspinOutput.SampleRate;
            }
            _analyzer.AddSamples(mono);
        }
        if (now - _fedUntilUs > SilenceAfterUs)
        {
            var frames = (int)Math.Min(_silence.Length, (now - _fedUntilUs) * SendspinOutput.SampleRate / 1_000_000);
            _analyzer.AddSamples(_silence.AsSpan(0, frames));
            _fedUntilUs = now;
        }
    }

    public void Dispose()
    {
        _output.Mixed -= OnMixed;
        lock (_queue) _queue.Clear();
    }
}
