using System.Text.Json.Nodes;

namespace Linc.Desktop.Services;

/// <summary>
/// The generation counter (PROTOCOL.md v18) — "the whole defence against the worst bug in this
/// design". A monotonic counter the phone increments on every link event and stamps on all four
/// v18 messages.
/// <para>
/// Without it, a role flip in flight (phone-AP → PC-AP) lets a stale announcement from the
/// previous topology arrive AFTER the new one and point ADB at an address that no longer
/// exists. The failure is intermittent, looks like flaky hardware, and costs weeks.
/// </para>
/// </summary>
public static class HotspotGeneration
{
    /// <summary>
    /// Seed for "nothing has been seen yet". Deliberately below every legal <c>gen</c> so the
    /// very first announcement is accepted whatever number the phone starts at.
    /// </summary>
    public const int NothingSeen = -1;

    /// <summary>
    /// True when a message carrying <paramref name="incomingGen"/> may be acted on: only a
    /// STRICTLY LOWER gen is discarded, an equal one is accepted (PROTOCOL.md v18, M13d §1.1).
    /// <para>
    /// <c>gen</c> marks the LINK EPOCH, not the message, and several messages legitimately share
    /// one epoch. The case that proves it: <c>adb tcpip 5555</c> rebinds adbd with no link event,
    /// so the phone re-announces the new port under the SAME gen. Strictly-greater would discard
    /// that and the promotion optimisation would silently never fire. Same shape for a
    /// <c>tls → tcpip</c> mode change, and for a re-announce after a failed <c>adb.ack</c>.
    /// </para>
    /// <para>
    /// Within an epoch the rule is last-write-wins: the control socket is a single ordered
    /// stream, so a later message with the same gen is by definition the newer truth.
    /// </para>
    /// </summary>
    public static bool ShouldAccept(int incomingGen, int highestSeenGen) => incomingGen >= highestSeenGen;
}

/// <summary>One entry of the announcement's <c>addrs</c> array. Untrusted: filtered before use.</summary>
public sealed record HotspotAnnouncedAddress(string Ip, string? Iface, int Prefix);

/// <summary>A parsed, structurally valid <c>adb.announce</c> payload.</summary>
public sealed record HotspotAnnouncement(
    int Gen,
    string Serial,
    string Mode,
    int Port,
    bool Verified,
    IReadOnlyList<HotspotAnnouncedAddress> Addrs);

/// <summary>
/// Pure parsing and admission rules for the v18 <c>adb.announce</c> message. No sockets, no
/// ADB, no I/O of any kind — which is what lets <c>tools\hotspotsim</c> exercise the
/// <c>verified</c> refusal and the generation rule directly, and what lets the two negative
/// proofs break them for real.
/// </summary>
public static class HotspotAnnounce
{
    public const string ModeTls = "tls";
    public const string ModeTcpIp = "tcpip";

    /// <summary>Parses one payload. Null when it is not a usable announcement at all.</summary>
    public static HotspotAnnouncement? Parse(JsonObject? payload)
    {
        if (payload is null)
        {
            return null;
        }
        var gen = (int?)payload["gen"];
        var serial = (string?)payload["serial"];
        var mode = (string?)payload["mode"];
        var port = (int?)payload["port"];
        var verified = (bool?)payload["verified"] ?? false;
        if (gen is null || port is null)
        {
            return null;
        }
        // `serial` is deliberately NOT required. An unprivileged Android app usually cannot read
        // its own hardware serial at all (ro.serialno is SELinux-guarded and Build.SERIAL returns
        // "UNKNOWN" since API 26), so requiring it here would reject every real announcement.
        // Identity is still enforced — the desktop verifies `adb get-serialno` against the serial
        // it is already PAIRED with, and treats this field as a cross-check. See M13b's report.

        var addresses = new List<HotspotAnnouncedAddress>();
        if (payload["addrs"] is JsonArray array)
        {
            foreach (var entry in array)
            {
                if (entry is not JsonObject item)
                {
                    continue;
                }
                var ip = (string?)item["ip"];
                if (string.IsNullOrWhiteSpace(ip))
                {
                    continue;
                }
                addresses.Add(new HotspotAnnouncedAddress(ip.Trim(), (string?)item["iface"], (int?)item["prefix"] ?? 0));
            }
        }

        return new HotspotAnnouncement(
            gen.Value, (serial ?? "").Trim(), mode ?? ModeTcpIp, port.Value, verified, addresses);
    }

