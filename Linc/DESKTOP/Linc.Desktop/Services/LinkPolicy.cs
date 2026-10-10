namespace Linc.Desktop.Services;

public enum LinkTransport { AdbUsb, AdbWireless, DirectTls }

/// <summary>
/// Something happened that may mean the phone is reachable now (D-081). Every trigger the
/// supervisor reacts to is one of these, and all of them go through one nudge path.
/// </summary>
public enum LinkSignal
{
    CableIn,
    CableOut,
    NetworkChanged,
    Resume,
    BleSighting,
    PhoneDialledIn,
    QuickShareSighting,
    LinkDropped,
    ProbeFailing,
    PreferenceChanged,
    UserAsked,
    AppStart,
}

/// <summary>
/// The pure rules behind the PC's connection state machine (D-081, Vibe/agent/design/
/// connection.md §2): which transport outranks which, when to engage, how long to wait after a
/// failure, when to forget an endpoint, and which signals are strong enough to cut a wait short.
/// No I/O and no clock of its own, so <c>tools/linksim</c> tests every rule directly.
/// </summary>
public static class LinkPolicy
{
    /// <summary>
    /// USB above Wi-Fi above nothing. Wireless ADB and Direct TLS are both Wi-Fi and rank the
    /// same: whichever answered first carries the link, and the two never swap on their own —
    /// that swap was a visible blip for no gain (connection.md §1.5 item 8).
    /// </summary>
    public static int Rank(LinkTransport transport) => transport switch
    {
        LinkTransport.AdbUsb => 2,
        LinkTransport.AdbWireless or LinkTransport.DirectTls => 1,
        _ => 0,
    };

    /// <summary>
    /// Should a connect to <paramref name="candidate"/> start now? Idle: yes. Connected: only
    /// to a strictly better transport (a handoff, made before the old link is broken). Never
    /// while a connect is in flight or the user paused, never to a transport the phone's
    /// preference forbids, and never away from a transport the user picked by hand.
    /// </summary>
    public static bool ShouldEngage(
        LinkTransport candidate,
        LinkActivity activity,
        LinkTransport? current,
        LinkTransport? manual,
        ConnectionPreference preference)
    {
        if (!IsAllowed(candidate, preference))
        {
            return false;
        }
        if (manual is { } pinned && candidate != pinned)
        {
            return false;
        }
        return activity switch
        {
            LinkActivity.Idle => true,
            LinkActivity.Connected => current is { } live && Rank(candidate) > Rank(live),
            _ => false,
        };
    }

    public static bool IsAllowed(LinkTransport transport, ConnectionPreference preference) => preference switch
    {
        ConnectionPreference.UsbOnly => transport == LinkTransport.AdbUsb,
        ConnectionPreference.WirelessOnly => transport == LinkTransport.AdbWireless,
        ConnectionPreference.DirectOnly => transport == LinkTransport.DirectTls,
        _ => true,
    };

    /// <summary>
    /// A strong signal is evidence the phone may be reachable right now, so every backoff wait is
    /// cut short. Weak ones (a probe hiccup, a drop we caused) aren't.
    /// </summary>
    public static bool IsStrong(LinkSignal signal) => signal is
        LinkSignal.CableIn or LinkSignal.CableOut or LinkSignal.NetworkChanged or LinkSignal.Resume or
        LinkSignal.BleSighting or LinkSignal.PhoneDialledIn or LinkSignal.QuickShareSighting or
        LinkSignal.PreferenceChanged or LinkSignal.UserAsked or LinkSignal.AppStart;

    /// <summary>Signals that mean a live link may have died under us: probe it now.</summary>
    public static bool ShouldProbeLiveLink(LinkSignal signal) =>
        signal is LinkSignal.NetworkChanged or LinkSignal.Resume;

    /// <summary>How long a better link that just died may take to be replaced before it shows.</summary>
    public static readonly TimeSpan HandoffGrace = TimeSpan.FromSeconds(12);

    /// <summary>
    /// The live link just died. A pulled cable with Wi-Fi possible is handed off silently (the
    /// phone dials in, or wireless ADB is adopted, within <see cref="HandoffGrace"/>); anything
    /// else is a visible drop.
    /// </summary>
    public static LostLink OnLiveLinkLost(LinkSignal why, bool wifiPossible) =>
        why == LinkSignal.CableOut && wifiPossible ? LostLink.HandOff : LostLink.Drop;

    /// <summary>
    /// Whether an attempt shows as Connecting. Only from nothing: beside a live link (or one being
    /// handed off) it is silent, and its failure leaves the live link exactly as it was.
    /// </summary>
    public static bool AttemptIsVisible(LinkActivity activity, bool handingOff) =>
        activity != LinkActivity.Connected && !handingOff;

    /// <summary>
    /// The PC slept if a tick meant for every <paramref name="interval"/> arrives after more than
    /// <paramref name="gapFactor"/> times it. Modern Standby PCs never raise PowerModeChanged to a
    /// desktop app, but the app's timers stop while the PC sleeps, which this sees.
    /// </summary>
    public static bool LooksLikeResume(TimeSpan sinceLastTick, TimeSpan interval, double gapFactor = 4) =>
        sinceLastTick > interval * gapFactor;
}

public enum LostLink { HandOff, Drop }

/// <summary>
/// Per-endpoint retry bookkeeping (D-081): waits of 1, 2, 5, 10, 20, then 30 s; a strong signal
/// cuts every wait short; an endpoint that failed <see cref="ForgetAfter"/> times in a row is
/// stale and is forgotten. Not thread-safe — the supervisor calls it under its gate.
/// </summary>
public sealed class EndpointBackoff
{
    public static readonly int[] WaitSeconds = [1, 2, 5, 10, 20, 30];
    public const int ForgetAfter = 5;

    private readonly Dictionary<string, (int Failures, DateTime NextAttemptUtc)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsWaiting(string endpoint, DateTime nowUtc) =>
        _entries.TryGetValue(Key(endpoint), out var entry) && nowUtc < entry.NextAttemptUtc;

    public int Failures(string endpoint) =>
        _entries.TryGetValue(Key(endpoint), out var entry) ? entry.Failures : 0;

    /// <summary>Record a failed attempt. Returns true for the first failure in a streak.</summary>
    public bool Failed(string endpoint, DateTime nowUtc)
    {
        var key = Key(endpoint);
        var failures = _entries.TryGetValue(key, out var entry) ? entry.Failures + 1 : 1;
        var wait = WaitSeconds[Math.Min(failures, WaitSeconds.Length) - 1];
        _entries[key] = (failures, nowUtc.AddSeconds(wait));
        return failures == 1;
    }

    public void Succeeded(string endpoint) => _entries.Remove(Key(endpoint));

    /// <summary>Failed often enough in a row that it is probably an old address: stop trying it.</summary>
    public bool IsStale(string endpoint) => Failures(endpoint) >= ForgetAfter;

    /// <summary>A strong signal: everything may be reachable now, so no one waits — counts stay.</summary>
    public void CutWaits()
    {
        foreach (var key in _entries.Keys.ToList())
        {
            _entries[key] = (_entries[key].Failures, DateTime.MinValue);
        }
    }

    public void Forget(string endpoint) => _entries.Remove(Key(endpoint));

    public void Clear() => _entries.Clear();

    private static string Key(string endpoint) => (endpoint ?? "").Trim();
}
