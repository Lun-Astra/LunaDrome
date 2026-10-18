namespace LunaDrome.Core.Sendspin.Decoding;

/// <summary>
/// Streaming sample-rate converter for interleaved 16-bit audio: windowed-sinc (Kaiser)
/// interpolation from a precomputed kernel table, with the cutoff lowered when going down in
/// rate so nothing above the new Nyquist folds back. Good enough to be inaudible for music
/// (stopband well under -80 dB), cheap enough for a phone.
/// </summary>
public sealed class Resampler
{
    private const int HalfTaps = 24;          // zero crossings each side (at the input rate when upsampling)
    private const int TableResolution = 512;  // kernel samples per zero crossing
    private const double Beta = 8.6;          // Kaiser window shape (~ -90 dB sidelobes)

    private readonly int _channels;
    private readonly double _step;            // input samples per output sample
    private readonly double _cutoff;          // relative to the input Nyquist
    private readonly int _halfWidth;          // input samples each side the kernel spans
    private readonly float[] _table;
    private float[] _history;                 // interleaved input not yet fully used
    private int _historyFrames;
    // The next output's input position is computed from integers each time (output index × step,
    // minus the input frames dropped), so results don't depend on how the input was chunked.
    private long _outputIndex;
    private long _dropped;

    public Resampler(int inputRate, int outputRate, int channels)
    {
        InputRate = inputRate;
        OutputRate = outputRate;
        _channels = channels;
        _step = (double)inputRate / outputRate;
        _cutoff = Math.Min(1.0, 1.0 / _step) * 0.94;   // a little below Nyquist: room for the transition band
        _halfWidth = (int)Math.Ceiling(HalfTaps / _cutoff);
        _table = BuildTable();
        _history = new float[(_halfWidth * 2 + 4096) * channels];
        // Start "in the past" so the first output sample sits on the first input sample.
        _historyFrames = _halfWidth;
    }

    public int InputRate { get; }
    public int OutputRate { get; }
    public bool IsPassThrough => InputRate == OutputRate;

    /// <summary>Converts the next piece; output length varies with the ratio.</summary>
    public short[] Process(ReadOnlySpan<short> input)
    {
        if (IsPassThrough) return input.ToArray();
        var frames = input.Length / _channels;
        EnsureCapacity(_historyFrames + frames);
        for (var i = 0; i < frames * _channels; i++)
            _history[_historyFrames * _channels + i] = input[i];
        _historyFrames += frames;

        var output = new List<short>((int)(frames / _step + 2) * _channels);
        var scale = _cutoff;   // unity gain at DC
        while (true)
        {
            var position = _outputIndex * _step + _halfWidth - _dropped;
            if (position + _halfWidth >= _historyFrames) break;
            var center = (int)Math.Floor(position);
            var frac = position - center;
            for (var ch = 0; ch < _channels; ch++)
            {
                double sum = 0;
                for (var k = -_halfWidth + 1; k <= _halfWidth; k++)
                {
                    var x = (k - frac) * _cutoff;   // distance in zero crossings
                    sum += _history[(center + k) * _channels + ch] * Kernel(x);
                }
                output.Add((short)Math.Clamp(Math.Round(sum * scale), short.MinValue, short.MaxValue));
            }
            _outputIndex++;
        }
        // Drop input no longer needed (keep the kernel's reach behind the next position).
        var next = _outputIndex * _step + _halfWidth - _dropped;
        var keepFrom = Math.Max(0, (int)Math.Floor(next) - _halfWidth);
        if (keepFrom > 0)
        {
            Array.Copy(_history, keepFrom * _channels, _history, 0, (_historyFrames - keepFrom) * _channels);
            _historyFrames -= keepFrom;
            _dropped += keepFrom;
        }
        return output.ToArray();
    }

    /// <summary>Forgets buffered input (after a seek).</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _historyFrames = _halfWidth;
        _outputIndex = 0;
        _dropped = 0;
    }

    private float Kernel(double x)
    {
        var ax = Math.Abs(x);
        if (ax >= HalfTaps) return 0;
        var t = ax * TableResolution;
        var i = (int)t;
        var f = (float)(t - i);
        return _table[i] + (_table[i + 1] - _table[i]) * f;
    }

    private float[] BuildTable()
    {
        var table = new float[HalfTaps * TableResolution + 2];
        var denominator = BesselI0(Beta);
        for (var i = 0; i < table.Length; i++)
        {
            var x = (double)i / TableResolution;
            if (x >= HalfTaps) { table[i] = 0; continue; }
            var sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
            var r = x / HalfTaps;
            var window = BesselI0(Beta * Math.Sqrt(1 - r * r)) / denominator;
            table[i] = (float)(sinc * window);
        }
        return table;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        for (var k = 1; k < 50; k++)
        {
            term *= (x / (2 * k)) * (x / (2 * k));
            sum += term;
            if (term < 1e-12 * sum) break;
        }
        return sum;
    }

    private void EnsureCapacity(int frames)
    {
        if (frames * _channels <= _history.Length) return;
        Array.Resize(ref _history, Math.Max(_history.Length * 2, frames * _channels));
    }
}
