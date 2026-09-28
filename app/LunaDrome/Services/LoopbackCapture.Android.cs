using LunaDrome.Core.Services;

namespace LunaDrome.Services;

/// <summary>Android stand-in for the desktop visualizer capture (WASAPI loopback on Windows,
/// PulseAudio on Linux): capturing the app's own output needs a different API on Android
/// (android.media.audiofx.Visualizer), so for now the visualizer just stays flat there.</summary>
public sealed class LoopbackCapture : IDisposable
{
    public LoopbackCapture(SpectrumAnalyzer analyzer) { }

    public bool IsRunning { get; private set; }

    public void Start() => IsRunning = true;

    public void Stop() => IsRunning = false;

    public void Dispose() => Stop();
}
