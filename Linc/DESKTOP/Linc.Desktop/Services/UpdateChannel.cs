namespace Linc.Desktop.Services;

/// <summary>
/// M19 C1 — the update channel, <b>baked in at build time, not a user setting</b>.
///
/// <para>Linc has no backend. The project's own GitHub repository is the backend: the desktop and
/// the phone both GET <see cref="ManifestUrl"/>, a static file tracked in this very repo at
/// <c>Releases/update.json</c>. <b>Editing that one file and committing it is the whole release
/// mechanism</b> — raising its <c>latest</c> offers an optional update, and raising its
/// <c>minimumSupported</c> turns that into a forced one for every build below it.</para>
///
/// <para>The Android twin of this constant is <c>UpdateSettings.MANIFEST_URL</c>. If you change one
/// you must change the other; a mismatch would silently split the two apps onto different channels.</para>
///
/// <para>This replaces M17b B2's user-entered manifest URL. The on/off switch is now
/// <c>IDeviceRegistry.UpdatesEnabled</c> ("Keep Linc up to date", default ON), and the
/// <see cref="UpdateDecision"/> rule it feeds is unchanged.</para>
/// </summary>
public static class UpdateChannel
{
    /// <summary>
    /// The raw-content URL of <c>Releases/update.json</c> on <c>main</c>. Raw githubusercontent
    /// rather than the GitHub API: no token, no rate limit, no JSON envelope to unwrap.
    /// </summary>
    public const string ManifestUrl =
        "https://raw.githubusercontent.com/deloweProjects/Linc/main/Releases/update.json";

    /// <summary>Where a user is sent to update by hand when they have turned checking off.</summary>
    public const string ReleasesUrl = "https://github.com/deloweProjects/Linc/releases";
}
