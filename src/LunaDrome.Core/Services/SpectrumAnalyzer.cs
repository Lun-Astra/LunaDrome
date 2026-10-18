namespace LunaDrome.Core.Services;

/// <summary>
/// Turns a stream of mono float samples into a fixed-size, log-frequency-scaled spectrum.
/// Ported from the WPF app's AudioVisualizer (band count and dB mapping; a bigger FFT since #32), but
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

    // #32: with a 1024-sample FFT a bin is ~43 Hz wide, narrower than every log band below
    // ~900 Hz - so the left half of the 128 bands repeated the same few bins and the bass drew
    // one broad, flat-topped hump. 4096 samples gives ~11 Hz bins (and narrow bands are
    // interpolated between bins); a new spectrum still comes every 1024 samples (overlapping
    // windows), so the update rate stays ~43 per second.
    public const int FftLength = 4096; // power of two
    private const int HopLength = 1024;

    // #32: music has far more energy in the bass than in the highs, so after auto-gain the left
    // side towered and the right side barely moved. Tilting the bands +3 dB per octave (around
    // 1 kHz) evens that out, the way spectrum analyzers usually do.
    private const float TiltDbPerOctave = 3f;

    private static readonly float[] HannWindow = BuildHannWindow();

    private readonly float[] _ring = new float[FftLength];
    private int _ringPos;
    private int _ringFilled;
    private int _sinceLastSpectrum;
    private readonly float[] _re = new float[FftLength];
    private readonly float[] _im = new float[FftLength];
    private readonly float[] _magnitudes = new float[FftLength / 2];
    private readonly float[] _bands = new float[BandCount];
    private readonly float[] _bandDb = new float[BandCount];

    private readonly object _lock = new();
    private readonly float[] _latestBands = new float[BandCount];

    public int SampleRate { get; set; } = 44100;

    /// <summary>
    /// Automatic gain: scale so the recent loudest band sits near full height, making the
    /// visualizer independent of playback volume (GitHub issue #1: on both Windows and Linux it
    /// barely moved below ~90% volume, because the captured signal is taken after LunaDrome's
    /// volume is applied). On by default.
    /// </summary>
    public bool AutoGain { get; set; } = true;

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
            _ring[_ringPos] = sample;
            _ringPos = (_ringPos + 1) % FftLength;
            if (_ringFilled < FftLength)
                _ringFilled++;
            if (++_sinceLastSpectrum < HopLength || _ringFilled < FftLength)
                continue;

            _sinceLastSpectrum = 0;
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

        var decayPerFrame = PeakDecayDbPerSecond * HopLength / Math.Max(1, SampleRate);
        _peakDb = Math.Max(frameMax, _peakDb - decayPerFrame);

        if (_peakDb < SilenceDb)
            return 0f;

        return Math.Clamp(TargetPeakDb - _peakDb, 0f, MaxBoostDb);
    }

    public void Reset()
    {
        _peakDb = -60f;
        Array.Clear(_ring);
        _ringPos = 0;
        _ringFilled = 0;
        _sinceLastSpectrum = 0;
        lock (_lock)
            Array.Clear(_latestBands);
    }

    private void ComputeSpectrum()
    {
        // The ring holds the last FftLength samples; the oldest one sits at _ringPos.
        for (var i = 0; i < FftLength; i++)
        {
            _re[i] = _ring[(_ringPos + i) % FftLength] * HannWindow[i];
            _im[i] = 0;
        }

        Fft(_re, _im);

        // NAudio's FFT (which the WPF app's dB thresholds were tuned against) scales its output
        // by 1/N; match that so a tone's level doesn't depend on the FFT size.
        const float scale = 1f / FftLength;
        var usableBins = FftLength / 2;
        for (var i = 0; i < usableBins; i++)
            _magnitudes[i] = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]) * scale;

        var binHz = SampleRate / (float)FftLength;
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
            var freqCenter = MathF.Sqrt(freqStart * freqEnd);

            float magnitude;
            var binStart = (int)MathF.Ceiling(freqStart / binHz);
            var binEnd = (int)MathF.Floor(freqEnd / binHz);
            if (binEnd < binStart)
            {
                // Narrower than a bin: read the spectrum at the band's center, between two bins.
                var pos = Math.Clamp(freqCenter / binHz, 1f, usableBins - 2);
                var low = (int)pos;
                var t = pos - low;
                magnitude = _magnitudes[low] * (1 - t) + _magnitudes[low + 1] * t;
            }
            else
            {
                binStart = Math.Clamp(binStart, 1, usableBins - 1);
                binEnd = Math.Clamp(binEnd, binStart, usableBins - 1);
                float sum = 0;
                for (var i = binStart; i <= binEnd; i++)
                    sum += _magnitudes[i];
                magnitude = sum / (binEnd - binStart + 1);
            }

            _bandDb[band] = 20f * MathF.Log10(magnitude + 1e-6f) + TiltDbPerOctave * MathF.Log2(freqCenter / 1000f);
        }

        var gainDb = AutoGain ? UpdateAutoGain(_bandDb) : 0f;
        for (var band = 0; band < BandCount; band++)
            _bands[band] = Math.Clamp((_bandDb[band] + gainDb + 60f) / 60f, 0f, 1f);

        lock (_lock)
            Array.Copy(_bands, _latestBands, BandCount);
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
