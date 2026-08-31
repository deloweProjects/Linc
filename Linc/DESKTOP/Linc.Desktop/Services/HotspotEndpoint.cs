using System.Net;
using System.Net.Sockets;

namespace Linc.Desktop.Services;

/// <summary>
/// Builds and recognises <c>ip:port</c> ADB endpoints (M13f §5). Pure — plain System.Net, no
/// WinUI — so tools\hotspotsim compiles it verbatim. This is the one place an endpoint string
/// is formed, so a port can only ever be appended once.
/// </summary>
public static class HotspotEndpoint
{
    /// <summary>
    /// Builds an <c>ip:port</c> endpoint from a bare address and a port. When
    /// <paramref name="address"/> is ALREADY an endpoint it is returned untouched — appending a
    /// second port is exactly the malformed <c>192.168.43.1:5555:5555</c> the M13c health loop
    /// used to hand adb, and the whole reason this guard exists.
    /// </summary>
    public static string Build(string address, int port)
    {
        var trimmed = (address ?? "").Trim();
        if (IsEndpoint(trimmed))
        {
            return trimmed;
        }
        if (IPAddress.TryParse(trimmed, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return $"[{trimmed}]:{port}";
        }
        return $"{trimmed}:{port}";
    }

    /// <summary>True when <paramref name="value"/> is a well-formed <c>ip:port</c> endpoint (IPv6
    /// literals bracketed, port in range). A bare address, a hostname and <c>ip:port:5555</c>
    /// are all false — the last because it has one port too many to be an endpoint.</summary>
    public static bool IsEndpoint(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }
        var colon = trimmed.LastIndexOf(':');
        if (colon <= 0)
        {
            return false;
        }
        var host = trimmed[..colon];
        var portText = trimmed[(colon + 1)..];
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            return false;
        }
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }
        return IPAddress.TryParse(host, out _);
    }
}
