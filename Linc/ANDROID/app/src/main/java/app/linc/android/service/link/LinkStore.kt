package app.linc.android.service.link

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import app.linc.android.service.LogLevel
import app.linc.android.service.LogStore
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import org.json.JSONObject

/**
 * LincLink's settings and secrets on the phone (linclink.md §2, §10):
 *  - the engine switch (Settings › Connection › New connection engine; on by default on the
 *    LincLink test builds; off = protocol v31 and the old paths exactly),
 *  - whether the user stopped Linc by hand (boot and app-open then leave it stopped),
 *  - each PC's link secret, which keys the UDP beacon, encrypted with an AES key that never
 *    leaves the Android KeyStore,
 *  - the per-network memory of where each PC was.
 */
object LinkStore {
    private const val PREFS = "linc"
    private const val KEY_ENGINE = "linkEngine"
    private const val KEY_USER_STOPPED = "userStopped"
    private const val KEY_SECRETS = "linkSecrets"
    private const val KEY_MEMORY = "linkMemory"
    private const val KEY_ALIAS = "linc-link-secrets"

    fun engineOn(context: Context): Boolean = prefs(context).getBoolean(KEY_ENGINE, true)

    fun setEngineOn(context: Context, on: Boolean) = prefs(context).edit().putBoolean(KEY_ENGINE, on).apply()

    /** The highest protocol version to negotiate: 32 with the new engine, 31 without it. */
    fun maxVersion(context: Context, built: Int): Int = if (engineOn(context)) built else minOf(built, LinkBrain.VERSION - 1)

    fun userStopped(context: Context): Boolean = prefs(context).getBoolean(KEY_USER_STOPPED, false)

    /**
     * Linc may start by itself (after a restart, an update, on opening Linc) only once this build
     * has seen the owner run it: a phone updated from a build that didn't record "stopped by hand"
     * stays as the owner left it until Linc is started once (by them, or by the PC over ADB).
     */
    fun mayStartByItself(context: Context): Boolean =
        prefs(context).let { it.contains(KEY_USER_STOPPED) && !it.getBoolean(KEY_USER_STOPPED, false) }

    /** Linc's service is running and nobody had said otherwise: it isn't stopped by hand. */
    fun noteRunning(context: Context) {
        if (!prefs(context).contains(KEY_USER_STOPPED)) setUserStopped(context, false)
    }

    fun setUserStopped(context: Context, stopped: Boolean) = prefs(context).edit().putBoolean(KEY_USER_STOPPED, stopped).apply()

    // ---- link secrets ----------------------------------------------------------------------

    /** This PC's link secret and its generation, or null when it hasn't sent one yet. */
    @Synchronized
    fun secret(context: Context, pcId: String): Pair<ByteArray, Int>? {
        val entry = secrets(context).optJSONObject(pcId) ?: return null
        val plain = runCatching { decrypt(entry.getString("box")) }.getOrNull() ?: return null
        return plain to entry.optInt("gen")
    }

    fun secretGen(context: Context, pcId: String?): Int = pcId?.let { secret(context, it)?.second } ?: 0

    @Synchronized
    fun saveSecret(context: Context, pcId: String, secret: ByteArray, gen: Int): Boolean {
        if (secret.size != 32) return false
        val box = runCatching { encrypt(secret) }.getOrElse {
            LogStore.log(LogLevel.WARN, "Linc couldn't store the PC's link key (${it.javaClass.simpleName}); it will be sent again")
            return false
        }
        val all = secrets(context).put(pcId, JSONObject().put("box", box).put("gen", gen))
        prefs(context).edit().putString(KEY_SECRETS, all.toString()).apply()
        return true
    }

    /** Whether any PC has given this phone its link key (no decryption: cheap enough to poll). */
    @Synchronized
    fun hasSecrets(context: Context): Boolean = secrets(context).length() > 0

    /** Every PC's secret, for matching a probe from whichever PC sent it. */
    @Synchronized
    fun allSecrets(context: Context): List<Pair<String, ByteArray>> {
        val all = secrets(context)
        return all.keys().asSequence().mapNotNull { id -> secret(context, id)?.let { id to it.first } }.toList()
    }

    @Synchronized
    fun forgetPc(context: Context, pcId: String) {
        val all = secrets(context)
        all.remove(pcId)
        val memory = NetMemory.load(prefs(context).getString(KEY_MEMORY, null)).also { it.forgetPc(pcId) }
        prefs(context).edit().putString(KEY_SECRETS, all.toString()).putString(KEY_MEMORY, memory.save()).apply()
    }

    private fun secrets(context: Context): JSONObject =
        runCatching { JSONObject(prefs(context).getString(KEY_SECRETS, null) ?: "{}") }.getOrDefault(JSONObject())

    // ---- per-network memory ----------------------------------------------------------------

    @Synchronized
    fun memory(context: Context): NetMemory = NetMemory.load(prefs(context).getString(KEY_MEMORY, null))

    @Synchronized
    fun saveMemory(context: Context, memory: NetMemory) = prefs(context).edit().putString(KEY_MEMORY, memory.save()).apply()

    // ---- the KeyStore box ------------------------------------------------------------------

    private fun key(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    private fun encrypt(plain: ByteArray): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key()) // the KeyStore picks the IV
        val sealed = cipher.iv + cipher.doFinal(plain)
        return Base64.encodeToString(sealed, Base64.NO_WRAP)
    }

    private fun decrypt(box: String): ByteArray {
        val sealed = Base64.decode(box, Base64.NO_WRAP)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, sealed, 0, 12))
        return cipher.doFinal(sealed, 12, sealed.size - 12)
    }

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
