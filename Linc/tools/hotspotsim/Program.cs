using System.Text.Json.Nodes;
using Linc.Desktop.Services;

// M13a: the pure-logic half of the hotspot link — the role rule, the address filter, and the
// disconnect-before-connect ordering. No network, no phone, no ADB server: every check below
// either calls the production static directly or reads the production source file.
//
//   dotnet run --project tools/hotspotsim        (CWD must be ...\yellow\Linc)
//
// Nothing here proves a hotspot link works. It proves the decisions Linc makes about one are
// the decisions that were specified.

var failures = new List<string>();

void True(bool condition, string claim)
{
    Console.WriteLine($"    {(condition ? "ok  " : "FAIL")} {claim}");
    if (!condition)
    {
        failures.Add(claim);
    }
}

// A /24 hotspot segment: the phone hosts 192.168.43.1, this PC joined as .100.
HotspotInterface Hotspot(string address = "192.168.43.100", string? gateway = "192.168.43.1") =>
    new("Wi-Fi", address, 24, gateway, "Test adapter");

Console.WriteLine("--- DecideRole (§3.1)");
{
    True(HotspotRoleRule.DecideRole(null, "192.168.137.1") == HotspotRole.Ap,
        "no gateway at all -> AP (we are the top of this interface, so we only listen)");
    True(HotspotRoleRule.DecideRole("", "192.168.137.1") == HotspotRole.Ap,
        "empty gateway -> AP");
    True(HotspotRoleRule.DecideRole("   ", "192.168.137.1") == HotspotRole.Ap,
        "whitespace-only gateway -> AP");
    True(HotspotRoleRule.DecideRole("0.0.0.0", "192.168.137.1") == HotspotRole.Ap,
        "0.0.0.0 gateway -> AP (Windows spells 'no gateway' this way on some adapters)");
    True(HotspotRoleRule.DecideRole("::", "fe80::1") == HotspotRole.Ap,
        ":: gateway -> AP");
    True(HotspotRoleRule.DecideRole("192.168.137.1", "192.168.137.1") == HotspotRole.Ap,
        "gateway == my own address -> AP (Windows Mobile Hotspot hosting the link)");
    True(HotspotRoleRule.DecideRole("192.168.43.1", "192.168.43.100") == HotspotRole.Client,
        "a different gateway -> CLIENT (the phone hosts; we dial it)");
    True(HotspotRoleRule.DecideRole("192.168.043.001", "192.168.43.100") == HotspotRole.Client,
        "a padded-but-valid gateway literal still parses to CLIENT");

    True(!HotspotRoleRule.ShouldDial(HotspotRole.Ap), "AP never dials");
    True(HotspotRoleRule.ShouldDial(HotspotRole.Client), "CLIENT dials");
    // Belt and braces (§3.1): the listener is unconditional, so 'should I listen?' is not a
    // question the rule is allowed to answer at all. If a future change adds a
    // ShouldListen(role) that can return false, this check is where it gets caught.
    True(typeof(HotspotRoleRule).GetMethod("ShouldListen") is null,
        "the rule exposes no ShouldListen — both roles listen unconditionally, only dialling is conditional");
}

Console.WriteLine("--- IsSameSubnet (the filter §3.2 rests on)");
{
    True(HotspotAddress.IsSameSubnet("192.168.43.1", "192.168.43.100", 24), "/24: .1 and .100 are the same subnet");
    True(!HotspotAddress.IsSameSubnet("192.168.44.1", "192.168.43.100", 24), "/24: a neighbouring third octet is NOT");
    True(HotspotAddress.IsSameSubnet("192.168.44.1", "192.168.43.100", 16), "/16: the same pair IS, at a shorter prefix");
    True(!HotspotAddress.IsSameSubnet("192.168.43.130", "192.168.43.100", 25), "/25: a non-byte-aligned prefix splits .100 from .130");
    True(HotspotAddress.IsSameSubnet("192.168.43.126", "192.168.43.100", 25), "/25: and keeps .100 with .126");
    True(!HotspotAddress.IsSameSubnet("192.168.43.1", "192.168.43.100", 0), "/0 is rejected outright — it would make every address 'local'");
    True(!HotspotAddress.IsSameSubnet("fe80::1", "192.168.43.100", 24), "an IPv6 address is never in an IPv4 subnet");
    True(!HotspotAddress.IsSameSubnet("not-an-ip", "192.168.43.100", 24), "unparseable text is not in any subnet");
    // Measured on the dev PC: every adapter's link-local address is inside fe80::/64, so on
    // prefix bits alone Wi-Fi's gateway "matched" the VPN adapter and the socket failed with
    // AddressNotAvailable. The scope id is what separates them.
    True(!HotspotAddress.IsSameSubnet("fe80::9a03:8eff:fe84:f698%12", "fe80::f083:cdb1:7b48:e3f9%19", 64),
        "two link-local IPv6 addresses on DIFFERENT adapters are not the same subnet (scope ids differ)");
    True(HotspotAddress.IsSameSubnet("fe80::9a03:8eff:fe84:f698%12", "fe80::a5f5:4863:1e49:ea60%12", 64),
        "two link-local IPv6 addresses on the SAME adapter are");
}

Console.WriteLine("--- SelectEndpoint: accepts the peer when it is on our segment (§3.2 order 1)");
{
    var chosen = HotspotAddress.SelectEndpoint("192.168.43.1", null, [Hotspot()]);
    True(chosen == "192.168.43.1", "the control socket's peer address is taken when it is inside our own subnet");
}

Console.WriteLine("--- SelectEndpoint: rejects a foreign peer/announced address (§3.2, the load-bearing filter)");
{
    // This is the case that costs a full connect timeout per unfiltered entry: Android
    // advertises its mobile-data address, which is not reachable from the hotspot segment.
    var chosen = HotspotAddress.SelectEndpoint("10.116.24.9", null, [Hotspot(gateway: null)]);
    True(chosen is null, "a peer address outside every one of our subnets is rejected, not dialled");

    var withGateway = HotspotAddress.SelectEndpoint("10.116.24.9", null, [Hotspot()]);
    True(withGateway == "192.168.43.1", "a foreign peer falls through to the gateway rather than being dialled");

    var announced = HotspotAddress.SelectEndpoint(null, ["10.116.24.9", "198.51.100.3"], [Hotspot(gateway: null)]);
    True(announced is null, "every announced address outside our subnets is rejected too");

    var mixed = HotspotAddress.SelectEndpoint(null, ["10.116.24.9", "192.168.43.7"], [Hotspot(gateway: null)]);
    True(mixed == "192.168.43.7", "the one announced address on our segment is picked out of a list of unreachable ones");
}

Console.WriteLine("--- SelectEndpoint: gateway fallback covers the phone-is-AP role (§3.2 order 3)");
{
    var chosen = HotspotAddress.SelectEndpoint(null, null, [Hotspot()]);
    True(chosen == "192.168.43.1", "with no peer and nothing announced, the interface's default gateway is used");

    var weAreAp = HotspotAddress.SelectEndpoint(null, null, [new HotspotInterface("Local Area Connection* 2", "192.168.137.1", 24, null)]);
    True(weAreAp is null, "when WE host the link there is no gateway and nothing to dial — the listener covers that side");

    var selfGateway = HotspotAddress.SelectEndpoint(null, null, [new HotspotInterface("Local Area Connection* 2", "192.168.137.1", 24, "192.168.137.1")]);
    True(selfGateway is null, "a gateway equal to our own address is not a peer to dial either");
}

Console.WriteLine("--- SelectEndpoint: a VPN/virtual gateway never outranks a real one (§3.3, wrong-NIC)");
{
    // Measured, not imagined: on the dev PC a "Famatech Radmin VPN Ethernet Adapter" holding
    // a /8 with its own gateway enumerates BEFORE Wi-Fi, and was chosen outright. The real
    // addresses are stood in for by RFC 5737 documentation ones (198.51.100.0/24) here.
    var vpn = new HotspotInterface("Radmin VPN", "198.51.100.175", 8, "198.51.100.1", "Famatech Radmin VPN Ethernet Adapter", IsVirtual: true);
    var wifi = new HotspotInterface("WiFi", "192.168.43.100", 24, "192.168.43.1", "Realtek Wireless LAN");

    True(HotspotAddress.SelectEndpoint(null, null, [vpn, wifi]) == "192.168.43.1",
        "a virtual/VPN gateway listed FIRST still loses to a physical one");
    True(HotspotAddress.SelectEndpoint(null, null, [wifi, vpn]) == "192.168.43.1",
        "and loses when listed second, too");
    True(HotspotAddress.SelectEndpoint(null, null, [vpn]) == "198.51.100.1",
        "but a virtual gateway is still used when it is the only thing there — ranking, never rejection");
}

Console.WriteLine("--- SelectEndpoint: precedence (peer beats announced beats gateway)");
{
    var chosen = HotspotAddress.SelectEndpoint("192.168.43.55", ["192.168.43.7"], [Hotspot()]);
    True(chosen == "192.168.43.55", "the live peer address wins over an announced one and over the gateway");

    var announcedWins = HotspotAddress.SelectEndpoint(null, ["192.168.43.7"], [Hotspot()]);
    True(announcedWins == "192.168.43.7", "an announced address wins over the gateway when there is no peer");
}

Console.WriteLine("--- SelectEndpoint: link-local IPv6 peer prefers an IPv4 candidate (§3.2)");
{
    var ifaces = new List<HotspotInterface>
    {
        new("Wi-Fi", "fe80::aaaa:bbbb:cccc:dddd", 64, null),
        Hotspot(gateway: null),
    };
    var chosen = HotspotAddress.SelectEndpoint("fe80::1122:3344:5566:7788", ["192.168.43.7"], ifaces);
    True(chosen == "192.168.43.7",
        "a link-local IPv6 peer yields to an IPv4 candidate — 'adb connect [fe80::x%iface]:5555' is unreliable on Windows");

    var noIpv4 = HotspotAddress.SelectEndpoint("fe80::1122:3344:5566:7788", null, ifaces);
    True(noIpv4 == "fe80::1122:3344:5566:7788",
        "with no IPv4 candidate at all, the link-local IPv6 peer is still returned rather than nothing");

    var globalV6 = HotspotAddress.SelectEndpoint("2001:db8::2", ["192.168.43.7"],
        [new HotspotInterface("Wi-Fi", "2001:db8::1", 64, null), Hotspot(gateway: null)]);
    True(globalV6 == "2001:db8::2", "a NON-link-local IPv6 peer is kept — the preference is specific to fe80::/10");
}

Console.WriteLine("--- SelectEndpoint: degenerate input");
{
    True(HotspotAddress.SelectEndpoint(null, null, null) is null, "no peer, nothing announced, no interfaces -> null");
    True(HotspotAddress.SelectEndpoint("", [], []) is null, "empty everything -> null");
    True(HotspotAddress.SelectEndpoint("127.0.0.1", null, [new HotspotInterface("lo", "127.0.0.1", 8, null)]) is null,
        "a loopback peer is never a hotspot peer");
    True(HotspotAddress.SelectEndpoint("192.168.43.100", null, [Hotspot()]) == "192.168.43.1",
        "a peer that is literally our own address is not dialled; the gateway is used instead");
}

