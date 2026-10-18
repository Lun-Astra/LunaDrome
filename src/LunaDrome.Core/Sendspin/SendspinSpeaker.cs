using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace LunaDrome.Core.Sendspin;

/// <summary>
/// One Sendspin speaker LunaDrome streams to: a WebSocket that LunaDrome (the server) opens to
/// the address the speaker advertises over mDNS. The speaker's first message picks the
/// protocol revision:
/// <list type="bullet">
/// <item><c>client/init</c>: the current, encrypted revision (aiosendspin 7+): a Noise KKpsk2
/// handshake, then every message as an encrypted binary frame, 13-byte audio headers with
/// send_ahead, <c>server/activate</c>, and pairing (see <see cref="SendspinPairing"/>).</item>
/// <item><c>client/hello</c>: the older unencrypted revision (aiosendspin up to 6.x, Music
/// Assistant 2.8): plain JSON text frames and 9-byte audio headers.</item>
/// </list>
/// </summary>
/// <remarks>Events are raised on background threads.</remarks>
public sealed class SendspinSpeaker : IAsyncDisposable
{
    private const int AudioChunkType = 4;
    private const int JsonType = 0, FragmentType = 1;
    private const int MaxPlaintext = 65535 - 16 - 1;   // per Noise transport message, minus the type byte
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly ClientWebSocket _ws = new();
    private readonly Channel<Outgoing> _outgoing = Channel.CreateUnbounded<Outgoing>(new() { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<(long EndUs, int Bytes)> _inFlight = new();
    private readonly object _lock = new();
    private FlacEncoder? _flac;
    private long _inFlightBytes;
    private Task? _receiveLoop, _sendLoop;
    private int _closed;

    // Encrypted revision
    private NoiseCipher? _sendCipher, _receiveCipher;
    private NoiseSuite _suite;
    private byte[] _clientKey = [];
    private SendspinCredentials? _credentials;
    // An in-band re-handshake (after pairing): its initiator, and who waits for it.
    private NoiseInitiator? _rehandshake;
    private string _rehandshakeCategory = "";
    private TaskCompletionSource? _rehandshakeDone;
    // From Noise message 1 of a re-handshake until the next server/activate, only the handshake
    // and that activation may go out (spec: no new application messages in between).
    private volatile bool _holdApplication;
    private byte[]? _fragmentBuffer;
    private int _fragmentType;

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
    /// <summary>True for the encrypted revision.</summary>
    public bool Encrypted { get; private set; }
    /// <summary>Encrypted revision: the PSK category the session runs under ("lt", "pr", "sn").</summary>
    public string PskCategory { get; private set; } = "";
    /// <summary>Encrypted: we referenced a pairing the speaker no longer has (it answered under
    /// the Sentinel) - it needs pairing again before it may play.</summary>
    public bool CredentialMismatch { get; private set; }
    /// <summary>Encrypted: whether the speaker lets a server play without pairing.</summary>
    public bool UnpairedAccess { get; private set; } = true;
    /// <summary>Encrypted: the pairing methods the speaker offers (keys of supported_pair_methods).</summary>
    public IReadOnlyList<string> PairMethods { get; private set; } = [];
    /// <summary>Encrypted: the dynamic code's formats ("digits", "qr_code"), if offered.</summary>
    public IReadOnlyList<string> DynamicCodeFormats { get; private set; } = [];
    /// <summary>Encrypted, but from before spec #167/#177 (aiosendspin 9.x): its hello still lists
    /// player commands (or trust_level). It expects the 9-byte audio header without send_ahead
    /// - sending 13 bytes would put 4 stray bytes into its audio.</summary>
    public bool PreSendAheadWire { get; private set; }
    /// <summary>The client/hello as received (diagnostics).</summary>
    public string HelloJson { get; private set; } = "";
    /// <summary>True when the speaker is connected but won't play until it's paired.</summary>
    public bool NeedsPairing { get; private set; }
    public byte[] HandshakeHash { get; private set; } = [];
    /// <summary>Pairing server/activate messages sent since the last Noise handshake.</summary>
    public int PairingIndex { get; private set; }
    public NoiseSuite Suite => _suite;
    public byte[] ClientKey => _clientKey;

    public IReadOnlyList<string> SupportedRoles { get; private set; } = [];
    public IReadOnlyList<string> ActiveRoles { get; private set; } = [];
    public IReadOnlyList<SendspinFormat> SupportedFormats { get; private set; } = [];
    public long BufferCapacity { get; private set; }
    public bool CanSetVolume { get; private set; }
    public bool CanMute { get; private set; }

    /// <summary>'synchronized' | 'error' | 'external_source' (empty until the first client/state).
    /// The encrypted revision's available=true/false maps to synchronized/external_source.</summary>
    public string State { get; private set; } = "";
    public bool IsAvailable => State is "synchronized";
    public int Volume { get; private set; } = 100;
    public bool Muted { get; private set; }
    public int StaticDelayMs { get; private set; }
    public int RequiredLeadTimeMs { get; private set; } = 250;
    public int MinBufferMs { get; private set; } = 250;
    /// <summary>Encrypted: the format the speaker currently prefers (client/state), if any.</summary>
    public SendspinFormat? PreferredFormat { get; private set; }
    /// <summary>True once the initial client/state arrived; audio may only go out after that.</summary>
    public bool HasState { get; private set; }

    /// <summary>Whether audio chunks carry send_ahead (13-byte header) for this speaker.</summary>
    private bool SendsAhead => Encrypted && !PreSendAheadWire;

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
    /// <summary>A pairing message from the speaker (type, payload) - handled by SendspinPairing.</summary>
    internal event Action<string, JsonObject>? PairingMessage;

    /// <summary>Opens the WebSocket and runs the hello exchange (and, for the encrypted revision,
    /// the handshake and activation). Afterwards <see cref="NeedsPairing"/> says whether it plays.</summary>
    /// <exception cref="SendspinProtocolException">The speaker spoke something else (or nothing).</exception>
    public async Task ConnectAsync(SendspinCredentials credentials, string serverName, bool forPlayback,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        linked.CancelAfter(HandshakeTimeout);
        await _ws.ConnectAsync(Url, linked.Token).ConfigureAwait(false);

        var (first, _) = await ReceiveRawAsync(linked.Token).ConfigureAwait(false)
            ?? throw new SendspinProtocolException("The speaker closed the connection before saying hello.");
        if (first.Text == null)
            throw new SendspinProtocolException("The speaker's first message wasn't text.");
        var message = JsonNode.Parse(first.Text) as JsonObject
            ?? throw new SendspinProtocolException("The speaker's first message wasn't JSON.");
        switch ((string?)message["type"])
        {
            case "client/init":
                Encrypted = true;
                await EncryptedHandshakeAsync(first.Text, message, credentials, serverName, forPlayback, linked.Token)
                    .ConfigureAwait(false);
                break;
            case "client/hello" when message["payload"] is JsonObject hello:
                await LegacyHelloAsync(hello, credentials.LegacyServerId, serverName, forPlayback, linked.Token)
                    .ConfigureAwait(false);
                break;
            default:
                throw new SendspinProtocolException($"Expected client/init or client/hello, got {message["type"]}.");
        }
        _sendLoop = Task.Run(SendLoopAsync);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    // ---- older unencrypted revision ----

    private async Task LegacyHelloAsync(JsonObject hello, string serverId, string serverName, bool forPlayback,
        CancellationToken token)
    {
        if ((int?)hello["version"] != 1)
            throw new SendspinProtocolException($"Unsupported Sendspin version {hello["version"]}.");
        ReadHello(hello);
        ActiveRoles = ChooseRoles();
        await SendRawTextAsync(Serialize("server/hello", new JsonObject
        {
            ["server_id"] = serverId,
            ["name"] = serverName,
            ["version"] = 1,
            ["active_roles"] = new JsonArray(ActiveRoles.Select(r => (JsonNode)r).ToArray()),
            ["connection_reason"] = forPlayback ? "playback" : "discovery",
        }), token).ConfigureAwait(false);
    }

    // ---- encrypted revision ----

    private async Task EncryptedHandshakeAsync(string clientInitText, JsonObject clientInit, SendspinCredentials credentials,
        string serverName, bool forPlayback, CancellationToken token)
    {
        var payload = clientInit["payload"] as JsonObject;
        if (payload?["version"] is JsonValue v && v.TryGetValue(out int version) && version != 1)
        {
            await SendServerErrorAsync("unsupported_version", token).ConfigureAwait(false);
            throw new SendspinProtocolException($"The speaker speaks Sendspin core version {version}; LunaDrome speaks 1.");
        }
        var suite = (string?)payload?["suite"] switch
        {
            "25519_ChaChaPoly_SHA256" => NoiseSuite.ChaChaPoly,
            "25519_AESGCM_SHA256" => NoiseSuite.AesGcm,
            _ => (NoiseSuite?)null,
        };
        var clientId = (string?)payload?["client_id"];
        byte[]? clientKey = null;
        try { if (clientId is { Length: 43 }) clientKey = Base64Url.Decode(clientId); } catch (FormatException) { }
        if (payload == null || clientKey is not { Length: 32 } || (int?)payload["version"] != 1)
        {
            await SendServerErrorAsync("malformed", token).ConfigureAwait(false);
            throw new SendspinProtocolException("The speaker's client/init is malformed.");
        }
        if (suite == null)
        {
            await SendServerErrorAsync("unsupported_suite", token).ConfigureAwait(false);
            throw new SendspinProtocolException($"Unknown cipher suite {payload["suite"]}.");
        }
        ClientId = clientId!;
        _suite = suite.Value;
        _clientKey = clientKey;
        _credentials = credentials;

        // A long-term PSK from an earlier pairing, else the published Sentinel.
        var longTerm = credentials.LongTermPsk(ClientId);
        var psk = longTerm ?? SendspinCredentials.SentinelPsk;
        var category = longTerm != null ? "lt" : "sn";

        var serverInitText = Serialize("server/init", new JsonObject
        {
            ["server_id"] = credentials.ServerId,
            ["version"] = 1,
        });
        // The prologue binds the exact bytes of both cleartext init messages.
        var prologue = Encoding.UTF8.GetBytes(clientInitText + serverInitText);
        var noise = new NoiseInitiator(suite.Value, credentials.StaticPrivateKey, clientKey, prologue, psk);
        var msg1Payload = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["psk_id"] = SendspinCredentials.PskId(psk),
            ["psk_category"] = category,
        }.ToJsonString());
        var msg1 = noise.WriteMessage1(msg1Payload);
        // server/init and Noise message 1 go out back to back.
        await SendRawTextAsync(serverInitText, token).ConfigureAwait(false);
        await SendRawTextAsync(Serialize("noise/handshake", new JsonObject { ["data"] = Base64Url.Encode(msg1) }), token)
            .ConfigureAwait(false);

        var (reply, _) = await ReceiveRawAsync(token).ConfigureAwait(false)
            ?? throw new SendspinProtocolException("The speaker closed the connection during the handshake.");
        var msg2 = (JsonNode.Parse(reply.Text ?? "{}") as JsonObject) is { } m && (string?)m["type"] == "noise/handshake"
            ? Base64Url.Decode((string?)m["payload"]?["data"] ?? "")
            : throw new SendspinProtocolException("Expected the speaker's noise/handshake.");
        try
        {
            noise.ReadMessage2(msg2);   // its payload is "{}" by spec; nothing in it
        }
        catch (CryptographicException) when (category != "sn")
        {
            // It couldn't use our pairing: if message 2 verifies under the Sentinel, it lost the
            // record (reset, evicted). That session may not play until it's paired again.
            noise = NoiseInitiator.ForkAfterMessage1(suite.Value, credentials.StaticPrivateKey, clientKey, prologue,
                SendspinCredentials.SentinelPsk, msg1Payload, noise);
            try { noise.ReadMessage2(msg2); }
            catch (CryptographicException) { throw new SendspinProtocolException("The encrypted handshake with the speaker failed."); }
            category = "sn";
            CredentialMismatch = true;
        }
        catch (CryptographicException)
        {
            throw new SendspinProtocolException("The encrypted handshake with the speaker failed.");
        }
        (_sendCipher, _receiveCipher) = noise.Split();
        HandshakeHash = noise.HandshakeHash;
        PskCategory = category;

        await SendEncryptedJsonAsync(Serialize("server/hello", new JsonObject { ["name"] = serverName }), token)
            .ConfigureAwait(false);
        var hello = await ReceiveEncryptedJsonAsync(token).ConfigureAwait(false);
        if ((string?)hello?["type"] != "client/hello" || hello["payload"] is not JsonObject helloPayload)
            throw new SendspinProtocolException("Expected client/hello after the handshake.");
        ReadHello(helloPayload);

        // May it play? Paired (long-term PSK), or unpaired access is on (choosing the speaker
        // in LunaDrome is the operator's approval) - but never on a lost-pairing session.
        var mayPlay = category == "lt" || (category == "sn" && UnpairedAccess && !CredentialMismatch);
        NeedsPairing = !mayPlay;
        ActiveRoles = mayPlay ? ChooseRoles() : [];
        await SendEncryptedJsonAsync(Serialize("server/activate", new JsonObject
        {
            ["activities"] = mayPlay && forPlayback ? new JsonArray("playback") : new JsonArray(),
            ["active_roles"] = new JsonArray(ActiveRoles.Select(r => (JsonNode)r).ToArray()),
        }), token).ConfigureAwait(false);
    }

    private Task SendServerErrorAsync(string reason, CancellationToken token) =>
        SendRawTextAsync(Serialize("server/error", new JsonObject { ["reason"] = reason }), token);

    private void ReadHello(JsonObject hello)
    {
        HelloJson = hello.ToJsonString();
        if ((string?)hello["client_id"] is { } legacyId) ClientId = legacyId;
        Name = (string?)hello["name"] ?? ClientId;
        ProductName = (string?)hello["device_info"]?["product_name"];
        SoftwareVersion = (string?)hello["device_info"]?["software_version"];
        SupportedRoles = hello["supported_roles"]?.AsArray().Select(r => (string?)r).OfType<string>().ToList() ?? [];

        if (hello["player@v1_support"] is JsonObject player)
        {
            SupportedFormats = player["supported_formats"]?.AsArray().OfType<JsonObject>()
                .Select(ReadFormat).ToList() ?? [];
            BufferCapacity = (long?)player["buffer_capacity"] ?? 1_000_000;
            // Older revision: commands in the hello; the encrypted one sends them in client/state.
            if (player["supported_commands"] is JsonArray commands)
            {
                if (Encrypted) PreSendAheadWire = true;
                var list = commands.Select(c => (string?)c).ToList();
                CanSetVolume = list.Contains("volume");
                CanMute = list.Contains("mute");
            }
        }
        if (Encrypted && hello["trust_level"] != null) PreSendAheadWire = true;
        if (hello["unpaired_access"] is JsonObject unpaired)
            UnpairedAccess = (bool?)unpaired["enabled"] ?? false;
        if (hello["supported_pair_methods"] is JsonArray methodList)
            PairMethods = methodList.Select(m => (string?)m).OfType<string>().ToList();   // older hellos: a list
        else if (hello["supported_pair_methods"] is JsonObject methods)
        {
            PairMethods = methods.Select(kv => kv.Key).ToList();
            DynamicCodeFormats = methods["dynamic_pairing_code"]?["formats"]?.AsArray()
                .Select(f => (string?)f).OfType<string>().ToList() ?? [];
        }
    }

    /// <summary>One version per role family: the first one listed that LunaDrome implements.</summary>
    private IReadOnlyList<string> ChooseRoles()
    {
        var implemented = new[] { "player@v1", "metadata@v1", "controller@v1" };
        return implemented.Where(SupportedRoles.Contains).ToList();
    }

    private static SendspinFormat ReadFormat(JsonObject f) =>
        new((string?)f["codec"] ?? "", (int?)f["sample_rate"] ?? 0, (int?)f["channels"] ?? 0, (int?)f["bit_depth"] ?? 0);

    /// <summary>Encrypted: re-runs the Noise handshake inside the current channel to switch to
    /// another PSK (after pairing, or to the pairing PSK first). Afterwards the caller must
    /// send <see cref="Activate"/> - the first message under the new keys.</summary>
    internal async Task RehandshakeAsync(byte[] psk, string category, CancellationToken token)
    {
        if (!Encrypted || _credentials == null) throw new InvalidOperationException("Only the encrypted revision re-handshakes.");
        var initiator = new NoiseInitiator(_suite, _credentials.StaticPrivateKey, _clientKey, HandshakeHash, psk);
        var msg1 = initiator.WriteMessage1(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["psk_id"] = SendspinCredentials.PskId(psk),
            ["psk_category"] = category,
        }.ToJsonString()));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _rehandshake = initiator;
            _rehandshakeCategory = category;
            _rehandshakeDone = done;
        }
        _holdApplication = true;
        _outgoing.Writer.TryWrite(new Outgoing(Serialize("noise/handshake", new JsonObject { ["data"] = Base64Url.Encode(msg1) }),
            null, null, 0, Forced: true));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(HandshakeTimeout);
        await done.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Message 2 of a re-handshake, decrypted under the old keys: switch keys here, in
    /// the receive loop, so the next frame is already read with the new ones.</summary>
    private void CompleteRehandshake(JsonObject payload)
    {
        NoiseInitiator? initiator;
        TaskCompletionSource? done;
        lock (_lock) { initiator = _rehandshake; done = _rehandshakeDone; _rehandshake = null; _rehandshakeDone = null; }
        if (initiator == null) return;   // unexpected: ignore
        try
        {
            initiator.ReadMessage2(Base64Url.Decode((string?)payload["data"] ?? ""));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // A failed re-handshake is a silent failure: close without another message.
            done?.TrySetException(new SendspinProtocolException("The speaker didn't accept the new key."));
            _cts.Cancel();
            return;
        }
        var (send, receive) = initiator.Split();
        lock (_lock)
        {
            _sendCipher = send;
            _receiveCipher = receive;
        }
        HandshakeHash = initiator.HandshakeHash;
        PskCategory = _rehandshakeCategory;
        CredentialMismatch = false;
        PairingIndex = 0;
        done?.TrySetResult();
    }

    /// <summary>Encrypted: declares what the connection is for now (playback, and/or a pairing
    /// attempt). Returns the pairing index of a pairing activation.</summary>
    internal int Activate(bool playback, JsonObject? pairing = null)
    {
        var activities = new JsonArray();
        if (playback) activities.Add("playback");
        if (pairing != null) activities.Add("pairing");
        ActiveRoles = playback ? ChooseRoles() : [];
        NeedsPairing = !playback;
        var payload = new JsonObject
        {
            ["activities"] = activities,
            ["active_roles"] = new JsonArray(ActiveRoles.Select(r => (JsonNode)r).ToArray()),
        };
        if (pairing != null)
        {
            payload["pairing"] = pairing;
            PairingIndex++;
        }
        _outgoing.Writer.TryWrite(new Outgoing(Serialize("server/activate", payload), null, null, 0, Forced: true, ReleaseHold: true));
        return PairingIndex;
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
        if (Encrypted)
            // server_transmitted is taken when it actually goes out.
            EnqueueJsonAtSend(() => Serialize("stream/start", new JsonObject
            {
                ["server_transmitted"] = SendspinClock.NowUs,
                ["player"] = player.DeepClone(),
            }));
        else
            EnqueueJson("stream/start", new JsonObject { ["player"] = player });
    }

    /// <summary>Drops the speaker's buffered audio but keeps the stream (pause, seek, skip).</summary>
    public void ClearStream()
    {
        if (StreamFormat == null) return;
        lock (_lock) { _inFlight.Clear(); _inFlightBytes = 0; }
        if (Encrypted)
            EnqueueJsonAtSend(() => Serialize("stream/clear", new JsonObject
            {
                ["server_transmitted"] = SendspinClock.NowUs,
                ["roles"] = new JsonArray("player"),
            }));
        else
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
        var size = encoded.Length + (SendsAhead ? 13 : 9);
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
        _outgoing.Writer.TryWrite(new Outgoing(null, null, encoded, timestampUs));
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

    public void SendGroupUpdate(bool playing, string groupId, string groupName)
    {
        if (Encrypted && NeedsPairing) return;   // nothing but pairing until it may play
        EnqueueJson("group/update", new JsonObject
        {
            ["playback_state"] = playing ? "playing" : "stopped",
            ["group_id"] = groupId,
            ["group_name"] = groupName,
        });
    }

    /// <summary>server/state for the metadata and/or controller roles (whichever are active).</summary>
    public void SendServerState(JsonObject? metadata, JsonObject? controller)
    {
        var payload = new JsonObject();
        if (metadata != null && HasMetadata) payload["metadata"] = WithoutNulls(metadata);
        if (controller != null && HasController) payload["controller"] = controller.DeepClone();
        if (payload.Count > 0) EnqueueJson("server/state", payload);
    }

    /// <summary>Optional fields are left out rather than sent as null (the encrypted revision
    /// forbids undefined values; the older one treats both the same).</summary>
    private static JsonObject WithoutNulls(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in source)
            if (value != null) copy[key] = value is JsonObject o ? WithoutNulls(o) : value.DeepClone();
        return copy;
    }

    internal void SendPairingJson(string type, JsonObject payload) =>
        _outgoing.Writer.TryWrite(new Outgoing(Serialize(type, payload), null, null, 0, Forced: true));

    private void EnqueueJson(string type, JsonObject payload) =>
        _outgoing.Writer.TryWrite(new Outgoing(Serialize(type, payload), null, null, 0));

    private void EnqueueJsonAtSend(Func<string> build) =>
        _outgoing.Writer.TryWrite(new Outgoing(null, build, null, 0));

    private static string Serialize(string type, JsonObject payload) =>
        new JsonObject { ["type"] = type, ["payload"] = payload }.ToJsonString();

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var item in _outgoing.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_holdApplication && !item.Forced) continue;   // mid re-handshake: dropped (time replies, state)
                if (item.ReleaseHold) _holdApplication = false;
                if (item.Audio != null)
                {
                    // The header is finished here, as late as possible: send_ahead reports the
                    // lead in hand at transmission (0 = late, saturating at uint.MaxValue).
                    var headerSize = SendsAhead ? 13 : 9;
                    var frame = new byte[headerSize + item.Audio.Length];
                    frame[0] = AudioChunkType;
                    BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1, 8), item.TimestampUs);
                    if (SendsAhead)
                    {
                        var ahead = item.TimestampUs - SendspinClock.NowUs;
                        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(9, 4),
                            ahead <= 0 ? 0 : ahead >= uint.MaxValue ? uint.MaxValue : (uint)ahead);
                    }
                    item.Audio.CopyTo(frame.AsSpan(headerSize));
                    if (Encrypted) await SendEncryptedAsync(frame, _cts.Token).ConfigureAwait(false);
                    else await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, _cts.Token).ConfigureAwait(false);
                }
                else
                {
                    var json = item.Json ?? item.JsonAtSend!();
                    if (Encrypted) await SendEncryptedJsonAsync(json, _cts.Token).ConfigureAwait(false);
                    else await SendRawTextAsync(json, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            Close(ex is OperationCanceledException ? "closed" : ex.Message);
        }
    }

    private Task SendRawTextAsync(string json, CancellationToken token) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, token);

    private Task SendEncryptedJsonAsync(string json, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var plaintext = new byte[bytes.Length + 1];
        plaintext[0] = JsonType;
        bytes.CopyTo(plaintext, 1);
        return SendEncryptedAsync(plaintext, token);
    }

    /// <summary>Encrypts one typed message; larger than a Noise frame allows → type-1 fragments.</summary>
    private async Task SendEncryptedAsync(byte[] plaintext, CancellationToken token)
    {
        if (plaintext.Length <= MaxPlaintext + 1)
        {
            await _ws.SendAsync(EncryptFrame(plaintext), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            return;
        }
        var type = plaintext[0];
        var data = plaintext.AsMemory(1);
        var first = true;
        while (data.Length > 0)
        {
            var room = MaxPlaintext - (first ? 2 : 1);
            var piece = data[..Math.Min(room, data.Length)];
            data = data[piece.Length..];
            var flags = (byte)((first ? 0x02 : 0) | (data.Length == 0 ? 0x01 : 0));
            byte[] frame = first ? [FragmentType, flags, type, .. piece.Span] : [FragmentType, flags, .. piece.Span];
            await _ws.SendAsync(EncryptFrame(frame), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            first = false;
        }
    }

    private byte[] EncryptFrame(byte[] plaintext)
    {
        lock (_lock) return _sendCipher!.Encrypt(plaintext);
    }

    // ---- incoming ----

    private async Task ReceiveLoopAsync()
    {
        var reason = "connection lost";
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var received = await ReceiveRawAsync(_cts.Token).ConfigureAwait(false);
                if (received == null) break;
                var (raw, receivedUs) = received.Value;
                string? text;
                if (Encrypted)
                {
                    if (raw.Binary == null) throw new SendspinProtocolException("Cleartext message after the handshake.");
                    var message = DecryptMessage(raw.Binary);
                    if (message == null) continue;            // a fragment, more to come
                    if (message[0] != JsonType) continue;     // no binary messages from clients for these roles
                    text = Encoding.UTF8.GetString(message, 1, message.Length - 1);
                }
                else
                {
                    if (raw.Text == null) continue;   // binary from a client isn't defined for these roles
                    text = raw.Text;
                }
                if (JsonNode.Parse(text) is not JsonObject json) continue;
                var payload = json["payload"] as JsonObject ?? new JsonObject();
                var type = (string?)json["type"];
                switch (type)
                {
                    case "client/time":
                        var clientTransmitted = (long?)payload["client_transmitted"] ?? 0;
                        EnqueueJsonAtSend(() => Serialize("server/time", new JsonObject
                        {
                            ["client_transmitted"] = clientTransmitted,
                            ["server_received"] = receivedUs,
                            ["server_transmitted"] = SendspinClock.NowUs,
                        }));
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
                        // Older revision: switch when it's a format LunaDrome makes, else answer with the current one.
                        if (StreamFormat is { } current && payload["player"] is JsonObject wanted)
                        {
                            var requested = new SendspinFormat((string?)wanted["codec"] ?? current.Codec,
                                (int?)wanted["sample_rate"] ?? current.SampleRate, (int?)wanted["channels"] ?? current.Channels,
                                (int?)wanted["bit_depth"] ?? current.BitDepth);
                            StartStream(SendspinOutput.Produces(requested) ? requested : current);
                        }
                        break;
                    case "client/leave":
                        State = "external_source";
                        StateChanged?.Invoke(this);
                        break;
                    case "noise/handshake" when Encrypted:
                        CompleteRehandshake(payload);
                        break;
                    case "client/goodbye":
                        reason = (string?)payload["reason"] ?? "goodbye";
                        _cts.Cancel();
                        break;
                    default:
                        if (type != null && (type.StartsWith("client/pair-", StringComparison.Ordinal) || type == "pair/abort"))
                            PairingMessage?.Invoke(type, payload);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException
                                       or ObjectDisposedException or CryptographicException or SendspinProtocolException)
        {
            if (ex is not OperationCanceledException) reason = ex.Message;
        }
        Close(reason);
    }

    /// <summary>Decrypts one frame; returns the whole typed message, or null while a fragmented
    /// one is still incomplete.</summary>
    private byte[]? DecryptMessage(byte[] frame)
    {
        byte[] plaintext;
        lock (_lock) plaintext = _receiveCipher!.Decrypt(frame);
        if (plaintext.Length == 0) throw new SendspinProtocolException("Empty message after decryption.");
        if (plaintext[0] != FragmentType)
        {
            if (_fragmentBuffer != null) throw new SendspinProtocolException("A message arrived inside a fragmented one.");
            return plaintext;
        }
        if (plaintext.Length < 2 || (plaintext[1] & 0xFC) != 0) throw new SendspinProtocolException("Malformed fragment.");
        var flags = plaintext[1];
        if ((flags & 0x02) != 0)
        {
            if (_fragmentBuffer != null || plaintext.Length < 3 || plaintext[2] == FragmentType)
                throw new SendspinProtocolException("Malformed first fragment.");
            _fragmentType = plaintext[2];
            _fragmentBuffer = plaintext[3..];
        }
        else
        {
            if (_fragmentBuffer == null) throw new SendspinProtocolException("A fragment with nothing to continue.");
            _fragmentBuffer = [.. _fragmentBuffer, .. plaintext.AsSpan(2)];
        }
        if ((flags & 0x01) == 0) return null;
        byte[] whole = [(byte)_fragmentType, .. _fragmentBuffer];
        _fragmentBuffer = null;
        return whole;
    }

    private async Task<JsonObject?> ReceiveEncryptedJsonAsync(CancellationToken token)
    {
        while (true)
        {
            var (raw, _) = await ReceiveRawAsync(token).ConfigureAwait(false)
                ?? throw new SendspinProtocolException("The speaker closed the connection.");
            if (raw.Binary == null) throw new SendspinProtocolException("Cleartext message after the handshake.");
            var message = DecryptMessage(raw.Binary);
            if (message == null || message[0] != JsonType) continue;
            return JsonNode.Parse(Encoding.UTF8.GetString(message, 1, message.Length - 1)) as JsonObject;
        }
    }

    private void ReadState(JsonObject payload)
    {
        if ((string?)payload["state"] is { } state) State = state;                                   // older revision
        if (payload["available"] is JsonValue available && available.TryGetValue(out bool isAvailable))
            State = isAvailable ? "synchronized" : "external_source";                                  // encrypted revision
        if (payload["player"] is JsonObject player)
        {
            // Older clients put state inside the player object.
            if ((string?)player["state"] is { } playerState && payload["state"] == null && payload["available"] == null)
                State = playerState;
            if ((int?)player["volume"] is { } volume) Volume = volume;
            if ((bool?)player["muted"] is { } muted) Muted = muted;
            if ((int?)player["static_delay_ms"] is { } delay) StaticDelayMs = Math.Clamp(delay, 0, 5000);
            if ((int?)player["output_delay_ms"] is { } outputDelay) StaticDelayMs = Math.Clamp(outputDelay, 0, 5000);
            if ((int?)player["required_lead_time_ms"] is { } lead) RequiredLeadTimeMs = Math.Clamp(lead, 0, 30000);
            if ((int?)player["min_buffer_ms"] is { } buffer) MinBufferMs = Math.Clamp(buffer, 0, 30000);
            if (Encrypted && player["supported_commands"] is JsonArray commands)
            {
                var list = commands.Select(c => (string?)c).ToList();
                CanSetVolume = list.Contains("volume");
                CanMute = list.Contains("mute");
            }
            if (Encrypted)
                PreferredFormat = player["format"] is JsonObject f ? ReadFormat(f) : null;
        }
        HasState = true;
    }

    /// <summary>The next whole WebSocket message (text or binary) with the time its last byte
    /// arrived; null when the socket closed.</summary>
    private async Task<(RawMessage Message, long ReceivedUs)?> ReceiveRawAsync(CancellationToken token)
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
                ? (new RawMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), null), receivedUs)
                : (new RawMessage(null, message.ToArray()), receivedUs);
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

    private sealed record Outgoing(string? Json, Func<string>? JsonAtSend, byte[]? Audio, long TimestampUs,
        bool Forced = false, bool ReleaseHold = false);
    private sealed record RawMessage(string? Text, byte[]? Binary);
}

public sealed class SendspinProtocolException(string message) : Exception(message);
