namespace Linc.Desktop.Services;

/// <summary>Which player the phone's PC-media widget is showing — and therefore which one
/// `pc.media.control` has to reach.</summary>
public enum PcMediaTarget
{
    /// <summary>Nothing is registered anywhere; the phone gets `none: true`.</summary>
    None,
    /// <summary>A Windows SMTC session (browser video, PotPlayer, Spotify...).</summary>
    Smtc,
    /// <summary>A Winamp-API player (AIMP and friends) that never registers with SMTC.</summary>
    Winamp,
}

/// <summary>
/// The pure part of <see cref="PcMediaService"/>: picking the player the phone sees. Kept free of
/// WinRT types so a harness can link it and prove the rule with no media playing (D-036 pattern);
/// the caller reduces its live sessions to the four booleans below.
/// </summary>
public static class PcMediaRouting
{
    /// <summary>
    /// Picks the player to publish AND to control. Both used to be decided separately, which is
    /// how M16 Part A's defect arose: publishing showed a paused SMTC session while control still
    /// required <c>IsPlaying</c>, so a paused player could be paused but never resumed. One rule,
    /// one answer.
    /// </summary>
    /// <remarks>
    /// A Winamp-API player that is actually making sound outranks an SMTC session that is not, so
    /// the phone never shows a stale track while music plays. Otherwise the SMTC session wins
    /// whatever its playback state — a paused player is still a usable, reachable player.
    /// </remarks>
    public static PcMediaTarget PickTarget(
        bool hasSmtcSession, bool smtcPlaying, bool hasWinamp, bool winampPlaying)
    {
        if ((!hasSmtcSession || !smtcPlaying) && hasWinamp && winampPlaying)
        {
            return PcMediaTarget.Winamp;
        }
        if (!hasSmtcSession)
        {
            return hasWinamp ? PcMediaTarget.Winamp : PcMediaTarget.None;
        }
        return PcMediaTarget.Smtc;
    }
}