// M13d §1.1 INVERTS the equal-gen check this harness used to make. That is deliberate and it is
// the planner's own correction: `gen` marks the LINK EPOCH, not the message, so several messages
// legitimately share one. The check below is not weakened to force a pass — the opposite claim is
// asserted just as tightly, and the case that motivated the flip has its own check underneath.
Console.WriteLine("--- v18 ShouldAccept: the generation rule (M13b §2.1, corrected by M13d §1.1)");
{
    True(HotspotGeneration.ShouldAccept(2, 1), "a HIGHER gen is accepted");
    True(!HotspotGeneration.ShouldAccept(1, 2), "a STRICTLY LOWER gen is rejected — this is the role-flip defence");
    True(HotspotGeneration.ShouldAccept(2, 2),
        "an EQUAL gen is ACCEPTED — gen marks the link epoch, and several messages legitimately share one");
    True(!HotspotGeneration.ShouldAccept(0, 5), "a gen far behind is rejected");
    True(!HotspotGeneration.ShouldAccept(4, 5), "and so is one only a single epoch behind");
    True(HotspotGeneration.ShouldAccept(1, HotspotGeneration.NothingSeen),
        "the FIRST announcement is accepted against the 'nothing seen' seed");
    True(HotspotGeneration.ShouldAccept(0, HotspotGeneration.NothingSeen),
        "even a phone that starts counting at 0 gets its first announcement through");
    True(HotspotGeneration.NothingSeen < 0, "the 'nothing seen' seed is below every legal gen");
}

Console.WriteLine("--- v18 the re-announce-at-same-gen case (M13d §1.1, the case that proves the rule)");
{
    // `adb tcpip 5555` rebinds adbd with NO link event, so the phone re-announces the new port
    // under the SAME gen. Under strictly-greater this was discarded and the whole promotion
    // optimisation silently never fired — which presents as "promotion doesn't work".
    HotspotAnnouncement At(int gen, int port) => new(gen, "<your-device-serial>", "tcpip", port, true, []);

    True(HotspotAnnounce.RejectReason(At(gen: 7, port: 37561), 7) is null,
        "a re-announce carrying a NEW port under the SAME gen is admitted");
    // 5555 spelled out rather than imported: HotspotPromotion.cs needs ILogService and AdbProcess,
    // so it is not one of the files this harness compiles. HotspotPromotion.FixedPort is asserted
    // to BE 5555 by the M13a source-text check on HotspotConnect.cs further up.
    True(HotspotAnnounce.RejectReason(At(gen: 7, port: 5555), 7) is null,
        "specifically: the post-`adb tcpip 5555` re-announce at the fixed port is admitted");
    True(HotspotAnnounce.RejectReason(At(gen: 6, port: 5555), 7) is not null,
        "while a genuinely stale epoch is still refused — the flip did not open the role-flip hole");
    // The other two same-gen re-announce shapes the ruling names.
    True(HotspotAnnounce.RejectReason(
            new HotspotAnnouncement(7, "<your-device-serial>", HotspotAnnounce.ModeTcpIp, 5555, true, []), 7) is null,
        "a tls -> tcpip mode change inside one epoch is admitted");
    True(HotspotAnnounce.RejectReason(At(gen: 7, port: 37561), 7) is null,
        "and so is a re-announce after a failed adb.ack, which carries no new gen either");
    // Last-write-wins within an epoch: the rule must not depend on the port having changed.
    True(HotspotAnnounce.RejectReason(At(gen: 7, port: 37561), 7) is null &&
         HotspotAnnounce.RejectReason(At(gen: 7, port: 37561), 7) is null,
        "the same announcement admitted twice — within an epoch it is last-write-wins, not first-only");
    // A stale adb.down must still not tear down a link a newer announcement built.
    True(!HotspotGeneration.ShouldAccept(3, 4), "an adb.down from a previous epoch is still discarded");
    True(HotspotGeneration.ShouldAccept(4, 4), "an adb.down from the CURRENT epoch is acted on");
}

Console.WriteLine("--- v18 announce payloads: all four message shapes parse (M13b §2.6)");
{
    var payload = new JsonObject
    {
        ["gen"] = 4,
        ["serial"] = "<your-device-serial>",
        ["mode"] = "tcpip",
        ["port"] = 5555,
        ["verified"] = true,
        ["addrs"] = new JsonArray(
            new JsonObject { ["ip"] = "192.168.43.100", ["iface"] = "wlan0", ["prefix"] = 24 },
            new JsonObject { ["ip"] = "10.116.24.9", ["iface"] = "rmnet0", ["prefix"] = 30 }),
    };
    var announcement = HotspotAnnounce.Parse(payload);
    True(announcement is not null, "a full adb.announce payload parses");
    True(announcement!.Gen == 4 && announcement.Port == 5555 && announcement.Verified,
        "gen, port and verified survive the parse");
    True(announcement.Serial == "<your-device-serial>" && announcement.Mode == "tcpip", "serial and mode survive the parse");
    True(announcement.Addrs.Count == 2 && announcement.Addrs[0].Ip == "192.168.43.100" && announcement.Addrs[0].Prefix == 24,
        "every addrs entry survives the parse with its iface and prefix");

    True(HotspotAnnounce.Parse(null) is null, "a null payload is not an announcement");
    True(HotspotAnnounce.Parse([]) is null, "an empty payload is not an announcement");
    True(HotspotAnnounce.Parse(new JsonObject { ["gen"] = 1 }) is null, "gen without a port is not an announcement");
    True(HotspotAnnounce.Parse(new JsonObject { ["port"] = 5555 }) is null, "a port without a gen is not an announcement");
    // An unprivileged Android app usually cannot read its own serial, so requiring it here
    // would reject every real announcement; identity is enforced by ExpectedSerial instead.
    True(HotspotAnnounce.Parse(new JsonObject { ["gen"] = 1, ["port"] = 5555 })?.Serial == "",
        "an announcement with no serial still parses (the desktop checks against the PAIRED serial)");
    True(HotspotAnnounce.Parse(new JsonObject { ["gen"] = 1, ["port"] = 5555 })?.Verified == false,
        "a missing verified field means NOT verified, never 'assume yes'");
}

Console.WriteLine("--- v18 verified:false is refused (M13b §2.3 / §2.6)");
{
    HotspotAnnouncement Announcement(bool verified, int gen = 3, int port = 5555) =>
        new(gen, "<your-device-serial>", "tcpip", port, verified, []);

    True(HotspotAnnounce.RejectReason(Announcement(verified: true), 2) is null,
        "a verified, newer announcement is admitted");
    var refusal = HotspotAnnounce.RejectReason(Announcement(verified: false), 2);
    True(refusal is not null && refusal.Contains("verified", StringComparison.Ordinal),
        "verified:false is REFUSED — the L3 link comes up before adbd re-binds, so link-up is not readiness");
    True(HotspotAnnounce.RejectReason(Announcement(verified: true, gen: 1), 2) is not null,
        "a stale gen is refused even when verified is true");
    True(HotspotAnnounce.RejectReason(Announcement(verified: true, port: 0), 2) is not null, "port 0 is refused");
    True(HotspotAnnounce.RejectReason(Announcement(verified: true, port: 70000), 2) is not null, "port 70000 is refused");
    True(HotspotAnnounce.RejectReason(null, 2) is not null, "an unparseable announcement is refused");
    // Ordering matters: a stale message must be discarded on its gen, not on its contents.
    True(HotspotAnnounce.RejectReason(Announcement(verified: false, gen: 1), 2)!.Contains("stale", StringComparison.Ordinal),
        "gen is checked BEFORE verified, so a stale message is discarded as stale");
}

Console.WriteLine("--- v18 addrs go through M13a's same-subnet filter (M13b §2.4)");
{
    var announcement = HotspotAnnounce.Parse(new JsonObject
    {
        ["gen"] = 1,
        ["port"] = 5555,
        ["verified"] = true,
        ["addrs"] = new JsonArray(
            new JsonObject { ["ip"] = "10.116.24.9", ["iface"] = "rmnet0", ["prefix"] = 30 },
            new JsonObject { ["ip"] = "192.168.43.7", ["iface"] = "wlan0", ["prefix"] = 24 }),
    })!;
    var addresses = HotspotAnnounce.AddressStrings(announcement);
    True(addresses.Count == 2, "both announced addresses reach the filter");
    var chosen = HotspotAddress.SelectEndpoint(null, addresses, [Hotspot(gateway: null)]);
    True(chosen == "192.168.43.7",
        "the mobile-data address the phone announced is filtered out and the hotspot one is chosen");
    True(HotspotAddress.SelectEndpoint(null, ["10.116.24.9"], [Hotspot(gateway: null)]) is null,
        "an addrs list containing ONLY a foreign subnet yields nothing to dial");
}

Console.WriteLine("--- v18 identity: the ADB serial is the only stable identity");
{
    True(HotspotAnnounce.ExpectedSerial("<your-device-serial>", "") == "<your-device-serial>",
        "with no announced serial, the PAIRED serial is what get-serialno must match");
    // The announced serial here is the paired one with its case flipped, which is the whole point
    // of the assertion: agreement must be case-INSENSITIVE. (M19 A5 replaced the real device
    // serial with a placeholder; this line has to keep differing in case to still test anything.)
    True(HotspotAnnounce.ExpectedSerial("<your-device-serial>", "<YOUR-DEVICE-SERIAL>") == "<your-device-serial>",
        "an agreeing announced serial (any case) keeps the paired one");
    True(HotspotAnnounce.ExpectedSerial("<your-device-serial>", "SOMEOTHERPHONE") is null,
        "a DISAGREEING announced serial is a refusal, not a tiebreak");
    True(HotspotAnnounce.ExpectedSerial(null, "<your-device-serial>") == "<your-device-serial>",
        "with nothing paired yet, the announced serial is at least something to check");
    True(HotspotAnnounce.ExpectedSerial(null, null) is null,
        "with neither, there is nothing to verify against and the attempt must be refused");
}

Console.WriteLine("--- v18 get-state routing (PROTOCOL.md v18 'reachability is not identity')");
{
    True(HotspotAnnounce.ClassifyState("device") == HotspotStateVerdict.Usable, "'device' is usable");
    True(HotspotAnnounce.ClassifyState("unauthorized") == HotspotStateVerdict.NeedsPairing,
        "'unauthorized' routes to pairing — retrying can never fix a missing pairing");
    True(HotspotAnnounce.ClassifyState("offline") == HotspotStateVerdict.RetryOnce,
        "'offline' is stale server state: disconnect and retry once");
    True(HotspotAnnounce.ClassifyState("") == HotspotStateVerdict.Unusable, "empty output is unusable");
    True(HotspotAnnounce.ClassifyState(null) == HotspotStateVerdict.Unusable, "no output is unusable");
    True(HotspotAnnounce.ClassifyState("  device  ") == HotspotStateVerdict.Usable, "surrounding whitespace is tolerated");
    True(HotspotAnnounce.ClassifyState("Device") == HotspotStateVerdict.Unusable,
        "the match is exact — 'device' is adb's literal answer, not a prefix to guess at");
}

