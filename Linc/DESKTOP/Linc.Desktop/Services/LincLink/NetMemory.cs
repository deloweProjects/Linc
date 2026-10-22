using System.Security.Cryptography;
using System.Text;

namespace Linc.Desktop.Services;

/// <summary>How a route reached the phone, for the memory and the logs.</summary>
public static class LinkRoute
{
    public const string Usb = "usb";
    public const string AdbWifi = "adb-wifi";

    /// <summary>The PC dialled the phone's LincLink listener (v32).</summary>
    public const string TlsOut = "tls-out";

    /// <summary>The phone dialled the PC (v9).</summary>
    public const string TlsIn = "tls-in";
}

/// <summary>One remembered way to the phone on one network.</summary>
public sealed class RouteMemo
{
    public string Route { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTime LastOkUtc { get; set; }
    public int OkCount { get; set; }
    public int FailStreak { get; set; }
    public int RttMs { get; set; }
}

/// <summary>Everything remembered about one phone on one network.</summary>
public sealed class NetEntry
{
    public DateTime LastUsedUtc { get; set; }
    public List<RouteMemo> Routes { get; set; } = [];
}

/// <summary>
/// Which route won, and at which address, per network and phone (linclink.md §6), so the next
/// race on that network starts with the winner. Keyed by a network fingerprint (a hash of the
/// gateway's hardware address and the subnet, so two homes with the same 192.168.1.x are told
/// apart). It holds addresses and counters, never a secret. Pure: the owner serialises
/// <see cref="Entries"/> as JSON and passes the clock in.
/// </summary>
public sealed class NetMemory
{
    public const int RoutesPerEntry = 8;
    public const int MaxEntries = 16;
    public const int ForgetAfter = 5;

    public Dictionary<string, NetEntry> Entries { get; set; } = new(StringComparer.Ordinal);

    /// <summary>A short, stable id for a network: nothing about it can be read back from the hash.</summary>
    public static string Fingerprint(string? gatewayMac, string? subnet)
    {
        var raw = $"{(gatewayMac ?? "").Trim().ToLowerInvariant()}|{(subnet ?? "").Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    }

    private static string Key(string network, string phone) => $"{network}|{phone}";

    public void Succeeded(string network, string phone, string route, string address, int rttMs, DateTime nowUtc)
    {
        var entry = Touch(network, phone, nowUtc);
        var memo = entry.Routes.FirstOrDefault(m => Same(m, route, address));
        if (memo is null)
        {
            memo = new RouteMemo { Route = route, Address = address };
            entry.Routes.Add(memo);
        }
        memo.LastOkUtc = nowUtc;
        memo.OkCount++;
        memo.FailStreak = 0;
        memo.RttMs = rttMs;
        if (entry.Routes.Count > RoutesPerEntry)
        {
            entry.Routes.Remove(entry.Routes.OrderBy(m => m.LastOkUtc).First());
        }
    }

    /// <summary>A remembered route failed; after <see cref="ForgetAfter"/> in a row it is forgotten (an old DHCP lease).</summary>
    public void Failed(string network, string phone, string route, string address)
    {
        if (!Entries.TryGetValue(Key(network, phone), out var entry))
        {
            return;
        }
        var memo = entry.Routes.FirstOrDefault(m => Same(m, route, address));
        if (memo is null)
        {
            return;
        }
        if (++memo.FailStreak >= ForgetAfter)
        {
            entry.Routes.Remove(memo);
        }
    }

    /// <summary>What to try first on this network: routes that last worked, the most recent first.</summary>
    public IReadOnlyList<RouteMemo> Ranked(string network, string phone) =>
        Entries.TryGetValue(Key(network, phone), out var entry)
            ? entry.Routes.OrderBy(m => m.FailStreak).ThenByDescending(m => m.LastOkUtc).ToList()
            : [];

    public void ForgetPhone(string phone)
    {
        foreach (var key in Entries.Keys.Where(k => k.EndsWith("|" + phone, StringComparison.Ordinal)).ToList())
        {
            Entries.Remove(key);
        }
    }

    private NetEntry Touch(string network, string phone, DateTime nowUtc)
    {
        var key = Key(network, phone);
        if (!Entries.TryGetValue(key, out var entry))
        {
            entry = new NetEntry { LastUsedUtc = nowUtc }; // stamped first, or it would evict itself
            Entries[key] = entry;
            if (Entries.Count > MaxEntries)
            {
                Entries.Remove(Entries.MinBy(e => e.Value.LastUsedUtc).Key);
            }
        }
        entry.LastUsedUtc = nowUtc;
        return entry;
    }

    private static bool Same(RouteMemo memo, string route, string address) =>
        memo.Route == route && string.Equals(memo.Address, address, StringComparison.OrdinalIgnoreCase);
}
