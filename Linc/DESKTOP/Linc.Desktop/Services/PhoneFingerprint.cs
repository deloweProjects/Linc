namespace Linc.Desktop.Services;

/// <summary>What the PC has just seen of a phone, through whichever door it came in by.</summary>
/// <param name="Serial">ADB serial: over USB, wireless ADB, or read out of an mDNS name.</param>
/// <param name="CertBase64">The phone's TLS certificate (Direct TLS).</param>
/// <param name="WifiMac">Its Wi-Fi hardware address, from the PC's neighbour (ARP) table.</param>
/// <param name="Model">The model name, if anything reported it.</param>
public sealed record PhoneSignals(
    string? Serial = null,
    string? CertBase64 = null,
    string? WifiMac = null,
    string? Model = null);

/// <summary>
/// Which known phone is this? (D-077) One score over every identity signal the PC has for a
/// phone, so a phone is recognised by whatever it happens to show: its serial over USB or
/// wireless ADB, its certificate over Direct TLS, or only its Wi-Fi address in the PC's
/// neighbour table before either app has said a word.
///
/// <para><b>The scoring.</b> Signals differ in strength, and a strong one is decisive both ways:</para>
/// <list type="bullet">
/// <item>Certificate (+100 / −100) and serial (+80 / −80) are unique per phone. A match all but
/// proves identity, and a <i>mismatch</i> is proof it is a different phone, which no number of
/// weaker coincidences may outvote.</item>
/// <item>Wi-Fi MAC (+40). Android randomises it, but per network and stably, so a phone keeps one
/// MAC per Wi-Fi it joins; the PC learns each one when the phone connects over the LAN. Enough on
/// its own to say "your phone is here", not enough to overrule a serial.</item>
/// <item>Model (+5 / −20): a tie-breaker only, since two phones can share it, and a mismatch is
/// a mild sign of a different phone.</item>
/// </list>
/// <para><b>The decision.</b> The best phone must score at least <see cref="MatchThreshold"/>
/// and beat the runner-up by <see cref="Margin"/>. Otherwise the answer is "don't know": two
/// phones of the same model on a network where neither MAC is known look alike, and guessing
/// would connect to the wrong one.</para>
/// </summary>
public static class PhoneFingerprint
{
    public const int MatchThreshold = 40;
    public const int Margin = 20;

    public static int Score(KnownDevice device, PhoneSignals seen)
    {
        int score = 0;
        if (seen.CertBase64 is { Length: > 0 } cert && device.PhoneCertBase64 is { Length: > 0 } knownCert)
            score += string.Equals(cert, knownCert, StringComparison.Ordinal) ? 100 : -100;
        if (seen.Serial is { Length: > 0 } serial && device.Serial is { Length: > 0 })
            score += string.Equals(serial.Trim(), device.Serial.Trim(), StringComparison.Ordinal) ? 80 : -80;
        if (NormalizeMac(seen.WifiMac) is { } mac && device.WifiMacs is { Count: > 0 } macs &&
            macs.Any(m => NormalizeMac(m) == mac))
            score += 40;
        if (seen.Model is { Length: > 0 } model && device.Model is { Length: > 0 } knownModel)
            score += string.Equals(model.Trim(), knownModel.Trim(), StringComparison.OrdinalIgnoreCase) ? 5 : -20;
        return score;
    }

    /// <summary>The known phone these signals belong to, or null when it can't be said with confidence.</summary>
    public static KnownDevice? Best(IEnumerable<KnownDevice> known, PhoneSignals seen)
    {
        var ranked = known
            .Select(d => (Device: d, Score: Score(d, seen)))
            .OrderByDescending(r => r.Score)
            .Take(2)
            .ToList();
        if (ranked.Count == 0 || ranked[0].Score < MatchThreshold) return null;
        if (ranked.Count > 1 && ranked[0].Score - ranked[1].Score < Margin) return null;
        return ranked[0].Device;
    }

    /// <summary>
    /// "aa:bb:cc:dd:ee:ff", or null for anything that can't identify one device: malformed,
    /// all-zero, broadcast, or multicast (low bit of the first byte).
    /// </summary>
    public static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (hex.Length != 12) return null;
        if (hex == "000000000000" || hex == "ffffffffffff") return null;
        if ((Convert.ToByte(hex[..2], 16) & 0x01) != 0) return null; // multicast
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