Console.WriteLine("--- source-text check: HotspotLinkService asks the gate before it dials (M13b §2.4)");
{
    var repoRoot0 = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot0, "DESKTOP")) && Directory.GetParent(repoRoot0) != null)
    {
        repoRoot0 = Directory.GetParent(repoRoot0)!.FullName;
    }
    var servicePath = Path.Combine(repoRoot0, "DESKTOP", "Linc.Desktop", "Services", "HotspotLinkService.cs");
    if (!File.Exists(servicePath))
    {
        failures.Add($"Missing production file: {servicePath}");
        Console.WriteLine($"    FAIL: cannot read {servicePath}");
    }
    else
    {
        var service = File.ReadAllText(servicePath);
        var start = service.IndexOf("private async Task HandleAnnounceAsync", StringComparison.Ordinal);
        True(start >= 0, "HotspotLinkService declares HandleAnnounceAsync");
        if (start >= 0)
        {
            var body = service[start..];
            var idxReject = body.IndexOf("HotspotAnnounce.RejectReason", StringComparison.Ordinal);
            var idxConnect = body.IndexOf("connector.ConnectAsync", StringComparison.Ordinal);
            True(idxReject >= 0, "it calls the pure admission gate rather than re-deciding");
            True(idxConnect >= 0, "it dials through the M13a connector");
            True(idxReject >= 0 && idxConnect >= 0 && idxReject < idxConnect,
                "the gate is consulted BEFORE anything is dialled");
            True(body.IndexOf("HotspotAnnounce.ExpectedSerial", StringComparison.Ordinal) is var idxSerial &&
                 idxSerial >= 0 && idxSerial < idxConnect,
                "identity is settled before the dial, not after");
            True(body.Contains("HotspotAddress.SelectEndpoint", StringComparison.Ordinal),
                "the announced addrs go through M13a's same-subnet filter, not straight to adb");
            True(!body.Contains("Verified ||", StringComparison.Ordinal) &&
                 !body.Contains("|| announcement.Verified", StringComparison.Ordinal),
                "the verified refusal is not weakened with an escape hatch in the service");
            True(service.Contains("MessageType.AdbAck", StringComparison.Ordinal),
                "an outcome is acked back to the phone so it stops retrying");
        }
    }
}

// GUIDE.md §4.1's second pattern: HotspotConnector.ConnectAsync cannot be called here — it
// spawns adb.exe and needs a phone — so the one property that matters about it is asserted
// against the production source text instead of against a harness-side copy of the sequence.
Console.WriteLine("--- source-text check: HotspotConnect.cs disconnects before it connects (§3.3)");
{
    var repoRoot = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }
    var path = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Services", "HotspotConnect.cs");

    if (!File.Exists(path))
    {
        failures.Add($"Missing production file: {path}");
        Console.WriteLine($"    FAIL: cannot read {path} (run this with CWD = ...\\yellow\\Linc)");
    }
    else
    {
        var source = File.ReadAllText(path);
        var bodyStart = source.IndexOf("public async Task<HotspotConnectResult> ConnectAsync", StringComparison.Ordinal);
        if (bodyStart < 0)
        {
            failures.Add("HotspotConnect.cs no longer declares ConnectAsync");
            Console.WriteLine("    FAIL: HotspotConnect.cs no longer declares ConnectAsync");
        }
        else
        {
            // Scoped to ConnectAsync's body, not the whole file: an adb disconnect sitting in
            // some other method would not protect this path (GUIDE.md §4.4).
            var body = source[bodyStart..];
            var idxDisconnect = body.IndexOf("RunAdbAsync(adb, ct, DisconnectVerb", StringComparison.Ordinal);
            var idxConnect = body.IndexOf("RunAdbAsync(adb, ct, ConnectVerb", StringComparison.Ordinal);
            True(idxDisconnect >= 0, "ConnectAsync's body issues an adb disconnect");
            True(idxConnect >= 0, "ConnectAsync's body issues an adb connect");
            True(idxDisconnect >= 0 && idxConnect >= 0 && idxDisconnect < idxConnect,
                "the disconnect comes BEFORE the connect (a cached stale endpoint makes adb silently refuse the replacement)");
            True(source.Contains("const string DisconnectVerb = \"disconnect\"", StringComparison.Ordinal) &&
                 source.Contains("const string ConnectVerb = \"connect\"", StringComparison.Ordinal),
                "those two verbs really are adb's 'disconnect' and 'connect'");

            // Reachability is not identity: all three verifications must survive.
            True(body.Contains("\"connected to\"", StringComparison.Ordinal),
                "ConnectAsync checks the connect output for 'connected to'");
            True(body.Contains("get-state", StringComparison.Ordinal),
                "ConnectAsync runs get-state");
            True(body.Contains("get-serialno", StringComparison.Ordinal),
                "ConnectAsync runs get-serialno");
            True(source.Contains("const string UsableState = \"device\"", StringComparison.Ordinal),
                "the accepted state is exactly 'device'");
            True(body.Contains("expectedSerial", StringComparison.Ordinal),
                "the reported serial is compared against the expected one, not merely printed");
            True(source.Contains("public const int AdbTcpPort = 5555", StringComparison.Ordinal),
                "the port is the hardcoded 5555 this milestone specifies (discovery is a later one)");
            // Scoped to the code, not the file: the class doc above ConnectAsync explains that
            // `adb tcpip 5555` is assumed to have been run BY HAND, and saying so must not be
            // indistinguishable from doing it.
            True(!body.Contains("tcpip", StringComparison.OrdinalIgnoreCase) &&
                 !source.Contains("\"tcpip\"", StringComparison.Ordinal),
                "no code in HotspotConnect.cs arms the phone — there is no 'tcpip' argument anywhere in it");
        }
    }
}

Console.WriteLine("--- M13c backoff schedule (§3.2)");
{
    True(HotspotBackoff.DelayFor(0) == TimeSpan.Zero, "nothing has failed yet -> no delay");
    True(HotspotBackoff.DelayFor(1) == TimeSpan.FromMilliseconds(250), "first retry is 250 ms");
    True(HotspotBackoff.DelayFor(2) == TimeSpan.FromMilliseconds(500), "second is 500 ms");
    True(HotspotBackoff.DelayFor(3) == TimeSpan.FromSeconds(1), "third is 1 s");
    True(HotspotBackoff.DelayFor(4) == TimeSpan.FromSeconds(2), "fourth is 2 s");
    True(HotspotBackoff.DelayFor(5) == TimeSpan.FromSeconds(4), "fifth is 4 s");
    True(HotspotBackoff.DelayFor(6) == TimeSpan.FromSeconds(8), "sixth is 8 s");
    True(HotspotBackoff.DelayFor(7) == TimeSpan.FromSeconds(15), "seventh caps at 15 s rather than 16");
    True(HotspotBackoff.DelayFor(50) == TimeSpan.FromSeconds(15), "and stays capped however long it has been failing");
    True(HotspotBackoff.DelayFor(int.MaxValue) == TimeSpan.FromSeconds(15),
        "the cap holds at int.MaxValue — the doubling must not overflow into a tiny delay");
    True(HotspotBackoff.DelayFor(-1) == TimeSpan.Zero, "a negative count is not a delay");
    True(HotspotBackoff.PollInterval >= TimeSpan.FromSeconds(3) && HotspotBackoff.PollInterval <= TimeSpan.FromSeconds(5),
        "the adb devices poll interval is inside the 3-5 s the spec asks for");

    True(HotspotBackoff.ShouldReset(verifiedSuccess: true, 1, 1), "a verified success resets the backoff");
    True(HotspotBackoff.ShouldReset(verifiedSuccess: false, 2, 1),
        "a NEW generation resets it too — a fresh link event means conditions genuinely changed");
    True(!HotspotBackoff.ShouldReset(verifiedSuccess: false, 1, 1), "the same generation failing again does not reset it");
    True(!HotspotBackoff.ShouldReset(verifiedSuccess: false, 0, 3), "a stale generation certainly does not reset it");
}

Console.WriteLine("--- M13c the speculative race: first VERIFIED success wins, loser stands down (§3.1)");
{
    var race = new HotspotRaceState();
    True(!race.IsSettled, "a fresh race is open");
    True(race.TryClaim(HotspotRaceBranch.Speculative, identityVerified: true) == HotspotRaceClaim.Won,
        "the first branch to arrive with a verified identity wins");
    True(race.IsSettled && race.Winner == HotspotRaceBranch.Speculative, "the race records who won");
    True(race.TryClaim(HotspotRaceBranch.Announced, identityVerified: true) == HotspotRaceClaim.LostRaceAlreadySettled,
        "the loser is told to stand down rather than tearing down the winner's link");

    var other = new HotspotRaceState();
    True(other.TryClaim(HotspotRaceBranch.Announced, identityVerified: true) == HotspotRaceClaim.Won,
        "either branch can win — the announced one is not privileged");

    // The rule the whole race rests on.
    var unverified = new HotspotRaceState();
    True(unverified.TryClaim(HotspotRaceBranch.Speculative, identityVerified: false) == HotspotRaceClaim.RejectedUnverified,
        "a branch that did NOT verify identity cannot claim the race, however fast it was");
    True(!unverified.IsSettled, "and a rejected claim leaves the race open for the branch that does verify");
    True(unverified.TryClaim(HotspotRaceBranch.Announced, identityVerified: true) == HotspotRaceClaim.Won,
        "which then wins normally");

    // Concurrency: exactly one winner, whatever the interleaving.
    var contended = new HotspotRaceState();
    var wins = 0;
    Parallel.For(0, 64, _ =>
    {
        if (contended.TryClaim(HotspotRaceBranch.Speculative, identityVerified: true) == HotspotRaceClaim.Won)
        {
            Interlocked.Increment(ref wins);
        }
    });
    True(wins == 1, $"exactly one of 64 concurrent claims wins (got {wins})");
}

Console.WriteLine("--- M13c per-stage instrumentation (§3.4)");
{
    var timer = new HotspotStageTimer();
    timer.Mark("resolve");
    timer.Mark("adb connect");
    timer.Mark("verify");
    var line = timer.Format();
    True(line.Contains("resolve", StringComparison.Ordinal) &&
         line.Contains("adb connect", StringComparison.Ordinal) &&
         line.Contains("verify", StringComparison.Ordinal),
        "every stage is named separately, so a slow one can be identified");
    True(line.Contains("total", StringComparison.Ordinal), "and a total is reported");
    True(HotspotStageTimer.SuspiciouslySlow == TimeSpan.FromMilliseconds(1500),
        "the 'something is scanning' threshold is the 1.5 s the spec names");
    True(!timer.IsSuspiciouslySlow, "three no-op stages are not slow");
}

