namespace LunaDrome.Core.Sendspin;

/// <summary>
/// Decoded audio from one player (LunaDrome's VLC player, or the second one during a
/// crossfade): interleaved 16-bit stereo at <see cref="SendspinOutput.SampleRate"/>. The player
/// writes it as it decodes, at real-time pace; <see cref="SendspinOutput"/> mixes all sources.
/// </summary>
public sealed class SendspinSource
{
    private const int MaxBufferedFrames = SendspinOutput.SampleRate * 5;
    /// <summary>How far the player may run ahead of what's been sent. VLC decodes well ahead
    /// when it hands audio to a callback; holding it here keeps the speakers close to the
    /// screen (and pause quick) instead of a second or more behind.</summary>
    internal const int MaxAheadFrames = SendspinOutput.SampleRate / 4;   // 250 ms
    private static readonly TimeSpan MaxBlock = TimeSpan.FromMilliseconds(200);
    private const int Channels = SendspinOutput.Channels;

    private readonly SendspinOutput _output;
    private readonly object _lock = new();
    private short[] _ring = new short[SendspinOutput.SampleRate * Channels];
    private int _start, _count;   // in samples (not frames)
    // After a track ends, its last buffered samples (the "tail") must still be heard, even if the
    // player flushes while it opens the next track. _tail counts how many of the oldest samples
    // in the buffer are that tail; _drainedAtUs is when the track ended.
    private int _tail;
    private long _drainedAtUs = long.MinValue / 2;   // "long ago" without overflowing the subtraction
    private static readonly long TrackChangeUs = 1_500_000;
    private volatile float _gain = 1f;
    private volatile bool _muted;

    private static int _nextId;
    private readonly int _id = Interlocked.Increment(ref _nextId);

    internal SendspinSource(SendspinOutput output) => _output = output;

    public SourceState State { get; private set; } = SourceState.Idle;

    /// <summary>Linear amplitude, 1 = full scale (the player's volume; the crossfade moves it).</summary>
    public float Gain { get => _gain; set => _gain = Math.Clamp(value, 0f, 2f); }
    public bool Muted { get => _muted; set => _muted = value; }

    internal int BufferedFrames { get { lock (_lock) return _count / Channels; } }
    private int BufferedFramesUnlocked => _count / Channels;

    /// <summary>Appends decoded samples (interleaved, native-endian 16-bit).</summary>
    public void Write(ReadOnlySpan<short> samples)
    {
        lock (_lock)
        {
            if (_count + samples.Length > _ring.Length)
                Grow(_count + samples.Length);
            // Never let a stalled output pile up minutes of audio: keep the newest.
            var overflow = _count + samples.Length - MaxBufferedFrames * Channels;
            if (overflow > 0) Discard(overflow - overflow % Channels);
            var end = (_start + _count) % _ring.Length;
            var first = Math.Min(samples.Length, _ring.Length - end);
            samples[..first].CopyTo(_ring.AsSpan(end, first));
            samples[first..].CopyTo(_ring.AsSpan(0, samples.Length - first));
            _count += samples.Length;
            // New audio means playing - unless it's paused: a write still in flight when the pause
            // came must not undo it (only Resume does).
            if (State != SourceState.Paused) State = SourceState.Playing;

            // Backpressure: block the player's audio thread (VLC allows it) until the output has
            // taken enough. Bounded, and only while the output is consuming, so it can't freeze
            // playback when nothing streams.
            var deadline = Environment.TickCount64 + (long)MaxBlock.TotalMilliseconds;
            while (_count / Channels > MaxAheadFrames && State == SourceState.Playing && _output.IsConsuming
                   && Environment.TickCount64 < deadline)
                Monitor.Wait(_lock, 20);
        }
    }

    /// <summary>Paused: what is already buffered still plays out, nothing new arrives.</summary>
    public void Pause()
    {
        lock (_lock) if (State == SourceState.Playing) State = SourceState.Paused;
        _output.Trace($"source {_id}: pause");
    }

    public void Resume()
    {
        lock (_lock) if (State == SourceState.Paused) State = SourceState.Playing;
        _output.Trace($"source {_id}: resume");
    }

    /// <summary>Seek or track jump: the buffered audio is stale. The speakers drop theirs too
    /// when this is the only source playing.</summary>
    public void Flush()
    {
        var softened = false;
        lock (_lock)
        {
            // The player also flushes as it closes a track that already ended: that tail is
            // real audio still to be heard, so only a flush mid-track (seek, skip) drops audio.
            if (State is SourceState.Draining or SourceState.Idle)
            {
                _output.Trace($"source {_id}: flush after the end ignored ({BufferedFramesUnlocked} frames kept)");
                return;
            }
            // ...and once more as it starts the next one: then only that new audio goes (the
            // player sends it again), the previous track's tail stays and the speakers keep theirs.
            if (_tail > 0 || SendspinClock.NowUs - _drainedAtUs < TrackChangeUs)
            {
                _count = Math.Min(_count, _tail);
                softened = true;
            }
            else
            {
                _start = 0;
                _count = 0;
            }
            // A flush ends a pause too (a new song opened while paused never sends "resume");
            // the next audio written makes it playing again.
            if (State == SourceState.Paused) State = SourceState.Idle;
            Monitor.PulseAll(_lock);
        }
        if (softened)
        {
            _output.Trace($"source {_id}: flush at a track change - the previous track's tail is kept");
            return;
        }
        _output.Trace($"source {_id}: flush");
        _output.OnSourceFlushed(this);
    }

    /// <summary>The track ended: let the buffered audio play out, then go idle.</summary>
    public void Drain()
    {
        lock (_lock)
        {
            if (State != SourceState.Idle) State = SourceState.Draining;
            _tail = _count;
            _drainedAtUs = SendspinClock.NowUs;
        }
        _output.Trace($"source {_id}: drain (track ended)");
    }

    /// <summary>The player stopped (output switched away, app closing).</summary>
    public void Stop()
    {
        lock (_lock) { _start = 0; _count = 0; _tail = 0; State = SourceState.Idle; }
    }

    /// <summary>Adds up to <paramref name="frames"/> frames into <paramref name="mix"/> with this
    /// source's gain; returns how many frames it had.</summary>
    internal int MixInto(Span<int> mix, int frames)
    {
        lock (_lock)
        {
            var samples = Math.Min(frames * Channels, _count);
            var gain = _muted ? 0f : _gain;
            for (var i = 0; i < samples; i++)
                mix[i] += (int)(_ring[(_start + i) % _ring.Length] * gain);
            _start = (_start + samples) % _ring.Length;
            _count -= samples;
            _tail = Math.Max(0, _tail - samples);
            if (_count == 0 && State == SourceState.Draining) State = SourceState.Idle;
            if (samples > 0) Monitor.PulseAll(_lock);   // room again for a waiting Write
            return samples / Channels;
        }
    }

    private void Discard(int samples)
    {
        samples = Math.Min(samples, _count);
        _start = (_start + samples) % _ring.Length;
        _count -= samples;
        _tail = Math.Max(0, _tail - samples);
    }

    private void Grow(int needed)
    {
        var size = Math.Min(Math.Max(_ring.Length * 2, needed), MaxBufferedFrames * Channels + needed);
        var bigger = new short[size];
        for (var i = 0; i < _count; i++) bigger[i] = _ring[(_start + i) % _ring.Length];
        _ring = bigger;
        _start = 0;
    }
}

public enum SourceState { Idle, Playing, Paused, Draining }
