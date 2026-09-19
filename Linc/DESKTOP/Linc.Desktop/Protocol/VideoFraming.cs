using System;
using System.Buffers.Binary;
using System.IO;

namespace Linc.Desktop.Protocol;

/// <summary>
/// Packet framing for the pc-video channel (5) — protocol v14, M05 (docs/PROTOCOL.md).
///
/// <para>A 12-byte big-endian header per packet: an 8-byte presentation timestamp in
/// microseconds whose top two bits are flags, then a 4-byte payload length. Bit 63 marks a
/// codec-config packet (SPS/PPS only, no picture) and bit 62 marks a keyframe. That leaves
/// 62 bits of timestamp, which is not a practical limit.</para>
///
/// <para>This is deliberately the same shape scrcpy uses, which M16a already had to parse —
/// reusing it means the phone's decoder feeding logic is a known quantity rather than a new
/// invention (D-023's lesson was that this layer is easy to get subtly wrong).</para>
/// </summary>
public static class VideoFraming
{
    public const int HeaderBytes = 12;

    private const ulong ConfigFlag = 1UL << 63;
    private const ulong KeyframeFlag = 1UL << 62;
    private const ulong TimestampMask = ~(ConfigFlag | KeyframeFlag);

    /// <summary>A decoded packet header.</summary>
    public readonly record struct PacketHeader(long TimestampUs, bool Config, bool Keyframe, int Length);

    public static void WriteHeader(Span<byte> destination, long timestampUs, bool config, bool keyframe, int length)
    {
        if (destination.Length < HeaderBytes)
            throw new ArgumentException($"need {HeaderBytes} bytes for a packet header", nameof(destination));
        if (timestampUs < 0)
            throw new ArgumentOutOfRangeException(nameof(timestampUs));

        ulong word = (ulong)timestampUs & TimestampMask;
        if (config) word |= ConfigFlag;
        if (keyframe) word |= KeyframeFlag;

        BinaryPrimitives.WriteUInt64BigEndian(destination, word);
        BinaryPrimitives.WriteInt32BigEndian(destination[8..], length);
    }

    public static PacketHeader ReadHeader(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderBytes)
            throw new ArgumentException($"need {HeaderBytes} bytes for a packet header", nameof(source));

        ulong word = BinaryPrimitives.ReadUInt64BigEndian(source);
        return new PacketHeader(
            (long)(word & TimestampMask),
            (word & ConfigFlag) != 0,
            (word & KeyframeFlag) != 0,
            BinaryPrimitives.ReadInt32BigEndian(source[8..]));
    }

    // There is deliberately no WritePacketAsync here any more. Packets are written by
    // PcMirrorSender, which builds the header and payload into ONE buffer and one write: a
    // separate 12-byte write per frame is a wasted TCP segment on a wireless link, and the
    // sender must also decide what not to send, which a plain write helper cannot.
}
