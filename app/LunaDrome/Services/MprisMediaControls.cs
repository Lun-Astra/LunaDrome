using System.Text;
using LunaDrome.Core.Models;
using Tmds.DBus.Protocol;

namespace LunaDrome.Services;

/// <summary>
/// MPRIS (org.mpris.MediaPlayer2) on the D-Bus session bus: the Linux counterpart of Windows'
/// media flyout. KDE/GNOME media widgets show the track and art, and the desktop routes the
/// keyboard's media keys (and playerctl) to us through it. Uses Tmds.DBus.Protocol, which Uno's
/// X11 host already ships.
/// </summary>
internal sealed class MprisMediaControls : IMediaControls, IPathMethodHandler
{
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private readonly DBusConnection _connection;
    private readonly object _lock = new();

    // Snapshot of the player state, read from the D-Bus thread.
    private string _playbackStatus = "Stopped";
    private Dictionary<string, VariantValue> _metadata = new();
    private bool _canGoPrevious, _canGoNext;
    private long _positionMicroseconds;

    public event Action<MediaCommand>? CommandReceived;

    public string Path => ObjectPath;

    private MprisMediaControls(DBusConnection connection) => _connection = connection;

    public static MprisMediaControls? TryCreate()
    {
        if (DBusAddress.Session is not { } address)
            return null;

        var connection = new DBusConnection(address);
        var controls = new MprisMediaControls(connection);
        _ = controls.ConnectAsync();
        return controls;
    }

