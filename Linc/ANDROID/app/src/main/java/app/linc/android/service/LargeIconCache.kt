package app.linc.android.service

import java.security.MessageDigest

/**
 * Small in-memory store for notification large icons (sender avatars), served to the
 * desktop over the bulk channel as kind `largeIcon` (PROTOCOL.md v6). Ids are content
 * hashes, so the desktop can cache per id indefinitely; entries are evicted LRU and on
 * notification removal, in which case a fetch simply returns 0-length ("unavailable").
 */
object LargeIconCache {

    private const val MAX_ENTRIES = 32

    private val lock = Any()
    private val idToPng = object : LinkedHashMap<String, ByteArray>(MAX_ENTRIES, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, ByteArray>) =
            size > MAX_ENTRIES
    }
    private val keyToId = HashMap<String, String>()

    /** Stores [png] for the notification [key]; returns the stable content-hash id. */
    fun put(key: String, png: ByteArray): String {
        val id = MessageDigest.getInstance("SHA-1").digest(png)
            .joinToString("") { "%02x".format(it) }.take(16)
        synchronized(lock) {
            idToPng[id] = png
            keyToId[key] = id
        }
        return id
    }

    fun get(id: String): ByteArray? = synchronized(lock) { idToPng[id] }

    fun remove(key: String) {
        synchronized(lock) {
            val id = keyToId.remove(key) ?: return
            // Another live notification may share the same icon bytes; keep the png
            // unless no key references it anymore.
            if (keyToId.values.none { it == id }) {
                idToPng.remove(id)
            }
        }
    }
}
