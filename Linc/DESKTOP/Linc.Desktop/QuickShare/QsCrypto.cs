using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Securegcm;
using Securemessage;

namespace Linc.Desktop.QuickShare;

/// <summary>
/// Quick Share (Nearby Share) framing: every protobuf on the TCP stream is prefixed with its
/// length as a 4-byte big-endian integer.
/// </summary>
public static class QsFraming
{
    /// <summary>
    /// Refuse anything bigger. File chunks are 512 KB and the largest control message is a few
    /// KB, so 5 MB only ever trips on a corrupt or hostile stream — and stops it allocating
    /// whatever length it claims.
    /// </summary>
    public const int MaxFrame = 5 * 1024 * 1024;

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length < 0 || length > MaxFrame)
        {
            throw new InvalidDataException($"Quick Share frame of {length} bytes is out of range.");
        }
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return body;
    }

    public static async Task WriteAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }
}

/// <summary>
/// The pieces of Quick Share that are plain arithmetic: the mDNS names, the endpoint-info
/// record, and the 4-digit PIN. Pure, so tools can prove them without a socket.
/// </summary>
public static class QsWire
{
    /// <summary>SHA256("NearbySharing")[0..6] as the mDNS service type Android browses for.</summary>
    public const string ServiceType = "_FC9F5ED42C8A._tcp";

    /// <summary>The next-protocol string both UKEY2 ends must agree on.</summary>
    public const string NextProtocol = "AES_256_CBC-HMAC_SHA256";

    /// <summary>Android's device-type field. Only these three matter for a PC.</summary>
    public enum DeviceType { Unknown = 0, Phone = 1, Tablet = 2, Laptop = 3 }

    public static string Base64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    /// <summary>A fresh 4-character endpoint id, the way Android makes them.</summary>
    public static string NewEndpointId()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return string.Create(4, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
        });
    }

    /// <summary>The mDNS instance name: 0x23, endpoint id, service id FC 9F 5E, two zeros.</summary>
    public static string ServiceInstanceName(string endpointId)
    {
        var bytes = new byte[10];
        bytes[0] = 0x23;
        Encoding.ASCII.GetBytes(endpointId, 0, 4, bytes, 1);
        bytes[5] = 0xFC;
        bytes[6] = 0x9F;
        bytes[7] = 0x5E;
        return Base64Url(bytes);
    }

    /// <summary>
    /// The endpoint-info record (TXT <c>n</c>, and the ConnectionRequest's endpoint_info): one
    /// bit-field byte (device type in bits 1-3, visibility bit 4 clear = visible), 16 opaque
    /// bytes that only mean something to Google's servers, then the length-prefixed name.
    /// </summary>
    public static byte[] EndpointInfo(string deviceName, DeviceType type)
    {
        var name = Encoding.UTF8.GetBytes(deviceName);
        if (name.Length > 255)
        {
            name = name[..255];
        }
        var info = new byte[1 + 16 + 1 + name.Length];
        info[0] = (byte)((int)type << 1);
        RandomNumberGenerator.Fill(info.AsSpan(1, 16));
        info[17] = (byte)name.Length;
        name.CopyTo(info, 18);
        return info;
    }

    /// <summary>Reads the sender's name and device type back out of an endpoint-info record.</summary>
    public static (string? Name, DeviceType Type) ParseEndpointInfo(ReadOnlySpan<byte> info)
    {
        if (info.Length < 17)
        {
            return (null, DeviceType.Unknown);
        }
        var type = (DeviceType)((info[0] >> 1) & 0x7);
        var hidden = ((info[0] >> 4) & 0x1) == 1;
        if (hidden || info.Length < 18)
        {
            return (null, type);
        }
        var length = info[17];
        if (info.Length < 18 + length)
        {
            return (null, type);
        }
        return (Encoding.UTF8.GetString(info.Slice(18, length)), type);
    }

    /// <summary>
    /// The 4-digit PIN both screens show, from the UKEY2 authentication string. This is
    /// Chromium's algorithm (nearby_sharing_service_impl.cc), bytes read as SIGNED.
    /// </summary>
    public static string Pin(ReadOnlySpan<byte> authString)
    {
        const int modulo = 9973;
        const int multiplierStep = 31;
        var hash = 0;
        var multiplier = 1;
        foreach (var b in authString)
        {
            hash = (hash + (sbyte)b * multiplier) % modulo;
            multiplier = multiplier * multiplierStep % modulo;
        }
        return Math.Abs(hash).ToString("D4");
    }

    /// <summary>
    /// The BLE service data that makes Android phones nearby start advertising their Quick Share
    /// mDNS service (what Google's Windows app does before a send): UUID 0xFE2C, a fixed 14-byte
    /// prefix, then 10 random bytes.
    /// </summary>
    public static byte[] WakeServiceData()
    {
        byte[] prefix = [0xFC, 0x12, 0x8E, 0x01, 0x42, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var data = new byte[prefix.Length + 10];
        prefix.CopyTo(data, 0);
        RandomNumberGenerator.Fill(data.AsSpan(prefix.Length));
        return data;
    }
}

