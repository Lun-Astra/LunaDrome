using System.Diagnostics;
using System.Runtime.Versioning;
using LunaDrome.Core.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LunaDrome.Services;

/// <summary>
/// Feeds what LunaDrome is playing (downmixed to mono) into a <see cref="SpectrumAnalyzer"/>.
/// The playback engine (Uno's MediaPlayer) exposes no raw samples, so - like the WPF app - the
/// visualizer listens to the audio output instead of the player itself.
///
/// Windows: WASAPI *process* loopback on our own PID first (Windows 10 2004+), so only
/// LunaDrome's audio drives it - the player's LibVLC backend renders in-process. Falls back to
/// whole-system loopback (every app's audio) if that isn't available.
/// Linux: PulseAudio/PipeWire's monitor source via `parec` (pulseaudio-utils; works on PipeWire
/// through pipewire-pulse) - whole-system, like the fallback. Anything else, or a missing
/// `parec`, just leaves the spectrum flat rather than failing.
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    private readonly SpectrumAnalyzer _analyzer;

    private IDisposable? _wasapi;
    private WaveFormat? _wasapiFormat;
    private Process? _parec;
    private Thread? _parecReader;
    private float[] _mono = Array.Empty<float>();

    // Bumped on every Start/Stop so an async Windows start that finishes after Stop() (or after a
    // newer Start) knows to throw its recorder away instead of installing it.
    private int _generation;

    public LoopbackCapture(SpectrumAnalyzer analyzer) => _analyzer = analyzer;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning)
            return;

        IsRunning = true;
        var generation = ++_generation;
        _analyzer.Reset();

        try
        {
            if (OperatingSystem.IsWindows())
                _ = StartWindowsAsync(generation);
            else if (OperatingSystem.IsLinux())
                StartParec();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loopback capture unavailable: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        _generation++;
        IsRunning = false;

        if (_wasapi != null && OperatingSystem.IsWindows())
            StopWasapi();

        if (_parec != null)
        {
            try { if (!_parec.HasExited) _parec.Kill(); } catch { /* already gone */ }
            _parec.Dispose();
            _parec = null;
            _parecReader = null; // exits on its own once the pipe closes
        }

        _analyzer.Reset();
    }

    // --- Windows ---

    [SupportedOSPlatform("windows")]
    private async Task StartWindowsAsync(int generation)
    {
        IDisposable? recorder = null;
        WaveFormat? format = null;

        try
        {
            // Process loopback has no mix format of its own to report, so request one explicitly;
            // the audio engine converts to it.
            format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            var processRecorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(format)
                .BuildAsync();
            processRecorder.DataAvailable += (buffer, _, _, _) => OnWasapiBytes(buffer);
            recorder = processRecorder;

            if (!Install(generation, recorder, format))
                return;

            processRecorder.StartRecording();
            Debug.WriteLine("Visualizer: process loopback");
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Process loopback unavailable, falling back to system loopback: {ex.Message}");
            if (_wasapi == recorder)
                _wasapi = null;
            recorder?.Dispose();
        }

        try
        {
#pragma warning disable CS0618 // the replacement's system-wide mode needs explicit device plumbing; this still works
            var system = new WasapiLoopbackCapture();
#pragma warning restore CS0618
            system.DataAvailable += (_, e) => OnWasapiBytes(e.Buffer.AsSpan(0, e.BytesRecorded));
            if (!Install(generation, system, system.WaveFormat))
                return;

            system.StartRecording();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"System loopback unavailable: {ex.Message}");
        }
    }

    private bool Install(int generation, IDisposable recorder, WaveFormat format)
    {
        if (generation != _generation)
        {
            recorder.Dispose();
            return false;
        }

        _wasapiFormat = format;
        _analyzer.SampleRate = format.SampleRate;
        _wasapi = recorder;
        return true;
    }

    [SupportedOSPlatform("windows")]
    private void StopWasapi()
    {
        var recorder = _wasapi;
        _wasapi = null;
        _wasapiFormat = null;

        try
        {
            switch (recorder)
            {
                case WasapiRecorder r: r.StopRecording(); break;
#pragma warning disable CS0618
                case WasapiLoopbackCapture c: c.StopRecording(); break;
#pragma warning restore CS0618
            }
        }
        catch { /* already stopped */ }

        recorder?.Dispose();
    }

    private void OnWasapiBytes(ReadOnlySpan<byte> buffer)
    {
        var format = _wasapiFormat;
        if (format == null)
            return;

        // The shared-mode mix format is 32-bit float in practice (sometimes reported as
        // WaveFormatExtensible rather than IeeeFloat), so treat any 32-bit format as float.
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32;
        var bytesPerSample = format.BitsPerSample / 8;
        var channels = Math.Max(1, format.Channels);
        var frameSize = bytesPerSample * channels;
        var frameCount = buffer.Length / frameSize;

        if (_mono.Length < frameCount)
            _mono = new float[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * frameSize;
            float sum = 0;
            for (var ch = 0; ch < channels; ch++)
            {
                var o = offset + ch * bytesPerSample;
                sum += isFloat
                    ? BitConverter.ToSingle(buffer.Slice(o, 4))
                    : BitConverter.ToInt16(buffer.Slice(o, 2)) / 32768f;
            }
            _mono[frame] = sum / channels;
        }

        _analyzer.AddSamples(_mono.AsSpan(0, frameCount));
    }

    // --- Linux ---

    private const int ParecRate = 44100;

    private void StartParec()
    {
        var psi = new ProcessStartInfo("parec")
        {
            ArgumentList =
            {
                "--device=@DEFAULT_MONITOR@",
                "--format=float32le",
                "--channels=1",
                $"--rate={ParecRate}",
                "--latency-msec=20",
                "--raw"
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _parec = Process.Start(psi) ?? throw new InvalidOperationException("parec failed to start");
        _analyzer.SampleRate = ParecRate;

        var stream = _parec.StandardOutput.BaseStream;
        _parecReader = new Thread(() => ReadParec(stream)) { IsBackground = true, Name = "parec reader" };
        _parecReader.Start();
    }

    private void ReadParec(Stream stream)
    {
        var bytes = new byte[4096];
        var samples = new float[bytes.Length / 4];
        var carry = 0;

        try
        {
            int read;
            while ((read = stream.Read(bytes, carry, bytes.Length - carry)) > 0)
            {
                var total = carry + read;
                var count = total / 4;
                Buffer.BlockCopy(bytes, 0, samples, 0, count * 4);
                _analyzer.AddSamples(samples.AsSpan(0, count));

                // Keep any partial trailing sample for the next read.
                carry = total - count * 4;
                if (carry > 0)
                    Buffer.BlockCopy(bytes, count * 4, bytes, 0, carry);
            }
        }
        catch { /* pipe closed on Stop() */ }
    }

    public void Dispose() => Stop();
}
