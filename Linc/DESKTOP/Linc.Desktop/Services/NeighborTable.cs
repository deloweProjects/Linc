using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Linc.Desktop.Services;

/// <summary>
/// The PC's IPv4 neighbour (ARP) table: which hardware address answered for which LAN address
/// lately (D-077). Reading it sends nothing on the network, so it is a free way to notice that a
/// known phone has just joined the Wi-Fi.
/// </summary>
public static class NeighborTable
{
    /// <summary>Every reachable neighbour as (IPv4 address, "aa:bb:…" MAC). Empty on failure.</summary>
    public static IReadOnlyList<(string Address, string Mac)> Read()
    {
        var result = new List<(string, string)>();
        try
        {
            int size = 0;
            GetIpNetTable(IntPtr.Zero, ref size, false);
            if (size <= 0) return result;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpNetTable(buffer, ref size, false) != 0) return result;
                int count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MIB_IPNETROW>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_IPNETROW>(buffer + 4 + i * rowSize);
                    // 2 = invalid, 4 = static (routers, broadcast); 3 = dynamic is a real neighbour.
                    if (row.dwType != 3 || row.dwPhysAddrLen != 6) continue;
                    var mac = PhoneFingerprint.NormalizeMac(Convert.ToHexString(row.bPhysAddr, 0, 6));
                    if (mac is null) continue;
                    result.Add((new IPAddress(BitConverter.GetBytes(row.dwAddr)).ToString(), mac));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { /* no table, no answer: a hint, never a requirement */ }
        return result;
    }

    /// <summary>The MAC behind one LAN address (asks with ARP if the table doesn't have it), or null.</summary>
    public static string? MacOf(string? address)
    {
        if (address is null || !IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return null;
        if (IPAddress.IsLoopback(ip)) return null;
        try
        {
            var mac = new byte[6];
            int length = mac.Length;
            if (SendARP(BitConverter.ToInt32(ip.GetAddressBytes(), 0), 0, mac, ref length) != 0 || length != 6)
                return null;
            return PhoneFingerprint.NormalizeMac(Convert.ToHexString(mac));
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNETROW
    {
        public int dwIndex;
        public int dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] bPhysAddr;
        public uint dwAddr;
        public int dwType;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr table, ref int size, bool sort);

    [DllImport("iphlpapi.dll")]
    private static extern int SendARP(int destIp, int srcIp, byte[] mac, ref int length);
}
