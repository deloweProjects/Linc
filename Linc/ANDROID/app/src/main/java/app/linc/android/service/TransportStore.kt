package app.linc.android.service

import android.content.Context
import android.util.Base64
import java.security.MessageDigest
import org.json.JSONArray
import org.json.JSONObject

/**
 * Persisted Direct-TLS pairing state (PROTOCOL.md v9, D-022), one record per PC (D-088): each PC's
 * pinned certificate and where its listener lives, the PC the owner picked (null = whichever PC
 * finds the phone first), plus the "Background connection" (presence) toggle. Lives in the same
 * prefs file as onboarding state.
 *
 * Until D-088 the phone kept one PC, so pairing with a second PC overwrote the first and the two
 * then fought over the phone. A single-PC store from before is migrated into the list on first read.
 */
object TransportStore {

    /** A PC this phone has paired with. [id] is the PC's own `pcId` (see [idOf]). */
    data class KnownPc(
        val id: String,
        val name: String,
        val cert: String,
        val tlsPort: Int,
        val reversePort: Int,
        val lastEndpoint: String?,
        val lastSeen: Long,
    )

    private const val PREFS = "linc"
    private const val KEY_PCS = "knownPcs"
    private const val KEY_SELECTED = "selectedPcId"
    private const val KEY_PRESENCE = "presenceEnabled"

    // The single-PC keys from before D-088, read once for the migration.
    private const val OLD_DESKTOP_CERT = "desktopCertDer"
    private const val OLD_TLS_PORT = "desktopTlsPort"
    private const val OLD_REVERSE_PORT = "desktopReversePort"
    private const val OLD_LAST_ENDPOINT = "lastDirectEndpoint"

    /**
     * The PC's id: the first 16 hex digits of the SHA-256 of its certificate (DER). The PC computes
     * the same from its own certificate and sends it in `hello` and its mDNS advert.
     */
    fun idOf(certB64: String): String {
        val der = Base64.decode(certB64, Base64.NO_WRAP)
        return MessageDigest.getInstance("SHA-256").digest(der)
            .joinToString("") { "%02x".format(it) }.substring(0, 16)
    }

    /** Every known PC, most recently seen first. */
    @Synchronized
    fun pcs(context: Context): List<KnownPc> = load(context).sortedByDescending { it.lastSeen }

    fun pc(context: Context, id: String?): KnownPc? = id?.let { wanted -> pcs(context).firstOrNull { it.id == wanted } }

    /** An older PC (no `pcId` in its hello) is recognised by its name. */
    fun pcNamed(context: Context, name: String?): KnownPc? =
        name?.let { wanted -> pcs(context).firstOrNull { it.name.equals(wanted, ignoreCase = true) } }

    fun hasAnyPc(context: Context): Boolean = pcs(context).isNotEmpty()

    /** The PC the owner picked, or null for "whichever PC finds the phone first". */
    fun selectedPcId(context: Context): String? =
        prefs(context).getString(KEY_SELECTED, null)?.takeIf { id -> pcs(context).any { it.id == id } }

    fun selectPc(context: Context, id: String?) =
        prefs(context).edit().apply { if (id == null) remove(KEY_SELECTED) else putString(KEY_SELECTED, id) }.apply()

    /** The PCs the presence loop may dial: the picked one, or all of them. */
    fun dialTargets(context: Context): List<KnownPc> =
        selectedPcId(context)?.let { listOfNotNull(pc(context, it)) } ?: pcs(context)

    /** A PC handed over its certificate (`tls.exchange`). Adds it, or refreshes it. */
    @Synchronized
    fun saveExchange(context: Context, name: String?, certB64: String, tlsPort: Int, reversePort: Int): KnownPc {
        val id = idOf(certB64)
        val list = load(context)
        val old = list.firstOrNull { it.id == id }
        val pc = KnownPc(
            id = id,
            name = name?.takeIf { it.isNotBlank() } ?: old?.name ?: "PC ${id.take(4)}",
            cert = certB64,
            tlsPort = tlsPort,
            reversePort = reversePort,
            lastEndpoint = old?.lastEndpoint,
            lastSeen = System.currentTimeMillis(),
        )
        save(context, list.filter { it.id != id } + pc)
        LogStore.log(LogLevel.INFO, if (old == null) "Paired with ${pc.name} for direct connections" else "Refreshed the direct-connection pairing with ${pc.name}")
        return pc
    }

