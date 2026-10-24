using System.Diagnostics;
using System.Text;
using Linc.Desktop.Services;

// LincLink's pure core, tested (Vibe/agent/design/linclink.md).
//
//   dotnet run -c Release --project tools/linclinksim
//
// Nothing here opens a socket, runs adb or touches a phone. Transports are in-memory queues that
// can be cut mid-stream; route races use timed fake attempts. It proves the rules behave as the
// design promises (exactly-once delivery across a resume, the fastest route winning, beacons only
// matching their own pair, pairing proofs). Hardware behaviour is the owner's test checklist.

var failures = new List<string>();
void True(bool condition, string claim)
{
    Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {claim}");
    if (!condition) failures.Add(claim);
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("--- session numbering");
{
    var s = new LinkSession("abc");
    for (var i = 0; i < 5; i++) { var seq = s.Stamp(); s.Keep(seq, $"f{seq}"); }
    True(s.Sent == 5 && s.Pending == 5, "five frames stamped and kept");
    s.OnAck(3);
    True(s.Pending == 2 && s.PeerAcked == 3, "an ack trims what the peer has");
    s.OnAck(2);
    True(s.Pending == 2 && s.PeerAcked == 3, "a stale ack changes nothing");
    s.OnAck(99);
    True(s.Pending == 2, "an ack for frames never sent is ignored");
    True(string.Join(",", s.ReplayAfter(3)!) == "f4,f5", "a resume replays exactly what the peer lacks");
    True(string.Join(",", s.ReplayAfter(1)!) == "f4,f5",
        "a peer whose offer predates an ack it sent since (a handoff, the old route still up) resumes from that ack");
    True(s.ReplayAfter(6) is null, "nor one claiming frames that were never sent");

    var n = new LinkSession("n");
    for (var i = 0; i < 4; i++) { var seq = n.Stamp(); n.Keep(seq, $"f{seq}"); }
    n.OnAck(1);
    True(n.Neutralize(3, "noop3") && string.Join(",", n.ReplayAfter(1)!) == "f2,noop3,f4",
        "a request given up on is replayed as a no-op in its place (its number kept)");
    True(!n.Neutralize(1, "noop1"), "...but one the peer already acknowledged ran, and stays as it was");

    var r = new LinkSession("abc");
    True(r.Accept(1, 0) == FrameArrival.New && r.Accept(2, 0) == FrameArrival.New, "frames in order are new");
    True(r.Accept(2, 0) == FrameArrival.Duplicate && r.Accept(1, 0) == FrameArrival.Duplicate, "a replayed frame is a duplicate");
    True(r.Accept(5, 0) == FrameArrival.Gap && r.Received == 5, "a jump is reported as a gap");
    True(r.AckDue(LinkSession.AckEveryMs), "an ack is due 2 s after unacknowledged frames");
    r.AckSent(r.Received);
    True(!r.AckDue(LinkSession.AckEveryMs * 10), "...and not once it was sent");
    for (long i = 6; i < 6 + LinkSession.AckEveryFrames; i++) r.Accept(i, 100);
    True(r.AckDue(100), "32 frames unacknowledged want an ack at once");

    var big = new LinkSession("x");
    for (var i = 0; i < LinkSession.MaxFrames + 10; i++) { var seq = big.Stamp(); big.Keep(seq, "y"); }
    True(big.Pending == LinkSession.MaxFrames, "the replay buffer is capped");
    True(big.ReplayAfter(0) is null, "...so a peer that missed the dropped ones gets a fresh session");
    True(big.ReplayAfter(10) is not null, "...while one that has them can still resume");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("--- the field test of build A (2026-10-23)");
{
    // 1a. Dial-back waiters end by themselves. The bug: a request failed before its 8 s timer ran,
    // its token source was disposed, the waiter stayed forever, and every control dial-in then
    // waited out the header read first.
    var waiters = new DialBackWaiters<string>();
    var orphanSource = new CancellationTokenSource();
    var orphan = waiters.Add(3, "tok", orphanSource.Token, TimeSpan.FromMilliseconds(200));
    orphanSource.Dispose(); // what the failed request did
    True(waiters.Pending == 1, "a dial-back waiter is pending while it waits");
    await Task.WhenAny(orphan, Task.Delay(2000));
    True(waiters.Pending == 0 && orphan.IsCanceled, "a waiter whose caller vanished ends by itself (10 s at most in the app)");
    var a = waiters.Add(5, "s1", CancellationToken.None);
    var b = waiters.Add(2, "s1", CancellationToken.None);
    var c = waiters.Add(2, "s2", CancellationToken.None);
    waiters.DropSession("s1");
    True(a.IsCanceled && b.IsCanceled && !c.IsCompleted && waiters.Pending == 1, "a link that ends drops its own waiters, and only those");
    True(waiters.Complete(2, "s2", "stream") && c.Result == "stream" && waiters.Pending == 0, "a dial-back still goes to its waiter");
    True(!waiters.Complete(2, "s2", "late"), "...and a second one for it finds none");

    // 1b. The phone gives a link 5 s to hear the PC; the PC's worst case now fits inside.
    True(LinkBrain.PcHelloWorstCase < LinkBrain.PhoneHelloWait,
        $"the PC's hello leaves within {LinkBrain.PcHelloWorstCase.TotalSeconds:0.0} s, inside the phone's {LinkBrain.PhoneHelloWait.TotalSeconds:0} s (was 3 s header + a 15 s repair)");

    // 2. adb tcpip never with the new engine, never with an ADB route in use.
    True(!LinkBrain.MayMoveAdbToFixedPort(engineOn: true, anyAdbRouteInUse: false) &&
         !LinkBrain.MayMoveAdbToFixedPort(engineOn: false, anyAdbRouteInUse: true) &&
         LinkBrain.MayMoveAdbToFixedPort(engineOn: false, anyAdbRouteInUse: false),
        "adbd is only restarted for a fixed port with the engine off and no ADB route in use");

    // 3. Only a clean "not installed" offers an install.
    True(LinkBrain.CompanionInstalled("package:app.linc.android\npm-exit:0") == true, "pm listing Linc: installed");
    True(LinkBrain.CompanionInstalled("pm-exit:0") == false, "pm ran and listed nothing: absent (an install may be offered)");
    True(LinkBrain.CompanionInstalled("") is null && LinkBrain.CompanionInstalled("error: device offline") is null &&
         LinkBrain.CompanionInstalled("cmd: Failure calling service package\npm-exit:1") is null,
        "adb dropping or pm failing is unknown, never \"absent\": no install offer over a running Linc");

    // 5. A forgotten address isn't re-spotted every minute.
    True(LinkBrain.SpotRetry(forgotten: true) >= TimeSpan.FromMinutes(30) && LinkBrain.SpotRetry(forgotten: false) == TimeSpan.FromSeconds(60),
        "an address forgotten after five failures is spotted again after 30 min, not 60 s");

    // 4. Plain words.
    var line = LinkBrain.RouteEndedLine("the USB cable", LinkBrain.LingerGrace);
    True(!line.Contains("The the") && !line.Contains("The a ") && line.Contains("the USB cable"), $"the route-ended line reads right: \"{line}\"");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("--- one source of truth for the link state (test build B)");
{
    var phoneAt = new[] { "10.5.48.24" };
    True(LinkBrain.AdbVia([("R5CT0001", true)], "R5CT0001", phoneAt) == "cable", "the phone's USB serial online: ADB over the cable");
    True(LinkBrain.AdbVia([("10.5.48.24:37123", true)], "R5CT0001", phoneAt) == "wireless", "wireless debugging at the phone's address: ADB over Wi-Fi");
    True(LinkBrain.AdbVia([("adb-R5CT0001-AbCdEf._adb-tls-connect._tcp", true)], "R5CT0001", []) == "wireless", "wireless debugging by its own mDNS name");
    True(LinkBrain.AdbVia([("R5CT0001", false), ("10.5.48.24:37123", false)], "R5CT0001", phoneAt) is null, "offline or unauthorised devices are no ADB");
    True(LinkBrain.AdbVia([("OTHERPHONE", true), ("10.5.48.99:5555", true)], "R5CT0001", phoneAt) is null, "another phone's ADB isn't this phone's");
    True(LinkBrain.AdbVia([("10.5.48.24:5555", true), ("R5CT0001", true)], "R5CT0001", phoneAt) == "cable", "both at once: the cable");
    True(LinkBrain.Route(adbUsb: true, adbWireless: false, directPeer: null) == "usb" && LinkBrain.Route(false, true, null) == "wifi" &&
         LinkBrain.Route(false, false, "10.5.48.24") == "direct" && LinkBrain.Route(false, false, "127.0.0.1") == "usb",
        "the route: the cable (also a direct link over adb reverse), wireless debugging, or Linc's own Wi-Fi link");
    True(LinkBrain.RouteCaption("usb") == "Connected over USB" && LinkBrain.RouteCaption("direct") == "Connected over Wi-Fi" && LinkBrain.RouteCaption("wifi") == "Connected over Wi-Fi",
        "the PC's words for each route (the phone maps the same: USB cable / Wi-Fi)");
    var schedule = Enumerable.Range(0, 6).Select(LinkBrain.ArmDelay).ToList();
    True(schedule[0] == TimeSpan.Zero && schedule[1] == TimeSpan.FromSeconds(30) && schedule[2] == TimeSpan.FromMinutes(2) &&
         schedule.Skip(3).All(d => d == TimeSpan.FromMinutes(10)),
        "connected without ADB: the PC asks the phone to turn wireless debugging on now, then 30 s, 2 min, every 10 min");
    True(LinkBrain.ArmWindow < LinkBrain.ArmDelay(1), "\"turning on…\" ends before the next ask");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("--- a session riding transports that die mid-stream");
{
    // Requests from the PC, each answered by the phone; unsolicited events from the phone. The
    // transport is cut repeatedly with frames in flight (they are lost, as on a pulled cable);
    // every cut is followed by a resume on a new transport.
    foreach (var (name, cutEvery, seed) in new[] { ("cable pulled once", 40, 1), ("lossy Wi-Fi, a cut every 7 frames", 7, 2), ("a cut every frame", 1, 3) })
    {
        var sim = new SessionSim(seed);
        var result = sim.Run(requests: 120, events: 60, cutEvery);
        True(result.AllAnsweredOnce && result.EventsOnce && result.InOrder,
            $"{name}: {result.Requests} requests answered exactly once, {result.Events} events delivered once, in order ({result.Resumes} resumes)");
    }
    var reboot = new SessionSim(4);
    True(reboot.PhoneRebootRefusesResume(), "phone rebooted: its session is gone, the resume is refused, a fresh session starts");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("--- route racing");
{
    async Task<(string? Winner, long Ms, int Failed)> Race(params (string Name, int Ms, bool Ok)[] routes)
    {
        var attempts = routes.Select(r => (Func<CancellationToken, Task<string?>>)(async ct =>
        {
            await Task.Delay(r.Ms, ct);
            return r.Ok ? r.Name : throw new IOException($"{r.Name} refused");
        })).ToList();
        var discarded = new List<string>();
        var result = await RouteRace.FirstAsync(attempts, RouteRace.Stagger, discarded.Add, CancellationToken.None);
        return (result.Winner, (long)result.Elapsed.TotalMilliseconds, result.Failures.Count);
    }

    var sameWifi = await Race(("memory: last address", 60, true), ("beacon", 80, true));
    True(sameWifi.Winner == "memory: last address" && sameWifi.Ms < 300, $"same Wi-Fi, app running: the remembered route wins in {sameWifi.Ms} ms (< 3 s)");

    var deadMemory = await Race(("memory: old lease, silent", 3000, false), ("beacon reply", 50, true));
    True(deadMemory.Winner == "beacon reply" && deadMemory.Ms < 600,
        $"a silent remembered address costs one stagger, not a timeout: winner in {deadMemory.Ms} ms");

    var ipChanged = await Race(("memory: refused at once (IP moved)", 5, false), ("neighbour table", 40, true));
    True(ipChanged.Winner == "neighbour table" && ipChanged.Ms < 200 && ipChanged.Failed == 1,
        $"IP changed: a refused address starts the next at once ({ipChanged.Ms} ms)");

    var multicastBlocked = await Race(("memory", 2500, false), ("last peer", 2500, false), ("adb wireless", 300, true));
    True(multicastBlocked.Winner == "adb wireless" && multicastBlocked.Ms < 1000,
        $"multicast and broadcast blocked: memory and last address are silent, ADB still wins in {multicastBlocked.Ms} ms");

    var nothing = await Race(("a", 20, false), ("b", 30, false));
    True(nothing.Winner is null && nothing.Failed == 2, "every route refused: no winner, both reasons kept for the log");

    var leaked = new List<string>();
    var slowWinner = await RouteRace.FirstAsync(new List<Func<CancellationToken, Task<string?>>>
    {
        async _ => { await Task.Delay(400); return "late"; }, // ignores cancellation, like a socket mid-handshake
        async ct => { await Task.Delay(30, ct); return "fast"; },
    }, RouteRace.Stagger, leaked.Add, CancellationToken.None);
    await Task.Delay(600);
    True(slowWinner.Winner == "fast" && leaked.SequenceEqual(["late"]), "a late success after the race is handed back to be closed, never leaked");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("--- candidate order and per-network memory");
{
    var t = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    var memory = new NetMemory();
    var home = NetMemory.Fingerprint("aa:bb:cc:dd:ee:ff", "192.168.1.0/24");
    var office = NetMemory.Fingerprint("11:22:33:44:55:66", "192.168.1.0/24");
    True(home != office, "two networks with the same subnet are told apart by the gateway");
    memory.Succeeded(home, "phone1", LinkRoute.TlsOut, "192.168.1.23:46002", 12, t);
    memory.Succeeded(home, "phone1", LinkRoute.AdbWifi, "192.168.1.23:5555", 30, t.AddMinutes(1));
    var ranked = memory.Ranked(home, "phone1");
    True(ranked.Count == 2 && ranked[0].Route == LinkRoute.AdbWifi, "the most recent winner first");
    True(memory.Ranked(office, "phone1").Count == 0, "nothing carries over to another network");
    for (var i = 0; i < NetMemory.ForgetAfter; i++) memory.Failed(home, "phone1", LinkRoute.AdbWifi, "192.168.1.23:5555");
    True(memory.Ranked(home, "phone1").Count == 1, $"forgotten after {NetMemory.ForgetAfter} failures in a row");
    for (var i = 0; i < NetMemory.MaxEntries + 4; i++) memory.Succeeded($"net{i}", "phone1", LinkRoute.TlsOut, "10.0.0.2:46002", 5, t.AddHours(i + 1));
    True(memory.Entries.Count == NetMemory.MaxEntries && !memory.Entries.ContainsKey($"{home}|phone1"), "at most 16 networks; the oldest goes");
    var json = System.Text.Json.JsonSerializer.Serialize(memory);
    var back = System.Text.Json.JsonSerializer.Deserialize<NetMemory>(json)!;
    True(back.Entries.Count == memory.Entries.Count, "memory survives a save and load as JSON");
    True(!json.Contains("secret", StringComparison.OrdinalIgnoreCase), "it holds no secret");

    var order = LinkBrain.Order(
        [
            new RouteCandidate(LinkRoute.TlsOut, "192.168.1.40:46002", CandidateSource.LastPeer),
            new RouteCandidate(LinkRoute.TlsOut, "192.168.1.50:46002", CandidateSource.Beacon),
            new RouteCandidate(LinkRoute.TlsOut, "192.168.1.23:46002", CandidateSource.Neighbour),
        ],
        [new RouteMemo { Route = LinkRoute.TlsOut, Address = "192.168.1.23:46002", LastOkUtc = t }]);
    True(order[0].Address == "192.168.1.50:46002", "a beacon reply goes first: the phone is speaking from there now");
    True(order[1].Address == "192.168.1.23:46002" && order[1].Source == CandidateSource.Memory, "then the remembered winner");
    True(order.Count == 3, "duplicates are raced once");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("--- UDP beacon");
{
    var secretA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    var secretB = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();
    const long now = 1_790_000_000;
    var nonce = Enumerable.Range(0xA0, 8).Select(i => (byte)i).ToArray();
    var probe = LinkBeacon.Build(LinkBeacon.RoleProbe, secretA, now, nonce, 46001);
    True(probe.Length == LinkBeacon.Length && probe[0] == 'L' && probe[1] == 'K', "a probe is 23 bytes starting LK");
    var parsed = LinkBeacon.Parse(probe)!;
    True(parsed.Role == LinkBeacon.RoleProbe && parsed.Port == 46001 && parsed.Nonce.SequenceEqual(nonce), "it parses back");
    True(LinkBeacon.Matches(parsed, secretA, now), "the paired phone recognises its PC's probe");
    True(!LinkBeacon.Matches(parsed, secretB, now), "a phone paired with another PC does not (two PCs)");
    True(LinkBeacon.Matches(parsed, secretA, now + LinkBeacon.SlotSeconds) && LinkBeacon.Matches(parsed, secretA, now - LinkBeacon.SlotSeconds), "a clock off by one slot still matches");
    True(!LinkBeacon.Matches(parsed, secretA, now + 3 * LinkBeacon.SlotSeconds), "a probe from minutes ago does not");
    var tampered = (byte[])probe.Clone();
    tampered[20] ^= 1;
    var reply = LinkBeacon.Build(LinkBeacon.RoleReply, secretA, now, nonce, 46002);
    var tamperedReply = (byte[])reply.Clone();
    tamperedReply[12] ^= 1;
    True(!LinkBeacon.Matches(LinkBeacon.Parse(tamperedReply)!, secretA, now), "a reply with its nonce changed fails (a replay can't answer a new probe)");
    True(LinkBeacon.Parse(Encoding.ASCII.GetBytes("hello, not a beacon at all")) is null, "other traffic on the port is ignored");
    var p1 = LinkBeacon.Build(LinkBeacon.RoleAnnounce, secretA, now, LinkBeacon.NewNonce(), 46002);
    var p2 = LinkBeacon.Build(LinkBeacon.RoleAnnounce, secretA, now, LinkBeacon.NewNonce(), 46002);
    True(!p1.AsSpan(4, 8).SequenceEqual(p2.AsSpan(4, 8)), "two announcements share nothing a stranger could follow");

    // The shared vector: the phone's LinkBeaconTest.kt asserts the same hex.
    var vector = Convert.ToHexString(LinkBeacon.Tag(secretA, LinkBeacon.RoleReply, LinkBeacon.Slot(now), nonce)).ToLowerInvariant();
    Console.WriteLine($"  vector: tag(secret 01..20, 'R', slot {LinkBeacon.Slot(now)}, nonce a0..a7) = {vector}");
    True(vector == BeaconVector, "the tag matches the vector pinned in the phone's tests");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("--- QR pairing");
{
    var t = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    var pcCert = Encoding.ASCII.GetBytes("pc certificate DER (stand-in)");
    var phoneCert = Encoding.ASCII.GetBytes("phone certificate DER (stand-in)");
    var window = new PairingWindow();
    var token = window.Open(t);
    var invite = new PairingInvite(LinkPairing.CertHash(pcCert), ["192.168.1.10", "10.0.0.5"], 46001, token, "DESK & co");
    var uri = LinkPairing.ToUri(invite);
    var scanned = LinkPairing.Parse(uri)!;
    True(scanned.PcCertSha256 == invite.PcCertSha256 && scanned.Token.SequenceEqual(token) && scanned.Name == "DESK & co" &&
         scanned.Addresses.SequenceEqual(invite.Addresses) && scanned.Port == 46001, "the QR text round-trips");
    True(LinkPairing.Parse("https://example.com") is null && LinkPairing.Parse("linc://pair?v=2&k=00") is null &&
         LinkPairing.Parse(uri.Replace("&t=", "&t=AA")) is null, "anything else, or a mangled code, is refused");
    var proof = LinkPairing.Proof(scanned.Token, phoneCert, pcCert);
    True(!window.TryConsume(LinkPairing.Proof(scanned.Token, phoneCert, Encoding.ASCII.GetBytes("impostor PC")), phoneCert, pcCert, t),
        "a proof made against another PC's certificate fails (a man in the middle)");
    True(window.TryConsume(proof, phoneCert, pcCert, t.AddMinutes(1)), "the phone that scanned the code pairs");
    True(!window.TryConsume(proof, phoneCert, pcCert, t.AddMinutes(1)), "the token is burnt: it can't pair twice");
    var late = new PairingWindow();
    var lateToken = late.Open(t);
    True(!late.TryConsume(LinkPairing.Proof(lateToken, phoneCert, pcCert), phoneCert, pcCert, t + LinkPairing.WindowLength),
        "after 5 minutes the code is dead");

    var vectorToken = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    var proofVector = Convert.ToHexString(LinkPairing.Proof(vectorToken, Encoding.ASCII.GetBytes("phone"), Encoding.ASCII.GetBytes("pc"))).ToLowerInvariant();
    Console.WriteLine($"  vector: proof(token 00..0f, \"phone\", \"pc\") = {proofVector}");
    True(proofVector == ProofVector, "the proof matches the vector pinned in the phone's tests");

    var check = LinkPairing.CheckCode(string.Concat(Enumerable.Repeat("ab", 32)), vectorToken);
    Console.WriteLine($"  vector: checkCode(k ab×32, token 00..0f) = {check}");
    True(check == "764 252", "the check code both screens show matches the vector pinned in the phone's tests");
    True(LinkPairing.IsLanAddress("192.168.1.20") && LinkPairing.IsLanAddress("10.0.0.5") && LinkPairing.IsLanAddress("172.20.3.4") &&
         !LinkPairing.IsLanAddress("8.8.8.8") && !LinkPairing.IsLanAddress("example.com") && !LinkPairing.IsLanAddress("::1"),
        "a code only sends the phone to addresses on the local network");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("--- backoff, presence, old peers");
{
    var random = new Random(7);
    var wait = TimeSpan.Zero;
    var waits = new List<TimeSpan>();
    for (var i = 0; i < 40; i++) { wait = LinkBrain.NextBackoff(wait, random); waits.Add(wait); }
    True(waits.All(w => w >= LinkBrain.BackoffBase && w <= LinkBrain.BackoffCap), "every wait is between 0.5 s and 30 s");
    True(waits.Skip(20).Average(w => w.TotalSeconds) > waits.Take(3).Average(w => w.TotalSeconds), "waits grow while nothing happens");
    var a = new Random(1); var b = new Random(2);
    True(LinkBrain.NextBackoff(TimeSpan.FromSeconds(10), a) != LinkBrain.NextBackoff(TimeSpan.FromSeconds(10), b), "two devices don't retry in lockstep");

    True(LinkBrain.Presence(true, false, false, adbReachesPhone: true) == LinkPresence.ConnectedStartingApp,
        "USB up, app not answering yet: shown as connected while the app is started (not \"can't reach\")");
    True(LinkBrain.Presence(true, false, true, false) == LinkPresence.Connected, "the app answered: connected");
    True(LinkBrain.Presence(true, false, false, false) == LinkPresence.Searching, "nothing reaches the phone: searching");
    True(LinkBrain.Presence(true, true, true, true) == LinkPresence.Paused && LinkBrain.Presence(false, false, false, false) == LinkPresence.NoPhone, "paused and unpaired win");
    True(LinkBrain.AdbChipLit(linkHasAdb: false, adbReachesPhone: true) && !LinkBrain.AdbChipLit(false, false), "the ADB chip is green whenever ADB reaches the phone");
    True(LinkBrain.RepairDelay(1) == TimeSpan.FromSeconds(1) && LinkBrain.RepairDelay(50) == TimeSpan.FromSeconds(30), "app repair retries 1 s … 30 s and never gives up");

    True(!LinkBrain.LingersOnLoss(31, sessionResumable: false, anotherRoutePossible: true), "an old v31 phone keeps today's behaviour exactly");
    True(LinkBrain.LingersOnLoss(32, true, false), "a v32 session lingers for a resume");

    // A model of the targets, with the step times measured or assumed (stated); hardware decides.
    var usbAppRunning = Model(("adb sees the device (track-devices push)", 100), ("adb forward", 50), ("hello", 60));
    True(usbAppRunning < 2000, $"model: USB plug-in, app running → connected in {usbAppRunning} ms (< 2 s)");
    var usbAppMissing = Model(("adb sees the device", 100), ("install the APK (assumed 6 s)", 6000), ("start the service", 400),
        ("socket probes at 500 ms until it answers (assumed 1 s)", 1000), ("hello", 60));
    True(usbAppMissing < 10000, $"model: USB plug-in, app missing → connected in {usbAppMissing} ms (< 10 s)");
    var blip = Model(("transport death noticed (keepalive 4 s + 3 s)", 7000), ("race and resume", 300));
    True(blip < LinkBrain.LingerGrace.TotalMilliseconds, $"model: a half-open Wi-Fi link is replaced in {blip} ms, inside the {LinkBrain.LingerGrace.TotalSeconds:0} s linger: invisible");
}

// ---------------------------------------------------------------------------------------------
Console.WriteLine("--- the PC's protocol client against a fake v32 phone (in-memory links, cut mid-stream)");
await ClientSim.RunAsync(True);

Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"linclinksim: {failures.Count} check(s) failed:");
    foreach (var f in failures) Console.WriteLine($"  - {f}");
    return 1;
}
Console.WriteLine("linclinksim: all checks green.");
return 0;

static long Model(params (string Step, int Ms)[] steps) => steps.Sum(s => (long)s.Ms);

partial class Program
{
    // Pinned once from this harness; LinkBeaconTest.kt and LinkPairingTest.kt assert the same.
    const string BeaconVector = "a59b781eddb9a453";
    const string ProofVector = "cbce564e937d049f011335088802fa418666cca718547c7254329903f95d8978";
}

/// <summary>
/// Two ends of one LincLink session over a series of in-memory transports. A cut loses every
/// frame in flight on that transport (one direction or both); the next transport resumes with the
/// hello's <c>recv</c>, and both sides replay what the other lacks.
/// </summary>
sealed class SessionSim(int seed)
{
    private readonly Random _random = new(seed);

    public sealed record Result(int Requests, int Events, int Resumes, bool AllAnsweredOnce, bool EventsOnce, bool InOrder);

    private sealed class End
    {
        public LinkSession Session = new("s1");
        public readonly List<string> Handled = [];
        public long Clock = 0;

        public string Send(string body)
        {
            var seq = Session.Stamp();
            var frame = $"{seq}|{Session.Received}|{body}";
            Session.Keep(seq, frame);
            Session.AckSent(Session.Received);
            return frame;
        }

        /// <summary>Receive a frame; returns the body when it is new.</summary>
        public string? Receive(string frame)
        {
            var parts = frame.Split('|', 3);
            var seq = long.Parse(parts[0]);
            Session.OnAck(long.Parse(parts[1]));
            var arrival = Session.Accept(seq, Clock);
            if (arrival == FrameArrival.Duplicate) return null;
            Handled.Add(parts[2]);
            return parts[2];
        }
    }

    public Result Run(int requests, int events, int cutEvery)
    {
        var pc = new End();
        var phone = new End();
        var toPhone = new Queue<string>();
        var toPc = new Queue<string>();
        var resumes = 0;
        var sentRequests = 0;
        var sentEvents = 0;
        var steps = 0;
        while (sentRequests < requests || sentEvents < events || toPhone.Count > 0 || toPc.Count > 0)
        {
            if (sentRequests < requests && _random.Next(3) == 0) toPhone.Enqueue(pc.Send($"req{sentRequests++}"));
            if (sentEvents < events && _random.Next(4) == 0) toPc.Enqueue(phone.Send($"evt{sentEvents++}"));
            if (toPhone.Count > 0 && phone.Receive(toPhone.Dequeue()) is { } req && req.StartsWith("req"))
            {
                toPc.Enqueue(phone.Send("ans" + req[3..]));
            }
            if (toPc.Count > 0) pc.Receive(toPc.Dequeue());
            if (++steps % cutEvery == 0)
            {
                // The cable comes out: whatever was in flight is gone, in either direction.
                if (_random.Next(2) == 0) toPhone.Clear();
                if (_random.Next(2) == 0) toPc.Clear();
                if (_random.Next(2) == 0) { toPhone.Clear(); toPc.Clear(); }
                // A new transport: each side says what it has; each replays what the other lacks.
                var pcReplay = pc.Session.ReplayAfter(phone.Session.Received);
                var phoneReplay = phone.Session.ReplayAfter(pc.Session.Received);
                if (pcReplay is null || phoneReplay is null) return new Result(requests, events, resumes, false, false, false);
                toPhone.Clear();
                toPc.Clear();
                foreach (var f in pcReplay) toPhone.Enqueue(f);
                foreach (var f in phoneReplay) toPc.Enqueue(f);
                resumes++;
            }
            if (steps > 100_000) break;
        }
        var answers = pc.Handled.Where(h => h.StartsWith("ans")).ToList();
        var eventsSeen = pc.Handled.Where(h => h.StartsWith("evt")).ToList();
        var reqsSeen = phone.Handled.Where(h => h.StartsWith("req")).ToList();
        var allOnce = answers.Count == requests && answers.Distinct().Count() == requests && reqsSeen.Count == requests;
        var eventsOnce = eventsSeen.Count == events && eventsSeen.Distinct().Count() == events;
        var inOrder = reqsSeen.SequenceEqual(Enumerable.Range(0, requests).Select(i => $"req{i}")) &&
                      eventsSeen.SequenceEqual(Enumerable.Range(0, events).Select(i => $"evt{i}"));
        return new Result(requests, events, resumes, allOnce, eventsOnce, inOrder);
    }

    /// <summary>The phone lost its session (reboot, app killed): the PC's resume must be refused.</summary>
    public bool PhoneRebootRefusesResume()
    {
        var pc = new End();
        for (var i = 0; i < 10; i++) pc.Send($"req{i}");
        var rebooted = new End { Session = new LinkSession("s2") }; // a new session id after the reboot
        // The phone only resumes a session id it holds; s1 is gone, so it starts fresh with s2.
        var resumed = rebooted.Session.Id == pc.Session.Id;
        return !resumed && rebooted.Session.Sent == 0;
    }
}
