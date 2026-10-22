using System.Security.Cryptography;
using System.Text;

namespace Linc.Desktop.Services;

/// <summary>What the PC's pairing QR code carries (v32, linclink.md §2).</summary>
/// <param name="PcCertSha256">SHA-256 of the PC's TLS certificate (DER), lowercase hex: the phone
/// accepts only a PC that presents exactly this certificate.</param>
/// <param name="Addresses">The PC's LAN addresses to dial, best first.</param>
/// <param name="Port">The PC's TLS port (46001).</param>
/// <param name="Token">16 random bytes, one use, short-lived: proves the phone saw this screen.</param>
/// <param name="Name">The PC's name, for the phone to show.</param>
public sealed record PairingInvite(string PcCertSha256, IReadOnlyList<string> Addresses, int Port, byte[] Token, string Name);

/// <summary>
/// Pairing a phone without ADB, by QR code. The phone dials the PC and checks the PC's certificate
/// hash against the code (so it can't be pointed at an impostor); the PC, while its pairing window
/// is open, lets an unknown phone send exactly one <c>link.pair</c> whose proof is an HMAC, keyed by
/// the code's one-time token, over both certificates. A man in the middle would need the PC's
/// private key or the token, and the token only ever travelled from the screen to the camera.
/// Pure; the phone's <c>LinkPairing.kt</c> computes the same proof.
/// </summary>
public static class LinkPairing
{
    public const string Prefix = "linc://pair?";
    public const int TokenBytes = 16;
    public static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(5);

    private static readonly byte[] ProofLabel = Encoding.ASCII.GetBytes("linc-pair");

    public static byte[] NewToken() => RandomNumberGenerator.GetBytes(TokenBytes);

    public static string CertHash(byte[] certDer) => Convert.ToHexString(SHA256.HashData(certDer)).ToLowerInvariant();

    public static string ToUri(PairingInvite invite) =>
        Prefix +
        $"v=1&k={invite.PcCertSha256}" +
        $"&a={Uri.EscapeDataString(string.Join(",", invite.Addresses))}" +
        $"&p={invite.Port}" +
        $"&t={Base64Url(invite.Token)}" +
        $"&n={Uri.EscapeDataString(invite.Name)}";

    /// <summary>The invite in a scanned code, or null if it isn't a Linc pairing code this side understands.</summary>
    public static PairingInvite? Parse(string? text)
    {
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text[Prefix.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                fields[part[..eq]] = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }
        if (!fields.TryGetValue("v", out var v) || v != "1" ||
            !fields.TryGetValue("k", out var k) || k.Length != 64 || !k.All(Uri.IsHexDigit) ||
            !fields.TryGetValue("t", out var t) || FromBase64Url(t) is not { Length: TokenBytes } token ||
            !fields.TryGetValue("p", out var p) || !int.TryParse(p, out var port) || port is <= 0 or > 65535)
        {
            return null;
        }
        var addresses = fields.TryGetValue("a", out var a)
            ? a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return new PairingInvite(k.ToLowerInvariant(), addresses, port, token, fields.GetValueOrDefault("n") ?? "PC");
    }

    /// <summary><c>HMAC-SHA256(token, "linc-pair" ‖ SHA-256(phone cert) ‖ SHA-256(PC cert))</c>.</summary>
    public static byte[] Proof(byte[] token, byte[] phoneCertDer, byte[] pcCertDer)
    {
        var message = new byte[ProofLabel.Length + 64];
        ProofLabel.CopyTo(message, 0);
        SHA256.HashData(phoneCertDer).CopyTo(message, ProofLabel.Length);
        SHA256.HashData(pcCertDer).CopyTo(message, ProofLabel.Length + 32);
        return HMACSHA256.HashData(token, message);
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The PC's pairing window: one token, open for <see cref="LinkPairing.WindowLength"/>, burnt by
/// the first valid proof (or by closing). Pure; the clock is passed in.
/// </summary>
public sealed class PairingWindow
{
    private byte[]? _token;
    private DateTime _closesUtc;

    public bool IsOpen(DateTime nowUtc) => _token is not null && nowUtc < _closesUtc;

    public byte[] Open(DateTime nowUtc)
    {
        _token = LinkPairing.NewToken();
        _closesUtc = nowUtc + LinkPairing.WindowLength;
        return _token;
    }

    public void Close() => _token = null;

    /// <summary>True, and the window closes, when <paramref name="proof"/> is right for these two certificates.</summary>
    public bool TryConsume(byte[] proof, byte[] phoneCertDer, byte[] pcCertDer, DateTime nowUtc)
    {
        if (!IsOpen(nowUtc) || _token is not { } token)
        {
            return false;
        }
        if (!CryptographicOperations.FixedTimeEquals(LinkPairing.Proof(token, phoneCertDer, pcCertDer), proof))
        {
            return false;
        }
        _token = null;
        return true;
    }
}