/// <summary>
/// UKEY2 (github.com/google/ukey2) with P-256/SHA-512, both roles, and the SecureMessage channel
/// it produces. One instance per connection.
/// </summary>
public sealed class QsUkey2 : IDisposable
{
    private static readonly byte[] AuthSalt = Encoding.ASCII.GetBytes("UKEY2 v1 auth");
    private static readonly byte[] NextSalt = Encoding.ASCII.GetBytes("UKEY2 v1 next");

    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private byte[]? _clientInitRaw;
    private byte[]? _serverInitRaw;
    private byte[]? _commitment;
    private byte[]? _clientFinishRaw;

    public void Dispose() => _key.Dispose();

    // ---- server (receiver) role -------------------------------------------------------------

    /// <summary>Validates a ClientInit and returns the ServerInit to send back.</summary>
    public byte[] HandleClientInit(byte[] raw)
    {
        var message = Ukey2Message.Parser.ParseFrom(raw);
        if (message.MessageType != Ukey2Message.Types.Type.ClientInit)
        {
            throw new InvalidDataException("Quick Share: expected a UKEY2 ClientInit.");
        }
        var init = Ukey2ClientInit.Parser.ParseFrom(message.MessageData);
        if (init.Version != 1 || init.NextProtocol != QsWire.NextProtocol)
        {
            throw new InvalidDataException($"Quick Share: unsupported UKEY2 version {init.Version} / '{init.NextProtocol}'.");
        }
        var commitment = init.CipherCommitments.FirstOrDefault(c => c.HandshakeCipher == Ukey2HandshakeCipher.P256Sha512)
            ?? throw new InvalidDataException("Quick Share: the sender offered no P-256 key exchange.");
        _commitment = commitment.Commitment.ToByteArray();
        _clientInitRaw = raw;

        var serverInit = new Ukey2ClientInitReply(RandomNumberGenerator.GetBytes(32), EncodePublicKey());
        _serverInitRaw = new Ukey2Message
        {
            MessageType = Ukey2Message.Types.Type.ServerInit,
            MessageData = new Ukey2ServerInit
            {
                Version = 1,
                Random = ByteString.CopyFrom(serverInit.Random),
                HandshakeCipher = Ukey2HandshakeCipher.P256Sha512,
                PublicKey = ByteString.CopyFrom(serverInit.PublicKey),
            }.ToByteString(),
        }.ToByteArray();
        return _serverInitRaw;
    }