Console.WriteLine("--- M13c the onboarding grant is idempotent and non-fatal (§2.1, acceptance 5)");
{
    const string package_ = "app.linc.android";
    const string listener = "app.linc.android/app.linc.android.service.LincNotificationListener";
    string[] runtime = ["android.permission.POST_NOTIFICATIONS", "android.permission.READ_SMS"];

    var plan = PhoneSetupCommands.GrantPlan(package_, listener, runtime);
    var again = PhoneSetupCommands.GrantPlan(package_, listener, runtime);
    True(plan.SequenceEqual(again), "the plan is deterministic — running onboarding twice issues the same commands");
    True(plan.Count(PhoneSetupCommands.IsSelfArmGrant) == 1, "the self-arm grant appears exactly once");
    True(plan.Any(c => c.Contains("WRITE_SECURE_SETTINGS", StringComparison.Ordinal)),
        "the WRITE_SECURE_SETTINGS grant really is in the post-install plan");
    True(plan.Last() == $"pm grant {package_} {PhoneSetupCommands.SelfArmPermission}",
        "it is LAST, so a phone that refuses it still got every grant before it");
    True(!plan.Any(c => c.Contains("revoke", StringComparison.OrdinalIgnoreCase)),
        "nothing in the plan revokes anything");

    // The failure case, run through the REAL loop with a runner that throws on that one command.
    var ran = new List<string>();
    var reported = new List<string>();
    var failed = PhoneSetupCommands.RunPlanAsync(
        plan,
        (command, _) =>
        {
            ran.Add(command);
            return PhoneSetupCommands.IsSelfArmGrant(command)
                ? throw new InvalidOperationException("Operation not allowed: java.lang.SecurityException")
                : Task.CompletedTask;
        },
        (command, _) => reported.Add(command),
        CancellationToken.None).GetAwaiter().GetResult();

    True(ran.Count == plan.Count, "every command in the plan was still attempted");
    True(failed.Count == 1 && PhoneSetupCommands.IsSelfArmGrant(failed[0]),
        "the failure is reported as exactly the one command that failed");
    True(reported.Count == 1, "and it was surfaced once, not swallowed");
    True(!string.IsNullOrWhiteSpace(PhoneSetupCommands.SelfArmUnavailableReason) &&
         PhoneSetupCommands.SelfArmUnavailableReason.Contains("Everything else works", StringComparison.Ordinal),
        "the user-facing reason says the rest still works (disable, don't hide)");

    // A failure in the MIDDLE must not stop the rest either.
    var ranAll = new List<string>();
    var failedMiddle = PhoneSetupCommands.RunPlanAsync(
        plan,
        (command, _) =>
        {
            ranAll.Add(command);
            return command.Contains("POST_NOTIFICATIONS", StringComparison.Ordinal)
                ? throw new InvalidOperationException("boom")
                : Task.CompletedTask;
        },
        null,
        CancellationToken.None).GetAwaiter().GetResult();
    True(ranAll.Count == plan.Count, "a failure part-way through does not stop the commands after it");
    True(failedMiddle.Count == 1, "and only that one is counted as failed");
}

Console.WriteLine("--- M13e §1.1 ShouldRedial: the promotion case and the idempotent case");
{
    var never = TimeSpan.MaxValue;
    var settled = HotspotRedial.MinInterval + TimeSpan.FromSeconds(1);

    // THE PROMOTION CASE, by name. `adb tcpip 5555` rebinds adbd with no link event, so the
    // phone re-announces 5555 under the SAME gen while we still hold the ephemeral port. M13d
    // admitted this message at the gate; without a redial it then died in AttemptAsync.
    True(HotspotRedial.ShouldRedial("192.168.43.1:5555", "192.168.43.1:37419", verified: true, never),
        "PROMOTION: :37419 live and :5555 announced at the same gen -> redial");
    True(HotspotRedial.ShouldRedial("192.168.43.1:37419", "192.168.43.1:5555", verified: true, never),
        "…and the reverse direction too — the rule is 'different', not 'higher' or 'lower'");
    True(HotspotRedial.ShouldRedial("192.168.43.9:5555", "192.168.43.1:5555", verified: true, never),
        "a different ADDRESS at the same port is also a redial");

    // THE IDEMPOTENT CASE, by name.
    True(!HotspotRedial.ShouldRedial("192.168.43.1:5555", "192.168.43.1:5555", verified: true, never),
        "IDEMPOTENT: the same endpoint announced again -> NO redial, however often it arrives");
    True(!HotspotRedial.ShouldRedial(" 192.168.43.1:5555 ", "192.168.43.1:5555", verified: true, never),
        "…and whitespace does not make it look like a different endpoint");
    True(!HotspotRedial.ShouldRedial("[FE80::1%12]:5555", "[fe80::1%12]:5555", verified: true, never),
        "…nor does the case of an IPv6 literal");

    // verified:false must never tear down a WORKING link.
    True(!HotspotRedial.ShouldRedial("192.168.43.1:5555", "192.168.43.1:37419", verified: false, never),
        "an UNVERIFIED announcement never redials — tearing a live link down for an unproven port is strictly worse");

    // The rate limit, so a flapping phone cannot thrash the link.
    True(!HotspotRedial.ShouldRedial("192.168.43.1:5555", "192.168.43.1:37419", true, TimeSpan.Zero),
        "a redial immediately after the last one is refused");
    True(!HotspotRedial.ShouldRedial("192.168.43.1:5555", "192.168.43.1:37419", true,
            HotspotRedial.MinInterval - TimeSpan.FromMilliseconds(1)),
        "…and so is one a millisecond inside the floor");
    True(HotspotRedial.ShouldRedial("192.168.43.1:5555", "192.168.43.1:37419", true, HotspotRedial.MinInterval),
        "exactly at the floor is allowed");
    True(HotspotRedial.ShouldRedial("192.168.43.1:5555", "192.168.43.1:37419", true, settled),
        "and comfortably past it certainly is");
    True(HotspotRedial.MinInterval >= HotspotStageTimer.SuspiciouslySlow * 2,
        $"the floor ({HotspotRedial.MinInterval.TotalSeconds:0} s) is at least twice the 'something is scanning' threshold, so two redials cannot overlap");
    True(HotspotRedial.MinInterval < HotspotBackoff.PollInterval,
        "and below the health-loop poll interval, so a genuinely dead link is still the health loop's call");

    // No link at all is not a redial — the ordinary race owns that, and answering true would
    // double-dial it.
    True(!HotspotRedial.ShouldRedial("192.168.43.1:5555", null, verified: true, never),
        "with nothing connected there is nothing to redial");
    True(!HotspotRedial.ShouldRedial("192.168.43.1:5555", "", verified: true, never),
        "an empty current endpoint is not a link either");
    True(!HotspotRedial.ShouldRedial(null, "192.168.43.1:5555", verified: true, never),
        "and an announcement with no endpoint to go to is not a redial");

    // The two ports really are distinguished — a comparison that ignored the port would make the
    // promotion case above pass for the wrong reason.
    True(!HotspotRedial.SameEndpoint("192.168.43.1:37419", "192.168.43.1:5555"),
        "two endpoints differing ONLY in port are not the same endpoint");
    True(HotspotRedial.SameEndpoint("192.168.43.1:5555", "192.168.43.1:5555"),
        "and two identical ones are");
    // (That the strings being compared are adb's own ip:port format is asserted against
    // HotspotLinkService's source text below — HotspotConnect.cs needs ToolLocator and an adb
    // process, so it is not one of the files this harness compiles.)
}

Console.WriteLine("--- M13d §1.4 IsHotspotShaped: which peers are worth racing at all");
{
    True(HotspotAddress.IsHotspotShaped("192.168.43.1", [Hotspot()]),
        "a peer inside one of our own subnets is hotspot-shaped");
    True(!HotspotAddress.IsHotspotShaped("10.116.24.9", [Hotspot()]),
        "a foreign peer is NOT — even though SelectEndpoint would still return the gateway for it");
    True(HotspotAddress.SelectEndpoint("10.116.24.9", null, [Hotspot()]) == "192.168.43.1",
        "…and that is exactly the distinction: SelectEndpoint falls through to the gateway, the trigger must not");
    True(!HotspotAddress.IsHotspotShaped(null, [Hotspot()]), "no peer is not hotspot-shaped");
    True(!HotspotAddress.IsHotspotShaped("", [Hotspot()]), "an empty peer is not hotspot-shaped");
    True(!HotspotAddress.IsHotspotShaped("not-an-ip", [Hotspot()]), "unparseable text is not hotspot-shaped");
    True(!HotspotAddress.IsHotspotShaped("127.0.0.1", [new HotspotInterface("lo", "127.0.0.1", 8, null)]),
        "a loopback peer is not hotspot-shaped — that is an ADB port-forward, not a phone on a link");
    True(!HotspotAddress.IsHotspotShaped("192.168.43.100", [Hotspot()]),
        "our own address is not a peer to race");
    True(!HotspotAddress.IsHotspotShaped("192.168.43.1", null), "with no interfaces there is no trust anchor, so no");
}

