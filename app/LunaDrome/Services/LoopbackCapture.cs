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
/// Linux: `parec --monitor-stream` on LunaDrome's own PulseAudio/PipeWire stream (found via
/// `pactl` by our PID), so here too only LunaDrome's audio drives it; system-mix fallback only
/// if the sound server can't monitor single streams. Levels are auto-gained (SpectrumAnalyzer.AutoGain)
/// because every capture path here is post-volume. Needs pulseaudio-utils (parec/pactl). Anything else just leaves it flat.
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

    // Which capture path is active matters (process = LunaDrome only, system = every app), so
    // report it on stderr with LUNADROME_LOG=1 rather than only to an attached debugger.
    private static void Log(string message)
    {
        Debug.WriteLine(message);
        if (Environment.GetEnvironmentVariable("LUNADROME_LOG") == "1")
            Console.Error.WriteLine($"[LunaDrome] {message}");
    }

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
                StartLinux(generation);
        }
        catch (Exception ex)
        {
            Log($"loopback capture unavailable: {ex}");
            Stop();
        }
    }

    public void Stop()
    {
        _generation++;
        IsRunning = false;

        if (_wasapi != null && OperatingSystem.IsWindows())
            StopWasapi();

        StopParec();

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
            Log("visualizer: process loopback (LunaDrome audio only)");
            return;
        }
        catch (Exception ex)
        {
            Log($"visualizer: process loopback unavailable, falling back to SYSTEM loopback (all apps): {ex}");
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
            Log("visualizer: system loopback (all apps)");
        }
        catch (Exception ex)
        {
            Log($"visualizer: system loopback unavailable: {ex}");
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
    //
    // GitHub issue #2: the first version recorded @DEFAULT_MONITOR@ - the whole system mix - so
    // every app's sound moved the visualizer. PulseAudio / PipeWire (via pipewire-pulse) can
    // record a single app's stream instead: `parec --monitor-stream=<sink-input index>`. LibVLC
    // plays in-process, so LunaDrome's stream is the sink input whose application.process.id is
    // our own PID. VLC recreates that stream per track, so a watcher re-resolves it every second.
    // If the sound server can't monitor single streams, fall back to the system mix (logged).

    private const int ParecRate = 44100;
    private readonly object _parecLock = new();
    private int _parecStreamIndex = -1;
    private bool _monitorStreamUnsupported;

    private void StartLinux(int generation)
    {
        _analyzer.SampleRate = ParecRate;
        _ = Task.Run(() => WatchOwnStreamAsync(generation));
    }

    private async Task WatchOwnStreamAsync(int generation)
    {
        var pid = Environment.ProcessId;
        var lastLogged = int.MinValue;

        while (generation == _generation)
        {
            if (_monitorStreamUnsupported)
            {
                EnsureParec(generation, -1); // system mix fallback
            }
            else
            {
                var index = FindOwnSinkInput(pid);
                if (index != lastLogged)
                {
                    Log(index >= 0
                        ? $"visualizer: capturing LunaDrome's own stream (sink input #{index})"
                        : "visualizer: LunaDrome has no audio stream right now (paused/stopped)");
                    lastLogged = index;
                }

                if (index >= 0)
                    EnsureParec(generation, index);
                else
                    StopParec();
            }

            await Task.Delay(1000);
        }
    }

    /// <summary>Index of this process's (preferably un-corked) sink input, or -1.</summary>
    private static int FindOwnSinkInput(int pid)
    {
        string output;
        try
        {
            var psi = new ProcessStartInfo("pactl", "list sink-inputs")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["LC_ALL"] = "C"; // stable, untranslated labels
            using var p = Process.Start(psi);
            if (p == null)
                return -1;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
        }
        catch
        {
            return -1; // pactl missing
        }

        int current = -1, best = -1;
        var corked = false;
        var ours = false;

        void Finish()
        {
            if (current >= 0 && ours && (best < 0 || !corked))
                best = current;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Sink Input #", StringComparison.Ordinal))
            {
                Finish();
                current = int.TryParse(line["Sink Input #".Length..], out var i) ? i : -1;
                corked = false;
                ours = false;
            }
            else if (line.StartsWith("Corked:", StringComparison.Ordinal))
            {
                corked = line.EndsWith("yes", StringComparison.Ordinal);
            }
            else if (line.StartsWith("application.process.id = ", StringComparison.Ordinal))
            {
                ours = line["application.process.id = ".Length..].Trim('"') == pid.ToString();
            }
        }
        Finish();
        return best;
    }

    /// <summary>Make sure parec is recording stream <paramref name="index"/> (-1 = system mix).</summary>
    private void EnsureParec(int generation, int index)
    {
        lock (_parecLock)
        {
            if (generation != _generation)
                return;
            if (_parec != null && !_parec.HasExited && _parecStreamIndex == index)
                return;

            StopParecLocked();

            var psi = new ProcessStartInfo("parec")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (index >= 0)
                psi.ArgumentList.Add($"--monitor-stream={index}");
            else
                psi.ArgumentList.Add("--device=@DEFAULT_MONITOR@");
            foreach (var arg in new[] { "--format=float32le", "--channels=1", $"--rate={ParecRate}", "--latency-msec=20", "--raw" })
                psi.ArgumentList.Add(arg);

            try
            {
                _parec = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log($"visualizer: parec unavailable (install pulseaudio-utils): {ex.Message}");
                _parec = null;
                return;
            }
            if (_parec == null)
                return;

            _parecStreamIndex = index;
            if (index < 0)
                Log("visualizer: parec capturing the system mix (all apps)");

            var process = _parec;
            var stream = process.StandardOutput.BaseStream;
            _parecReader = new Thread(() => ReadParec(stream, process, index)) { IsBackground = true, Name = "parec reader" };
            _parecReader.Start();
        }
    }

    private void StopParec()
    {
        lock (_parecLock)
            StopParecLocked();
    }

    private void StopParecLocked()
    {
        if (_parec == null)
            return;

        try { if (!_parec.HasExited) _parec.Kill(); } catch { /* already gone */ }
        _parec.Dispose();
        _parec = null;
        _parecReader = null; // exits on its own once the pipe closes
        _parecStreamIndex = -1;
        _analyzer.Reset();
    }

    private void ReadParec(Stream stream, Process process, int index)
    {
        var bytes = new byte[4096];
        var samples = new float[bytes.Length / 4];
        var carry = 0;
        long received = 0;
        var started = Stopwatch.StartNew();

        try
        {
            int read;
            while ((read = stream.Read(bytes, carry, bytes.Length - carry)) > 0)
            {
                received += read;
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

        // parec quit on its own almost immediately with no data while monitoring a single stream:
        // the sound server can't do per-stream monitoring - fall back to the system mix.
        if (index >= 0 && received == 0 && started.Elapsed < TimeSpan.FromSeconds(2))
        {
            try
            {
                if (process.HasExited && process.ExitCode != 0 && !_monitorStreamUnsupported)
                {
                    _monitorStreamUnsupported = true;
                    Log($"visualizer: per-stream monitoring not supported (parec exit {process.ExitCode}: {process.StandardError.ReadToEnd().Trim()}), falling back to the system mix");
                }
            }
            catch { /* process disposed */ }
        }
    }

    public void Dispose() => Stop();
}
