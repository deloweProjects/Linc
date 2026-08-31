using System.Net;
using System.Net.Sockets;

namespace Linc.Desktop.Services;

/// <summary>One of this PC's addresses, with the prefix and default gateway that go with it.</summary>
/// <param name="Name">The Windows adapter name, e.g. "Wi-Fi" or "Local Area Connection* 2".</param>
/// <param name="Address">This PC's address on that interface.</param>
/// <param name="PrefixLength">Subnet prefix length in bits (24 for a /24).</param>
/// <param name="Gateway">The interface's default gateway, or null when it has none.</param>
/// <param name="Description">The adapter description, for diagnostics only.</param>
/// <param name="IsVirtual">
/// The adapter looks like a VPN/tunnel/hypervisor adapter rather than a physical link. Only
/// ever used to RANK candidates, never to reject one — see <see cref="HotspotAddress"/>.
/// </param>
public sealed record HotspotInterface(
    string Name,
    string Address,
    int PrefixLength,
    string? Gateway,
    string? Description = null,
    bool IsVirtual = false);

/// <summary>
/// Picks the one address worth dialling on a hotspot link (docs/ROADMAP.md M13), as a pure
/// static so it is testable with no network (tools\hotspotsim).
/// <para>
/// The same-subnet filter is the load-bearing part. Android happily advertises its
/// mobile-data address and any secondary interface it has, none of which are reachable from
/// the hotspot segment; every unfiltered one costs a full TCP connect timeout before the
/// next candidate is even tried. So anything the peer tells us about itself is only
/// believed when it lands inside one of OUR OWN interfaces' subnets.
/// </para>
/// </summary>
public static class HotspotAddress
{
    /// <summary>
    /// Resolution order: (1) the control socket's peer address, (2) an address the phone
    /// announced (the v18 <c>adb.announce</c> hook — M13b fills the list, this code already
    /// consumes it), (3) the default gateway of one of our interfaces, which covers the
    /// phone-is-AP role. Returns null when nothing survives the filter.
    /// </summary>
    /// <param name="peer">Remote endpoint address of the live control connection, if any.</param>
    /// <param name="announced">Addresses the phone claims to have. Untrusted: filtered.</param>
    /// <param name="myInterfaces">This PC's own addresses — the trust anchor for the filter.</param>
    public static string? SelectEndpoint(
        string? peer,
        IReadOnlyList<string>? announced,
        IReadOnlyList<HotspotInterface>? myInterfaces)
    {
        var mine = ParseInterfaces(myInterfaces);
        var candidates = new List<IPAddress>();

        // (1) and (2) come from the far end, so both go through the same-subnet filter.
        AddIfLocal(candidates, peer, mine);
        foreach (var address in announced ?? [])
        {
            AddIfLocal(candidates, address, mine);
        }

        // (3) Gateways are read from our own adapter configuration, not from the peer, so
        // there is nothing to filter — they are on our segment by construction. A gateway
        // equal to our own address means we host the link and there is nobody to dial.
        //
        // Physical adapters first. A dev PC routinely has a VPN or hypervisor adapter up with
        // a default gateway of its own, and on this machine a Radmin VPN adapter enumerated
        // ahead of Wi-Fi and won outright until this ranking existed — which is the "connects
        // leave via the wrong NIC" failure, decided one layer earlier than the socket.
        // Virtual adapters are still kept, last: ranking, never rejection.
        var physicalGateways = new List<IPAddress>();
        var virtualGateways = new List<IPAddress>();
        foreach (var (iface, address, _) in mine)
        {
            if (iface.Gateway is null || !IPAddress.TryParse(iface.Gateway.Trim(), out var gateway))
            {
                continue;
            }
            if (gateway.Equals(IPAddress.Any) || gateway.Equals(IPAddress.IPv6Any) || gateway.Equals(address))
            {
                continue;
            }
            (iface.IsVirtual ? virtualGateways : physicalGateways).Add(gateway);
        }
        foreach (var gateway in physicalGateways.Concat(virtualGateways))
        {
            Add(candidates, gateway);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // `adb connect [fe80::x%iface]:5555` is unreliable on Windows — the scope id has to
        // survive adb's own parsing and it frequently does not. When the best candidate is a
        // link-local IPv6 address and any IPv4 candidate exists at all, take the IPv4 one.
        if (candidates[0].IsIPv6LinkLocal)
        {
            var ipv4 = candidates.FirstOrDefault(c => c.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is not null)
            {
                return ipv4.ToString();
            }
        }
        return candidates[0].ToString();
    }

    /// <summary>
    /// True when <paramref name="peer"/> is an address <see cref="SelectEndpoint"/> would dial in
    /// its own right — that is, it survived the same-subnet filter rather than being replaced by
    /// the gateway fallback. This is what "hotspot-shaped" means for M13d §1.4's trigger.
    /// <para>
    /// It is deliberately expressed THROUGH <see cref="SelectEndpoint"/> rather than as a second
    /// copy of the filter: one filter, one place it can be wrong.
    /// </para>
    /// </summary>
    public static bool IsHotspotShaped(string? peer, IReadOnlyList<HotspotInterface>? myInterfaces)
    {
        if (!IPAddress.TryParse((peer ?? "").Trim(), out var parsed))
        {
            return false;
        }
        var chosen = SelectEndpoint(peer, null, myInterfaces);
        return chosen is not null && IPAddress.TryParse(chosen, out var chosenAddress) && chosenAddress.Equals(parsed);
    }

    /// <summary>True when <paramref name="address"/> lies inside interfaceAddress/prefixLength.</summary>
    public static bool IsSameSubnet(string? address, string? interfaceAddress, int prefixLength)
    {
        if (!IPAddress.TryParse((address ?? "").Trim(), out var a) ||
            !IPAddress.TryParse((interfaceAddress ?? "").Trim(), out var b))
        {
            return false;
        }
        return IsSameSubnet(a, b, prefixLength);
    }

    private static bool IsSameSubnet(IPAddress a, IPAddress b, int prefixLength)
    {
        if (a.AddressFamily != b.AddressFamily)
        {
            return false;
        }
        // Every link-local IPv6 address on every adapter sits in fe80::/64, so the prefix
        // comparison alone calls two addresses on two DIFFERENT adapters "same subnet". The
        // scope id is the thing that separates them; without this, a link-local candidate gets
        // paired with the wrong interface and the socket fails with AddressNotAvailable.
        if (a.IsIPv6LinkLocal && b.IsIPv6LinkLocal && a.ScopeId != b.ScopeId)
        {
            return false;
        }
        var left = a.GetAddressBytes();
        var right = b.GetAddressBytes();
        if (prefixLength <= 0 || prefixLength > left.Length * 8)
        {
            return false; // a /0 would make every address "local", which defeats the filter
        }
        var wholeBytes = prefixLength / 8;
        for (var i = 0; i < wholeBytes; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }
        var remainingBits = prefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }
        var mask = (byte)(0xFF << (8 - remainingBits));
        return (left[wholeBytes] & mask) == (right[wholeBytes] & mask);
    }

