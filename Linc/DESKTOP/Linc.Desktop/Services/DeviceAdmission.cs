namespace Linc.Desktop.Services;

/// <summary>
/// The link's activity as the admission rules need to see it. A deliberate copy of the four
/// <see cref="LinkState"/> cases that matter here rather than a reference to that enum: the enum
/// lives in ConnectionSupervisor.cs, which pulls in AdvancedSharpAdbClient and Microsoft.Win32,
/// and this file must stay linkable into a plain net8.0 harness (the D-036 pattern). The mapping
/// is one switch, at the single call site in the supervisor.
/// </summary>
public enum LinkActivity
{
    /// <summary>Nothing is connected and nothing is in flight.</summary>
    Idle,

    /// <summary>A connect is already running.</summary>
    Connecting,

    /// <summary>A phone is live.</summary>
    Connected,

    /// <summary>The user asked to stay disconnected.</summary>
    Paused,
}

/// <summary>What to do with a device that is being offered while another may be live.</summary>
public enum DeviceSwitchAction
{
    /// <summary>Nothing is live — connect the requested device now.</summary>
    Connect,

    /// <summary>A different phone is live: tear that link down first, then connect this one.</summary>
    SwitchAfterDisconnect,

    /// <summary>The requested device is the one already live; there is nothing to do.</summary>
    AlreadyConnected,

    /// <summary>The switch cannot proceed now. <see cref="DeviceSwitchDecision.Message"/> says why.</summary>
    Refuse,
}

/// <summary>One switch decision plus the plain-language sentence that goes with it.</summary>
/// <param name="Action">What the supervisor should do.</param>
/// <param name="Message">
/// User-facing, always plain language and never raw adb text (GUARDRAILS). Empty only for
/// <see cref="DeviceSwitchAction.AlreadyConnected"/>, where there is nothing to say.
/// </param>
public sealed record DeviceSwitchDecision(DeviceSwitchAction Action, string Message);

/// <summary>
/// Admission rules for devices ADB can see (M15a Part A). Pure — plain strings and enums, no
/// sockets, no adb client and no WinUI — so tools\hotspotsim compiles it verbatim and proves the
/// rules with no phone attached, and so the supervisor and the harness share one decision instead
/// of two (GUIDE.md §4.1).
/// </summary>
public static class DeviceAdmission
{
    /// <summary>The `adb devices` state of a phone that is ready to be talked to.</summary>
    public const string OnlineState = "device";

    /// <summary>The `adb devices` state while the trust prompt is pending or was dismissed.</summary>
    public const string UnauthorizedState = "unauthorized";

    /// <summary>
    /// Plain language for a device ADB can see but cannot use yet, or null when
    /// <paramref name="adbState"/> is a usable device. The input is the state word `adb devices`
    /// prints, lower-cased — that is what makes this checkable without an adb client.
    /// </summary>
    public static string? DescribeUnusableState(string? adbState, string? model)
    {
        var name = string.IsNullOrWhiteSpace(model) ? "A phone" : model!.Trim();
        return (adbState ?? "").Trim().ToLowerInvariant() switch
        {
            OnlineState => null,
            UnauthorizedState =>
                $"{name} is asking whether to trust this PC. Unlock the phone and tap Allow " +
                "on the \"Allow USB debugging?\" prompt — Linc will pick it up on its own.",
            "offline" =>
                $"{name} is plugged in but not responding yet. Unplug and replug the cable, " +
                "or turn wireless debugging off and on again on the phone.",
            "" => null, // nothing to say about a device with no reported state
            _ =>
                $"{name} isn't ready to talk to this PC yet. Check the cable, and that USB " +
                "debugging is still switched on in the phone's developer options.",
        };
    }

    /// <summary>
    /// Whether a device in <paramref name="adbState"/> is the trust-prompt case, which is the one
    /// state Linc must keep watching for a flip to <see cref="OnlineState"/> rather than treat as
    /// a dead end (M15a A3).
    /// </summary>
    public static bool IsAwaitingTrust(string? adbState) =>
        string.Equals((adbState ?? "").Trim(), UnauthorizedState, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What to do when <paramref name="requested"/> is offered while <paramref name="current"/>
    /// may be live. D-037 stands — Linc talks to ONE phone at a time — so this never returns a
    /// decision that would leave two links up. What it does do is make the limit visible: a
    /// switch is a clean teardown-then-connect with a sentence saying so, and a refusal always
    /// says what to do instead, so nothing is ever left on a spinner that cannot resolve.
    /// </summary>
    /// <param name="current">Serial or name of the live phone, or null when none is.</param>
    /// <param name="requested">Serial or name of the phone being offered. Required.</param>
    /// <param name="currentState">What the link is doing right now.</param>
    public static DeviceSwitchDecision DecideDeviceSwitch(
        string? current, string requested, LinkActivity currentState)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return new DeviceSwitchDecision(
                DeviceSwitchAction.Refuse, "Linc didn't get a phone to connect to.");
        }
        requested = requested.Trim();

