using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace LunaDrome.Core.Dlna;

/// <summary>A DLNA / UPnP media renderer (TV, receiver, network speaker) and where to control it.</summary>
public sealed record DlnaRendererInfo(string Name, string Udn, Uri Description, Uri AvTransportControl,
    Uri? RenderingControl, string? Manufacturer, string? Model)
{
    public string Key => Udn;
}

/// <summary>Finds media renderers with an SSDP search (UDP multicast to 239.255.255.250:1900) and
/// reads each one's device description for its name and control addresses.</summary>
public static class DlnaDiscovery
{
    private const string RendererType = "urn:schemas-upnp-org:device:MediaRenderer:1";
    private const string AvTransportType = "urn:schemas-upnp-org:service:AVTransport:";
    private const string RenderingControlType = "urn:schemas-upnp-org:service:RenderingControl:";
    private static readonly IPEndPoint SsdpGroup = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly XNamespace Device = "urn:schemas-upnp-org:device-1-0";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<IReadOnlyList<DlnaRendererInfo>> SearchAsync(TimeSpan scanTime, CancellationToken cancellationToken)
    {
        var locations = await FindLocationsAsync(scanTime, cancellationToken).ConfigureAwait(false);
        var described = await Task.WhenAll(locations.Select(l => DescribeAsync(l, cancellationToken))).ConfigureAwait(false);
        return described.OfType<DlnaRendererInfo>().DistinctBy(r => r.Key)
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Reads one renderer's description (also for a renderer added by its address).</summary>
    public static async Task<DlnaRendererInfo?> DescribeAsync(Uri location, CancellationToken cancellationToken)
    {
        try
        {
            var xml = await Http.GetStringAsync(location, cancellationToken).ConfigureAwait(false);
            var root = XDocument.Parse(xml).Root;
            var baseUri = Uri.TryCreate((string?)root?.Element(Device + "URLBase"), UriKind.Absolute, out var b) ? b : location;
            // The renderer may be an embedded device (e.g. inside a TV's root device).
            foreach (var device in root?.Descendants(Device + "device") ?? [])
            {
                if (!((string?)device.Element(Device + "deviceType") ?? "").StartsWith("urn:schemas-upnp-org:device:MediaRenderer:", StringComparison.Ordinal))
                    continue;
                var services = device.Element(Device + "serviceList")?.Elements(Device + "service").ToList() ?? [];
                Uri? Control(string type) => services
                    .Where(s => ((string?)s.Element(Device + "serviceType") ?? "").StartsWith(type, StringComparison.Ordinal))
                    .Select(s => Uri.TryCreate(baseUri, ((string?)s.Element(Device + "controlURL"))?.Trim(), out var u) ? u : null)
                    .FirstOrDefault(u => u != null);
                if (Control(AvTransportType) is not { } avTransport)
                    continue;
                var name = ((string?)device.Element(Device + "friendlyName"))?.Trim();
                return new DlnaRendererInfo(string.IsNullOrEmpty(name) ? location.Host : name,
                    ((string?)device.Element(Device + "UDN"))?.Trim() ?? location.ToString(), location, avTransport,
                    Control(RenderingControlType), ((string?)device.Element(Device + "manufacturer"))?.Trim(),
                    ((string?)device.Element(Device + "modelName"))?.Trim());
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException or UriFormatException)
        {
        }
        return null;
    }

    // One socket per IPv4 interface: on a machine with several networks (VPN, Docker, Wi-Fi + cable)
    // the default multicast route may not be the one the renderers are on.
    private static async Task<HashSet<Uri>> FindLocationsAsync(TimeSpan scanTime, CancellationToken cancellationToken)
    {
        var request = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            $"MX: {Math.Clamp((int)scanTime.TotalSeconds - 1, 1, 5)}\r\n" +
            $"ST: {RendererType}\r\n" +
            "USER-AGENT: LunaDrome UPnP/1.0 DLNADOC/1.50\r\n\r\n");
        var locations = new HashSet<Uri>();
        var sockets = new List<Socket>();
        foreach (var address in LocalAddresses())
        {
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(address, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                sockets.Add(socket);
            }
            catch (SocketException) { }
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(scanTime);
        try
        {
            await Task.WhenAll(sockets.Select(async socket =>
            {
                try
                {
                    // UDP can drop a packet; renderers ignore the duplicate.
                    for (var i = 0; i < 2; i++)
                        await socket.SendToAsync(request, SocketFlags.None, SsdpGroup, stop.Token).ConfigureAwait(false);
                    var buffer = new byte[4096];
                    while (true)
                    {
                        var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop.Token).ConfigureAwait(false);
                        if (ReadLocation(Encoding.ASCII.GetString(buffer, 0, received.ReceivedBytes)) is { } location)
                            lock (locations) locations.Add(location);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
            })).ConfigureAwait(false);
        }
        finally
        {
            foreach (var socket in sockets) socket.Dispose();
        }
        return locations;
    }

    private static Uri? ReadLocation(string response)
    {
        foreach (var line in response.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(line[(colon + 1)..].Trim(), UriKind.Absolute, out var uri))
                return uri;
        }
        return null;
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        IEnumerable<IPAddress> found;
        try
        {
            found = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.SupportsMulticast)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .ToList();
        }
        catch (NetworkInformationException)
        {
            found = [];
        }
        // Some phones don't list their interfaces; the default route then has to do.
        return found.Any() ? found : [IPAddress.Any];
    }
}