    private static List<(HotspotInterface Iface, IPAddress Address, int Prefix)> ParseInterfaces(
        IReadOnlyList<HotspotInterface>? myInterfaces)
    {
        var parsed = new List<(HotspotInterface, IPAddress, int)>();
        foreach (var iface in myInterfaces ?? [])
        {
            if (IPAddress.TryParse((iface.Address ?? "").Trim(), out var address))
            {
                parsed.Add((iface, address, iface.PrefixLength));
            }
        }
        return parsed;
    }

    private static void AddIfLocal(
        List<IPAddress> candidates,
        string? address,
        List<(HotspotInterface Iface, IPAddress Address, int Prefix)> mine)
    {
        if (!IPAddress.TryParse((address ?? "").Trim(), out var parsed))
        {
            return;
        }
        if (IPAddress.IsLoopback(parsed) || parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any))
        {
            return;
        }
        foreach (var (_, myAddress, prefix) in mine)
        {
            if (!parsed.Equals(myAddress) && IsSameSubnet(parsed, myAddress, prefix))
            {
                Add(candidates, parsed);
                return;
            }
        }
    }

    private static void Add(List<IPAddress> candidates, IPAddress address)
    {
        if (!candidates.Contains(address))
        {
            candidates.Add(address);
        }
    }
}
