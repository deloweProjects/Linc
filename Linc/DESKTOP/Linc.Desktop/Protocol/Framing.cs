using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Linc.Desktop.Protocol;

/// <summary>
/// Frame codec: 4-byte unsigned big-endian length prefix + UTF-8 JSON (docs/PROTOCOL.md).
/// </summary>
public static class Framing
{
    public const int MaxFrameBytes = 1024 * 1024;

    public static async Task WriteAsync(Stream stream, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxFrameBytes)
        {
            throw new IOException($"outgoing frame of {bytes.Length} bytes exceeds {MaxFrameBytes}");
        }
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Reads one frame, or returns null on clean end-of-stream.</summary>
    public static async Task<string?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        var first = await stream.ReadAsync(prefix.AsMemory(0, 4), ct);
        if (first == 0)
        {
            return null;
        }
        if (first < 4)
        {
            await stream.ReadExactlyAsync(prefix.AsMemory(first, 4 - first), ct);
        }
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new IOException($"invalid frame length: {length}");
        }
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct);
        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary>
    /// Reads one raw binary frame (bulk channel, docs/PROTOCOL.md v5), or returns null on
    /// clean end-of-stream. Unlike <see cref="ReadAsync"/>, a zero-length frame is legal
    /// here and returns an empty array ("not found / unavailable").
    /// </summary>
    public static async Task<byte[]?> ReadBytesAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        var first = await stream.ReadAsync(prefix.AsMemory(0, 4), ct);
        if (first == 0)
        {
            return null;
        }
        if (first < 4)
        {
            await stream.ReadExactlyAsync(prefix.AsMemory(first, 4 - first), ct);
        }
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length < 0 || length > MaxFrameBytes)
        {
            throw new IOException($"invalid frame length: {length}");
        }
        if (length == 0)
        {
            return [];
        }
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }
}
