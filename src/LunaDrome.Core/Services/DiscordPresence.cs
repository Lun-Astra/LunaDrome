using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LunaDrome.Core.Services;

/// <summary>
/// "Listening to LunaDrome" on the user's Discord profile, through the local Discord client's
/// IPC (a named pipe on Windows, a Unix socket on Linux/macOS) - no library, no network.
///
/// Updates come from the UI thread and only record the wanted state; one background loop
/// connects (retrying while Discord isn't running) and sends the newest state, at most once per
/// <see cref="MinSendGap"/> (Discord drops activity updates sent faster than ~5 per 20s).
/// The image is the album's public cover link when there is one (Navidrome's /share/img, see
/// NavidromeClient.GetPublicCoverAsync) with the app's "lunadrome" art asset as the small
/// badge, else that asset alone - never the normal cover URL: that carries the login token.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    public const string ApplicationId = "1558971183121305600";

    private static readonly TimeSpan MinSendGap = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private sealed record Activity(string Title, string Artist, string Album, DateTimeOffset Start, DateTimeOffset End, string? ImageUrl);

    // Discord refuses an activity whose image key/URL is longer than this.
    private const int MaxImageLength = 256;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _changed = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Activity? _wanted;
    private bool _enabled;
    private int _version;
    private Stream? _lostStream;   // set by the reply reader when Discord closes the connection
    private Task? _loop;

    /// <summary>Diagnostics (LUNADROME_LOG=1); never throws into the caller.</summary>
    public Action<string>? Log { get; set; }

    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
        set
        {
            lock (_lock)
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                _version++;
                _loop ??= Task.Run(RunAsync);
            }
            _changed.Release();
        }
    }

    /// <summary>Show a playing song. <paramref name="position"/>/<paramref name="duration"/> set the
    /// progress bar; call again after a seek. <paramref name="imageUrl"/>: a public https image
    /// (the album cover) to show instead of the LunaDrome logo.</summary>
    public void SetPlaying(string title, string artist, string album, TimeSpan position, TimeSpan duration, string? imageUrl = null)
    {
        var start = DateTimeOffset.UtcNow - position;
        if (imageUrl is { Length: > MaxImageLength })
            imageUrl = null;
        var activity = new Activity(title, artist, album, start, start + duration, imageUrl);
        lock (_lock)
        {
            // A position report just confirming the same song + timing isn't a change (the player
            // reports every ~0.4s; only a seek moves the start by more than a couple of seconds).
            if (_wanted is { } w && w.Title == title && w.Artist == artist && w.Album == album && w.ImageUrl == imageUrl
                && Math.Abs((w.Start - start).TotalSeconds) < 2.5 && Math.Abs((w.End - activity.End).TotalSeconds) < 2.5)
                return;
            _wanted = activity;
            _version++;
        }
        _changed.Release();
    }

    /// <summary>Paused, stopped or nothing loaded: no activity.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_wanted == null)
                return;
            _wanted = null;
            _version++;
        }
        _changed.Release();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _changed.Release();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { /* closing anyway */ }
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        Stream? stream = null;
        var sentVersion = -1;
        var shown = false;   // an activity is on the profile now (so clearing has a purpose)
        var lastSend = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            bool enabled;
            int version;
            Activity? wanted;
            bool lost;
            lock (_lock)
            {
                enabled = _enabled;
                version = _version;
                wanted = _wanted;
                lost = stream != null && ReferenceEquals(_lostStream, stream);
            }

            if (lost)
            {
                // Discord quit (or restarted): connect again, the current song gets sent anew.
                Log?.Invoke("discord: connection closed by Discord");
                stream!.Dispose();
                stream = null;
            }

            try
            {
                if (!enabled)
                {
                    // Turned off: clear the profile, then let go of the connection.
                    if (stream != null)
                    {
                        await SendActivityAsync(stream, null, token);
                        stream.Dispose();
                        stream = null;
                        Log?.Invoke("discord: disconnected (turned off)");
                    }
                    sentVersion = -1;
                    shown = false;
                    await _changed.WaitAsync(token);
                    continue;
                }

                if (stream == null)
                {
                    stream = await ConnectAsync(token);
                    if (stream == null)
                    {
                        await WaitAsync(RetryDelay, token);   // Discord isn't running; try again later
                        continue;
                    }
                    sentVersion = -1;
                    shown = false;   // a new connection starts with an empty profile
                }

                if (version != sentVersion && wanted == null && !shown)
                {
                    // Nothing to clear: don't spend the rate limit (it would delay the next song).
                    sentVersion = version;
                }
                else if (version != sentVersion)
                {
                    var wait = lastSend + MinSendGap - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, token);
                        continue;   // re-read: a newer state may have come in meanwhile
                    }
                    await SendActivityAsync(stream, wanted, token);
                    lastSend = DateTimeOffset.UtcNow;
                    sentVersion = version;
                    shown = wanted != null;
                    Log?.Invoke(wanted == null ? "discord: cleared" : $"discord: {wanted.Title} - {wanted.Artist}");
                }

                await _changed.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Discord closed or the pipe broke: reconnect on the next round.
                Log?.Invoke($"discord: connection lost ({ex.Message})");
                stream?.Dispose();
                stream = null;
                try { await WaitAsync(RetryDelay, token); } catch (OperationCanceledException) { break; }
            }
        }

        if (stream != null)
        {
            try
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await SendActivityAsync(stream, null, quick.Token);
            }
            catch { /* exiting anyway */ }
            stream.Dispose();
        }
    }

    // Wakes early when the state changes (e.g. turned off), so a retry wait doesn't delay that.
    private async Task WaitAsync(TimeSpan delay, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var delayTask = Task.Delay(delay, linked.Token);
        var changedTask = _changed.WaitAsync(linked.Token);
        await Task.WhenAny(delayTask, changedTask);
        linked.Cancel();
        if (changedTask.IsCompletedSuccessfully)
            _changed.Release();   // leave the signal for the main loop
        token.ThrowIfCancellationRequested();
    }

    private async Task<Stream?> ConnectAsync(CancellationToken token)
    {
        foreach (var candidate in CandidatePaths())
        {
            Stream? stream = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeClientStream(".", candidate, PipeDirection.InOut, PipeOptions.Asynchronous);
                    stream = pipe;
                    await pipe.ConnectAsync(200, token);
                }
                else
                {
                    if (!File.Exists(candidate))
                        continue;
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(candidate), token);
                    stream = new NetworkStream(socket, ownsSocket: true);
                }

                await WriteFrameAsync(stream, 0, new JsonObject { ["v"] = 1, ["client_id"] = ApplicationId }, token);
                var (op, json) = await ReadFrameAsync(stream, token);
                if (op != 1 || json?["evt"]?.GetValue<string>() != "READY")
                    throw new IOException($"unexpected handshake reply (op {op}): {json?.ToJsonString()}");

                // Which app answered: the Discord client, or a stand-in such as arRPC (Vesktop,
                // Vencord, Legcord on Linux), which may not show image URLs.
                var who = json?["data"]?["user"]?["username"]?.GetValue<string>();
                Log?.Invoke($"discord: connected ({candidate}{(who == null ? "" : $", as {who}")})"
                            + (who == "arrpc" ? " - arRPC: album covers may not show, Discord's own app shows them" : ""));
                var connected = stream;
                var log = Log;
                _ = Task.Run(async () =>
                {
                    await DrainAsync(connected, log, token);
                    lock (_lock)
                        _lostStream = connected;
                    _changed.Release();
                });
                return stream;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                stream?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                if (OperatingSystem.IsWindows() && ex is TimeoutException)
                    Log?.Invoke($"discord: {candidate} not there");
                else
                    Log?.Invoke($"discord: {candidate}: {ex.Message}");
                stream?.Dispose();
            }
        }
        return null;
    }

    // Discord answers every command; read those replies so the pipe never fills up, and answer pings.
    private static async Task DrainAsync(Stream stream, Action<string>? log, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var (op, json) = await ReadFrameAsync(stream, token);
                if (op == 3)   // PING
                    await WriteFrameAsync(stream, 4, json ?? new JsonObject(), token);
                else if (op == 2)   // CLOSE
                    break;
                else if (json?["evt"]?.GetValue<string>() == "ERROR")
                    // Discord refused an activity (e.g. an image it can't use): say what it said.
                    log?.Invoke($"discord: Discord refused the status: {json["data"]?["message"]?.GetValue<string>() ?? json.ToJsonString()}");
            }
        }
        catch { /* closed; the send loop notices on its next write */ }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            for (var i = 0; i < 10; i++)
                yield return $"discord-ipc-{i}";
            yield break;
        }

        var dirs = new List<string>();
        foreach (var name in new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } dir && !dirs.Contains(dir))
                dirs.Add(dir);
        if (!dirs.Contains("/tmp"))
            dirs.Add("/tmp");

        // Plain install, Flatpak and Snap Discord each put the socket somewhere else.
        foreach (var dir in dirs)
            foreach (var sub in new[] { "", "app/com.discordapp.Discord/", "snap.discord/" })
                for (var i = 0; i < 10; i++)
                    yield return Path.Combine(dir, $"{sub}discord-ipc-{i}");
    }

    private static Task SendActivityAsync(Stream stream, Activity? activity, CancellationToken token)
    {
        JsonNode? payload = null;
        if (activity != null)
        {
            payload = new JsonObject
            {
                ["type"] = 2,   // "Listening to"
                ["details"] = Fit(activity.Title),
                ["state"] = Fit(activity.Artist),
                ["timestamps"] = new JsonObject
                {
                    ["start"] = activity.Start.ToUnixTimeMilliseconds(),
                    ["end"] = activity.End.ToUnixTimeMilliseconds()
                },
                ["assets"] = activity.ImageUrl == null
                    ? new JsonObject
                    {
                        ["large_image"] = "lunadrome",
                        ["large_text"] = Fit(string.IsNullOrWhiteSpace(activity.Album) ? "LunaDrome" : activity.Album)
                    }
                    : new JsonObject
                    {
                        // An https URL works in place of an asset key; Discord's media proxy fetches it.
                        ["large_image"] = activity.ImageUrl,
                        ["large_text"] = Fit(string.IsNullOrWhiteSpace(activity.Album) ? "LunaDrome" : activity.Album),
                        ["small_image"] = "lunadrome",
                        ["small_text"] = "LunaDrome"
                    }
            };
        }

        var frame = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = payload },
            ["nonce"] = Guid.NewGuid().ToString()
        };
        return WriteFrameAsync(stream, 1, frame, token);
    }

    // Discord wants 2..128 characters in each text field.
    private static string Fit(string text)
    {
        text = text.Trim();
        if (text.Length < 2)
            text = text.PadRight(2, '⠀');   // a blank braille cell: invisible, but counts
        return text.Length <= 128 ? text : text[..127] + "…";
    }

    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private static async Task WriteFrameAsync(Stream stream, int op, JsonNode payload, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);

        // The send loop and the ping answer share the stream.
        await WriteLock.WaitAsync(token);
        try
        {
            await stream.WriteAsync(frame, token);
            await stream.FlushAsync(token);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private static async Task<(int Op, JsonNode? Json)> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, token);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 1 << 20)
            throw new IOException($"bad frame length {length}");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        JsonNode? json = null;
        try { json = JsonNode.Parse(body); } catch (JsonException) { }
        return (op, json);
    }
}
