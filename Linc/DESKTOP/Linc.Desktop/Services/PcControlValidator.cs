namespace Linc.Desktop.Services;

/// <summary>
/// Pure structural validation for `pc.control` (v17, M4b, D-042): no P/Invoke, no COM, no I/O —
/// nothing in this class can reach a real power/volume/brightness call. That is deliberate and
/// load-bearing: this is what <c>tools\pccontrolsim</c> exercises directly, and what the two
/// negative proofs break, so the safety gate has to be provable without ever touching Win32.
///
/// <para>This only catches what the phone could have gotten wrong structurally — an unknown
/// action, a missing/out-of-range <c>level</c>, or a destructive action without <c>confirm:
/// true</c>. It deliberately does NOT decide <c>unsupported</c> (no WMI/DDC-CI brightness
/// instance, sleep/shutdown disabled by policy) or <c>denied</c> (the OS refused) — both of
/// those can only be known by actually attempting the action, which is
/// <see cref="PcControlService"/>'s job, never this class's.</para>
/// </summary>
public static class PcControlValidator
{
    /// <summary>`shutdown` and `restart` require `confirm: true` on the wire (PROTOCOL.md v17,
    /// "Confirmation is enforced on the wire, not only in the UI") — never trust the phone's own
    /// confirmation dialog alone.</summary>
    public static readonly IReadOnlySet<string> DestructiveActions =
        new HashSet<string> { "shutdown", "restart" };

    private static readonly IReadOnlySet<string> KnownActions = new HashSet<string>
    {
        "lock", "sleep", "shutdown", "restart",
        "volume.up", "volume.down", "volume.set", "volume.mute",
        "brightness.set",
    };

    private static readonly IReadOnlySet<string> LevelRequiredActions =
        new HashSet<string> { "volume.set", "brightness.set" };

    /// <summary>
    /// Validates one `pc.control` request structurally. Returns null when valid, otherwise one
    /// of the wire's error codes: <c>"needs-confirm"</c> or <c>"internal"</c> (never
    /// <c>"unsupported"</c>/<c>"denied"</c> — see the class doc).
    /// </summary>
    public static string? ValidateControl(string? action, int? level, int? step, bool? on, bool? confirm)
    {
        if (action is null || !KnownActions.Contains(action))
        {
            return "internal";
        }

        if (DestructiveActions.Contains(action) && confirm != true)
        {
            return "needs-confirm";
        }

        if (LevelRequiredActions.Contains(action) && (level is null || level < 0 || level > 100))
        {
            return "internal";
        }

        return null;
    }
}