    /** The PC connected: remember when, and its current name. */
    @Synchronized
    fun markSeen(context: Context, id: String, name: String?) = update(context, id) {
        it.copy(name = name?.takeIf { n -> n.isNotBlank() } ?: it.name, lastSeen = System.currentTimeMillis())
    }

    /** The address a direct link to this PC last worked on (D-081), tried before waiting for mDNS. */
    @Synchronized
    fun saveLastEndpoint(context: Context, id: String, endpoint: String?) = update(context, id) { it.copy(lastEndpoint = endpoint) }

    @Synchronized
    fun forgetPc(context: Context, id: String) {
        save(context, load(context).filter { it.id != id })
        if (prefs(context).getString(KEY_SELECTED, null) == id) selectPc(context, null)
    }

    fun presenceEnabled(context: Context): Boolean = prefs(context).getBoolean(KEY_PRESENCE, true)
    fun savePresenceEnabled(context: Context, enabled: Boolean) =
        prefs(context).edit().putBoolean(KEY_PRESENCE, enabled).apply()

    private fun update(context: Context, id: String, change: (KnownPc) -> KnownPc) {
        val list = load(context)
        if (list.none { it.id == id }) return
        save(context, list.map { if (it.id == id) change(it) else it })
    }

    private fun load(context: Context): List<KnownPc> {
        val prefs = prefs(context)
        val json = prefs.getString(KEY_PCS, null) ?: return migrate(context)
        return runCatching {
            val array = JSONArray(json)
            (0 until array.length()).map { i ->
                val o = array.getJSONObject(i)
                KnownPc(
                    id = o.getString("id"),
                    name = o.optString("name", "Your PC"),
                    cert = o.getString("cert"),
                    tlsPort = o.optInt("tlsPort"),
                    reversePort = o.optInt("reversePort"),
                    lastEndpoint = o.optString("lastEndpoint").takeIf { it.isNotBlank() },
                    lastSeen = o.optLong("lastSeen"),
                )
            }
        }.getOrDefault(emptyList())
    }

    private fun save(context: Context, list: List<KnownPc>) {
        val array = JSONArray()
        for (pc in list) {
            array.put(JSONObject()
                .put("id", pc.id).put("name", pc.name).put("cert", pc.cert)
                .put("tlsPort", pc.tlsPort).put("reversePort", pc.reversePort)
                .put("lastEndpoint", pc.lastEndpoint ?: "").put("lastSeen", pc.lastSeen))
        }
        prefs(context).edit().putString(KEY_PCS, array.toString()).apply()
    }

    /** The one PC a pre-D-088 phone knew becomes the first entry of the list. */
    private fun migrate(context: Context): List<KnownPc> {
        val prefs = prefs(context)
        val cert = prefs.getString(OLD_DESKTOP_CERT, null)
        val list = if (cert == null) emptyList() else listOf(KnownPc(
            id = runCatching { idOf(cert) }.getOrDefault("unknown"),
            name = "Your PC",
            cert = cert,
            tlsPort = prefs.getInt(OLD_TLS_PORT, 0),
            reversePort = prefs.getInt(OLD_REVERSE_PORT, 0),
            lastEndpoint = prefs.getString(OLD_LAST_ENDPOINT, null),
            lastSeen = System.currentTimeMillis(),
        ))
        save(context, list)
        prefs.edit().remove(OLD_DESKTOP_CERT).remove(OLD_TLS_PORT).remove(OLD_REVERSE_PORT).remove(OLD_LAST_ENDPOINT).apply()
        return list
    }

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
