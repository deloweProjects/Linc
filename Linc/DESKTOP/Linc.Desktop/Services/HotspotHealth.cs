using System.Diagnostics;

namespace Linc.Desktop.Services;

/// <summary>
/// The reconnect backoff for a hotspot ADB link (M13c §3.2). Pure, so the whole schedule is
/// provable without waiting for it.
/// </summary>
public static class HotspotBackoff
{
    /// <summary>First retry delay. Deliberately short — a hotspot link-up is a fast event.</summary>
    public static readonly TimeSpan First = TimeSpan.FromMilliseconds(250);

    /// <summary>Ceiling. Past this, waiting longer buys nothing but a slower recovery.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long to wait after <paramref name="consecutiveFailures"/> failed attempts:
    /// 250 ms, 500 ms, 1 s, 2 s, 4 s, 8 s, then 15 s forever. Zero when nothing has failed.
    /// </summary>
    public static TimeSpan DelayFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }
        // Shift rather than Math.Pow: at 31+ failures a double would still be growing while an
        // int shift would have overflowed into nonsense, and the cap has to hold at every count.
        var doublings = Math.Min(consecutiveFailures - 1, 20);
        var millis = First.TotalMilliseconds * (1L << doublings);
        return millis >= Cap.TotalMilliseconds ? Cap : TimeSpan.FromMilliseconds(millis);
    }

    /// <summary>
    /// Whether the failure count goes back to zero. A verified success obviously resets it; so
    /// does a NEW generation, because a fresh link event means the conditions that were failing
    /// have genuinely changed and the next attempt deserves to be eager rather than penalised
    /// for the old topology's failures (M13c §3.2).
    /// </summary>
    public static bool ShouldReset(bool verifiedSuccess, int incomingGen, int lastGenSeen) =>
        verifiedSuccess || incomingGen > lastGenSeen;

    /// <summary>`adb devices` poll interval. M13c §3.2 asks for 3-5 s; 4 s is the middle.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);
}

/// <summary>
/// When a control connection arriving over a link should fire the speculative branch (M13d
/// §1.4). M13c built and proved the race but nothing called it; this is the trigger, kept pure
/// so <c>tools\hotspotsim</c> proves both guards without a socket or an ADB server.
/// </summary>
public static class HotspotSpeculativeTrigger
{
    /// <summary>
    /// "No speculative attempt has been fired yet." Deliberately below <see
    /// cref="HotspotGeneration.NothingSeen"/>, because a control connection can arrive before any
    /// announcement has been seen and that very first attempt is the one worth racing.
    /// </summary>
    public const int NeverFired = int.MinValue;

    /// <summary>
    /// Whether a control connection from <paramref name="peer"/> should fire
    /// <c>StartSpeculative()</c>.
    /// <para>
    /// Three conditions, all required: the peer has to be hotspot-shaped (same-subnet with one of
    /// our own interfaces — a foreign peer is exactly the address that costs a full connect
    /// timeout); this generation must not already have fired one, because the phone re-dialling
    /// its control socket inside one epoch is routine and each extra fire is another
    /// disconnect/connect cycle; and there must be no VERIFIED link to that peer already, because
    /// racing something that has already been won is pure cost.
    /// </para>
    /// </summary>
    public static bool ShouldFire(
        string? peer,
        IReadOnlyList<HotspotInterface>? myInterfaces,
        int gen,
        int lastFiredGen,
        string? linkedEndpoint)
    {
        if (HasVerifiedLinkTo(linkedEndpoint, peer))
        {
            return false;
        }
        if (gen <= lastFiredGen)
        {
            return false;
        }
        return HotspotAddress.IsHotspotShaped(peer, myInterfaces);
    }

