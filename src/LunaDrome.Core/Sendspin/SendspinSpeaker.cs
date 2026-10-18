using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace LunaDrome.Core.Sendspin;

/// <summary>
/// One Sendspin speaker LunaDrome streams to: a WebSocket that LunaDrome (the server) opens to
/// the address the speaker advertises over mDNS. Speaks Sendspin core version 1 as it shipped in
/// aiosendspin up to 6.x and Music Assistant 2.8 (plain JSON text frames, client/hello first,
/// 9-byte audio header) - what the speakers on the network actually run. The newer encrypted
/// revision (client/init + Noise) is detected and reported, not spoken.
/// </summary>
/// <remarks>Events are raised on background threads.</remarks>
public sealed class SendspinSpeaker : IAsyncDisposable
{
    private const int AudioChunkType = 4;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _ws = new();
    private readonly Channel<Outgoing> _outgoing = Channel.CreateUnbounded<Outgoing>(new() { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<(long EndUs, int Bytes)> _inFlight = new();
    private readonly object _lock = new();
    private FlacEncoder? _flac;
    private long _inFlightBytes;
    private Task? _receiveLoop, _sendLoop;
    private int _closed;

    public SendspinSpeaker(Uri url)
    {
        Url = url;
        // Ping/pong is Sendspin's liveness check: a speaker that vanishes without closing the
        // connection (power cut, Wi-Fi gone) is noticed within ~15 s instead of never.
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        _ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
    }

    public Uri Url { get; }
    public string ClientId { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string? ProductName { get; private set; }
    public string? SoftwareVersion { get; private set; }
    public IReadOnlyList<string> ActiveRoles { get; private set; } = [];
    public IReadOnlyList<SendspinFormat> SupportedFormats { get; private set; } = [];
    public long BufferCapacity { get; private set; }
    public bool CanSetVolume { get; private set; }
    public bool CanMute { get; private set; }

    /// <summary>'synchronized' | 'error' | 'external_source' (empty until the first client/state).</summary>
    public string State { get; private set; } = "";
    public int Volume { get; private set; } = 100;
    public bool Muted { get; private set; }
    public int StaticDelayMs { get; private set; }
    public int RequiredLeadTimeMs { get; private set; } = 250;
    public int MinBufferMs { get; private set; } = 250;
    /// <summary>True once the initial client/state arrived; audio may only go out after that.</summary>
    public bool HasState { get; private set; }

    public bool IsPlayer => ActiveRoles.Contains("player@v1");
    public bool HasMetadata => ActiveRoles.Contains("metadata@v1");
    public bool HasController => ActiveRoles.Contains("controller@v1");
    public bool IsOpen => Volatile.Read(ref _closed) == 0 && _ws.State == WebSocketState.Open;
    public SendspinFormat? StreamFormat { get; private set; }
    /// <summary>Audio chunks skipped because they would overflow the speaker's buffer_capacity.</summary>
    public long DroppedChunks { get; private set; }

    /// <summary>Volume, mute, delay or state changed.</summary>
    public event Action<SendspinSpeaker>? StateChanged;
    /// <summary>A controller@v1 button on the speaker: command plus volume / mute where they apply.</summary>
    public event Action<SendspinSpeaker, string, int?, bool?>? ControllerCommand;
    /// <summary>The connection is gone; the string is the speaker's goodbye reason or the error.</summary>
    public event Action<SendspinSpeaker, string>? Closed;

    /// <summary>Opens the WebSocket, waits for client/hello and answers with server/hello.</summary>
    /// <exception cref="SendspinProtocolException">The speaker spoke something else (or nothing).</exception>
    public async Task ConnectAsync(string serverId, string serverName, bool forPlayback, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        linked.CancelAfter(HelloTimeout);
        await _ws.ConnectAsync(Url, linked.Token).ConfigureAwait(false);

        var (first, _) = await ReceiveTextAsync(linked.Token).ConfigureAwait(false)
            ?? throw new SendspinProtocolException("The speaker closed the connection before saying hello.");
        var message = JsonNode.Parse(first) as JsonObject
            ?? throw new SendspinProtocolException("The speaker's first message wasn't JSON.");
        var type = (string?)message["type"];
        if (type == "client/init")
            throw new SendspinProtocolException(
                "This speaker uses the newer encrypted Sendspin protocol, which LunaDrome doesn't speak yet.");
        if (type != "client/hello" || message["payload"] is not JsonObject hello)
            throw new SendspinProtocolException($"Expected client/hello, got {type ?? "nothing"}.");
        if ((int?)hello["version"] != 1)
            throw new SendspinProtocolException($"Unsupported Sendspin version {hello["version"]}.");

        ReadHello(hello);
        await SendRawTextAsync(Serialize("server/hello", new JsonObject
        {
            ["server_id"] = serverId,
            ["name"] = serverName,
            ["version"] = 1,
            ["active_roles"] = new JsonArray(ActiveRoles.Select(r => (JsonNode)r).ToArray()),
            ["connection_reason"] = forPlayback ? "playback" : "discovery",
        }), linked.Token).ConfigureAwait(false);

        _sendLoop = Task.Run(SendLoopAsync);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    private void ReadHello(JsonObject hello)
    {
        ClientId = (string?)hello["client_id"] ?? "";
        Name = (string?)hello["name"] ?? ClientId;
        ProductName = (string?)hello["device_info"]?["product_name"];
        SoftwareVersion = (string?)hello["device_info"]?["software_version"];

        // One version per role family: the first one listed that LunaDrome implements.
        var supported = hello["supported_roles"]?.AsArray().Select(r => (string?)r).OfType<string>().ToList() ?? [];
        var implemented = new[] { "player@v1", "metadata@v1", "controller@v1" };
        ActiveRoles = implemented.Where(supported.Contains).ToList();

        if (hello["player@v1_support"] is JsonObject player)
        {
            SupportedFormats = player["supported_formats"]?.AsArray().OfType<JsonObject>()
                .Select(f => new SendspinFormat((string?)f["codec"] ?? "", (int?)f["sample_rate"] ?? 0,
                    (int?)f["channels"] ?? 0, (int?)f["bit_depth"] ?? 0))
                .ToList() ?? [];
            BufferCapacity = (long?)player["buffer_capacity"] ?? 1_000_000;
            var commands = player["supported_commands"]?.AsArray().Select(c => (string?)c).ToList() ?? [];
            CanSetVolume = commands.Contains("volume");
            CanMute = commands.Contains("mute");
        }
    }

    // ---- outgoing ----

    /// <summary>Starts (or reconfigures) the audio stream. FLAC gets its own encoder, so its
    /// frame numbers start with the stream.</summary>
    public void StartStream(SendspinFormat format)
    {
        lock (_lock)
        {
            _inFlight.Clear();
            _inFlightBytes = 0;
            _flac = format.Codec == "flac"
                ? new FlacEncoder(format.SampleRate, format.Channels, SendspinOutput.ChunkFrames)
                : null;
            StreamFormat = format;
        }
        var player = new JsonObject
        {
            ["codec"] = format.Codec,
            ["sample_rate"] = format.SampleRate,
            ["channels"] = format.Channels,
            ["bit_depth"] = format.BitDepth,
        };
        if (_flac != null) player["codec_header"] = Convert.ToBase64String(_flac.Header());
        EnqueueJson("stream/start", new JsonObject { ["player"] = player });
    }

    /// <summary>Drops the speaker's buffered audio but keeps the stream (pause, seek, skip).</summary>
    public void ClearStream()
    {
        if (StreamFormat == null) return;
        lock (_lock) { _inFlight.Clear(); _inFlightBytes = 0; }
        EnqueueJson("stream/clear", new JsonObject { ["roles"] = new JsonArray("player") });
    }

    public void EndStream()
    {
        if (StreamFormat == null) return;
        StreamFormat = null;
        lock (_lock) { _inFlight.Clear(); _inFlightBytes = 0; _flac = null; }
        EnqueueJson("stream/end", new JsonObject { ["roles"] = new JsonArray("player") });
    }

    /// <summary>Queues one chunk of interleaved 16-bit samples (encoded for this speaker's
    /// stream format) whose first sample plays at <paramref name="timestampUs"/> (server clock).
    /// False when it was skipped because the speaker's buffer is full.</summary>
    public bool SendAudio(long timestampUs, long durationUs, ReadOnlySpan<short> samples)
    {
        if (StreamFormat == null || !HasState) return false;
        byte[] encoded;
        lock (_lock)
        {
            if (_flac != null)
                encoded = _flac.EncodeFrame(samples);
            else
            {
                // PCM: little-endian signed 16-bit.
                encoded = new byte[samples.Length * 2];
                for (var i = 0; i < samples.Length; i++)
                    BinaryPrimitives.WriteInt16LittleEndian(encoded.AsSpan(2 * i), samples[i]);
            }
        }
        var size = encoded.Length + 9;
        lock (_lock)
        {
            // Buffer accounting: a chunk counts until it has finished playing on the speaker.
            var now = SendspinClock.NowUs + StaticDelayMs * 1000L;
            while (_inFlight.Count > 0 && _inFlight.Peek().EndUs <= now)
                _inFlightBytes -= _inFlight.Dequeue().Bytes;
            if (_inFlightBytes + size > BufferCapacity)
            {
                DroppedChunks++;
                return false;
            }
            _inFlight.Enqueue((timestampUs + durationUs, size));
            _inFlightBytes += size;
        }
        var frame = new byte[size];
        frame[0] = AudioChunkType;
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1, 8), timestampUs);
        encoded.CopyTo(frame.AsSpan(9));
        _outgoing.Writer.TryWrite(new Outgoing(null, null, frame));
        return true;
    }

    public void SetVolume(int volume)
    {
        if (!CanSetVolume) return;
        EnqueueJson("server/command", new JsonObject
        {
            ["player"] = new JsonObject { ["command"] = "volume", ["volume"] = Math.Clamp(volume, 0, 100) },
        });
    }

    public void SetMuted(bool muted)
    {
        if (!CanMute) return;
        EnqueueJson("server/command", new JsonObject
        {
            ["player"] = new JsonObject { ["command"] = "mute", ["mute"] = muted },
        });
    }

    public void SendGroupUpdate(bool playing, string groupId, string groupName) =>
        EnqueueJson("group/update", new JsonObject
        {
            ["playback_state"] = playing ? "playing" : "stopped",
            ["group_id"] = groupId,
            ["group_name"] = groupName,
        });

    /// <summary>server/state for the metadata and/or controller roles (whichever are active).</summary>
    public void SendServerState(JsonObject? metadata, JsonObject? controller)
    {
        var payload = new JsonObject();
        if (metadata != null && HasMetadata) payload["metadata"] = metadata.DeepClone();
        if (controller != null && HasController) payload["controller"] = controller.DeepClone();
        if (payload.Count > 0) EnqueueJson("server/state", payload);
    }

    private void EnqueueJson(string type, JsonObject payload) =>
        _outgoing.Writer.TryWrite(new Outgoing(Serialize(type, payload), null, null));

    private static string Serialize(string type, JsonObject payload) =>
        new JsonObject { ["type"] = type, ["payload"] = payload }.ToJsonString();

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var item in _outgoing.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (item.Binary != null)
                    await _ws.SendAsync(item.Binary, WebSocketMessageType.Binary, true, _cts.Token).ConfigureAwait(false);
                else
                    await SendRawTextAsync(item.Json ?? item.JsonAtSend!(), _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            Close(ex is OperationCanceledException ? "closed" : ex.Message);
        }
    }

    private Task SendRawTextAsync(string json, CancellationToken token) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, token);

