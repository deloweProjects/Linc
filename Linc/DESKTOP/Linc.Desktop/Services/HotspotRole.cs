using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Linc.Desktop.Services;

/// <summary>Which end of a hotspot link this machine sits on (docs/ROADMAP.md M13).</summary>
public enum HotspotRole
{
    /// <summary>This machine hosts the link (or the interface has no gateway at all): LISTEN.</summary>
    Ap,

    /// <summary>This machine joined someone else's link: LISTEN, and also DIAL the gateway.</summary>
    Client,
}

/// <summary>
/// The role rule, as a pure static so it can be tested with no network at all
/// (tools\hotspotsim). Both ends run the identical logic: whoever hosts the link has no
/// gateway on it (or is its own gateway) and ends up listening; whoever joined sees the
/// host as its gateway and ends up dialling it.
/// </summary>
public static class HotspotRoleRule
{
    /// <summary>
    /// Decides the role for one interface from its default gateway and this machine's
    /// address on it. No I/O — see <see cref="HotspotInterfaces"/> for the lookup.
    /// </summary>
    public static HotspotRole DecideRole(string? gateway, string myAddress)
    {
        var mine = (myAddress ?? "").Trim();
        if (string.IsNullOrWhiteSpace(gateway))
        {
            return HotspotRole.Ap; // no route off this interface ⇒ we are the top of it
        }
        var gw = gateway.Trim();
        if (IPAddress.TryParse(gw, out var gatewayAddress))
        {
            // Windows reports "no gateway" on some adapters as 0.0.0.0 / :: rather than by
            // omitting the entry, so both spellings have to mean AP.
            if (gatewayAddress.Equals(IPAddress.Any) || gatewayAddress.Equals(IPAddress.IPv6Any))
            {
                return HotspotRole.Ap;
            }
            if (IPAddress.TryParse(mine, out var myAddressParsed) && gatewayAddress.Equals(myAddressParsed))
            {
                return HotspotRole.Ap; // we ARE the gateway: Windows Mobile Hotspot hosting the link
            }
            return HotspotRole.Client;
        }
        // Unparseable gateway text: fall back to a textual comparison rather than guessing.
        return string.Equals(gw, mine, StringComparison.OrdinalIgnoreCase)
            ? HotspotRole.Ap
            : HotspotRole.Client;
    }

    /// <summary>
    /// Belt and braces (M13 §3.1): both roles listen unconditionally — a duplicate inbound
    /// connection is cheap to drop, a missing one is not recoverable. Only the client dials.
    /// </summary>
    public static bool ShouldDial(HotspotRole role) => role == HotspotRole.Client;
}

/// <summary>
/// The thin platform wrapper around <see cref="HotspotRoleRule"/>: enumerates this PC's
/// interfaces and their default gateways so the pure rule can be applied to each.
/// <para>
/// The gateway lookup uses <c>IPInterfaceProperties.GatewayAddresses</c> rather than
/// <c>GetBestRoute2</c>/<c>Get-NetRoute</c>: it is the same per-interface default-gateway
/// information, in-box, with no P/Invoke and no process spawn, and it is already what
/// TlsTransportService uses to tell a real network from a virtual one.
/// </para>
/// </summary>
public static class HotspotInterfaces
{
    /// <summary>
    /// Every up, non-loopback interface with a unicast address, newest information each call.
    /// Virtual adapters are kept here on purpose — this feeds a diagnostic whose whole job is
    /// to show the owner what Windows thinks the machine is attached to.
    /// </summary>
    public static IReadOnlyList<HotspotInterface> Enumerate(bool includeIPv6 = true)
    {
        var result = new List<HotspotInterface>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            IPInterfaceProperties props;
            try
            {
                props = ni.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue; // adapter went away between enumeration and query
            }
            foreach (var unicast in props.UnicastAddresses)
            {
                var family = unicast.Address.AddressFamily;
                if (family != AddressFamily.InterNetwork &&
                    (family != AddressFamily.InterNetworkV6 || !includeIPv6))
                {
                    continue;
                }
                var gateway = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == family && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any));
                result.Add(new HotspotInterface(
                    Name: ni.Name,
                    Address: unicast.Address.ToString(),
                    PrefixLength: unicast.PrefixLength,
                    Gateway: gateway?.ToString(),
                    Description: ni.Description,
                    IsVirtual: LooksVirtual(ni)));
            }
        }
        return result;
    }

    /// <summary>The role this machine has on a given interface.</summary>
    public static HotspotRole RoleFor(HotspotInterface iface) =>
        HotspotRoleRule.DecideRole(iface.Gateway, iface.Address);

    /// <summary>
    /// Name/description keywords for adapters that are not a physical link. A heuristic, and
    /// deliberately only ever used to rank candidates last rather than to drop them — a false
    /// positive then costs ordering, not connectivity. Extends the list TlsTransportService
    /// already uses with the VPN family, because that list misses adapters whose description
    /// says only "Ethernet Adapter" (a Radmin VPN adapter on the dev PC is exactly that, and
    /// it out-ranked real Wi-Fi until this existed).
    /// </summary>
    private static readonly string[] VirtualKeywords =
    [
        "hyper-v", "virtual", "vethernet", "vmware", "virtualbox", "docker", "loopback",
        "bluetooth", "npcap", "vpn", "tap-windows", "tap-nordvpn", "wintun", "openvpn",
        "tailscale", "zerotier", "hamachi", "radmin", "teredo", "tunnel", "wireguard",
    ];

    private static bool LooksVirtual(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
        {
            return true;
        }
        var haystack = $"{ni.Name} {ni.Description}".ToLowerInvariant();
        return VirtualKeywords.Any(keyword => haystack.Contains(keyword, StringComparison.Ordinal));
    }
}