Console.WriteLine("--- M13d §1.4 the speculative trigger: fires once per gen, never over a verified link");
{
    var mine = new List<HotspotInterface> { Hotspot() };
    const string Peer = "192.168.43.1";

    True(HotspotSpeculativeTrigger.ShouldFire(Peer, mine, gen: 3,
            lastFiredGen: HotspotSpeculativeTrigger.NeverFired, linkedEndpoint: null),
        "a hotspot-shaped peer with nothing fired yet and no link fires the speculative branch");

    // GUARD 1 — at most once per gen. The phone re-dialling its control socket inside one epoch
    // is routine; each extra fire is another adb disconnect/connect cycle for nothing.
    True(!HotspotSpeculativeTrigger.ShouldFire(Peer, mine, gen: 3, lastFiredGen: 3, linkedEndpoint: null),
        "the SAME gen does not fire a second speculative attempt");
    True(!HotspotSpeculativeTrigger.ShouldFire(Peer, mine, gen: 2, lastFiredGen: 3, linkedEndpoint: null),
        "nor does an older gen");
    True(HotspotSpeculativeTrigger.ShouldFire(Peer, mine, gen: 4, lastFiredGen: 3, linkedEndpoint: null),
        "but the NEXT gen does — a new link epoch deserves its own race");
    True(HotspotSpeculativeTrigger.NeverFired < HotspotGeneration.NothingSeen,
        "'never fired' sits BELOW 'nothing seen', so a control connection arriving before any " +
        "announcement still gets the one attempt that matters most");
    True(HotspotSpeculativeTrigger.ShouldFire(Peer, mine, gen: HotspotGeneration.NothingSeen,
            lastFiredGen: HotspotSpeculativeTrigger.NeverFired, linkedEndpoint: null),
        "…demonstrated: gen is still the 'nothing seen' seed and it fires");
    True(!HotspotSpeculativeTrigger.ShouldFire(Peer, mine, gen: HotspotGeneration.NothingSeen,
            lastFiredGen: HotspotGeneration.NothingSeen, linkedEndpoint: null),
        "…and does not fire twice at that seed either");

    // GUARD 2 — never when a verified ADB connection for that endpoint already exists.
    True(!HotspotSpeculativeTrigger.ShouldFire(Peer, mine, gen: 9,
            lastFiredGen: HotspotSpeculativeTrigger.NeverFired, linkedEndpoint: "192.168.43.1:5555"),
        "a VERIFIED link to that peer suppresses the speculative attempt, however new the gen");
    True(HotspotSpeculativeTrigger.ShouldFire(Peer, mine, gen: 9,
            lastFiredGen: HotspotSpeculativeTrigger.NeverFired, linkedEndpoint: "192.168.43.77:5555"),
        "a verified link to a DIFFERENT address does not — that is not this peer");
    True(HotspotSpeculativeTrigger.HasVerifiedLinkTo("192.168.43.1:5555", "192.168.43.1"),
        "the endpoint match is on the host, because adbd's port is never the control socket's port");
    True(HotspotSpeculativeTrigger.HasVerifiedLinkTo("192.168.43.1:37561", "192.168.43.1"),
        "…so an ephemeral adbd port still matches its own host");
    True(!HotspotSpeculativeTrigger.HasVerifiedLinkTo(null, Peer), "no link means no suppression");
    True(!HotspotSpeculativeTrigger.HasVerifiedLinkTo("", Peer), "an empty endpoint means no suppression");
    True(!HotspotSpeculativeTrigger.HasVerifiedLinkTo("192.168.43.1:5555", null), "and no peer cannot match one");
    True(HotspotSpeculativeTrigger.HasVerifiedLinkTo("[fe80::1%12]:5555", "fe80::1%12"),
        "a bracketed IPv6 endpoint matches the unbracketed peer it came from");

    // The filter still applies: a foreign peer is not raced whatever the counters say.
    True(!HotspotSpeculativeTrigger.ShouldFire("10.116.24.9", mine, gen: 9,
            lastFiredGen: HotspotSpeculativeTrigger.NeverFired, linkedEndpoint: null),
        "a foreign peer is never raced, however fresh the gen");
    True(!HotspotSpeculativeTrigger.ShouldFire(null, mine, gen: 9,
            lastFiredGen: HotspotSpeculativeTrigger.NeverFired, linkedEndpoint: null),
        "and neither is a missing one");
}

Console.WriteLine("--- M13d §1.5 the adb.arm fallback for the gap the sweep would have covered");
{
    True(HotspotArmFallback.Window > TimeSpan.Zero && HotspotArmFallback.Window <= TimeSpan.FromSeconds(10),
        $"the wait before asking is short and bounded ({HotspotArmFallback.Window.TotalSeconds:0} s)");
    True(HotspotArmFallback.Window < TimeSpan.FromSeconds(44),
        "and far below the 44-175 s the ephemeral sweep measured on-device — the point of preferring this");
    True(HotspotArmFallback.Prefer == HotspotAnnounce.ModeTcpIp,
        "it asks for legacy tcpip on a KNOWN port, which is what sidesteps port discovery");

    True(HotspotArmFallback.ShouldRequest(usableAnnouncementSeen: false, linkedEndpoint: null),
        "no announcement and no link -> ask the phone to arm the standard port");
    True(!HotspotArmFallback.ShouldRequest(usableAnnouncementSeen: true, linkedEndpoint: null),
        "an announcement DID arrive -> there is nothing to ask for");
    True(!HotspotArmFallback.ShouldRequest(usableAnnouncementSeen: false, linkedEndpoint: "192.168.43.1:5555"),
        "a link came up some other way -> asking would only tear it down");
    True(!HotspotArmFallback.ShouldRequest(usableAnnouncementSeen: true, linkedEndpoint: "192.168.43.1:5555"),
        "and neither applies when both happened");
    True(HotspotArmFallback.ShouldRequest(usableAnnouncementSeen: false, linkedEndpoint: "   "),
        "a whitespace endpoint is not a link either — blank text must not be mistaken for one and suppress the ask");

    True(!string.IsNullOrWhiteSpace(HotspotArmFallback.Reason) &&
         HotspotArmFallback.Reason.Contains("port", StringComparison.OrdinalIgnoreCase) &&
         !HotspotArmFallback.Reason.Contains("adb", StringComparison.OrdinalIgnoreCase) &&
         !HotspotArmFallback.Reason.Contains("NsdManager", StringComparison.Ordinal),
        "the one line it logs names the situation in plain language — no raw adb or platform jargon");
}

Console.WriteLine("--- TransportRank: a transport that has not answered must lose its rank (M13f §2)");
{
    // The owner's failure: a stale USB entry the adb server still lists won against the live
    // wireless link, so the PC "stuck on USB long after the cable was gone". Rank alone had
    // decided; liveness had to be allowed to override it.
    True(TransportRank.Choose(
            [
                new TransportCandidate("AdbWireless", 2, Answered: true),
                new TransportCandidate("AdbUsb", 3, Answered: false),
            ]) == "AdbWireless",
        "a LIVE wireless link wins over a stale USB entry that has not answered — rank loses to liveness");

    True(TransportRank.Choose(
            [
                new TransportCandidate("AdbUsb", 3, Answered: true),
                new TransportCandidate("AdbWireless", 2, Answered: false),
            ]) == "AdbUsb",
        "a live USB link outranks a wireless one that has not answered — liveness alone never overrules rank");

    True(TransportRank.Choose(
            [
                new TransportCandidate("AdbUsb", 3, Answered: true),
                new TransportCandidate("AdbWireless", 2, Answered: true),
            ]) == "AdbUsb",
        "both answering: rank decides, USB first");

    True(TransportRank.Choose(
            [
                new TransportCandidate("AdbUsb", 3, Answered: false),
                new TransportCandidate("AdbWireless", 2, Answered: false),
            ]) is null,
        "nothing has answered: no transport is chosen at all — liveness is a gate, not a preference");

    True(TransportRank.Choose([]) is null,
        "an empty candidate list chooses nothing (negative proof: the rule never crashes into 'USB always wins')");
}

Console.WriteLine("--- VerifySerial: reachability is not identity (M13f §3)");
{
    // `adb get-serialno` over TCP returns the ENDPOINT (ip:port), so comparing that against the
    // paired hardware serial proved nothing — the bug was the check comparing the wrong thing.
    True(HotspotAnnounce.VerifySerial("<your-device-serial>", "<your-device-serial>"),
        "the paired hardware serial reported as-is passes");
    True(!HotspotAnnounce.VerifySerial("<your-device-serial>", "10.229.217.188:5555"),
        "the TCP endpoint (what get-serialno USED to return) fails against the paired serial — negative proof");
    True(!HotspotAnnounce.VerifySerial("<your-device-serial>", "2C141FDH20089N"),
        "a DIFFERENT serial fails — a wrong phone must never verify (negative proof)");
    True(HotspotAnnounce.VerifySerial(null, "10.229.217.188:5555"),
        "no expected serial means report-only: anything is accepted and identity is carried in the result");
    True(HotspotAnnounce.VerifySerial("", "whatever"),
        "a blank expected serial is report-only too");
}

Console.WriteLine("--- HotspotEndpoint: never double-append a port (M13f §5)");
{
    // The health loop aimed at "ip:port" but the connector appended :5555 again, dialling the
    // malformed "ip:port:5555" — which adb quietly failed on. The builder must recognise an
    // endpoint it is handed and leave it alone.
    True(HotspotEndpoint.Build("10.229.217.188:5555", 5555) == "10.229.217.188:5555",
        "an address that already IS an endpoint passes through untouched (this is the malformed dial)");
    True(HotspotEndpoint.Build("10.229.217.188", 5555) == "10.229.217.188:5555",
        "a bare address gets the port appended");
    True(HotspotEndpoint.Build("10.229.217.188", 44567) == "10.229.217.188:44567",
        "a bare address gets an explicit port appended");
    True(HotspotEndpoint.Build("fe80::9a03:8eff:fe84:f698", 5555) == "[fe80::9a03:8eff:fe84:f698]:5555",
        "an IPv6 literal is bracketed the way adb expects");
    True(HotspotEndpoint.Build(" [fe80::9a03:8eff:fe84:f698]:5555 ", 5555) == "[fe80::9a03:8eff:fe84:f698]:5555",
        "an already-bracketed IPv6 endpoint passes through too (after trimming)");
    True(!HotspotEndpoint.IsEndpoint("10.229.217.188:5555:5555"),
        "ip:port:5555 is NOT a valid endpoint — exactly the shape the health loop used to aim (negative proof)");
    True(HotspotEndpoint.IsEndpoint("10.229.217.188:5555"), "ip:port IS a valid endpoint");
    True(!HotspotEndpoint.IsEndpoint("10.229.217.188:99999"), "a port beyond 65535 is not a valid endpoint");
    True(!HotspotEndpoint.IsEndpoint("not-an-endpoint"), "non-IP text is not a valid endpoint");
}

