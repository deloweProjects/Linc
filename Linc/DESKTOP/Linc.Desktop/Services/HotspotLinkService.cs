using System.Net;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

public interface IHotspotLinkService
{
    /// <summary>Begin consuming the phone's <c>adb.announce</c> / <c>adb.down</c> (v18).</summary>
    void Attach();

    /// <summary>Highest <c>gen</c> acted on so far, or <see cref="HotspotGeneration.NothingSeen"/>.</summary>
    int HighestSeenGeneration { get; }

    /// <summary>Ask the phone to arm or re-arm adbd (v18 <c>adb.arm</c>).</summary>
    Task RequestArmAsync(string prefer, CancellationToken ct);

    /// <summary>
    /// Fire the optimistic connect at the best guess without waiting for an announcement
    /// (M13c §3.1). Races whatever announcement arrives; first VERIFIED success wins.
    /// </summary>
    void StartSpeculative();

    /// <summary>The endpoint the race settled on, or null when no hotspot link is up.</summary>
    string? LinkedEndpoint { get; }
}

/// <summary>
/// The desktop half of "instant ADB link-up over a hotspot" (PROTOCOL.md v18, M13b).
/// <para>
/// The phone announces which port <c>adbd</c> is accepting on — having just proved it with a
/// loopback connect — and the desktop already knows WHERE the phone is, because it holds a
/// socket over the same link. So there is no discovery: resolve an address, dial it, prove it
/// is the right phone, and tell the phone what happened.
/// </para>
/// <para>
/// Every admission decision is deferred to <see cref="HotspotAnnounce"/>, which is pure. This
/// class owns only the ordering, the ADB calls and the reply. Nothing here re-implements the
/// generation rule or the <c>verified</c> refusal — that is the point of the split, and
/// <c>tools\hotspotsim</c> asserts by source text that this class asks before it acts.
/// </para>
/// </summary>
public sealed class HotspotLinkService(
    IConnectionManager connection,
    ITlsTransportService tlsTransport,
    IHotspotConnector connector,
    IHotspotPromotion promotion,
    IDeviceRegistry registry,
    ILogService log) : IHotspotLinkService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _subscribed;

    /// <summary>The current race. Replaced whenever a new generation opens a fresh one.</summary>
    private HotspotRaceState _race = new();
    private CancellationTokenSource? _speculativeCts;
    private CancellationTokenSource? _healthCts;
    private CancellationTokenSource? _armFallbackCts;
    private int _consecutiveFailures;
    private int _lastSpeculativeGen = HotspotSpeculativeTrigger.NeverFired;
    private bool _usableAnnouncementSeen;

    /// <summary>When the last redial was started, or null when none has been (M13e §1.1).</summary>
    private DateTime? _lastRedialUtc;

    public int HighestSeenGeneration { get; private set; } = HotspotGeneration.NothingSeen;

    public string? LinkedEndpoint { get; private set; }

    public void Attach()
    {
        if (_subscribed)
        {
            return;
        }
        _subscribed = true;
        connection.CompanionMessageReceived += OnCompanionMessage;
        // M13d §1.4: the trigger the speculative branch never had. This is the control
        // connection being established, which is the earliest moment the phone's address on
        // this link is knowable — M13a widened the signature to carry it precisely because
        // there is no path from the SslStream back down to the socket.
        tlsTransport.ControlArrived += OnControlArrived;
    }

    /// <summary>
    /// A phone dialled in over Direct TLS. <b>The stream is not ours</b> — ConnectionSupervisor
    /// owns it and adopts or disposes it; this handler reads the peer endpoint and nothing else.
    /// </summary>
    private void OnControlArrived(Stream stream, IPEndPoint? peer)
    {
        if (peer is null)
        {
            return; // the platform did not report one; there is nothing to guess from
        }
        var address = peer.Address.ToString();
        var interfaces = HotspotInterfaces.Enumerate();
        if (HotspotSpeculativeTrigger.ShouldFire(
                address, interfaces, HighestSeenGeneration, _lastSpeculativeGen, LinkedEndpoint))
        {
            _lastSpeculativeGen = HighestSeenGeneration;
            log.Log(LogLevel.Info,
                $"The phone connected from {address}, which is on this PC's own network — " +
                "trying wireless debugging there straight away rather than waiting to be told.");
            StartSpeculative(address);
        }

        // M13d §1.5: the narrow gap the deferred port sweep would have covered. Armed off the
        // same event, because "no announcement arrived" is only meaningful from the moment one
        // became due.
        StartArmFallback(address, interfaces);
    }

    public async Task RequestArmAsync(string prefer, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["gen"] = HighestSeenGeneration,
            ["prefer"] = prefer,
        };
        await connection.SendToPhoneAsync(Envelope.Create(MessageType.AdbArm, payload), ct);
    }

    private void OnCompanionMessage(Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.AdbAnnounce:
                _ = HandleAnnounceAsync(envelope);
                break;

            case MessageType.AdbDown:
                HandleDown(envelope);
                break;
        }
    }

    private void HandleDown(Envelope envelope)
    {
        var gen = (int?)envelope.Payload["gen"];
        if (gen is null || !HotspotGeneration.ShouldAccept(gen.Value, HighestSeenGeneration))
        {
            return; // a stale down would tear down a link a NEWER announcement just built
        }
        HighestSeenGeneration = gen.Value;
        var reason = (string?)envelope.Payload["reason"] ?? "unknown";
        log.Log(LogLevel.Info, $"The phone reported wireless debugging went away ({reason}).");
    }

    private async Task HandleAnnounceAsync(Envelope envelope)
    {
        var timer = new HotspotStageTimer();
        var announcement = HotspotAnnounce.Parse(envelope.Payload);

        // The admission gate, in one call, before anything is dialled. Generation first, then
        // `verified`, then structure - all of it in HotspotAnnounce so it is provable.
        var reject = HotspotAnnounce.RejectReason(announcement, HighestSeenGeneration);
        if (reject is not null || announcement is null)
        {
            log.Log(LogLevel.Info, $"Ignoring an ADB announcement from the phone: {reject}.");
            return;
        }
        // The phone DID tell us a port, so M13d §1.5's fallback is no longer the situation it
        // exists for. Recorded before the dial: the ask is pointless whether or not it works.
        _usableAnnouncementSeen = true;
        CancelArmFallback();

        string expectedSerial;
        string chosenAddress;
        string? redialFrom = null;
        string? alreadyLinkedTo = null;
        await _gate.WaitAsync();
        try
        {
            // Re-check under the gate: two announcements can arrive back to back, and one from
            // an OLDER epoch must not be admitted against the generation the first has already
            // raised. Since M13d §1.1 an equal gen passes both checks by design — within an
            // epoch the rule is last-write-wins, and the control socket already orders them.
            if (!HotspotGeneration.ShouldAccept(announcement.Gen, HighestSeenGeneration))
            {
                return;
            }
            // A new generation is a genuinely new link event, so the backoff penalty from the
            // old topology is dropped and the race is reopened (M13c §3.1/§3.2).
            if (HotspotBackoff.ShouldReset(verifiedSuccess: false, announcement.Gen, HighestSeenGeneration))
            {
                _consecutiveFailures = 0;
                _race = new HotspotRaceState();
            }
            HighestSeenGeneration = announcement.Gen;

            // Reachability is not identity, and identity has to be settled BEFORE dialling -
            // there is no point connecting to something we could not have recognised.
            var serial = HotspotAnnounce.ExpectedSerial(registry.PairedSerial, announcement.Serial);
            if (serial is null)
            {
                log.Log(LogLevel.Warn,
                    "Ignoring an ADB announcement: there is no serial to check it against, or the phone " +
                    "announced a different one than this PC is paired with.");
                await AckAsync(announcement.Gen, ok: false, endpoint: "");
                return;
            }

            // M13a's filter, unchanged and still load-bearing: `addrs` is whatever the phone
            // claims about itself, and Android freely advertises mobile-data addresses that are
            // unreachable from the hotspot segment.
            var chosen = HotspotAddress.SelectEndpoint(
                connection.PeerAddress,
                HotspotAnnounce.AddressStrings(announcement),
                HotspotInterfaces.Enumerate());

            if (chosen is null)
            {
                log.Log(LogLevel.Warn,
                    "The phone announced wireless debugging, but none of the addresses it gave are on a " +
                    "network this PC is attached to, so there is nothing to connect to.");
                await AckAsync(announcement.Gen, ok: false, endpoint: "");
                return;
            }
            expectedSerial = serial;
            chosenAddress = chosen;

            // M13e §1.1 — the promotion case. A settled race means a link from this epoch is
            // already up, and AttemptAsync would drop this announcement on the floor. Compare
            // where the phone says adbd is NOW against where we are actually connected: a
            // different endpoint is a promotion and is the entire point of admitting a same-gen
            // re-announce; an identical one is a repeat and must be idempotent.
            if (_race.IsSettled)
            {
                var announcedEndpoint = HotspotConnector.BuildEndpoint(chosenAddress, announcement.Port);
                var sinceLastRedial = _lastRedialUtc is null
                    ? TimeSpan.MaxValue
                    : DateTime.UtcNow - _lastRedialUtc.Value;

                if (!HotspotRedial.ShouldRedial(
                        announcedEndpoint, LinkedEndpoint, announcement.Verified, sinceLastRedial))
                {
                    // Ack outside the gate so the phone stops re-announcing at a link that is
                    // already exactly where it wants it.
                    alreadyLinkedTo = LinkedEndpoint ?? "";
                }
                else
                {
                    redialFrom = LinkedEndpoint;
                    _lastRedialUtc = DateTime.UtcNow;
                    log.Log(LogLevel.Info,
                        $"The phone moved wireless debugging from {redialFrom} to {announcedEndpoint}; " +
                        "reconnecting there.");
                    // Reopen the race and stand the old link down, so AttemptAsync below is
                    // allowed to run at all.
                    _race = new HotspotRaceState();
                    LinkedEndpoint = null;
                    StopHealthLoop();
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (alreadyLinkedTo is not null)
        {
            // Only ack a success when there genuinely is an endpoint. A race can be settled a
            // moment before LinkedEndpoint is assigned, and acking ok:true with an empty endpoint
            // would tell the phone a link exists that this side cannot name. The attempt that is
            // landing acks for this same gen anyway.
            if (alreadyLinkedTo.Length > 0)
            {
                await AckAsync(announcement.Gen, ok: true, alreadyLinkedTo);
            }
            return;
        }

        // "Disconnect and redial": the OLD endpoint needs its own disconnect. ConnectAsync
        // disconnects the endpoint it is about to dial, which is a different ip:port here, so it
        // would leave the stale one cached and adb would keep reporting the dead device.
        if (redialFrom is not null)
        {
            await connector.DisconnectAsync(redialFrom, CancellationToken.None);
        }

        // Outside the gate on purpose: the speculative branch must be able to run at the same
        // time, or there is no race at all (M13c §3.1).
        timer.Mark("resolve");
        await AttemptAsync(HotspotRaceBranch.Announced, chosenAddress, expectedSerial,
            announcement.Gen, announcement.Port, timer, CancellationToken.None);
    }

    /// <summary>
    /// One branch of the race: connect, verify, and claim the link if it got there first with a
    /// verified identity. Both branches run this — there is deliberately no faster path that
    /// skips the checks (M13c §3.1: "a race that skips identity checking is worse than no race").
    /// </summary>
    private async Task AttemptAsync(
        HotspotRaceBranch branch,
        string address,
        string expectedSerial,
        int gen,
        int port,
        HotspotStageTimer timer,
        CancellationToken ct)
    {
        var race = _race;
        try
        {
            if (race.IsSettled)
            {
                return; // the other branch already won; do not disturb its link
            }

            var result = await connector.ConnectAsync(address, expectedSerial, ct, port);
            timer.Mark("adb connect");
            var verdict = HotspotAnnounce.ClassifyState(result.State);

            if (verdict == HotspotStateVerdict.RetryOnce)
            {
                // Stale ADB server state. ConnectAsync always disconnects first, so one more
                // attempt is exactly the "disconnect then retry once" the spec asks for.
                log.Log(LogLevel.Info, $"ADB reported {address} offline - retrying once.");
                result = await connector.ConnectAsync(address, expectedSerial, ct, port);
                verdict = HotspotAnnounce.ClassifyState(result.State);
            }
            timer.Mark("verify");

            if (verdict == HotspotStateVerdict.NeedsPairing)
            {
                // PROTOCOL.md v18: unauthorized on a TLS port means "not paired". Retrying can
                // never fix that, so this path deliberately does not.
                log.Log(LogLevel.Warn,
                    $"The phone answered at {result.Endpoint} but hasn't approved this PC for wireless " +
                    "debugging. Pair them once from the phone's Wireless debugging screen - retrying won't help.");
                RegisterFailure();
                await AckAsync(gen, ok: false, result.Endpoint);
                return;
            }

            // The claim is where identity actually decides the race. Passing anything other
            // than the connector's own IdentityVerified here would defeat the whole point.
            var claim = race.TryClaim(branch, result.IdentityVerified);
            switch (claim)
            {
                case HotspotRaceClaim.RejectedUnverified:
                    log.Log(LogLevel.Warn, result.Failure ?? $"Couldn't bring up an ADB link at {result.Endpoint}.");
                    RegisterFailure();
                    if (branch == HotspotRaceBranch.Announced)
                    {
                        await AckAsync(gen, ok: false, result.Endpoint);
                    }
                    return;

                case HotspotRaceClaim.LostRaceAlreadySettled:
                    // Harmless: the winner already holds an identical adb endpoint.
                    log.Log(LogLevel.Info, $"The {branch} attempt finished after the link was already up; ignoring it.");
                    return;
            }

            // Won.
            CancelSpeculative();
            CancelArmFallback(); // a link is up; asking the phone to re-arm would only break it
            LinkedEndpoint = result.Endpoint;
            _consecutiveFailures = 0;
            log.Log(LogLevel.Info,
                $"Wireless debugging is up at {result.Endpoint} via the {branch.ToString().ToLowerInvariant()} " +
                $"attempt - {timer.Format()}");

            if (branch == HotspotRaceBranch.Announced)
            {
                await AckAsync(gen, ok: true, result.Endpoint);
            }

            // M13c §2.3: an optimisation layered on top, never depended on. Only worth doing
            // when adbd is on some other port; it is already where we want it otherwise.
            if (port != HotspotConnector.AdbTcpPort)
            {
                await promotion.PromoteAsync(result.Endpoint, ct);
            }

            StartHealthLoop(result.Endpoint, expectedSerial);
        }
        catch (OperationCanceledException)
        {
            // The other branch won and cancelled this one. That is the design, not a failure.
        }
        catch (Exception ex) when (ex is LincException or IOException)
        {
            log.Log(LogLevel.Warn, $"A hotspot connect attempt failed: {ex.Message}");
            RegisterFailure();
        }
    }

    /// <summary>
    /// The optimistic branch (M13c §3.1): dial the best guess at link-up rather than waiting
    /// politely for the announcement. It lands whenever adbd is in legacy tcpip mode — which,
    /// after one promotion, is most of the time.
    /// </summary>
    public void StartSpeculative() => StartSpeculative(connection.PeerAddress);

    /// <summary>
    /// The same branch, dialled at a peer address the caller already holds.
    /// <para>
    /// M13d §1.4's trigger runs the instant the control connection arrives, which is BEFORE
    /// ConnectionSupervisor has adopted it and therefore before
    /// <see cref="IConnectionManager.PeerAddress"/> has been populated. Re-reading it there would
    /// find null and the branch would silently never fire — which is the exact defect §1.4
    /// exists to fix, reintroduced one layer down.
    /// </para>
    /// </summary>
    private void StartSpeculative(string? peer)
    {
        var expectedSerial = HotspotAnnounce.ExpectedSerial(registry.PairedSerial, null);
        if (expectedSerial is null)
        {
            return; // nothing paired: there would be no way to recognise what answered
        }
        var guess = HotspotAddress.SelectEndpoint(peer, null, HotspotInterfaces.Enumerate());
        if (guess is null || _race.IsSettled)
        {
            return;
        }
        CancelSpeculative();
        var cts = new CancellationTokenSource();
        _speculativeCts = cts;
        var timer = new HotspotStageTimer();
        _ = AttemptAsync(HotspotRaceBranch.Speculative, guess, expectedSerial,
            HighestSeenGeneration, HotspotConnector.AdbTcpPort, timer, cts.Token);
    }

    /// <summary>
    /// Arms M13d §1.5's escape hatch: if no usable announcement lands within
    /// <see cref="HotspotArmFallback.Window"/>, ask the phone for legacy tcpip on a KNOWN port
    /// (<c>adb.arm { prefer: "tcpip" }</c>) instead of hunting the ephemeral range for it.
    /// <para>
    /// Only armed for a hotspot-shaped peer: on any other link there was never an announcement
    /// due, so its absence says nothing.
    /// </para>
    /// </summary>
    private void StartArmFallback(string address, IReadOnlyList<HotspotInterface> interfaces)
    {
        if (!HotspotAddress.IsHotspotShaped(address, interfaces))
        {
            return;
        }
        CancelArmFallback();
        var cts = new CancellationTokenSource();
        _armFallbackCts = cts;
        _ = ArmFallbackAsync(cts.Token);
    }

    private async Task ArmFallbackAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(HotspotArmFallback.Window, ct);
            if (!HotspotArmFallback.ShouldRequest(_usableAnnouncementSeen, LinkedEndpoint))
            {
                return;
            }
            log.Log(LogLevel.Info, HotspotArmFallback.Reason);
            await RequestArmAsync(HotspotArmFallback.Prefer, ct);
        }
        catch (OperationCanceledException)
        {
            // An announcement arrived, or a link came up, and the ask is no longer wanted.
        }
        catch (Exception ex) when (ex is LincException or IOException)
        {
            // Not silent: a fire-and-forget failure nobody logs is how this codebase has lost
            // whole features before (GUARDRAILS, "a silent catch is a bug factory").
            log.Log(LogLevel.Warn, $"Couldn't ask the phone to use the standard wireless-debugging port: {ex.Message}");
        }
    }

    private void CancelArmFallback()
    {
        var cts = Interlocked.Exchange(ref _armFallbackCts, null);
        if (cts is null)
        {
            return;
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }
        cts.Dispose();
    }

    private void CancelSpeculative()
    {
        var cts = Interlocked.Exchange(ref _speculativeCts, null);
        if (cts is null)
        {
            return;
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by a previous winner.
        }
        cts.Dispose();
    }

    private void RegisterFailure()
    {
        _consecutiveFailures++;
    }

    /// <summary>
    /// Polls `adb devices` and re-resolves when the link stops reporting `device` (M13c §3.2).
    /// The <c>adb disconnect</c> before re-resolving is not optional: the adb server caches a
    /// stale endpoint and then silently refuses the replacement when the same ip:port returns.
    /// </summary>
    private void StartHealthLoop(string endpoint, string expectedSerial)
    {
        StopHealthLoop();
        var cts = new CancellationTokenSource();
        _healthCts = cts;
        _ = HealthLoopAsync(endpoint, expectedSerial, cts.Token);
    }

    private void StopHealthLoop()
    {
        var cts = Interlocked.Exchange(ref _healthCts, null);
        if (cts is null)
        {
            return;
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }
        cts.Dispose();
    }

    private async Task HealthLoopAsync(string endpoint, string expectedSerial, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HotspotBackoff.PollInterval, ct);

                var devices = await connector.ListDevicesAsync(ct);
                var state = devices.Contains(endpoint, StringComparison.OrdinalIgnoreCase)
                    ? await connector.GetStateAsync(endpoint, ct)
                    : "";

                if (HotspotAnnounce.ClassifyState(state) == HotspotStateVerdict.Usable)
                {
                    _consecutiveFailures = 0; // reset on any successful verify
                    continue;
                }

                RegisterFailure();
                var delay = HotspotBackoff.DelayFor(_consecutiveFailures);
                log.Log(LogLevel.Info,
                    $"The wireless debugging link to {endpoint} isn't answering (adb says " +
                    $"'{(state.Length == 0 ? "gone" : state)}'); re-resolving in {delay.TotalMilliseconds:0} ms.");
                await Task.Delay(delay, ct);

                // Disconnect BEFORE re-resolving. Without this the adb server keeps the dead
                // entry and refuses the replacement when the same ip:port comes back.
                await connector.DisconnectAsync(endpoint, ct);

                var result = await connector.ConnectAsync(endpoint, expectedSerial, ct);
                if (result.IdentityVerified)
                {
                    _consecutiveFailures = 0;
                    LinkedEndpoint = result.Endpoint;
                    log.Log(LogLevel.Info, $"Reconnected wireless debugging at {result.Endpoint}.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Loop stopped deliberately.
        }
        catch (Exception ex) when (ex is LincException or IOException)
        {
            log.Log(LogLevel.Warn, $"The wireless debugging health loop stopped: {ex.Message}");
        }
    }

    /// <summary>
    /// Tells the phone the outcome so it stops retrying and logs honestly (v18 <c>adb.ack</c>).
    /// The gen echoed back is the one that was acted on, never the desktop's own counter.
    /// </summary>
    private async Task AckAsync(int gen, bool ok, string endpoint)
    {
        try
        {
            await connection.SendToPhoneAsync(
                Envelope.Create(MessageType.AdbAck, new JsonObject
                {
                    ["gen"] = gen,
                    ["ok"] = ok,
                    ["endpoint"] = endpoint,
                }),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Couldn't send adb.ack: {ex.Message}");
        }
    }
}