    private async Task ConnectAsync()
    {
        try
        {
            await _connection.ConnectAsync();
            _connection.AddMethodHandler(this);
            // A second LunaDrome instance gets a unique suffix, as the MPRIS spec suggests.
            if (!await _connection.TryRequestNameAsync("org.mpris.MediaPlayer2.lunadrome", default))
                await _connection.TryRequestNameAsync($"org.mpris.MediaPlayer2.lunadrome.instance{Environment.ProcessId}", default);
            Log("MPRIS registered");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LunaDrome] MPRIS unavailable (no media keys / desktop media widget): {ex.Message}");
        }
    }

    // ----- Updates from the app -----

    public void SetTrack(Song? song, string? artUrl = null)
    {
        var metadata = new Dictionary<string, VariantValue>();
        if (song != null)
        {
            metadata["mpris:trackid"] = VariantValue.ObjectPath(new ObjectPath("/org/lunadrome/track/" + SanitizeForPath(song.Id)));
            metadata["mpris:length"] = VariantValue.Int64(song.DurationSeconds * 1_000_000L);
            metadata["xesam:title"] = VariantValue.String(song.Title);
            metadata["xesam:artist"] = new Array<string>(new[] { song.ArtistName }).AsVariantValue();
            metadata["xesam:album"] = VariantValue.String(song.AlbumName);
            if (!string.IsNullOrEmpty(song.AlbumArtistName))
                metadata["xesam:albumArtist"] = new Array<string>(new[] { song.AlbumArtistName }).AsVariantValue();
            if (!string.IsNullOrEmpty(artUrl))
                metadata["mpris:artUrl"] = VariantValue.String(artUrl);
        }

        lock (_lock)
            _metadata = metadata;
        EmitPlayerPropertiesChanged(("Metadata", MetadataVariant(metadata)));
    }

    public void SetPlaybackState(bool? playing)
    {
        var status = playing switch { true => "Playing", false => "Paused", null => "Stopped" };
        lock (_lock)
        {
            if (_playbackStatus == status)
                return;
            _playbackStatus = status;
        }
        EmitPlayerPropertiesChanged(("PlaybackStatus", VariantValue.String(status)));
    }

    public void SetCanGoPreviousNext(bool canGoPrevious, bool canGoNext)
    {
        lock (_lock)
        {
            if (_canGoPrevious == canGoPrevious && _canGoNext == canGoNext)
                return;
            _canGoPrevious = canGoPrevious;
            _canGoNext = canGoNext;
        }
        EmitPlayerPropertiesChanged(("CanGoPrevious", VariantValue.Bool(canGoPrevious)), ("CanGoNext", VariantValue.Bool(canGoNext)));
    }

    // Position isn't signalled (the spec says clients poll it); just keep it current.
    public void SetPosition(TimeSpan position, TimeSpan duration) =>
        Interlocked.Exchange(ref _positionMicroseconds, (long)(position.TotalMilliseconds * 1000));

    // ----- D-Bus method handling -----

    public bool HandlesChildPaths => false;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        try
        {
            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml(new ReadOnlyMemory<byte>[] { IntrospectionXml });
                return ValueTask.CompletedTask;
            }

            var request = context.Request;
            switch (request.InterfaceAsString, request.MemberAsString)
            {
                case (PropertiesInterface, "Get"):
                {
                    var reader = request.GetBodyReader();
                    var iface = reader.ReadString();
                    var name = reader.ReadString();
                    if (!GetProperties(iface).TryGetValue(name, out var value))
                    {
                        context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {name} on {iface}");
                        return ValueTask.CompletedTask;
                    }
                    using var writer = context.CreateReplyWriter("v");
                    writer.WriteVariant(value);
                    context.Reply(writer.CreateMessage());
                    return ValueTask.CompletedTask;
                }
                case (PropertiesInterface, "GetAll"):
                {
                    var iface = request.GetBodyReader().ReadString();
                    using var writer = context.CreateReplyWriter("a{sv}");
                    writer.WriteDictionary(GetProperties(iface));
                    context.Reply(writer.CreateMessage());
                    return ValueTask.CompletedTask;
                }
                case (PropertiesInterface, "Set"):
                    // Volume/Rate/Loop/Shuffle are read-only here.
                    ReplyEmpty(context);
                    return ValueTask.CompletedTask;

                case (RootInterface, "Raise" or "Quit"):
                    ReplyEmpty(context);
                    return ValueTask.CompletedTask;

                case (PlayerInterface, var member):
                    MediaCommand? command = member switch
                    {
                        "Play" => MediaCommand.Play,
                        "Pause" => MediaCommand.Pause,
                        "PlayPause" => MediaCommand.PlayPause,
                        "Stop" => MediaCommand.Stop,
                        "Next" => MediaCommand.Next,
                        "Previous" => MediaCommand.Previous,
                        _ => null, // Seek / SetPosition / OpenUri: CanSeek is false
                    };
                    if (command is { } c)
                        CommandReceived?.Invoke(c);
                    ReplyEmpty(context);
                    return ValueTask.CompletedTask;
            }

            context.ReplyUnknownMethodError();
        }
        catch (Exception ex)
        {
            Log($"MPRIS request failed: {ex.Message}");
        }
        return ValueTask.CompletedTask;
    }

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected)
            return;
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    private Dictionary<string, VariantValue> GetProperties(string iface)
    {
        if (iface == RootInterface)
        {
            return new()
            {
                ["CanQuit"] = VariantValue.Bool(false),
                ["CanRaise"] = VariantValue.Bool(false),
                ["HasTrackList"] = VariantValue.Bool(false),
                ["Identity"] = VariantValue.String("LunaDrome"),
                ["DesktopEntry"] = VariantValue.String("lunadrome"),
                ["SupportedUriSchemes"] = new Array<string>().AsVariantValue(),
                ["SupportedMimeTypes"] = new Array<string>().AsVariantValue(),
            };
        }

        if (iface == PlayerInterface)
        {
            lock (_lock)
            {
                return new()
                {
                    ["PlaybackStatus"] = VariantValue.String(_playbackStatus),
                    ["LoopStatus"] = VariantValue.String("None"),
                    ["Rate"] = VariantValue.Double(1.0),
                    ["Shuffle"] = VariantValue.Bool(false),
                    ["Metadata"] = MetadataVariant(_metadata),
                    ["Volume"] = VariantValue.Double(1.0),
                    ["Position"] = VariantValue.Int64(Interlocked.Read(ref _positionMicroseconds)),
                    ["MinimumRate"] = VariantValue.Double(1.0),
                    ["MaximumRate"] = VariantValue.Double(1.0),
                    ["CanGoNext"] = VariantValue.Bool(_canGoNext),
                    ["CanGoPrevious"] = VariantValue.Bool(_canGoPrevious),
                    ["CanPlay"] = VariantValue.Bool(_metadata.Count > 0),
                    ["CanPause"] = VariantValue.Bool(_metadata.Count > 0),
                    ["CanSeek"] = VariantValue.Bool(false),
                    ["CanControl"] = VariantValue.Bool(true),
                };
            }
        }

        return new();
    }

    private static VariantValue MetadataVariant(Dictionary<string, VariantValue> metadata) =>
        new Dict<string, VariantValue>(metadata).AsVariantValue();

    private void EmitPlayerPropertiesChanged(params (string Name, VariantValue Value)[] changes)
    {
        try
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(null, ObjectPath, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
            writer.WriteString(PlayerInterface);
            writer.WriteDictionary(changes.ToDictionary(c => c.Name, c => c.Value));
            writer.WriteArray(System.Array.Empty<string>());
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch
        {
            // Not connected (yet) - clients read the current state when they connect anyway.
        }
    }

    // D-Bus object paths only allow [A-Za-z0-9_].
    private static string SanitizeForPath(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var ch in id)
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        return sb.Length > 0 ? sb.ToString() : "none";
    }

    private static readonly ReadOnlyMemory<byte> IntrospectionXml = Encoding.UTF8.GetBytes("""
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <method name="Seek"><arg direction="in" name="Offset" type="x"/></method>
          <method name="SetPosition"><arg direction="in" name="TrackId" type="o"/><arg direction="in" name="Position" type="x"/></method>
          <method name="OpenUri"><arg direction="in" name="Uri" type="s"/></method>
          <signal name="Seeked"><arg name="Position" type="x"/></signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="LoopStatus" type="s" access="readwrite"/>
          <property name="Rate" type="d" access="readwrite"/>
          <property name="Shuffle" type="b" access="readwrite"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="readwrite"/>
          <property name="Position" type="x" access="read"/>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>
        """);

    private static void Log(string message)
    {
        if (Environment.GetEnvironmentVariable("LUNADROME_LOG") == "1")
            Console.Error.WriteLine($"[LunaDrome] {DateTime.Now:HH:mm:ss.fff} {message}");
    }

    public void Dispose()
    {
        try { _connection.Dispose(); }
        catch { /* shutting down */ }
    }
}
