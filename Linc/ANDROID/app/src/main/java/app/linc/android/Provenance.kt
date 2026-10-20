package app.linc.android

/**
 * Who made Linc, stated openly.
 *
 * This is plain authorship information, the same idea as a copyright line in a book. It is shown
 * in the log (the first line of every run, and the header of Settings › Logs › Copy log) and sent
 * to the PC as an optional `author` field in the handshake. That is all it does: nothing here is
 * hidden, encrypted, checked, enforced or sent anywhere else, and the app behaves exactly the same
 * if these values are changed. The desktop app has the same constants in
 * `Linc.Desktop/Provenance.cs`; keep the two identical. See `NOTICE` at the repository root.
 */
object Provenance {
    const val PRODUCT = "Linc"
    const val AUTHOR = "deloweProjects"
    const val SOURCE = "https://github.com/deloweProjects/Linc"
    const val COPYRIGHT = "© 2026 deloweProjects"

    /** The sentence the fingerprint is computed from (UTF-8, no trailing newline). */
    const val STATEMENT = "Linc by deloweProjects — https://github.com/deloweProjects/Linc"

    /**
     * A stable id for this project: the SHA-256 of [STATEMENT]. Anyone can reproduce it:
     *
     *     printf '%s' 'Linc by deloweProjects — https://github.com/deloweProjects/Linc' | sha256sum
     *
     * (PowerShell: `[BitConverter]::ToString([Security.Cryptography.SHA256]::HashData(
     * [Text.Encoding]::UTF8.GetBytes('Linc by deloweProjects — https://github.com/deloweProjects/Linc'))).Replace('-','').ToLower()`)
     */
    const val FINGERPRINT = "93b660d11f6717cfe67f72a92881210977685b4954a673cadf67e2e11358f281"

    /** "Linc 1.0.0.17 by deloweProjects — https://github.com/deloweProjects/Linc". */
    fun banner(version: String): String = "$PRODUCT $version by $AUTHOR — $SOURCE"
}