        if (currentState == LinkActivity.Paused)
        {
            return new DeviceSwitchDecision(
                DeviceSwitchAction.Refuse,
                "Linc is disconnected because you asked it to be. Choose Reconnect first, " +
                $"then {requested} can be connected.");
        }
        if (currentState == LinkActivity.Connecting)
        {
            return new DeviceSwitchDecision(
                DeviceSwitchAction.Refuse,
                $"Linc is already connecting to a phone. It'll be ready for {requested} in a moment.");
        }
        if (currentState != LinkActivity.Connected || string.IsNullOrWhiteSpace(current))
        {
            return new DeviceSwitchDecision(
                DeviceSwitchAction.Connect, $"Connecting to {requested}…");
        }

        var live = current!.Trim();
        if (string.Equals(live, requested, StringComparison.OrdinalIgnoreCase))
        {
            return new DeviceSwitchDecision(DeviceSwitchAction.AlreadyConnected, "");
        }

        // The whole point of A5: one link, but a switch that says what it is doing rather than
        // hanging. Never Connect while another phone is live — that is the concurrent-link
        // outcome D-037 forbids, and tools\hotspotsim fails if this ever returns Connect here.
        return new DeviceSwitchDecision(
            DeviceSwitchAction.SwitchAfterDisconnect,
            $"Linc talks to one phone at a time. Disconnecting {live} so it can connect to {requested}.");
    }

    /// <summary>
    /// The label for the card's confirming button (M15c A1). It has to name both phones, because
    /// the whole complaint was that the user did not know the first one would be dropped — a
    /// button reading "Connect" is what let that surprise happen. Null when nothing is live: the
    /// card is then an ordinary offer, not a switch, and the caller uses its plain label.
    /// </summary>
    public static string? SwitchActionLabel(string? current, string requested)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }
        return $"Disconnect {current!.Trim()} and connect {requested.Trim()}";
    }

    /// <summary>
    /// The known serial <paramref name="candidate"/> belongs to, or null when it is a phone this
    /// PC has never connected to. With <paramref name="contains"/> the candidate is an mDNS
    /// instance name (<c>adb-&lt;serialno&gt;-XXXXXX</c>), which embeds the serial rather than
    /// being it. This is the one rule for "have we seen this phone before" — USB, Wi-Fi discovery
    /// and the onboarding wizard all ask it, so a returning phone is never offered as new.
    /// </summary>
    public static string? RecogniseKnown(string? candidate, IEnumerable<string> knownSerials, bool contains = false)
    {
        var text = (candidate ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }
        foreach (var serial in knownSerials)
        {
            if (string.IsNullOrWhiteSpace(serial))
            {
                continue;
            }
            var hit = contains
                ? text.Contains(serial.Trim(), StringComparison.OrdinalIgnoreCase)
                : string.Equals(text, serial.Trim(), StringComparison.OrdinalIgnoreCase);
            if (hit)
            {
                return serial.Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Whether a known phone that is NOT the active one may be connected on Linc's own initiative.
    /// Only when nothing is live or in flight: recognising a returning phone must never take the
    /// link off the phone the user is already using (D-037 — that stays the confirmation card).
    /// </summary>
    public static bool MayAdoptKnownPhone(LinkActivity state) => state == LinkActivity.Idle;

    /// <summary>Decodes the stored certificate pins, skipping missing or corrupt ones.</summary>
    public static List<byte[]> PinnedCertificates(IEnumerable<string?> base64Pins)
    {
        var list = new List<byte[]>();
        foreach (var pin in base64Pins)
        {
            if (string.IsNullOrWhiteSpace(pin))
            {
                continue;
            }
            try
            {
                list.Add(Convert.FromBase64String(pin));
            }
            catch (FormatException)
            {
                // A corrupt pin only disqualifies that one phone; the others still authenticate.
            }
        }
        return list;
    }

    /// <summary>True when <paramref name="raw"/> is byte-for-byte one of <paramref name="pins"/>.</summary>
    public static bool MatchesAny(byte[] raw, IEnumerable<byte[]> pins) =>
        pins.Any(pin => raw.AsSpan().SequenceEqual(pin));

    /// <summary>
    /// The standing line shown wherever a phone can be added while one is already connected
    /// (M15c A2) — stated BEFORE the collision, not after it. Null when nothing is live, so the
    /// note simply is not there rather than saying something untrue. Pure text: no state machine,
    /// which is the point.
    /// </summary>
    public static string? OneAtATimeNotice(string? current) =>
        string.IsNullOrWhiteSpace(current)
            ? null
            : $"Linc connects to one phone at a time. Adding another will disconnect {current!.Trim()}.";
}
