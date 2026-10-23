using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Linc.Desktop.Services;

/// <summary>A parsed LincLink UDP packet (see <see cref="LinkBeacon"/>).</summary>
public sealed record BeaconPacket(byte Role, byte[] Tag, byte[] Nonce, int Port, byte Flags);

/// <summary>
/// The LAN "I'm here" signal (v32, linclink.md §6). The PC broadcasts a probe for each phone it
/// knows a link secret for; a phone that recognises one answers unicast to the probe's source
/// address and port (which Windows Firewall lets in for 3 s after a broadcast, so this works with
/// no firewall rule); the phone also broadcasts an announcement on its own strong events (boot,
/// Wi-Fi joined), which reaches a PC whose firewall lets UDP 46003 in.
///
/// Wire format, 23 bytes:
/// <c>"LK" ‖ version (1) ‖ role (1) ‖ tag (8) ‖ nonce (8) ‖ port (2, big-endian) ‖ flags (1)</c>,
/// with <c>tag = HMAC-SHA256(linkSecret, "linc-beacon" ‖ role ‖ slot (8, big-endian) ‖ nonce)[..8]</c>
/// and <c>slot = unixSeconds / 30</c>, matched at ±1 slot for clock skew. A reply reuses the
/// probe's nonce, so an old reply can't be replayed at a later probe. A stranger sees two magic
/// bytes and noise that changes every packet; only a holder of the pair's secret can make or read
/// a tag. Pure: the caller does the sockets. The phone's <c>LinkBeacon.kt</c> is byte-identical
/// (the shared test vector is in tools/linclinksim and the phone's unit tests).
/// </summary>
public static class LinkBeacon
{
    public const int Port = 46003;
    public const int SlotSeconds = 30;
    public const byte Version = 1;
    public const int Length = 23;

    /// <summary>PC → broadcast: "is one of my phones here?"</summary>
    public const byte RoleProbe = (byte)'P';

    /// <summary>Phone → the probing PC, unicast: "yes, dial me on this port".</summary>
    public const byte RoleReply = (byte)'R';

    /// <summary>Phone → broadcast, on its own: "I just arrived".</summary>
    public const byte RoleAnnounce = (byte)'A';

    private static readonly byte[] Label = Encoding.ASCII.GetBytes("linc-beacon");

    public static long Slot(long unixSeconds) => unixSeconds / SlotSeconds;

    public static byte[] Tag(byte[] secret, byte role, long slot, ReadOnlySpan<byte> nonce)
    {
        var message = new byte[Label.Length + 1 + 8 + nonce.Length];
        Label.CopyTo(message, 0);
        message[Label.Length] = role;
        BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(Label.Length + 1, 8), slot);
        nonce.CopyTo(message.AsSpan(Label.Length + 9));
        return HMACSHA256.HashData(secret, message)[..8];
    }

    public static byte[] Build(byte role, byte[] secret, long unixSeconds, byte[] nonce, int port, byte flags = 0)
    {
        if (nonce.Length != 8)
        {
            throw new ArgumentException("A beacon nonce is 8 bytes.", nameof(nonce));
        }
        var packet = new byte[Length];
        packet[0] = (byte)'L';
        packet[1] = (byte)'K';
        packet[2] = Version;
        packet[3] = role;
        Tag(secret, role, Slot(unixSeconds), nonce).CopyTo(packet, 4);
        nonce.CopyTo(packet, 12);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), (ushort)port);
        packet[22] = flags;
        return packet;
    }

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(8);

    /// <summary>A well-formed packet of a version this side speaks, or null (anything else on the port is ignored).</summary>
    public static BeaconPacket? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length || data[0] != (byte)'L' || data[1] != (byte)'K' || data[2] != Version)
        {
            return null;
        }
        var role = data[3];
        if (role is not (RoleProbe or RoleReply or RoleAnnounce))
        {
            return null;
        }
        return new BeaconPacket(role, data.Slice(4, 8).ToArray(), data.Slice(12, 8).ToArray(),
            BinaryPrimitives.ReadUInt16BigEndian(data.Slice(20, 2)), data[22]);
    }

    /// <summary>True when <paramref name="packet"/> was made with <paramref name="secret"/> within a slot of now.</summary>
    public static bool Matches(BeaconPacket packet, byte[] secret, long unixSeconds)
    {
        var slot = Slot(unixSeconds);
        for (var s = slot - 1; s <= slot + 1; s++)
        {
            if (CryptographicOperations.FixedTimeEquals(Tag(secret, packet.Role, s, packet.Nonce), packet.Tag))
            {
                return true;
            }
        }
        return false;
    }
}
