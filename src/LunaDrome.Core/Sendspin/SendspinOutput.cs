using System.Text.Json.Nodes;

namespace LunaDrome.Core.Sendspin;

/// <summary>
/// LunaDrome as a Sendspin server: mixes its <see cref="SendspinSource"/>s and streams the
/// result, timestamped, to every connected speaker so they all play in sync. The sources are
/// paced by the players (real time); a pump thread cuts the mix into 20 ms chunks on one
/// continuous timeline and sends each a fixed lead ahead, which is what the speakers buffer.
/// </summary>
public sealed class SendspinOutput : IAsyncDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public static readonly SendspinFormat PcmFormat = new("pcm", SampleRate, Channels, 16);
    /// <summary>Lossless and about half of PCM's bitrate; one FLAC frame per chunk.</summary>
    public static readonly SendspinFormat FlacFormat = new("flac", SampleRate, Channels, 16);

    internal const int ChunkFrames = SampleRate / 50;                 // 20 ms
    private const long ChunkUs = ChunkFrames * 1_000_000L / SampleRate;
    private const int PrebufferFrames = SampleRate / 10;              // wait for 100 ms before (re)starting
    private const long MinLeadUs = 200_000, MaxLeadUs = 5_000_000;
    private static readonly TimeSpan EndStreamAfterIdle = TimeSpan.FromSeconds(30);

    private readonly SendspinCredentials _credentials;
    private readonly string _serverId, _serverName;
    private readonly object _lock = new();
    private readonly List<SendspinSource> _sources = [];
    private readonly List<SendspinSpeaker> _speakers = [];
    private readonly Thread _pump;
    private readonly CancellationTokenSource _cts = new();
    private readonly int[] _mix = new int[ChunkFrames * Channels];
    private readonly short[] _pcm = new short[ChunkFrames * Channels];

    // Timeline (pump thread + _lock).
    private volatile bool _running;
    private long _wallStartUs, _timelineStartUs, _framesOut, _leadUs;
    private long _nextFreeUs;          // where already-sent audio ends; a restart continues after it
    private long _idleSinceUs;
    private bool _groupPlaying;
    private JsonObject? _metadata, _controller;

    public SendspinOutput(SendspinCredentials credentials, string serverName)
    {
        _credentials = credentials;
        _serverId = credentials.LegacyServerId;   // also the group id
        _serverName = serverName;
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "Sendspin pump", Priority = ThreadPriority.AboveNormal };
        _pump.Start();
    }

    /// <summary>The connected speakers (a snapshot).</summary>
    public IReadOnlyList<SendspinSpeaker> Speakers { get { lock (_lock) return _speakers.ToList(); } }

    /// <summary>How far behind the player the speakers are: the send lead plus what the
    /// sources may hold (see <see cref="SendspinSource.MaxAheadFrames"/>).</summary>
    public TimeSpan Latency { get { lock (_lock) return TimeSpan.FromTicks((_running ? _leadUs : ComputeLeadUs()) * 10 + SendspinSource.MaxAheadFrames * 10_000_000L / SampleRate); } }

    /// <summary>True while the pump takes audio from the sources (they may then hold the player back).</summary>
    internal bool IsConsuming => _running;

    /// <summary>Diagnostics (pause/seek/track events, timeline starts and stops); not per chunk.</summary>
    public Action<string>? Log { get; set; }

    internal void Trace(string message) => Log?.Invoke("sendspin: " + message);

    /// <summary>Each chunk as it goes out, with the time the speakers play it (see
    /// <see cref="SendspinClock"/>); called on the pump thread, so keep it quick.</summary>
    public event MixedAudioHandler? Mixed;

    public event Action? SpeakersChanged;
    /// <summary>A speaker's play/pause/next/previous/stop button (volume and mute are handled here).</summary>
    public event Action<string>? ControllerCommand;
    /// <summary>A speaker dropped off (string = reason).</summary>
    public event Action<SendspinSpeaker, string>? SpeakerLost;

    public SendspinSource CreateSource()
    {
        var source = new SendspinSource(this);
        lock (_lock) _sources.Add(source);
        return source;
    }

    public void RemoveSource(SendspinSource source)
    {
        lock (_lock) _sources.Remove(source);
    }

    /// <summary>Connects to a speaker and adds it to the group (it starts playing along).</summary>
    public async Task<SendspinSpeaker> AddSpeakerAsync(Uri url, CancellationToken cancellationToken)
    {
        var speaker = new SendspinSpeaker(url);
        speaker.StateChanged += OnSpeakerStateChanged;
        speaker.ControllerCommand += OnSpeakerCommand;
        speaker.Closed += OnSpeakerClosed;
        try
        {
            await speaker.ConnectAsync(_credentials, _serverName, forPlayback: true, cancellationToken).ConfigureAwait(false);
            if (speaker.NeedsPairing)
                throw new SendspinPairingRequiredException(speaker);
            if (speaker.IsPlayer && FormatFor(speaker) == null)
                throw new SendspinProtocolException(
                    $"{speaker.Name} doesn't accept {PcmFormat} or {FlacFormat}, the formats LunaDrome sends.");
        }
        catch (SendspinPairingRequiredException)
        {
            throw;   // the connection stays open: pairing runs on it (the caller owns it now)
        }
        catch
        {
            await speaker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return await AdmitAsync(speaker).ConfigureAwait(false);
    }

    /// <summary>Adds a connected speaker that may play (also after pairing) to the group.</summary>
    public async Task<SendspinSpeaker> AdmitAsync(SendspinSpeaker speaker)
    {
        speaker.StateChanged -= OnSpeakerStateChanged;
        speaker.ControllerCommand -= OnSpeakerCommand;
        speaker.Closed -= OnSpeakerClosed;
        speaker.StateChanged += OnSpeakerStateChanged;
        speaker.ControllerCommand += OnSpeakerCommand;
        speaker.Closed += OnSpeakerClosed;
        List<SendspinSpeaker> replaced;
        lock (_lock)
        {
            replaced = _speakers.Where(s => s.ClientId == speaker.ClientId).ToList();
            _speakers.RemoveAll(replaced.Contains);
            _speakers.Add(speaker);
            speaker.SendGroupUpdate(_groupPlaying, _serverId, _serverName);
            speaker.SendServerState(_metadata, _controller ?? DefaultController());
            // Joining mid-song: it gets chunks from now on (all timestamped ahead, so it starts in sync).
            if (_running && MayStream(speaker)) speaker.StartStream(FormatFor(speaker)!);
        }
        foreach (var old in replaced) await old.DisposeAsync().ConfigureAwait(false);
        SpeakersChanged?.Invoke();
        return speaker;
    }

    public async Task RemoveSpeakerAsync(SendspinSpeaker speaker)
    {
        lock (_lock)
        {
            if (!_speakers.Remove(speaker)) return;
            speaker.EndStream();
            speaker.SendGroupUpdate(false, _serverId, _serverName);
        }
        await speaker.DisposeAsync().ConfigureAwait(false);
        SpeakersChanged?.Invoke();
    }

    /// <summary>Now-playing info for speakers with a display. <paramref name="positionMs"/> is
    /// where the player is now; it's stamped for when the speakers actually play it.</summary>
    public void SetMetadata(string? title, string? artist, string? album, string? albumArtist, string? artworkUrl,
        int? year, int? track, long positionMs, long durationMs, bool playing)
    {
        lock (_lock)
        {
            // Stamped "now" (a future stamp would be a scheduled update): what the speakers
            // play now is the player's position minus the delay they run behind.
            var behindMs = playing ? (long)Latency.TotalMilliseconds : 0;
            positionMs = Math.Max(0, positionMs - behindMs);
            _metadata = new JsonObject
            {
                ["timestamp"] = SendspinClock.NowUs,
                ["title"] = title,
                ["artist"] = artist,
                ["album_artist"] = albumArtist,
                ["album"] = album,
                ["artwork_url"] = artworkUrl,
                ["year"] = year,
                ["track"] = track,
                ["progress"] = new JsonObject
                {
                    ["track_progress"] = Math.Max(0, positionMs),
                    ["track_duration"] = Math.Max(0, durationMs),
                    ["playback_speed"] = playing ? 1000 : 0,
                },
            };
            foreach (var speaker in _speakers) speaker.SendServerState(_metadata, null);
        }
    }

    /// <summary>What the speakers' controller buttons can do, plus repeat/shuffle display.</summary>
    public void SetControllerState(string repeat, bool shuffle)
    {
        lock (_lock)
        {
            _controller = DefaultController();
            _controller["repeat"] = repeat;
            _controller["shuffle"] = shuffle;
            foreach (var speaker in _speakers) speaker.SendServerState(null, _controller);
        }
    }

    private JsonObject DefaultController()
    {
        var players = _speakers.Where(s => s.IsPlayer).ToList();
        return new JsonObject
        {
            ["supported_commands"] = new JsonArray("play", "pause", "stop", "next", "previous", "volume", "mute"),
            ["volume"] = players.Count == 0 ? 100 : (int)Math.Round(players.Average(s => s.Volume)),
            ["muted"] = players.Count > 0 && players.All(s => s.Muted),
            ["repeat"] = (string?)_controller?["repeat"] ?? "off",
            ["shuffle"] = (bool?)_controller?["shuffle"] ?? false,
        };
    }

    /// <summary>Whether LunaDrome can send this format.</summary>
    public static bool Produces(SendspinFormat format) => format == PcmFormat || format == FlacFormat;

    /// <summary>The format the speaker currently prefers if LunaDrome makes it, else its most
    /// preferred one (its list is in priority order) that LunaDrome produces, or null.</summary>
    public static SendspinFormat? FormatFor(SendspinSpeaker speaker) =>
        speaker.PreferredFormat is { } preferred && Produces(preferred) && speaker.SupportedFormats.Contains(preferred)
            ? preferred
            : speaker.SupportedFormats.FirstOrDefault(Produces);

    /// <summary>A player that has said it's ready (the encrypted revision forbids stream/start
    /// before client/state reports available: true).</summary>
    private static bool MayStream(SendspinSpeaker speaker) =>
        speaker.IsPlayer && FormatFor(speaker) != null && (!speaker.Encrypted || (speaker.HasState && speaker.IsAvailable));

    // ---- speaker events ----

    private void OnSpeakerStateChanged(SendspinSpeaker speaker)
    {
        lock (_lock)
        {
            if (_speakers.Contains(speaker))
            {
                if (speaker.StreamFormat != null && speaker.Encrypted && !speaker.IsAvailable)
                    speaker.EndStream();                                   // taken over by something else
                else if (speaker.StreamFormat == null && _running && MayStream(speaker))
                    speaker.StartStream(FormatFor(speaker)!);              // ready now
                else if (speaker.StreamFormat != null && FormatFor(speaker) is { } wanted && wanted != speaker.StreamFormat)
                    speaker.StartStream(wanted);                           // its preferred format changed
            }
        }
        Trace($"{speaker.Name} reports state={speaker.State} volume={speaker.Volume} muted={speaker.Muted} delay={speaker.StaticDelayMs}ms");
        lock (_lock)
        {
            if (_controller != null || _speakers.Any(s => s.HasController))
            {
                var controller = DefaultController();
                foreach (var s in _speakers) s.SendServerState(null, controller);
            }
        }
        SpeakersChanged?.Invoke();
    }

    private void OnSpeakerCommand(SendspinSpeaker speaker, string command, int? volume, bool? mute)
    {
        Trace($"{speaker.Name} sent controller command {command}{(volume is { } vol ? $" volume={vol}" : "")}{(mute is { } mu ? $" mute={mu}" : "")}");
        switch (command)
        {
            case "volume" when volume is { } v:
                SetGroupVolume(v);
                break;
            case "mute" when mute is { } m:
                foreach (var s in Speakers) s.SetMuted(m);
                break;
            default:
                ControllerCommand?.Invoke(command);
                break;
        }
    }

    /// <summary>Group volume: moves every speaker by the same amount, as the spec describes,
    /// handing what a clamped speaker couldn't take to the others.</summary>
    public void SetGroupVolume(int volume)
    {
        var players = Speakers.Where(s => s.IsPlayer && s.CanSetVolume).ToList();
        if (players.Count == 0) return;
        var target = players.ToDictionary(s => s, s => (double)s.Volume);
        var delta = Math.Clamp(volume, 0, 100) - target.Values.Average();
        var free = players.ToList();
        while (Math.Abs(delta) > 0.01 && free.Count > 0)
        {
            var share = delta / free.Count;
            delta = 0;
            foreach (var s in free.ToList())
            {
                var proposed = target[s] + share;
                var clamped = Math.Clamp(proposed, 0, 100);
                target[s] = clamped;
                if (clamped != proposed) { delta += proposed - clamped; free.Remove(s); }
            }
        }
        foreach (var (s, v) in target)
        {
            Trace($"group volume {volume}: setting {s.Name} to {(int)Math.Round(v)} (was {s.Volume})");
            s.SetVolume((int)Math.Round(v));
        }
    }

    private void OnSpeakerClosed(SendspinSpeaker speaker, string reason)
    {
        bool removed;
        lock (_lock) removed = _speakers.Remove(speaker);
        if (!removed) return;
        SpeakerLost?.Invoke(speaker, reason);
        SpeakersChanged?.Invoke();
        _ = speaker.DisposeAsync().AsTask();
    }

    internal void OnSourceFlushed(SendspinSource source)
    {
        lock (_lock)
        {
            // Another source still playing (a crossfade): the speakers keep their audio.
            if (_sources.Any(s => s != source && s.State == SourceState.Playing)) return;
            if (!_running && _nextFreeUs <= SendspinClock.NowUs) return;
            foreach (var speaker in _speakers) speaker.ClearStream();
            _running = false;
            _nextFreeUs = 0;
            _idleSinceUs = SendspinClock.NowUs;
            Trace("speakers cleared (seek / skip)");
        }
    }

    // ---- the pump ----

    private long ComputeLeadUs()
    {
        var lead = MinLeadUs;
        foreach (var s in _speakers)
            lead = Math.Max(lead, (Math.Max(s.MinBufferMs, s.RequiredLeadTimeMs) + s.StaticDelayMs) * 1000L + 50_000);
        return Math.Min(lead, MaxLeadUs);
    }

    private void PumpLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                lock (_lock) Pump(SendspinClock.NowUs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sendspin] pump: {ex}");
            }
            Thread.Sleep(5);
        }
    }

    private void Pump(long now)
    {
        if (!_running)
        {
            // Normally wait for a little audio in hand; when the speakers are still playing what
            // was sent (pause/resume, the next track), one chunk is enough to continue seamlessly.
            var stillPlaying = _nextFreeUs - now > 2 * ChunkUs;
            var needed = stillPlaying ? ChunkFrames : PrebufferFrames;
            if (!_sources.Any(s => s.State == SourceState.Playing && s.BufferedFrames >= needed))
            {
                IdleTick(now);
                return;
            }
            StartTimeline(now);
        }

        var due = (now - _wallStartUs) * SampleRate / 1_000_000 - _framesOut;
        while (due >= ChunkFrames && _running)
        {
            var timestamp = _timelineStartUs + _framesOut * 1_000_000 / SampleRate;
            // Only whole chunks go out. A partial one stays in its source, so pausing or the gap
            // to the next track never inserts padding: the audio continues from it later.
            if (!_sources.Any(s => s.BufferedFrames >= ChunkFrames))
            {
                if (!_sources.Any(s => s.State == SourceState.Playing))
                {
                    // Paused, stopped or the track ended: what was sent plays out; a restart
                    // continues right after it (gapless into the next track).
                    StopTimeline(now, timestamp);
                    return;
                }
                // Playing but the player is behind (network, buffering): wait for it. Every wait
                // eats into the lead; once the next chunk couldn't arrive in time, start over
                // (with a fresh lead) when the player delivers again.
                if (timestamp - now < 2 * ChunkUs) StopTimeline(now, 0);
                return;
            }

            Array.Clear(_mix);
            foreach (var source in _sources)
                source.MixInto(_mix, ChunkFrames);

            ToPcm16(_mix, _pcm);
            Mixed?.Invoke(timestamp, _pcm);
            foreach (var speaker in _speakers)
                if (speaker.StreamFormat != null) speaker.SendAudio(timestamp, ChunkUs, _pcm);
            _framesOut += ChunkFrames;
            due -= ChunkFrames;
        }
    }

    private void StartTimeline(long now)
    {
        _leadUs = ComputeLeadUs();
        _wallStartUs = now;
        _framesOut = 0;
        _timelineStartUs = Math.Max(now + _leadUs, _nextFreeUs);
        _running = true;
        Trace($"timeline started, lead {_leadUs / 1000} ms{(_timelineStartUs == _nextFreeUs ? " (continuing)" : "")}");
        foreach (var speaker in _speakers)
            if (speaker.StreamFormat == null && MayStream(speaker)) speaker.StartStream(FormatFor(speaker)!);
        if (!_groupPlaying)
        {
            _groupPlaying = true;
            foreach (var speaker in _speakers) speaker.SendGroupUpdate(true, _serverId, _serverName);
        }
    }

    private void StopTimeline(long now, long nextTimestamp)
    {
        Trace(nextTimestamp == 0 ? "timeline stopped (the player fell behind)" : "timeline stopped (paused / track ended)");
        _running = false;
        _nextFreeUs = nextTimestamp;
        _idleSinceUs = now;
    }

    private void IdleTick(long now)
    {
        // Speakers that joined while nothing played get their stream when it starts. After a
        // long idle the streams end, so the speakers show stopped and can be taken over.
        if (!_groupPlaying || now - _idleSinceUs < EndStreamAfterIdle.Ticks / 10) return;
        _groupPlaying = false;
        foreach (var speaker in _speakers)
        {
            speaker.EndStream();
            speaker.SendGroupUpdate(false, _serverId, _serverName);
        }
    }

    private static void ToPcm16(int[] mix, short[] samples)
    {
        for (var i = 0; i < mix.Length; i++)
            samples[i] = (short)Math.Clamp(mix[i], short.MinValue, short.MaxValue);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _pump.Join(TimeSpan.FromSeconds(1));
        List<SendspinSpeaker> speakers;
        lock (_lock)
        {
            speakers = _speakers.ToList();
            _speakers.Clear();
            foreach (var speaker in speakers)
            {
                speaker.EndStream();
                speaker.SendGroupUpdate(false, _serverId, _serverName);
            }
        }
        foreach (var speaker in speakers) await speaker.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

/// <summary>The speaker is connected but needs pairing before it may play; the connection is
/// left open for the pairing (<see cref="SendspinPairing"/>) and belongs to the caller.</summary>
public sealed class SendspinPairingRequiredException(SendspinSpeaker speaker)
    : Exception($"{speaker.Name} needs to be paired with LunaDrome first.")
{
    public SendspinSpeaker Speaker { get; } = speaker;
}

/// <summary>A mixed chunk (16-bit interleaved stereo) and when the speakers play it.</summary>
public delegate void MixedAudioHandler(long timestampUs, ReadOnlySpan<short> samples);