    /// <summary>Checks the ClientFinish against the ClientInit's commitment and derives the keys.</summary>
    public QsSecureChannel HandleClientFinish(byte[] raw, out string pin)
    {
        if (_commitment is null || _clientInitRaw is null || _serverInitRaw is null)
        {
            throw new InvalidOperationException("ClientFinish before ClientInit.");
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA512.HashData(raw), _commitment))
        {
            throw new CryptographicException("Quick Share: the sender's key does not match its commitment.");
        }
        var message = Ukey2Message.Parser.ParseFrom(raw);
        if (message.MessageType != Ukey2Message.Types.Type.ClientFinish)
        {
            throw new InvalidDataException("Quick Share: expected a UKEY2 ClientFinish.");
        }
        var finish = Ukey2ClientFinished.Parser.ParseFrom(message.MessageData);
        return Derive(finish.PublicKey.ToByteArray(), isServer: true, out pin);
    }

    // ---- client (sender) role ---------------------------------------------------------------

    /// <summary>Builds ClientInit (committing to the ClientFinish that will follow).</summary>
    public byte[] BuildClientInit()
    {
        _clientFinishRaw = new Ukey2Message
        {
            MessageType = Ukey2Message.Types.Type.ClientFinish,
            MessageData = new Ukey2ClientFinished { PublicKey = ByteString.CopyFrom(EncodePublicKey()) }.ToByteString(),
        }.ToByteArray();
        var init = new Ukey2ClientInit
        {
            Version = 1,
            Random = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            NextProtocol = QsWire.NextProtocol,
        };
        init.CipherCommitments.Add(new Ukey2ClientInit.Types.CipherCommitment
        {
            HandshakeCipher = Ukey2HandshakeCipher.P256Sha512,
            Commitment = ByteString.CopyFrom(SHA512.HashData(_clientFinishRaw)),
        });
        _clientInitRaw = new Ukey2Message
        {
            MessageType = Ukey2Message.Types.Type.ClientInit,
            MessageData = init.ToByteString(),
        }.ToByteArray();
        return _clientInitRaw;
    }

    /// <summary>Takes the ServerInit, returns the ClientFinish to send and the channel.</summary>
    public (byte[] ClientFinish, QsSecureChannel Channel) HandleServerInit(byte[] raw, out string pin)
    {
        if (_clientInitRaw is null || _clientFinishRaw is null)
        {
            throw new InvalidOperationException("ServerInit before ClientInit.");
        }
        var message = Ukey2Message.Parser.ParseFrom(raw);
        if (message.MessageType != Ukey2Message.Types.Type.ServerInit)
        {
            throw new InvalidDataException(message.MessageType == Ukey2Message.Types.Type.Alert
                ? "Quick Share: the receiver refused the key exchange."
                : "Quick Share: expected a UKEY2 ServerInit.");
        }
        var init = Ukey2ServerInit.Parser.ParseFrom(message.MessageData);
        if (init.Version != 1 || init.HandshakeCipher != Ukey2HandshakeCipher.P256Sha512)
        {
            throw new InvalidDataException("Quick Share: the receiver chose an unsupported key exchange.");
        }
        _serverInitRaw = raw;
        var channel = Derive(init.PublicKey.ToByteArray(), isServer: false, out pin);
        return (_clientFinishRaw, channel);
    }

    // ---- shared -----------------------------------------------------------------------------

    private QsSecureChannel Derive(byte[] peerGenericKey, bool isServer, out string pin)
    {
        var generic = GenericPublicKey.Parser.ParseFrom(peerGenericKey);
        if (generic.Type != PublicKeyType.EcP256 || generic.EcP256PublicKey is null)
        {
            throw new InvalidDataException("Quick Share: the peer's key is not P-256.");
        }
        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Fixed32(generic.EcP256PublicKey.X.Span),
                Y = Fixed32(generic.EcP256PublicKey.Y.Span),
            },
        });
        // UKEY2's DHS is SHA-256 of the raw shared X coordinate, which is exactly this call.
        var dhs = _key.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);
        var transcript = _clientInitRaw!.Concat(_serverInitRaw!).ToArray();
        var auth = HKDF.DeriveKey(HashAlgorithmName.SHA256, dhs, 32, AuthSalt, transcript);
        var next = HKDF.DeriveKey(HashAlgorithmName.SHA256, dhs, 32, NextSalt, transcript);
        pin = QsWire.Pin(auth);
        return new QsSecureChannel(next, isServer, auth);
    }

    /// <summary>
    /// GenericPublicKey carries X and Y as Java BigInteger bytes (two's complement), so a
    /// coordinate with the top bit set gets a leading zero — and one without it must not have
    /// its top byte read as a sign.
    /// </summary>
    private byte[] EncodePublicKey()
    {
        var q = _key.ExportParameters(false).Q;
        return new GenericPublicKey
        {
            Type = PublicKeyType.EcP256,
            EcP256PublicKey = new EcP256PublicKey
            {
                X = ByteString.CopyFrom(Signed(q.X!)),
                Y = ByteString.CopyFrom(Signed(q.Y!)),
            },
        }.ToByteArray();

        static byte[] Signed(byte[] value) => value[0] >= 0x80 ? [0, .. value] : value;
    }

    private static byte[] Fixed32(ReadOnlySpan<byte> value)
    {
        while (value.Length > 32 && value[0] == 0)
        {
            value = value[1..];
        }
        if (value.Length > 32)
        {
            throw new InvalidDataException("Quick Share: EC coordinate too long.");
        }
        var result = new byte[32];
        value.CopyTo(result.AsSpan(32 - value.Length));
        return result;
    }

    private readonly record struct Ukey2ClientInitReply(byte[] Random, byte[] PublicKey);
}

/// <summary>
/// The encrypted channel after UKEY2: AES-256-CBC + HMAC-SHA256 SecureMessages wrapping
/// sequence-numbered DeviceToDeviceMessages. Each direction has its own key pair and counter.
/// </summary>
public sealed class QsSecureChannel
{
    private static readonly byte[] D2DSalt = SHA256.HashData(Encoding.ASCII.GetBytes("D2D"));
    private static readonly byte[] SecureMessageSalt = SHA256.HashData(Encoding.ASCII.GetBytes("SecureMessage"));

    private readonly byte[] _encryptKey;
    private readonly byte[] _sendHmacKey;
    private readonly byte[] _decryptKey;
    private readonly byte[] _receiveHmacKey;
    private int _sendSequence;
    private int _receiveSequence;

    /// <summary>The UKEY2 authentication string (the PIN's source; also QR-handshake signing input).</summary>
    public byte[] AuthString { get; }

