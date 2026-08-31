package app.linc.android.service

import java.util.UUID

/**
 * Holds the session token issued to the current control connection (PROTOCOL.md v5).
 * Non-control connections (bulk etc.) must present a token that matches, or they are
 * dropped. The desktop is the only peer, so a single active token is enough: the
 * control connection issues one on handshake and clears it on disconnect.
 */
object SessionRegistry {

    @Volatile
    private var token: String? = null

    /** Issues a fresh token for a newly-handshaked control connection. */
    fun issue(): String = UUID.randomUUID().toString().also { token = it }

    fun isValid(candidate: String?): Boolean = candidate != null && candidate == token

    /** Clears the token if it still matches [issued] — the control connection that owns it is closing. */
    fun clear(issued: String?) {
        if (issued != null && issued == token) {
            token = null
        }
    }
}
