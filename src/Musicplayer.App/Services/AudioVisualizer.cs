using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Musicplayer.App.Services;

/// <summary>
/// Captures whatever the system is currently outputting via WASAPI loopback (Player is a WPF
/// MediaElement, which exposes no raw sample data of its own) and turns it into a fixed-size
/// spectrum. <see cref="WasapiLoopbackCapture"/> only exposes a parameterless/MMDevice
/// constructor publicly (confirmed via reflection - its base class supports a configurable
/// buffer size, but that overload isn't forwarded), so its ~100ms default buffer delivers audio
/// in bursts rather than a steady stream - not something this class can fix by asking WASAPI for
/// a smaller buffer. Instead this exposes a thread-safe pull (<see cref="CopyLatestBandsTo"/>)
/// rather than an event: the caller renders on its own steady cadence (e.g. CompositionTarget.
/// Rendering) and just reads whatever the latest computed spectrum is, decoupling animation
/// smoothness from how bursty the underlying audio delivery happens to be.
/// </summary>
public class AudioVisualizer : IDisposable
{
    public const int BandCount = 128;

    private const int FftLength = 1024; // power of two
    private readonly int _fftPow = (int)Math.Log2(FftLength);

    private WasapiLoopbackCapture? _capture;
    private Complex[] _fftBuffer = new Complex[FftLength];
    private int _bufferPos;

    private readonly object _lock = new();
    private readonly float[] _latestBands = new float[BandCount];

    public void Start()
    {
        if (_capture != null)
            return;

        _bufferPos = 0;
        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    public void CopyLatestBandsTo(float[] destination)
    {
        lock (_lock)
            Array.Copy(_latestBands, destination, BandCount);
    }

    public void Stop()
    {
        if (_capture == null)
            return;

        _capture.DataAvailable -= OnDataAvailable;
        try { _capture.StopRecording(); } catch { /* already stopped */ }
        _capture.Dispose();
        _capture = null;
        _bufferPos = 0;

        lock (_lock)
            Array.Clear(_latestBands);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _capture?.WaveFormat;
        if (format == null)
            return;

        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        var bytesPerSample = format.BitsPerSample / 8;
        var channels = Math.Max(1, format.Channels);
        var frameSize = bytesPerSample * channels;
        var frameCount = e.BytesRecorded / frameSize;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * frameSize;
            float sample = isFloat
                ? BitConverter.ToSingle(e.Buffer, offset)
                : BitConverter.ToInt16(e.Buffer, offset) / 32768f;

            _fftBuffer[_bufferPos].X = sample * (float)FastFourierTransform.HannWindow(_bufferPos, FftLength);
            _fftBuffer[_bufferPos].Y = 0;
            _bufferPos++;

            if (_bufferPos < FftLength)
                continue;

            _bufferPos = 0;
            var buffer = _fftBuffer;
            _fftBuffer = new Complex[FftLength];
            FastFourierTransform.FFT(true, _fftPow, buffer);
            var bands = ComputeBands(buffer, format.SampleRate);

            lock (_lock)
                Array.Copy(bands, _latestBands, BandCount);
        }
    }

    // Bands are split on a LOG frequency scale (like a real spectrum analyzer / audioMotion's
    // default), not linear. Linear splitting was the bug behind "only one bar moves": music's
    // energy is overwhelmingly concentrated below ~1kHz, so with 32 *equal-width* frequency
    // bands nearly all of it lands in band 0 (bass) while every higher band - each covering a
    // much wider, energy-sparse slice of the spectrum - stays near silent.
    private static float[] ComputeBands(Complex[] fft, int sampleRate)
    {
        var bands = new float[BandCount];
        var usableBins = FftLength / 2;
        const float minFreq = 40f;
        var maxFreq = Math.Min(16000f, sampleRate / 2f);
        var logMin = MathF.Log10(minFreq);
        var logMax = MathF.Log10(maxFreq);

        for (var band = 0; band < BandCount; band++)
        {
            var freqStart = MathF.Pow(10, logMin + (logMax - logMin) * band / BandCount);
            var freqEnd = MathF.Pow(10, logMin + (logMax - logMin) * (band + 1) / BandCount);

            var binStart = Math.Clamp((int)(freqStart / sampleRate * FftLength), 1, usableBins - 1);
            var binEnd = Math.Clamp((int)(freqEnd / sampleRate * FftLength), binStart, usableBins - 1);

            float sum = 0;
            for (var i = binStart; i <= binEnd; i++)
            {
                var bin = fft[i];
                sum += MathF.Sqrt(bin.X * bin.X + bin.Y * bin.Y);
            }

            var magnitude = sum / (binEnd - binStart + 1);
            var dB = 20f * MathF.Log10(magnitude + 1e-6f);
            bands[band] = Math.Clamp((dB + 60f) / 60f, 0f, 1f);
        }

        return bands;
    }

    public void Dispose() => Stop();
}
