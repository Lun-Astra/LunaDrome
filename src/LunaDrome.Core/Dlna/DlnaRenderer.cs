using System.Globalization;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace LunaDrome.Core.Dlna;

/// <summary>What a renderer is asked to play: the address it fetches the song from itself, plus
/// the DIDL-Lite metadata it shows (title, artist, album, cover). <paramref name="StartsAt"/>:
/// where in the song the stream begins; a stream that can't be seeked in
/// (<paramref name="SeekByReload"/>) is fetched again from the new point instead.</summary>
public sealed record DlnaTrack(Uri Url, string Title, string? Artist, string? Album, Uri? CoverArt,
    string MimeType, TimeSpan Duration, TimeSpan StartsAt = default, bool SeekByReload = false);

/// <summary>The renderer's own view of playback (AVTransport GetTransportInfo + GetPositionInfo).</summary>
public sealed record DlnaStatus(string TransportState, TimeSpan Position, TimeSpan Duration, string? TrackUri)
{
    public bool Playing => TransportState is "PLAYING" or "TRANSITIONING";
}

/// <summary>Controls one UPnP AV media renderer over SOAP: AVTransport for the song, play, pause,
/// seek and position, RenderingControl (when it has one) for the volume.</summary>
public sealed class DlnaRenderer(DlnaRendererInfo info)
{
    private const string AvTransport = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string RenderingControl = "urn:schemas-upnp-org:service:RenderingControl:1";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public DlnaRendererInfo Info { get; } = info;
    public string Name => Info.Name;
    public bool CanSetVolume => Info.RenderingControl != null;

    public async Task LoadAsync(DlnaTrack track, CancellationToken cancellationToken = default)
    {
        await AvAsync("SetAVTransportURI", cancellationToken,
            ("CurrentURI", track.Url.AbsoluteUri), ("CurrentURIMetaData", Didl(track))).ConfigureAwait(false);
    }

    public Task PlayAsync(CancellationToken cancellationToken = default) => AvAsync("Play", cancellationToken, ("Speed", "1"));
    public Task PauseAsync(CancellationToken cancellationToken = default) => AvAsync("Pause", cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => AvAsync("Stop", cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
        AvAsync("Seek", cancellationToken, ("Unit", "REL_TIME"), ("Target", FormatTime(position)));

    /// <summary>Volume 0-100 on the renderer itself.</summary>
    public async Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        if (Info.RenderingControl is not { } control)
            return;
        await CallAsync(control, RenderingControl, "SetVolume", cancellationToken,
            ("InstanceID", "0"), ("Channel", "Master"), ("DesiredVolume", Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    public async Task<DlnaStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var transport = await AvAsync("GetTransportInfo", cancellationToken).ConfigureAwait(false);
        var position = await AvAsync("GetPositionInfo", cancellationToken).ConfigureAwait(false);
        return new DlnaStatus(
            (Value(transport, "CurrentTransportState") ?? "").ToUpperInvariant(),
            ParseTime(Value(position, "RelTime")),
            ParseTime(Value(position, "TrackDuration")),
            Value(position, "TrackURI"));
    }

    // ---- SOAP ----

    private Task<XElement> AvAsync(string action, CancellationToken cancellationToken, params (string Name, string Value)[] arguments) =>
        CallAsync(Info.AvTransportControl, AvTransport, action, cancellationToken, [("InstanceID", "0"), .. arguments]);

    // Renderers' small HTTP servers sometimes drop a connection mid-answer; every action used
    // here is safe to send twice, so one retry hides that.
    private static async Task<XElement> CallAsync(Uri control, string service, string action, CancellationToken cancellationToken,
        params (string Name, string Value)[] arguments)
    {
        try
        {
            return await CallOnceAsync(control, service, action, cancellationToken, arguments).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            return await CallOnceAsync(control, service, action, cancellationToken, arguments).ConfigureAwait(false);
        }
    }

    private static async Task<XElement> CallOnceAsync(Uri control, string service, string action, CancellationToken cancellationToken,
        (string Name, string Value)[] arguments)
    {
        XNamespace u = service;
        var envelope = new XDocument(new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Soap),
            new XAttribute(Soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
            new XElement(Soap + "Body",
                new XElement(u + action, new XAttribute(XNamespace.Xmlns + "u", u),
                    arguments.Select(a => new XElement(a.Name, a.Value))))));
        using var content = new StringContent("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");
        using var request = new HttpRequestMessage(HttpMethod.Post, control) { Content = content };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service}#{action}\"");
        // Many renderers' UPnP stacks (libupnp) drop kept-alive connections; reusing one fails the next call.
        request.Headers.ConnectionClose = true;
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        XElement? result = null;
        try { result = XDocument.Parse(body).Root?.Element(Soap + "Body"); }
        catch (System.Xml.XmlException) { }
        if (!response.IsSuccessStatusCode || result?.Element(Soap + "Fault") != null)
        {
            var error = result?.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value
                        ?? result?.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value
                        ?? $"HTTP {(int)response.StatusCode}";
            throw new DlnaException($"{action} failed: {error}");
        }
        return result ?? new XElement("empty");
    }

    private static string? Value(XElement body, string name) =>
        body.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    // ---- formats ----

    /// <summary>DIDL-Lite for one music track; the protocolInfo tells the renderer the file type
    /// up front (some refuse a URL without it).</summary>
    public static string Didl(DlnaTrack track)
    {
        static string X(string? text) => SecurityElement.Escape(text ?? "") ?? "";
        var sb = new StringBuilder();
        sb.Append("<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" ")
          .Append("xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns:dlna=\"urn:schemas-dlna-org:metadata-1-0/\">")
          .Append("<item id=\"lunadrome\" parentID=\"0\" restricted=\"1\">")
          .Append("<dc:title>").Append(X(track.Title)).Append("</dc:title>")
          .Append("<upnp:class>object.item.audioItem.musicTrack</upnp:class>");
        if (!string.IsNullOrEmpty(track.Artist))
            sb.Append("<dc:creator>").Append(X(track.Artist)).Append("</dc:creator>")
              .Append("<upnp:artist>").Append(X(track.Artist)).Append("</upnp:artist>");
        if (!string.IsNullOrEmpty(track.Album))
            sb.Append("<upnp:album>").Append(X(track.Album)).Append("</upnp:album>");
        if (track.CoverArt != null)
            sb.Append("<upnp:albumArtURI>").Append(X(track.CoverArt.AbsoluteUri)).Append("</upnp:albumArtURI>");
        sb.Append("<res protocolInfo=\"http-get:*:").Append(X(track.MimeType)).Append(":*\"");
        if (track.Duration - track.StartsAt > TimeSpan.Zero)
            sb.Append(" duration=\"").Append(FormatTime(track.Duration - track.StartsAt)).Append(".000\"");
        sb.Append('>').Append(X(track.Url.AbsoluteUri)).Append("</res></item></DIDL-Lite>");
        return sb.ToString();
    }

    public static string FormatTime(TimeSpan time) =>
        $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";

    public static TimeSpan ParseTime(string? text)
    {
        // H+:MM:SS[.F+] (or NOT_IMPLEMENTED / empty)
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
        var parts = text.Trim().Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            return TimeSpan.Zero;
        return new TimeSpan(h, m, 0) + TimeSpan.FromSeconds(s);
    }
}

public sealed class DlnaException(string message) : Exception(message);
