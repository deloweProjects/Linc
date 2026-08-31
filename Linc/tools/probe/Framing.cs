using System.Buffers.Binary;
using System.Text;

namespace Linc.Probe;

/// <summary>
/// The wire format from PROTOCOL.md: [4-byte unsigned big-endian length][UTF-8 payload].
/// Shared by the control connection and every typed channel.
/// </summary>
public static class Framing
{
    public static async Task WriteAsync(Stream stream, string json, CancellationToken ct = default)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)body.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task WriteBytesAsync(Stream stream, byte[] body, CancellationToken ct = default)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)body.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<string> ReadAsync(Stream stream, CancellationToken ct = default) =>
        Encoding.UTF8.GetString(await ReadBytesAsync(stream, ct));

    public static async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken ct = default)
    {
        var prefix = await ReadExactlyAsync(stream, 4, ct);
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(prefix);
        return length == 0 ? [] : await ReadExactlyAsync(stream, length, ct);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (got == 0)
            {
                throw new EndOfStreamException($"Peer closed after {read} of {count} bytes.");
            }
            read += got;
        }
        return buffer;
    }
}
