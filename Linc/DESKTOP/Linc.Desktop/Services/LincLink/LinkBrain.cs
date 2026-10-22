namespace Linc.Desktop.Services;

/// <summary>A place the phone might be reached, before any socket is opened.</summary>
/// <param name="Route">One of <see cref="LinkRoute"/>.</param>
/// <param name="Address">"host:port".</param>
/// <param name="Source">Where the address came from; a fresher source is tried earlier.</param>
public sealed record RouteCandidate(string Route, string Address, CandidateSource Source);

/// <summary>Where a candidate address came from, freshest first.</summary>
public enum CandidateSource
{
    /// <summary>The phone answered a UDP probe or announced itself just now.</summary>
    Beacon,

    /// <summary>The phone said where it is (<c>link.where</c>) on a link that was up.</summary>
    Told,

    /// <summary>Remembered as the last winner on this network.</summary>
    Memory,

    /// <summary>The phone's Wi-Fi hardware address is in the PC's neighbour table.</summary>
    Neighbour,

    /// <summary>The address the last link used.</summary>
    LastPeer,
}

/// <summary>What the PC's status shows about the link (the "two faces", linclink.md §1).</summary>
public enum LinkPresence
{
    NoPhone,
    Searching,

    /// <summary>The Linc app on the phone answered: everything works.</summary>
    Connected,

    /// <summary>ADB reaches the phone but its Linc app isn't answering yet; Linc is installing or
    /// starting it in the background. Shown as connected, never as an error.</summary>
    ConnectedStartingApp,

    Paused,
}

/// <summary>
/// LincLink's decisions, pure (linclink.md §5): backoff, which candidate first, how long a lost
/// transport stays invisible, what the status says. The supervisor and the phone's presence loop
/// do the I/O and ask these. tools/linclinksim proves them; the phone's <c>LinkBrain.kt</c>
/// mirrors the parts it uses.
/// </summary>
public static class LinkBrain
{
    /// <summary>A transport that dies is replaced within this, with nothing shown (a blip, a cable
    /// pulled with Wi-Fi around). Only then is it a drop. The same 12 s as the cable-out handoff
    /// (<c>LinkPolicy.HandoffGrace</c>), which the supervisor uses for both.</summary>
    public static readonly TimeSpan LingerGrace = TimeSpan.FromSeconds(12);

    /// <summary>"Connecting…" appears only when a first connect takes longer than this.</summary>
    public static readonly TimeSpan ConnectingShownAfter = TimeSpan.FromMilliseconds(1500);

    /// <summary>v32 keepalive: a <c>link.ack</c> echo after this long with nothing received.</summary>
    public static readonly TimeSpan KeepaliveIdle = TimeSpan.FromSeconds(4);

    /// <summary>...and the transport is called dead when the echo isn't back within this.</summary>
    public static readonly TimeSpan KeepaliveTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The phone side: a PC that has said nothing for this long (it echoes every 4 s) is gone.</summary>
    public static readonly TimeSpan PeerSilenceLimit = TimeSpan.FromSeconds(12);

    /// <summary>How long the phone keeps a session with no transport, waiting for a resume.</summary>
    public static readonly TimeSpan SessionKeep = TimeSpan.FromSeconds(60);

    /// <summary>The most candidates one race starts (each costs a socket).</summary>
    public const int MaxCandidates = 8;

    public static readonly TimeSpan BackoffBase = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// "Decorrelated jitter": the next wait is random between the base and three times the last
    /// one, capped. Two devices that lost each other at the same moment don't retry in lockstep,
    /// and a strong event (cable, Wi-Fi joined, the phone seen) resets it to nothing anyway.
    /// </summary>
    public static TimeSpan NextBackoff(TimeSpan previous, Random random)
    {
        var low = BackoffBase.TotalMilliseconds;
        var high = Math.Max(low, Math.Min(BackoffCap.TotalMilliseconds, previous.TotalMilliseconds * 3));
        return TimeSpan.FromMilliseconds(low + random.NextDouble() * (high - low));
    }

    /// <summary>
    /// The order to race candidates in: what won last time on this network first (most recent
    /// first, failing ones last), then fresh addresses by how fresh their source is. Duplicates
    /// go; at most <see cref="MaxCandidates"/>.
    /// </summary>
    public static IReadOnlyList<RouteCandidate> Order(IEnumerable<RouteCandidate> fresh, IReadOnlyList<RouteMemo> remembered)
    {
        var ordered = new List<RouteCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(RouteCandidate candidate)
        {
            if (ordered.Count < MaxCandidates && seen.Add($"{candidate.Route}|{candidate.Address}"))
            {
                ordered.Add(candidate);
            }
        }
        var freshList = fresh.ToList();
        // A beacon reply is the phone speaking right now: it beats memory, which may be a lease ago.
        foreach (var beacon in freshList.Where(c => c.Source == CandidateSource.Beacon))
        {
            Add(beacon);
        }
        foreach (var memo in remembered.Where(m => m.FailStreak == 0))
        {
            Add(new RouteCandidate(memo.Route, memo.Address, CandidateSource.Memory));
        }
        foreach (var candidate in freshList.OrderBy(c => c.Source))
        {
            Add(candidate);
        }
        foreach (var memo in remembered.Where(m => m.FailStreak > 0))
        {
            Add(new RouteCandidate(memo.Route, memo.Address, CandidateSource.Memory));
        }
        return ordered;
    }

    /// <summary>
    /// What the status says. ADB up with the app not answering yet is connected — the app is being
    /// repaired in the background — so the owner never sees "can't reach the Linc app" while the
    /// cable plainly works.
    /// </summary>
    public static LinkPresence Presence(bool paired, bool paused, bool linked, bool adbReachesPhone) =>
        !paired ? LinkPresence.NoPhone
        : paused ? LinkPresence.Paused
        : linked ? LinkPresence.Connected
        : adbReachesPhone ? LinkPresence.ConnectedStartingApp
        : LinkPresence.Searching;

    /// <summary>The ADB chip is green when ADB carries the link or is up beside it.</summary>
    public static bool AdbChipLit(bool linkHasAdb, bool adbReachesPhone) => linkHasAdb || adbReachesPhone;

    /// <summary>Waiting for the phone's Linc app after starting it: probe this often...</summary>
    public static readonly TimeSpan AppProbeEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>...for this long, before calling the attempt failed.</summary>
    public static readonly TimeSpan AppProbeBudget = TimeSpan.FromSeconds(15);

    private static readonly int[] RepairSeconds = [1, 2, 5, 10, 30];

    /// <summary>After a failed app repair over ADB, the wait before the next (never gives up while ADB is there).</summary>
    public static TimeSpan RepairDelay(int failures) =>
        TimeSpan.FromSeconds(RepairSeconds[Math.Clamp(failures, 1, RepairSeconds.Length) - 1]);

    /// <summary>Whether a transport that just died should linger (invisible) rather than drop at once.</summary>
    public static bool LingersOnLoss(int negotiatedVersion, bool sessionResumable, bool anotherRoutePossible) =>
        negotiatedVersion >= LinkProtocol.Version && (sessionResumable || anotherRoutePossible);
}

/// <summary>v32 numbers shared by both sides (PROTOCOL.md §v32); the message names are in
/// <c>Protocol/Envelope.cs</c> with the others.</summary>
public static class LinkProtocol
{
    /// <summary>The protocol version that brings LincLink.</summary>
    public const int Version = 32;

    /// <summary>The phone's LincLink TLS listener, which the PC dials.</summary>
    public const int PhonePort = 46002;
}