Console.WriteLine("--- source-text check: the health loop and the race (M13c §3.1/§3.2)");
{
    var repoRoot1 = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot1, "DESKTOP")) && Directory.GetParent(repoRoot1) != null)
    {
        repoRoot1 = Directory.GetParent(repoRoot1)!.FullName;
    }
    var linkPath = Path.Combine(repoRoot1, "DESKTOP", "Linc.Desktop", "Services", "HotspotLinkService.cs");
    var promotionPath = Path.Combine(repoRoot1, "DESKTOP", "Linc.Desktop", "Services", "HotspotPromotion.cs");

    if (!File.Exists(linkPath) || !File.Exists(promotionPath))
    {
        failures.Add("Missing HotspotLinkService.cs or HotspotPromotion.cs");
        Console.WriteLine("    FAIL: cannot read HotspotLinkService.cs / HotspotPromotion.cs");
    }
    else
    {
        var link = File.ReadAllText(linkPath);

        var healthStart = link.IndexOf("private async Task HealthLoopAsync", StringComparison.Ordinal);
        True(healthStart >= 0, "HotspotLinkService has a health loop");
        if (healthStart >= 0)
        {
            var health = link[healthStart..];
            var idxDisconnect = health.IndexOf("connector.DisconnectAsync", StringComparison.Ordinal);
            var idxReconnect = health.IndexOf("connector.ConnectAsync", StringComparison.Ordinal);
            True(idxDisconnect >= 0, "the health loop issues an adb disconnect");
            True(idxReconnect >= 0, "the health loop reconnects");
            True(idxDisconnect >= 0 && idxReconnect >= 0 && idxDisconnect < idxReconnect,
                "the disconnect comes BEFORE the reconnect (the adb server caches a stale endpoint and refuses the replacement)");
            True(health.Contains("ListDevicesAsync", StringComparison.Ordinal),
                "the loop polls adb devices rather than trusting its own memory of the link");
            True(health.Contains("HotspotBackoff.DelayFor", StringComparison.Ordinal),
                "it backs off through the pure schedule instead of a hand-rolled delay");
        }

        var attemptStart = link.IndexOf("private async Task AttemptAsync", StringComparison.Ordinal);
        True(attemptStart >= 0, "both race branches go through one AttemptAsync");
        if (attemptStart >= 0)
        {
            var attempt = link[attemptStart..];
            True(attempt.Contains("TryClaim(branch, result.IdentityVerified)", StringComparison.Ordinal),
                "the race is claimed with the connector's OWN identity verdict, not a looser condition");
            True(!attempt.Contains("TryClaim(branch, true)", StringComparison.Ordinal),
                "no branch claims the race unconditionally");
            True(attempt.Contains("expectedSerial", StringComparison.Ordinal),
                "both branches dial with an expected serial — a race that skips identity is worse than no race");
        }
        True(link.Contains("StartSpeculative", StringComparison.Ordinal), "the speculative branch exists");
        True(link.Contains("CancelSpeculative", StringComparison.Ordinal), "and the loser is cancelled rather than left running");

        // M13c §2.3 inverts M13a's "no tcpip anywhere in the connect path" by ADDING arming as a
        // deliberate capability. Rather than weaken that check, promotion got its own file — so
        // the M13a guarantee still holds verbatim and the new capability is visible here.
        var promotionSource = File.ReadAllText(promotionPath);
        True(promotionSource.Contains("TcpIpVerb = \"tcpip\"", StringComparison.Ordinal),
            "promotion to the fixed port lives in HotspotPromotion.cs (M13c §2.3)");
        True(promotionSource.Contains("\"-s\"", StringComparison.Ordinal),
            "promotion always names the device with -s — an unqualified tcpip could hit another phone");
        True(link.Contains("promotion.PromoteAsync", StringComparison.Ordinal),
            "and it is layered on AFTER a verified link, never used to establish one");
    }
}

// The trigger is wiring, not a pure function, so its EXISTENCE is asserted against the
// production source (GUIDE.md §4.1's second pattern). The RULES it applies are proved by calling
// HotspotSpeculativeTrigger directly, above — this only proves the service actually asks.
Console.WriteLine("--- source-text check: StartSpeculative has a trigger and it is guarded (M13d §1.4/§1.5)");
{
    var repoRoot2 = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot2, "DESKTOP")) && Directory.GetParent(repoRoot2) != null)
    {
        repoRoot2 = Directory.GetParent(repoRoot2)!.FullName;
    }
    var linkPath2 = Path.Combine(repoRoot2, "DESKTOP", "Linc.Desktop", "Services", "HotspotLinkService.cs");
    if (!File.Exists(linkPath2))
    {
        failures.Add($"Missing production file: {linkPath2}");
        Console.WriteLine($"    FAIL: cannot read {linkPath2}");
    }
    else
    {
        var link2 = File.ReadAllText(linkPath2);

        var attachStart = link2.IndexOf("public void Attach()", StringComparison.Ordinal);
        True(attachStart >= 0, "HotspotLinkService still declares Attach()");
        if (attachStart >= 0)
        {
            var attach = link2[attachStart..(link2.IndexOf("private void OnControlArrived", StringComparison.Ordinal) is var e && e > attachStart ? e : link2.Length)];
            True(attach.Contains("ControlArrived +=", StringComparison.Ordinal),
                "Attach() subscribes to the control-connection event — this is what stopped StartSpeculative being dead code");
        }

        var arrivedStart = link2.IndexOf("private void OnControlArrived", StringComparison.Ordinal);
        True(arrivedStart >= 0, "there is a control-connection handler");
        if (arrivedStart >= 0)
        {
            var arrived = link2[arrivedStart..];
            var idxGuard = arrived.IndexOf("HotspotSpeculativeTrigger.ShouldFire", StringComparison.Ordinal);
            var idxFire = arrived.IndexOf("StartSpeculative(address)", StringComparison.Ordinal);
            True(idxGuard >= 0, "it consults the pure trigger rather than re-deciding when to fire");
            True(idxFire >= 0, "it really does fire the speculative branch");
            True(idxGuard >= 0 && idxFire >= 0 && idxGuard < idxFire,
                "the guard is consulted BEFORE the speculative branch is fired");
            // It must dial the address the EVENT carried. IConnectionManager.PeerAddress is only
            // populated by AdoptTlsConnectionAsync, which has not run yet at this point — reading
            // it here would find null and the branch would silently never fire, which is the very
            // defect §1.4 exists to fix.
            True(!arrived[..(idxFire > 0 ? idxFire : arrived.Length)]
                    .Contains("connection.PeerAddress", StringComparison.Ordinal),
                "it dials the address the control-connection event carried, not IConnectionManager.PeerAddress");
            True(link2.Contains("private void StartSpeculative(string? peer)", StringComparison.Ordinal),
                "…which is why the branch takes the peer as a parameter");
            // The stream belongs to ConnectionSupervisor, which adopts or disposes it. Reading
            // it here — or worse, disposing it — would silently kill the control connection.
            var handlerEnd = arrived.IndexOf("\n    /// <summary>", StringComparison.Ordinal);
            var handler = handlerEnd > 0 ? arrived[..handlerEnd] : arrived;
            True(!handler.Contains("stream.", StringComparison.Ordinal),
                "the handler never touches the stream — ConnectionSupervisor owns it");
        }

        // M13e §1.1: the announce path must consult ShouldRedial and, when it says yes, disconnect
        // the OLD endpoint before dialling the new one. ConnectAsync only disconnects the endpoint
        // it is about to dial, which on a promotion is a different ip:port — so without this the
        // dead entry stays cached and adb keeps reporting a device that is gone.
        var announceStart = link2.IndexOf("private async Task HandleAnnounceAsync", StringComparison.Ordinal);
        True(announceStart >= 0, "HotspotLinkService still declares HandleAnnounceAsync");
        if (announceStart >= 0)
        {
            var announce = link2[announceStart..];
            var idxRedial = announce.IndexOf("HotspotRedial.ShouldRedial", StringComparison.Ordinal);
            var idxDisconnect = announce.IndexOf("connector.DisconnectAsync", StringComparison.Ordinal);
            var idxAttempt = announce.IndexOf("AttemptAsync(HotspotRaceBranch.Announced", StringComparison.Ordinal);
            True(idxRedial >= 0, "it asks the pure ShouldRedial rule rather than re-deciding when to tear a link down");
            True(idxDisconnect >= 0, "it disconnects the old endpoint on a redial");
            True(idxRedial >= 0 && idxDisconnect >= 0 && idxRedial < idxDisconnect,
                "the decision is made BEFORE anything is disconnected");
            True(idxDisconnect >= 0 && idxAttempt >= 0 && idxDisconnect < idxAttempt,
                "and the old endpoint is disconnected BEFORE the new one is dialled");
            True(announce.Contains("HotspotConnector.BuildEndpoint(chosenAddress, announcement.Port)", StringComparison.Ordinal),
                "the endpoint it compares is built by the production BuildEndpoint from the ANNOUNCED port");
            True(announce.Contains("_race = new HotspotRaceState()", StringComparison.Ordinal),
                "a redial reopens the race, or AttemptAsync would drop it exactly as M13d reported");
        }
        // The announced port has to reach the dial, or every announcement is dialled at 5555
        // whatever it said and ShouldRedial compares two endpoints that can never differ.
        True(link2.Contains("connector.ConnectAsync(address, expectedSerial, ct, port)", StringComparison.Ordinal),
            "the ANNOUNCED port is passed through to the connector, not silently replaced by 5555");

        True(link2.Contains("HotspotArmFallback.ShouldRequest", StringComparison.Ordinal),
            "the adb.arm fallback asks the pure rule whether it is still warranted");
        True(link2.Contains("HotspotArmFallback.Window", StringComparison.Ordinal),
            "it waits the named window rather than a hand-rolled delay");
        True(link2.Contains("RequestArmAsync(HotspotArmFallback.Prefer", StringComparison.Ordinal),
            "and then sends adb.arm asking for the KNOWN port, not a discovered one");
        True(link2.Contains("log.Log(LogLevel.Info, HotspotArmFallback.Reason)", StringComparison.Ordinal),
            "one plain-language line names the situation where the user will hit it");
        // §1.5 is explicit that the sweep stays deferred. If a port scan ever appears in this
        // file, the escape hatch has quietly grown into the thing it was chosen to avoid.
        True(!link2.Contains("for (var port", StringComparison.Ordinal) &&
             !link2.Contains("Enumerable.Range(", StringComparison.Ordinal),
            "no ephemeral port sweep crept in — §1.5 defers it, and this is where it would appear");
    }
}

Console.WriteLine("--- source-text check: the M13f fixes are wired into production (Parts A/C/D)");
{
    var repoRoot3 = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot3, "DESKTOP")) && Directory.GetParent(repoRoot3) != null)
    {
        repoRoot3 = Directory.GetParent(repoRoot3)!.FullName;
    }
    var svcDir = Path.Combine(repoRoot3, "DESKTOP", "Linc.Desktop", "Services");
    var supervisorPath = Path.Combine(svcDir, "ConnectionSupervisor.cs");
    var connectPath = Path.Combine(svcDir, "HotspotConnect.cs");
    var tlsPath = Path.Combine(svcDir, "TlsTransportService.cs");
    if (!File.Exists(supervisorPath) || !File.Exists(connectPath) || !File.Exists(tlsPath))
    {
        failures.Add("Missing a production file for the M13f source-text checks");
        Console.WriteLine("    FAIL: cannot read ConnectionSupervisor.cs / HotspotConnect.cs / TlsTransportService.cs");
    }
    else
    {
        var supervisor = File.ReadAllText(supervisorPath);
        var connect = File.ReadAllText(connectPath);
        var tls = File.ReadAllText(tlsPath);

        // Part A: the paired-USB branch consults TransportRank, and liveness (LastRttMs) decides
        // whether the current link has "answered" — the two together stop a stale USB poll
        // pre-empting a live wireless link.
        True(supervisor.Contains("TransportRank.Choose", StringComparison.Ordinal),
            "the supervisor asks the pure TransportRank rule (M13f §2)");
        True(supervisor.Contains("Answered: currentAnswered &&", StringComparison.Ordinal),
            "a candidate only counts as answered when the current link is live and on that transport");
        True(supervisor.Contains("LastRttMs is not null", StringComparison.Ordinal),
            "liveness is the connection having answered health checks (LastRttMs), not adb's stale 'device' claim");

        // Part B: identity now comes from the phone's own property, through the pure rule.
        True(connect.Contains("shell\", \"getprop\", \"ro.serialno\"", StringComparison.Ordinal),
            "the connect path reads the HARDWARE serial via getprop (M13f §3), not get-serialno");
        True(!connect.Contains("\"get-serialno\"", StringComparison.Ordinal),
            "…and never invokes get-serialno (the doc comment may name it, the code must not call it)");
        True(connect.Contains("HotspotAnnounce.VerifySerial", StringComparison.Ordinal),
            "the compare is the pure VerifySerial rule, exercised above");
        True(connect.Contains("GetStateAsync", StringComparison.Ordinal) ||
             connect.Contains("\"get-state\"", StringComparison.Ordinal),
            "`get-state` survives as the liveness half of the check");

        // Part C: the TLS advert tracks live interfaces, reuses the hotspot classification
        // (virtual last), and republishes on link change — not a snapshot from startup.
        True(tls.Contains("HotspotInterfaces.Enumerate()", StringComparison.Ordinal),
            "the advert enumerates the CURRENT interfaces via the hotspot classifier (M13f §4)");
        True(tls.Contains("iface.IsVirtual", StringComparison.Ordinal),
            "…and ranks virtual/VPN adapters last, so a VPN can no longer out-rank Wi-Fi");
        True(tls.Contains("NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged", StringComparison.Ordinal),
            "a link change re-publishes the advert instead of keeping the startup snapshot");
        True(tls.Contains("AdvertiseCooldown", StringComparison.Ordinal),
            "the republish is cooldown-gated — link changes arrive in bursts");

        // Part D: the connector builds its endpoint through HotspotEndpoint, which never
        // double-appends a port to an address that already is one.
        True(connect.Contains("HotspotEndpoint.Build", StringComparison.Ordinal),
            "the connector's endpoint builder delegates to HotspotEndpoint (M13f §5)");
        True(connect.Contains("BuildEndpoint(address, port)", StringComparison.Ordinal),
            "…and it is the shared Build(address, port) that both the dial and the health loop use");
    }
}

