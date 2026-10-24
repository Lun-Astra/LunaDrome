using System.Net.Sockets;
using Zeroconf;

namespace LunaDrome.Core.Sendspin;

/// <summary>A speaker found on the network: what it advertises over mDNS.</summary>
public sealed record SendspinSpeakerInfo(string Name, Uri Url)
{
    public string Key => Url.ToString();
}

/// <summary>Finds Sendspin speakers: they advertise <c>_sendspin._tcp</c> over mDNS with the
/// WebSocket port and a <c>path</c> TXT key, and wait for a server to connect to them.</summary>
public static class SendspinDiscovery
{
    public const string ServiceType = "_sendspin._tcp.local.";

    public static async Task<IReadOnlyList<SendspinSpeakerInfo>> BrowseAsync(TimeSpan scanTime, CancellationToken cancellationToken)
    {
        var hosts = await ZeroconfResolver.ResolveAsync(ServiceType, scanTime, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var found = new List<SendspinSpeakerInfo>();
        foreach (var host in hosts)
        {
            // Prefer an IPv4 address: link-local IPv6 needs a scope id that a URI can't carry well.
            var address = host.IPAddresses.FirstOrDefault(a => System.Net.IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                ?? host.IPAddress;
            if (string.IsNullOrEmpty(address)) continue;
            foreach (var service in host.Services.Values.Where(s => s.Name.EndsWith(ServiceType, StringComparison.OrdinalIgnoreCase)
                         || s.Name.Contains("_sendspin._tcp", StringComparison.OrdinalIgnoreCase)))
            {
                var txt = service.Properties.SelectMany(p => p).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
                var path = txt.TryGetValue("path", out var p) && !string.IsNullOrWhiteSpace(p) ? p : "/sendspin";
                if (!path.StartsWith('/')) path = "/" + path;
                var name = txt.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n) ? n : host.DisplayName;
                var hostPart = address.Contains(':') ? $"[{address}]" : address;
                if (Uri.TryCreate($"ws://{hostPart}:{service.Port}{path}", UriKind.Absolute, out var url))
                    found.Add(new SendspinSpeakerInfo(name, url));
            }
        }
        return found.DistinctBy(s => s.Key).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