    public QsSecureChannel(byte[] nextProtocolSecret, bool isServer, byte[] authString)
    {
        AuthString = authString;
        var clientKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, nextProtocolSecret, 32, D2DSalt, "client"u8.ToArray());
        var serverKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, nextProtocolSecret, 32, D2DSalt, "server"u8.ToArray());
        var (mine, theirs) = isServer ? (serverKey, clientKey) : (clientKey, serverKey);
        _encryptKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, mine, 32, SecureMessageSalt, "ENC:2"u8.ToArray());
        _sendHmacKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, mine, 32, SecureMessageSalt, "SIG:1"u8.ToArray());
        _decryptKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, theirs, 32, SecureMessageSalt, "ENC:2"u8.ToArray());
        _receiveHmacKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, theirs, 32, SecureMessageSalt, "SIG:1"u8.ToArray());
    }

    public byte[] Seal(byte[] offlineFrame)
    {
        var d2d = new DeviceToDeviceMessage
        {
            SequenceNumber = Interlocked.Increment(ref _sendSequence),
            Message = ByteString.CopyFrom(offlineFrame),
        }.ToByteArray();
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = _encryptKey;
        var body = aes.EncryptCbc(d2d, iv, PaddingMode.PKCS7);
        var headerAndBody = new HeaderAndBody
        {
            Header = new Header
            {
                SignatureScheme = SigScheme.HmacSha256,
                EncryptionScheme = EncScheme.Aes256Cbc,
                Iv = ByteString.CopyFrom(iv),
                PublicMetadata = new GcmMetadata { Type = Securegcm.Type.DeviceToDeviceMessage, Version = 1 }.ToByteString(),
            },
            Body = ByteString.CopyFrom(body),
        }.ToByteArray();
        return new SecureMessage
        {
            HeaderAndBody = ByteString.CopyFrom(headerAndBody),
            Signature = ByteString.CopyFrom(HMACSHA256.HashData(_sendHmacKey, headerAndBody)),
        }.ToByteArray();
    }

    public byte[] Open(byte[] raw)
    {
        var message = SecureMessage.Parser.ParseFrom(raw);
        var headerAndBody = message.HeaderAndBody.ToByteArray();
        var expected = HMACSHA256.HashData(_receiveHmacKey, headerAndBody);
        if (!CryptographicOperations.FixedTimeEquals(expected, message.Signature.Span))
        {
            throw new CryptographicException("Quick Share: a message failed its signature check.");
        }
        var parsed = HeaderAndBody.Parser.ParseFrom(headerAndBody);
        using var aes = Aes.Create();
        aes.Key = _decryptKey;
        var plain = aes.DecryptCbc(parsed.Body.Span, parsed.Header.Iv.Span, PaddingMode.PKCS7);
        var d2d = DeviceToDeviceMessage.Parser.ParseFrom(plain);
        var expectedSequence = ++_receiveSequence;
        if (d2d.SequenceNumber != expectedSequence)
        {
            throw new CryptographicException(
                $"Quick Share: message {d2d.SequenceNumber} arrived where {expectedSequence} was due (replay or loss).");
        }
        return d2d.Message.ToByteArray();
    }
}

/// <summary>
/// When a Quick Share sender may be treated as the user's own phone. Quick Share in everyone
/// mode proves nothing about who the sender is - the name is whatever the sender typed - so the
/// name alone is never enough. The extra fact Linc has is a live, certificate-authenticated link
/// to the phone and that link's network address: a sender at the same address, with the same
/// name, is that phone.
/// </summary>
public static class QsTrust
{
    public static bool IsOwnConnectedPhone(string? senderName, string? senderAddress, string? linkedModel, string? linkedAddress)
    {
        if (string.IsNullOrWhiteSpace(senderName) || string.IsNullOrWhiteSpace(linkedModel) ||
            string.IsNullOrWhiteSpace(senderAddress) || string.IsNullOrWhiteSpace(linkedAddress))
        {
            return false; // no live Wi-Fi link (USB has no address) or an anonymous sender
        }
        if (!System.Net.IPAddress.TryParse(senderAddress.Trim(), out var sender) ||
            !System.Net.IPAddress.TryParse(linkedAddress.Trim().Trim('[', ']'), out var linked))
        {
            return false;
        }
        // IPv4-mapped IPv6 from a dual-stack socket must still compare equal to the plain v4 form.
        if (sender.IsIPv4MappedToIPv6) sender = sender.MapToIPv4();
        if (linked.IsIPv4MappedToIPv6) linked = linked.MapToIPv4();
        return sender.Equals(linked) &&
            string.Equals(senderName.Trim(), linkedModel.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