    /// <summary>
    /// True when <paramref name="linkedEndpoint"/> — the <c>ip:port</c> a race has already won —
    /// names the same host as <paramref name="peer"/>. Compared on the host alone: the port adbd
    /// answers on is not the port the control socket came from, so a port comparison would never
    /// match and the guard would never fire.
    /// </summary>
    public static bool HasVerifiedLinkTo(string? linkedEndpoint, string? peer)
    {
        var endpoint = (linkedEndpoint ?? "").Trim();
        var host = (peer ?? "").Trim();
        if (endpoint.Length == 0 || host.Length == 0)
        {
            return false;
        }
        var separator = endpoint.LastIndexOf(':');
        var endpointHost = separator > 0 ? endpoint[..separator] : endpoint;
        return string.Equals(endpointHost.Trim('[', ']'), host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The escape hatch for the one gap M13d §1.5 leaves deliberately unbuilt: a phone whose adbd
/// port cannot be read from the system properties AND whose NsdManager lookup fails, on a first
/// connection, before any promotion. Sweeping the ephemeral range for it measured 44–175 s
/// on-device, so instead the desktop asks the phone to arm legacy tcpip on a KNOWN port, which
/// sidesteps port discovery entirely.
/// </summary>
public static class HotspotArmFallback
{
    /// <summary>
    /// How long to wait for a usable announcement before asking. Five seconds: the phone
    /// announces as soon as it has proved the port with a loopback connect, which is a
    /// sub-second path, so five is comfortably longer than the healthy case and short enough
    /// that a user has not yet decided the feature is broken — and two orders of magnitude
    /// below the sweep it stands in for.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>What the <c>adb.arm</c> asks for: a known port rather than a discovered one.</summary>
    public const string Prefer = HotspotAnnounce.ModeTcpIp;

    /// <summary>
    /// Whether the fallback is still warranted once <see cref="Window"/> has elapsed. Both
    /// conditions matter: an announcement that arrived makes the ask pointless, and a link that
    /// came up some other way (the speculative branch) makes it actively unwanted.
    /// </summary>
    public static bool ShouldRequest(bool usableAnnouncementSeen, string? linkedEndpoint) =>
        !usableAnnouncementSeen && string.IsNullOrWhiteSpace(linkedEndpoint);

    /// <summary>
    /// The one plain-language line this path logs, naming the situation rather than the
    /// mechanism — this is the gap a user actually hits, and the log is where they will hit it.
    /// </summary>
    public static string Reason =>
        $"The phone hasn't said which port wireless debugging is listening on within " +
        $"{Window.TotalSeconds:0} seconds, so Linc is asking it to use the standard port instead. " +
        "Some phones don't expose that port to an app at all, and hunting for it takes minutes.";
}

/// <summary>
/// Whether an announcement that arrives while a link is ALREADY up should tear it down and dial
/// somewhere else (PROTOCOL.md v18, M13e §1.1).
/// <para>
/// This is the second half of M13d §1.1. Admitting a same-<c>gen</c> re-announce at the gate was
/// necessary but not sufficient: the race from that epoch is already settled, so the admitted
/// message reached <c>AttemptAsync</c> and was dropped. The case that matters is promotion —
/// <c>adb tcpip 5555</c> rebinds adbd with no link event, so the phone re-announces port 5555
/// under the same epoch while the desktop still holds the ephemeral one.
/// </para>
/// </summary>
public static class HotspotRedial
{
    /// <summary>
    /// The floor between two redials. A redial is a real teardown — <c>adb disconnect</c>, then a
    /// full connect/get-state/get-serialno — so a phone re-announcing in a tight loop would
    /// otherwise thrash the link continuously.
    /// <para>
    /// Three seconds: twice <see cref="HotspotStageTimer.SuspiciouslySlow"/>, so one redial can
    /// never still be running when the next is allowed, and below
    /// <see cref="HotspotBackoff.PollInterval"/> so the health loop stays the authority on a link
    /// that has genuinely died. A legitimate promotion happens once per boot and is never rate
    /// limited in practice; only a flapping phone ever meets this floor.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// True when <paramref name="announcedEndpoint"/> should replace
    /// <paramref name="currentEndpoint"/>.
    /// </summary>
    /// <param name="announcedEndpoint">Where the phone now says adbd is, as an <c>ip:port</c>.</param>
    /// <param name="currentEndpoint">The <c>ip:port</c> a race has already won, or null for none.</param>
    /// <param name="verified">The announcement's <c>verified</c> flag. Never act without it.</param>
    /// <param name="sinceLastRedial">Elapsed time since the last redial; <see cref="TimeSpan.MaxValue"/> when there has been none.</param>
    public static bool ShouldRedial(
        string? announcedEndpoint,
        string? currentEndpoint,
        bool verified,
        TimeSpan sinceLastRedial)
    {
        // PROTOCOL.md v18: "never announce a port you have not just connected to". Tearing a
        // WORKING link down on an unproven one would be strictly worse than doing nothing.
        if (!verified)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(announcedEndpoint))
        {
            return false;
        }
        // Nothing is connected, so there is nothing to redial — the ordinary race path owns that
        // case, and answering true here would double-dial it.
        if (string.IsNullOrWhiteSpace(currentEndpoint))
        {
            return false;
        }
        // Identical and already verified: ignore, idempotently. This is the common case — the
        // phone re-announcing the port it is already reachable on — and a reconnect storm here
        // would be indistinguishable from the bug this whole feature exists to avoid.
        if (SameEndpoint(announcedEndpoint, currentEndpoint))
        {
            return false;
        }
        return sinceLastRedial >= MinInterval;
    }

    /// <summary>Endpoint equality as adb sees it: trimmed, case-insensitive.</summary>
    public static bool SameEndpoint(string? left, string? right) =>
        string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Which branch of the speculative race an attempt belongs to (M13c §3.1).</summary>
public enum HotspotRaceBranch
{
    /// <summary>The optimistic guess fired at link-up, before any announcement arrived.</summary>
    Speculative,

    /// <summary>The authoritative attempt driven by the phone's <c>adb.announce</c>.</summary>
    Announced,
}

public enum HotspotRaceClaim
{
    /// <summary>This branch got there first with a verified identity: it owns the link.</summary>
    Won,

    /// <summary>The other branch already settled it. Stand down; do not disconnect its link.</summary>
    LostRaceAlreadySettled,

    /// <summary>
    /// The branch connected but did not prove identity, so it may not win at any speed.
    /// </summary>
    RejectedUnverified,
}

/// <summary>
/// The speculative race (M13c §3.1): fire an optimistic connect at the best guess AND let the
/// authoritative announcement race it; first success wins and the loser is cancelled cleanly.
/// <para>
/// <b>The only rule that actually matters here:</b> a branch that has not verified identity
/// (<c>get-state</c> is <c>device</c> AND <c>get-serialno</c> matches) can never claim the race,
/// however fast it was. A race that skips identity checking is worse than no race — it turns
/// "something answered on 5555" into "the phone is connected", quickly and confidently.
/// </para>
/// </summary>
public sealed class HotspotRaceState
{
    private int _settled;

    public bool IsSettled => Volatile.Read(ref _settled) != 0;

    /// <summary>Which branch won, or null while the race is still open.</summary>
    public HotspotRaceBranch? Winner { get; private set; }

    /// <summary>
    /// Claims the race for <paramref name="branch"/>. Exactly one caller can ever receive
    /// <see cref="HotspotRaceClaim.Won"/>; every later one is told to stand down.
    /// </summary>
    public HotspotRaceClaim TryClaim(HotspotRaceBranch branch, bool identityVerified)
    {
        if (!identityVerified)
        {
            return HotspotRaceClaim.RejectedUnverified;
        }
        if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0)
        {
            return HotspotRaceClaim.LostRaceAlreadySettled;
        }
        Winner = branch;
        return HotspotRaceClaim.Won;
    }
}

/// <summary>
/// Per-stage timings for one link-up (M13c §3.4). The target is ~150-300 ms end to end;
/// anything past ~1.5 s means something is accidentally scanning, and only per-stage numbers
/// say which stage it is. One log line, so it is greppable rather than scattered.
/// </summary>
public sealed class HotspotStageTimer
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(string Stage, long Ms)> _stages = [];
    private long _lastMs;

    /// <summary>Anything past this is not a link-up, it is a search. Named so the log can say so.</summary>
    public static readonly TimeSpan SuspiciouslySlow = TimeSpan.FromMilliseconds(1500);

    /// <summary>Records the time since the previous mark under <paramref name="stage"/>.</summary>
    public void Mark(string stage)
    {
        var now = _clock.ElapsedMilliseconds;
        _stages.Add((stage, now - _lastMs));
        _lastMs = now;
    }

    public long TotalMs => _clock.ElapsedMilliseconds;

    public bool IsSuspiciouslySlow => TotalMs > SuspiciouslySlow.TotalMilliseconds;

    /// <summary>`stage 12 ms, stage 130 ms, ... = 210 ms total` — plus a nudge when it is slow.</summary>
    public string Format()
    {
        var parts = string.Join(", ", _stages.Select(s => $"{s.Stage} {s.Ms} ms"));
        var line = $"{parts} = {TotalMs} ms total";
        return IsSuspiciouslySlow
            ? line + $" (over {SuspiciouslySlow.TotalMilliseconds:0} ms — something is scanning; the stage above with the large number is the one to look at)"
            : line;
    }
}