Console.WriteLine("--- M15a A3: an unusable device gets plain language, never adb's word for it");
{
    True(DeviceAdmission.DescribeUnusableState("device", "Pixel 7") is null,
        "a usable device has nothing to report");

    var pending = DeviceAdmission.DescribeUnusableState("unauthorized", "Pixel 7");
    True(pending is not null, "an `unauthorized` device IS reported (it used to be dropped silently)");
    True(pending?.Contains("Pixel 7", StringComparison.Ordinal) == true,
        "…naming the phone the user is holding");
    True(pending?.Contains("Allow", StringComparison.Ordinal) == true,
        "…and telling them the one thing to do: tap Allow");
    True(pending?.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) != true,
        "…without ever showing them adb's word 'unauthorized' (GUARDRAILS: no raw adb text in the UI)");

    True(DeviceAdmission.DescribeUnusableState("offline", null) is not null,
        "an `offline` device is reported too");
    True(DeviceAdmission.DescribeUnusableState("offline", null)?.StartsWith("A phone", StringComparison.Ordinal) == true,
        "…and a phone with no model name still reads as a sentence");
    True(DeviceAdmission.IsAwaitingTrust("unauthorized"),
        "the trust-prompt state is the one flagged as worth waiting on");
    True(!DeviceAdmission.IsAwaitingTrust("device") && !DeviceAdmission.IsAwaitingTrust(null),
        "…and nothing else is");
}

Console.WriteLine("--- M15a A5: one link at a time, made visible instead of silent (D-037 stands)");
{
    var idle = DeviceAdmission.DecideDeviceSwitch(null, "SERIAL-B", LinkActivity.Idle);
    True(idle.Action == DeviceSwitchAction.Connect, "nothing live: connect the offered phone");

    var same = DeviceAdmission.DecideDeviceSwitch("SERIAL-A", "SERIAL-A", LinkActivity.Connected);
    True(same.Action == DeviceSwitchAction.AlreadyConnected, "the phone already live is a no-op");

    var other = DeviceAdmission.DecideDeviceSwitch("SERIAL-A", "SERIAL-B", LinkActivity.Connected);
    True(other.Action == DeviceSwitchAction.SwitchAfterDisconnect,
        "a SECOND phone while one is live is a switch — tear the first down, then connect");
    // The load-bearing one. Returning Connect here is what two concurrent links would look
    // like from this function, and D-037 says Linc does not do that. M15a acceptance 7b breaks
    // exactly this.
    True(other.Action != DeviceSwitchAction.Connect,
        "…and NEVER a plain Connect, which would be two live links at once (D-037)");
    True(other.Message.Contains("one phone at a time", StringComparison.OrdinalIgnoreCase),
        "…and it says why, in the user's words");
    True(other.Message.Contains("SERIAL-A", StringComparison.Ordinal) &&
         other.Message.Contains("SERIAL-B", StringComparison.Ordinal),
        "…naming both phones, so the sentence is actionable");

    var paused = DeviceAdmission.DecideDeviceSwitch("SERIAL-A", "SERIAL-B", LinkActivity.Paused);
    True(paused.Action == DeviceSwitchAction.Refuse, "paused refuses rather than reconnecting behind the user");
    True(paused.Message.Contains("Reconnect", StringComparison.OrdinalIgnoreCase),
        "…and says what to do about it — never a spinner that cannot resolve");

    var busy = DeviceAdmission.DecideDeviceSwitch("SERIAL-A", "SERIAL-B", LinkActivity.Connecting);
    True(busy.Action == DeviceSwitchAction.Refuse, "a connect already in flight refuses");
    True(busy.Message.Length > 0, "…and still says something");

    True(DeviceAdmission.DecideDeviceSwitch("SERIAL-A", "  ", LinkActivity.Connected).Action
            == DeviceSwitchAction.Refuse,
        "an empty request is refused, not connected to");
    True(DeviceAdmission.DecideDeviceSwitch("SERIAL-A", "serial-a", LinkActivity.Connected).Action
            == DeviceSwitchAction.AlreadyConnected,
        "serial comparison ignores case — adb is not consistent about it");
}

Console.WriteLine("--- M15a Part C: a hand-picked transport wins, but only while it answers");
{
    List<TransportCandidate> both =
    [
        new("AdbUsb", 3, Answered: true),
        new("AdbWireless", 2, Answered: true),
    ];
    var auto = TransportRank.ChooseWithOverride(both, null);
    True(auto.Name == "AdbUsb" && !auto.ByHand,
        "no override: the ranked pick, reported as automatic");

    var wireless = TransportRank.ChooseWithOverride(both, "AdbWireless");
    True(wireless.Name == "AdbWireless", "'use wireless now' beats the higher-ranked USB");
    True(wireless.ByHand, "…and the caption can say the user chose it");
    True(!wireless.OverrideDropped, "…with nothing dropped");

    List<TransportCandidate> wirelessDead =
    [
        new("AdbUsb", 3, Answered: true),
        new("AdbWireless", 2, Answered: false),
    ];
    var dropped = TransportRank.ChooseWithOverride(wirelessDead, "AdbWireless");
    True(dropped.Name == "AdbUsb", "a hand-picked transport that stopped answering falls back");
    True(dropped.OverrideDropped, "…and SAYS it fell back, rather than going quiet");
    True(!dropped.ByHand, "…and stops claiming the user chose the link");

    var absent = TransportRank.ChooseWithOverride(both, "DirectTls");
    True(absent.Name == "AdbUsb" && absent.OverrideDropped,
        "an override naming a transport that isn't a candidate falls back and reports it");

    List<TransportCandidate> nothingLive =
    [
        new("AdbUsb", 3, Answered: false),
        new("AdbWireless", 2, Answered: false),
    ];
    True(TransportRank.ChooseWithOverride(nothingLive, "AdbUsb").Name is null,
        "when nothing answers, an override cannot conjure a link");
}

Console.WriteLine("--- M15a source-text check: device-scoped adb calls name their device");
{
    var repoRoot4 = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot4, "DESKTOP")) && Directory.GetParent(repoRoot4) != null)
    {
        repoRoot4 = Directory.GetParent(repoRoot4)!.FullName;
    }
    var svcDir4 = Path.Combine(repoRoot4, "DESKTOP", "Linc.Desktop", "Services");
    var promotionPath = Path.Combine(svcDir4, "HotspotPromotion.cs");
    var connectPath4 = Path.Combine(svcDir4, "HotspotConnect.cs");
    var watcherPath = Path.Combine(svcDir4, "UsbWatcherService.cs");
    var supervisorPath4 = Path.Combine(svcDir4, "ConnectionSupervisor.cs");
    var appPath = Path.Combine(repoRoot4, "DESKTOP", "Linc.Desktop", "App.xaml.cs");
    if (!File.Exists(promotionPath) || !File.Exists(connectPath4) || !File.Exists(watcherPath) ||
        !File.Exists(supervisorPath4) || !File.Exists(appPath))
    {
        failures.Add("Missing a production file for the M15a source-text checks");
        Console.WriteLine("    FAIL: cannot read one of HotspotPromotion/HotspotConnect/UsbWatcherService/ConnectionSupervisor/App");
    }
    else
    {
        var promotion = File.ReadAllText(promotionPath);
        var connect4 = File.ReadAllText(connectPath4);
        var watcher = File.ReadAllText(watcherPath);
        var supervisor4 = File.ReadAllText(supervisorPath4);
        var app = File.ReadAllText(appPath);

        // A2 / acceptance 7a. `adb tcpip` with no `-s` picks whichever device adb feels like,
        // which with two phones attached is an error at best and the wrong phone at worst — the
        // exact failure Part A is about. Break this line and this check must fail.
        True(promotion.Contains("RunAsync(adb, ct, \"-s\", endpoint, TcpIpVerb", StringComparison.Ordinal),
            "HotspotPromotion's tcpip names its device (-s endpoint) — M15a A2");
        True(connect4.Contains("RunAdbAsync(adb, ct, \"-s\", endpoint, \"get-state\")", StringComparison.Ordinal),
            "the connect path's get-state names its device");
        True(connect4.Contains("RunAdbAsync(adb, ct, \"-s\", endpoint, \"shell\", \"getprop\"", StringComparison.Ordinal),
            "…and so does the getprop that establishes identity");

        // A3 in production: the watcher must report a non-Online device, not filter it away.
        True(watcher.Contains("UsbDeviceUnusable?.Invoke", StringComparison.Ordinal),
            "UsbWatcherService reports a device it cannot use (M15a A3)");
        True(watcher.Contains("DeviceAdmission.DescribeUnusableState", StringComparison.Ordinal),
            "…through the pure rule exercised above, not its own copy of the wording");
        True(!watcher.Contains("if (device.State == DeviceState.Online && !HostPortPattern", StringComparison.Ordinal),
            "…and the old single-condition filter that dropped every unauthorized phone is gone");

        // A5 in production: the supervisor asks the pure rule rather than returning in silence.
        True(supervisor4.Contains("DeviceAdmission.DecideDeviceSwitch", StringComparison.Ordinal),
            "the supervisor routes an unknown phone through DecideDeviceSwitch (M15a A5)");
        True(supervisor4.Contains("usbWatcher.UsbDeviceUnusable += OnUsbDeviceUnusable", StringComparison.Ordinal),
            "…and subscribes to the unusable-device report so it can surface it");

        // A4: one place, and it must sit above the ServiceCollection or half of Linc stays on 5037.
        var idxPort = app.IndexOf("AdbServerHost.UseIsolatedServerPort()", StringComparison.Ordinal);
        var idxServices = app.IndexOf("new ServiceCollection()", StringComparison.Ordinal);
        True(idxPort >= 0, "App claims Linc's private ADB server port (M15a A4)");
        True(idxPort >= 0 && idxServices > idxPort,
            "…before the ServiceCollection, because AdbClient captures its endpoint at construction");
    }
}