    // ---- incoming ----

    private async Task ReceiveLoopAsync()
    {
        var reason = "connection lost";
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var received = await ReceiveTextAsync(_cts.Token).ConfigureAwait(false);
                if (received == null) break;
                var (text, receivedUs) = received.Value;
                if (text.Length == 0) continue;   // binary from a client isn't defined for these roles
                if (JsonNode.Parse(text) is not JsonObject message) continue;
                var payload = message["payload"] as JsonObject ?? new JsonObject();
                switch ((string?)message["type"])
                {
                    case "client/time":
                        var clientTransmitted = (long?)payload["client_transmitted"] ?? 0;
                        // server_transmitted is taken when the message actually goes out.
                        _outgoing.Writer.TryWrite(new Outgoing(null, () => Serialize("server/time", new JsonObject
                        {
                            ["client_transmitted"] = clientTransmitted,
                            ["server_received"] = receivedUs,
                            ["server_transmitted"] = SendspinClock.NowUs,
                        }), null));
                        break;
                    case "client/state":
                        ReadState(payload);
                        StateChanged?.Invoke(this);
                        break;
                    case "client/command":
                        if (payload["controller"] is JsonObject controller && (string?)controller["command"] is { } command)
                            ControllerCommand?.Invoke(this, command, (int?)controller["volume"], (bool?)controller["mute"]);
                        break;
                    case "stream/request-format":
                        // Switch when it's a format LunaDrome makes; otherwise answer with the current one.
                        if (StreamFormat is { } current && payload["player"] is JsonObject wanted)
                        {
                            var requested = new SendspinFormat((string?)wanted["codec"] ?? current.Codec,
                                (int?)wanted["sample_rate"] ?? current.SampleRate, (int?)wanted["channels"] ?? current.Channels,
                                (int?)wanted["bit_depth"] ?? current.BitDepth);
                            StartStream(SendspinOutput.Produces(requested) ? requested : current);
                        }
                        break;
                    case "client/goodbye":
                        reason = (string?)payload["reason"] ?? "goodbye";
                        _cts.Cancel();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException or ObjectDisposedException)
        {
            if (ex is not OperationCanceledException) reason = ex.Message;
        }
        Close(reason);
    }

