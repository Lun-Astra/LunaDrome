namespace LunaDrome.Core.Services;

/// <summary>
/// Turns a stream of mono float samples into a fixed-size, log-frequency-scaled spectrum.
/// Ported from the WPF app's AudioVisualizer (same FFT size, band count and dB mapping), but
/// with its own small radix-2 FFT instead of NAudio.Dsp so Core stays free of any
/// platform-specific audio dependency - the capture side (WASAPI, PulseAudio, ...) lives in the
/// app and just calls <see cref="AddSamples"/>.
///
/// Thread-safety: <see cref="AddSamples"/> is called from the capture thread, and
/// <see cref="CopyLatestBandsTo"/> from the UI's render loop - the render side pulls whatever was
/// most recently computed instead of reacting to each capture callback, so animation smoothness
/// doesn't depend on how bursty audio delivery is.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int BandCount = 128;

    private const int FftLength = 1024; // power of two

    private static readonly float[] HannWindow = BuildHannWindow();

    private readonly float[] _window = new float[FftLength];
    private readonly float[] _re = new float[FftLength];
    private readonly float[] _im = new float[FftLength];
    private int _windowPos;

    private readonly object _lock = new();
    private readonly float[] _latestBands = new float[BandCount];

    public int SampleRate { get; set; } = 44100;

    /// <summary>
    /// Automatic gain: scale so the recent loudest band sits near full height, making the
    /// visualizer independent of playback volume (GitHub issue #1: on Linux it barely moved below
    /// ~90% volume, because the captured signal is taken after the volume is applied).
    /// Off by default - the Windows capture was tuned without it.
    /// </summary>
    public bool AutoGain { get; set; }

    // Peak-follower for AutoGain: jumps up to a louder peak immediately, decays slowly so quiet
    // passages in a song stay quieter than loud ones instead of all being pumped to full height.
    private float _peakDb = -60f;
    private const float PeakDecayDbPerSecond = 6f;
    private const float TargetPeakDb = -6f;   // loudest recent band lands at 90% height
    private const float MaxBoostDb = 40f;     // never amplify more than this...
    private const float SilenceDb = -85f;     // ...and never pump up (near-)silence

    public void AddSamples(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _window[_windowPos++] = sample;
            if (_windowPos < FftLength)
                continue;

            _windowPos = 0;
            ComputeSpectrum();
        }
    }

    public void CopyLatestBandsTo(float[] destination)
    {
        lock (_lock)
            Array.Copy(_latestBands, destination, BandCount);
    }

    private float UpdateAutoGain(float[] bandDb)
    {
        var frameMax = float.MinValue;
        foreach (var db in bandDb)
            frameMax = Math.Max(frameMax, db);

        var decayPerFrame = PeakDecayDbPerSecond * FftLength / Math.Max(1, SampleRate);
        _peakDb = Math.Max(frameMax, _peakDb - decayPerFrame);

        if (_peakDb < SilenceDb)
            return 0f;

        return Math.Clamp(TargetPeakDb - _peakDb, 0f, MaxBoostDb);
    }

    public void Reset()
    {
        _peakDb = -60f;
        _windowPos = 0;
        lock (_lock)
            Array.Clear(_latestBands);
    }

    private void ComputeSpectrum()
    {
        for (var i = 0; i < FftLength; i++)
        {
            _re[i] = _window[i] * HannWindow[i];
            _im[i] = 0;
        }

        Fft(_re, _im);

        // NAudio's FFT (which the WPF app's dB thresholds were tuned against) scales its output
        // by 1/N; match that so the same -60..0 dB window produces the same-looking movement.
        const float scale = 1f / FftLength;

        var bands = new float[BandCount];
        var bandDb = new float[BandCount];
        var usableBins = FftLength / 2;
        const float minFreq = 40f;
        var maxFreq = Math.Min(16000f, SampleRate / 2f);
        var logMin = MathF.Log10(minFreq);
        var logMax = MathF.Log10(maxFreq);

        // Log-scale bands, not linear - music's energy sits mostly below ~1kHz, so equal-width
        // bands would leave nearly everything in band 0 and the rest near-silent.
        for (var band = 0; band < BandCount; band++)
        {
            var freqStart = MathF.Pow(10, logMin + (logMax - logMin) * band / BandCount);
            var freqEnd = MathF.Pow(10, logMin + (logMax - logMin) * (band + 1) / BandCount);

            var binStart = Math.Clamp((int)(freqStart / SampleRate * FftLength), 1, usableBins - 1);
            var binEnd = Math.Clamp((int)(freqEnd / SampleRate * FftLength), binStart, usableBins - 1);

            float sum = 0;
            for (var i = binStart; i <= binEnd; i++)
                sum += MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]) * scale;

            var magnitude = sum / (binEnd - binStart + 1);
            bandDb[band] = 20f * MathF.Log10(magnitude + 1e-6f);
        }

        var gainDb = AutoGain ? UpdateAutoGain(bandDb) : 0f;
        for (var band = 0; band < BandCount; band++)
            bands[band] = Math.Clamp((bandDb[band] + gainDb + 60f) / 60f, 0f, 1f);

        lock (_lock)
            Array.Copy(bands, _latestBands, BandCount);
    }

    // In-place iterative radix-2 Cooley-Tukey FFT.
    private static void Fft(float[] re, float[] im)
    {
        var n = re.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wRe = (float)Math.Cos(angle);
            var wIm = (float)Math.Sin(angle);

            for (var i = 0; i < n; i += len)
            {
                float curRe = 1, curIm = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k;
                    var b = a + len / 2;

                    var tRe = re[b] * curRe - im[b] * curIm;
                    var tIm = re[b] * curIm + im[b] * curRe;

                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;

                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }

    private static float[] BuildHannWindow()
    {
        var w = new float[FftLength];
        for (var i = 0; i < FftLength; i++)
            w[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftLength - 1))));
        return w;
    }
}
