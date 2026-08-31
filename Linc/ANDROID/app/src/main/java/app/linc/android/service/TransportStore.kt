package app.linc.android.service

import android.content.Context

/**
 * Persisted Direct-TLS pairing state (PROTOCOL.md v9, D-022): the desktop's pinned
 * certificate and where its listener lives, plus the "Background connection"
 * (presence) toggle. Lives in the same prefs file as onboarding state.
 */
object TransportStore {

    private const val PREFS = "linc"
    private const val KEY_DESKTOP_CERT = "desktopCertDer"
    private const val KEY_TLS_PORT = "desktopTlsPort"
    private const val KEY_REVERSE_PORT = "desktopReversePort"
    private const val KEY_PRESENCE = "presenceEnabled"

    fun saveExchange(context: Context, certB64: String, tlsPort: Int, reversePort: Int) {
        prefs(context).edit()
            .putString(KEY_DESKTOP_CERT, certB64)
            .putInt(KEY_TLS_PORT, tlsPort)
            .putInt(KEY_REVERSE_PORT, reversePort)
            .apply()
        LogStore.log(LogLevel.INFO, "Paired with the PC for direct connections")
    }

    fun desktopCert(context: Context): String? = prefs(context).getString(KEY_DESKTOP_CERT, null)
    fun tlsPort(context: Context): Int = prefs(context).getInt(KEY_TLS_PORT, 0)
    fun reversePort(context: Context): Int = prefs(context).getInt(KEY_REVERSE_PORT, 0)

    fun presenceEnabled(context: Context): Boolean = prefs(context).getBoolean(KEY_PRESENCE, true)
    fun savePresenceEnabled(context: Context, enabled: Boolean) =
        prefs(context).edit().putBoolean(KEY_PRESENCE, enabled).apply()

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