Console.WriteLine("--- M15c A1/A2: the one-phone limit is a CHOICE stated up front, not a notice after");
{
    // Pure first - these call the production statics, so deleting them fails the build here.
    var label = DeviceAdmission.SwitchActionLabel("Pixel 7", "Galaxy S21");
    True(label == "Disconnect Pixel 7 and connect Galaxy S21",
        "the confirming button names BOTH phones, so the teardown is not a surprise (A1)");
    True(DeviceAdmission.SwitchActionLabel(null, "Galaxy S21") is null,
        "...and there is no switch label when nothing is live - that card is an ordinary offer");
    True(DeviceAdmission.SwitchActionLabel("  ", "Galaxy S21") is null,
        "...blank counts as nothing live, not as a phone named with a space");

    var note = DeviceAdmission.OneAtATimeNotice("Pixel 7");
    True(note == "Linc connects to one phone at a time. Adding another will disconnect Pixel 7.",
        "the standing line says the limit AND what adding another costs (A2)");
    True(DeviceAdmission.OneAtATimeNotice(null) is null,
        "...and says nothing at all when no phone is live, rather than something untrue");

    // The card's body is DecideDeviceSwitch's own sentence - no second copy of the rule (A1).
    var body = DeviceAdmission.DecideDeviceSwitch("Pixel 7", "Galaxy S21", LinkActivity.Connected);
    True(body.Action == DeviceSwitchAction.SwitchAfterDisconnect && body.Message.Contains("one phone at a time"),
        "...and the card's body comes from the same decision the supervisor acts on");
}

Console.WriteLine("--- M15c source-text checks against production (A1/A2/A3 and Part B)");
{
    var root15c = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(root15c, "DESKTOP")) && Directory.GetParent(root15c) != null)
    {
        root15c = Directory.GetParent(root15c)!.FullName;
    }
    string Desktop15c(string folder, string file) =>
        Path.Combine(root15c, "DESKTOP", "Linc.Desktop", folder, file);

    var paths15c = new Dictionary<string, string>
    {
        ["DevicePage.xaml"] = Desktop15c("Views", "DevicePage.xaml"),
        ["SettingsPage.xaml"] = Desktop15c("Views", "SettingsPage.xaml"),
        ["OnboardingView.xaml"] = Desktop15c("Views", "OnboardingView.xaml"),
        ["DeviceViewModel.cs"] = Desktop15c("ViewModels", "DeviceViewModel.cs"),
        ["SettingsViewModel.cs"] = Desktop15c("ViewModels", "SettingsViewModel.cs"),
        ["OnboardingViewModel.cs"] = Desktop15c("ViewModels", "OnboardingViewModel.cs"),
        ["UsbWatcherService.cs"] = Desktop15c("Services", "UsbWatcherService.cs"),
        ["ConnectionSupervisor.cs"] = Desktop15c("Services", "ConnectionSupervisor.cs"),
    };
    var missing15c = paths15c.Where(kv => !File.Exists(kv.Value)).Select(kv => kv.Key).ToList();
    if (missing15c.Count > 0)
    {
        failures.Add("Missing a production file for the M15c source-text checks");
        Console.WriteLine($"    FAIL: cannot read {string.Join(", ", missing15c)}");
    }
    else
    {
        var text15c = paths15c.ToDictionary(kv => kv.Key, kv => File.ReadAllText(kv.Value));

        // The body of a member, so a check lands in the right method instead of anywhere in the
        // file (GUIDE.md 4.4). Crude brace matching is enough for these one-level bodies.
        string Body15c(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0)
            {
                return "";
            }
            var open = source.IndexOf('{', start);
            var arrow = source.IndexOf("=>", start, StringComparison.Ordinal);
            if (arrow >= 0 && (open < 0 || arrow < open))
            {
                // An expression-bodied member: its "body" runs to the semicolon. Braces inside it
                // are property patterns (`is { } x`), not a block, so they are skipped over.
                var nested = 0;
                for (var i = arrow; i < source.Length; i++)
                {
                    if (source[i] == '{') nested++;
                    else if (source[i] == '}') nested--;
                    else if (source[i] == ';' && nested == 0) return source[arrow..i];
                }
                return "";
            }
            if (open < 0)
            {
                return "";
            }
            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}' && --depth == 0)
                {
                    return source[open..(i + 1)];
                }
            }
            return "";
        }

        var devicePage = text15c["DevicePage.xaml"];
        var deviceVm = text15c["DeviceViewModel.cs"];

        // A1. The card used to be a child of the disconnected panel, so it could not appear while
        // a phone was live - the whole bug. It must now be declared BEFORE that panel opens.
        var idxCard = devicePage.IndexOf("ViewModel.ShowUsbDeviceDetected", StringComparison.Ordinal);
        var idxDisconnected = devicePage.IndexOf("ViewModel.ShowDisconnectedArea", StringComparison.Ordinal);
        True(idxCard >= 0 && idxDisconnected >= 0 && idxCard < idxDisconnected,
            "the USB confirmation card is outside the disconnected panel, so it shows over a live link (A1)");
        True(!Body15c(deviceVm, "public bool ShowUsbDeviceDetected").Contains("IsDisconnected", StringComparison.Ordinal) &&
             deviceVm.Contains("public bool ShowUsbDeviceDetected => !PairingActive", StringComparison.Ordinal),
            "...and the view model no longer gates that card on being disconnected (A1)");

        // A1: exactly two actions, and the confirming one is the switch label, not a bare Connect.
        True(devicePage.Contains("Content=\"{x:Bind ViewModel.UsbPrimaryActionText, Mode=OneWay}\"", StringComparison.Ordinal),
            "the card's confirming button shows the switch label (A1)");
        True(deviceVm.Contains("DeviceAdmission.SwitchActionLabel", StringComparison.Ordinal),
            "...built by the pure static exercised above, not a second copy of the wording");
        True(devicePage.Contains("Content=\"Cancel\"", StringComparison.Ordinal) &&
             devicePage.Contains("ViewModel.DismissUsbDeviceCommand", StringComparison.Ordinal),
            "...and the other action is Cancel (A1)");
        True(devicePage.Contains("Text=\"{x:Bind ViewModel.UsbDeviceMessage, Mode=OneWay}\"", StringComparison.Ordinal) &&
             Body15c(deviceVm, "public string UsbDeviceMessage").Contains("DeviceAdmission.DecideDeviceSwitch", StringComparison.Ordinal),
            "...and the card's body is DecideDeviceSwitch's sentence (A1)");

        // A3: Cancel must leave the live link ALONE. Its body may dismiss and decline, nothing else.
        var cancelBody = Body15c(deviceVm, "private void DismissUsbDevice()");
        True(cancelBody.Length > 0, "Cancel's handler exists (A3)");
        True(cancelBody.Contains("_pendingUsbDevice = null", StringComparison.Ordinal),
            "...and it dismisses the card");
        True(cancelBody.Contains("DeclineUsbDevice", StringComparison.Ordinal),
            "...and stops the 3 s poll re-raising it until the cable is replugged (A1)");
        True(!cancelBody.Contains("Disconnect", StringComparison.Ordinal) &&
             !cancelBody.Contains("ConnectUsb", StringComparison.Ordinal) &&
             !cancelBody.Contains("RetargetActiveDevice", StringComparison.Ordinal),
            "...and touches the live link in NO way - Cancel is a no-op on the connection (A3)");

        // A2: the standing line on all three add-a-phone surfaces, from the one pure static.
        foreach (var (view, vm) in new[]
        {
            ("DevicePage.xaml", "DeviceViewModel.cs"),
            ("SettingsPage.xaml", "SettingsViewModel.cs"),
            ("OnboardingView.xaml", "OnboardingViewModel.cs"),
        })
        {
            True(text15c[view].Contains("ViewModel.OneAtATimeText", StringComparison.Ordinal) &&
                 text15c[view].Contains("ViewModel.ShowOneAtATimeNote", StringComparison.Ordinal),
                $"{view} shows the standing one-phone line (A2)");
            True(Body15c(text15c[vm], "public string OneAtATimeText").Contains("DeviceAdmission.OneAtATimeNotice", StringComparison.Ordinal),
                $"...and {vm} builds it from the pure static, not its own wording");
        }

        var watcher15c = text15c["UsbWatcherService.cs"];
        var supervisor15c = text15c["ConnectionSupervisor.cs"];

        // B2/B3: absence from the 3 s poll is a death signal, confirmed over two polls.
        True(watcher15c.Contains("public const int MissesToDeclareGone = 2", StringComparison.Ordinal),
            "a USB serial must be missing from two consecutive polls before it counts as gone (B3)");
        True(watcher15c.Contains("UsbDeviceGone?.Invoke", StringComparison.Ordinal),
            "...and the watcher raises that departure (B2)");
        True(supervisor15c.Contains("usbWatcher.UsbDeviceGone += OnUsbDeviceGone", StringComparison.Ordinal),
            "...and the supervisor consumes it (B2)");

        // B2's whole safety property: this shortcut is for the cable and NOTHING else. A wireless
        // link is not a USB row, so its absence from `adb devices` says nothing about its health.
        var goneBody = Body15c(supervisor15c, "private void OnUsbDeviceGone(string serial)");
        True(goneBody.Length > 0, "the USB-gone handler exists (B2)");
        True(goneBody.Contains("live.Transport != LinkTransport.AdbUsb", StringComparison.Ordinal),
            "...and drops the link ONLY when the live transport is the USB cable (B2)");
        True(goneBody.Contains("live.Serial", StringComparison.Ordinal),
            "...and only when the serial that vanished is the one that is live (B2)");
        True(goneBody.Contains("OnLinkDropped()", StringComparison.Ordinal),
            "...then runs the existing drop path rather than a parallel one (B2)");
        True(goneBody.Contains("log.Log(", StringComparison.Ordinal),
            "...and logs it, so the next cable pull is measurable from the log (B4)");

        // Out of scope, and easy to break by accident while tuning the above.
        True(supervisor15c.Contains("HealthInterval = TimeSpan.FromSeconds(15)", StringComparison.Ordinal),
            "HealthInterval is untouched at 15 s - M15c must not buy speed with battery");
    }
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL CHECKS PASSED");
    return 0;
}
Console.WriteLine($"{failures.Count} FAILURE(S):");
foreach (var f in failures)
{
    Console.WriteLine($"  - {f}");
}
return 1;