    private void ReadState(JsonObject payload)
    {
        if ((string?)payload["state"] is { } state) State = state;
        if (payload["player"] is JsonObject player)
        {
            // Older clients put state inside the player object.
            if ((string?)player["state"] is { } playerState && payload["state"] == null) State = playerState;
            if ((int?)player["volume"] is { } volume) Volume = volume;
            if ((bool?)player["muted"] is { } muted) Muted = muted;
            if ((int?)player["static_delay_ms"] is { } delay) StaticDelayMs = Math.Clamp(delay, 0, 5000);
            if ((int?)player["required_lead_time_ms"] is { } lead) RequiredLeadTimeMs = Math.Clamp(lead, 0, 30000);
            if ((int?)player["min_buffer_ms"] is { } buffer) MinBufferMs = Math.Clamp(buffer, 0, 30000);
        }
        HasState = true;
    }

    /// <summary>The next whole text message with the time its last byte arrived; binary messages
    /// come back as empty text; null when the socket closed.</summary>
    private async Task<(string Text, long ReceivedUs)?> ReceiveTextAsync(CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var receivedUs = SendspinClock.NowUs;
            return result.MessageType == WebSocketMessageType.Text
                ? (Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), receivedUs)
                : ("", receivedUs);
        }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _cts.Cancel();
        _outgoing.Writer.TryComplete();
        Closed?.Invoke(this, reason);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                // Let queued messages (stream/end) go out first.
                _outgoing.Writer.TryComplete();
                if (_sendLoop != null) await _sendLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
            }
            catch { /* closing anyway */ }
        }
        Close("closed by LunaDrome");
        _ws.Dispose();
        _cts.Dispose();
    }

    private sealed record Outgoing(string? Json, Func<string>? JsonAtSend, byte[]? Binary);
}

public sealed class SendspinProtocolException(string message) : Exception(message);