    /// <summary>
    /// Why this announcement must NOT be acted on, or null when it may be. This is the whole
    /// admission gate — <c>HotspotLinkService</c> calls it and does nothing else first.
    /// </summary>
    public static string? RejectReason(HotspotAnnouncement? announcement, int highestSeenGen)
    {
        if (announcement is null)
        {
            return "unparseable";
        }
        if (!HotspotGeneration.ShouldAccept(announcement.Gen, highestSeenGen))
        {
            return $"stale gen {announcement.Gen} (highest seen {highestSeenGen})";
        }
        // PROTOCOL.md v18: "never announce a port you have not just connected to ... the
        // desktop must treat verified: false as 'do not attempt'". The L3 link comes up BEFORE
        // adbd re-binds to the new interface, so link-up is not readiness, and acting on an
        // unverified port buys a full connect timeout in exchange for nothing.
        if (!announcement.Verified)
        {
            return "the phone has not confirmed adbd is accepting on that port (verified: false)";
        }
        if (announcement.Port is <= 0 or > 65535)
        {
            return $"port {announcement.Port} is not a port";
        }
        return null;
    }

    /// <summary>
    /// Which serial <c>adb get-serialno</c> must return, or null when identity cannot be
    /// established and the attempt must therefore be refused.
    /// <para>
    /// PROTOCOL.md v18: "the ADB serial is the only stable identity". The paired serial wins,
    /// because it is what this PC already trusts and it does not depend on the phone being able
    /// to read its own. An announced serial that disagrees with the paired one is a refusal, not
    /// a tiebreak — that is either the wrong phone or a spoofed announcement.
    /// </para>
    /// </summary>
    public static string? ExpectedSerial(string? pairedSerial, string? announcedSerial)
    {
        var paired = (pairedSerial ?? "").Trim();
        var announced = (announcedSerial ?? "").Trim();
        if (paired.Length == 0)
        {
            return announced.Length == 0 ? null : announced;
        }
        if (announced.Length > 0 && !string.Equals(paired, announced, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return paired;
    }

    /// <summary>The announced addresses as plain strings, for the same-subnet filter.</summary>
    public static IReadOnlyList<string> AddressStrings(HotspotAnnouncement announcement) =>
        announcement.Addrs.Select(a => a.Ip).ToList();

    /// <summary>
    /// The connect-side identity check (M13f §3): is the serial ADB reported the serial this PC
    /// is paired with? A <c>null</c>/blank expected serial accepts anything — that is report-only
    /// mode, where identity is carried back in the result for the caller to decide. Pure, so
    /// tools\hotspotsim can prove a WRONG serial fails: a check that cannot fail is not a check.
    /// </summary>
    public static bool VerifySerial(string? expectedSerial, string? reportedSerial)
    {
        var expected = (expectedSerial ?? "").Trim();
        if (expected.Length == 0)
        {
            return true;
        }
        return string.Equals((reportedSerial ?? "").Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How to treat an <c>adb get-state</c> answer (PROTOCOL.md v18: "reachability is not
    /// identity"). Pure, so the routing rule is provable without an ADB server.
    /// </summary>
    public static HotspotStateVerdict ClassifyState(string? state) => (state ?? "").Trim() switch
    {
        "device" => HotspotStateVerdict.Usable,
        // "not paired" on a TLS port. Retrying cannot fix it; only pairing can.
        "unauthorized" => HotspotStateVerdict.NeedsPairing,
        // Stale ADB server state: disconnect and try once more, then give up.
        "offline" => HotspotStateVerdict.RetryOnce,
        _ => HotspotStateVerdict.Unusable,
    };
}

public enum HotspotStateVerdict
{
    Usable,
    NeedsPairing,
    RetryOnce,
    Unusable,
}
