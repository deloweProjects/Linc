package app.linc.android.service

import android.content.Context
import app.linc.android.BuildConfig
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject

/**
 * The phone's update check (1.0.0.11, D-082). M17b–M19 built every piece — the manifest constant,
 * [UpdateDecision], [UpdateInstaller], the cards — but nothing ever fetched the manifest, so
 * "Keep Linc up to date" did nothing. This is the missing caller: a plain GET of
 * `Releases/update.json` when Linc opens (at most every 6 hours, or on demand), the decision, and —
 * when the user taps Update — a download with progress, a SHA-256 check, and Android's own installer.
 */
object UpdateCheck {

    data class Offer(val version: String, val notes: String?, val url: String, val sha256: String, val forced: Boolean)

    private const val PREFS = "linc"
    private const val KEY_LAST = "update_last_check"
    // A test build (Linc Test) looks every 30 minutes; a release every 6 hours.
    private val INTERVAL_MS = if (BuildConfig.TEST_BUILD > 0) 30 * 60 * 1000L else 6 * 60 * 60 * 1000L
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val json = Json { ignoreUnknownKeys = true }

    private val _offer = MutableStateFlow<Offer?>(null)
    val offer: StateFlow<Offer?> = _offer

    /** 0..1 while downloading; null otherwise. */
    private val _progress = MutableStateFlow<Float?>(null)
    val progress: StateFlow<Float?> = _progress

    private val _problem = MutableStateFlow<String?>(null)
    val problem: StateFlow<String?> = _problem

    /** Check the manifest. [report] gets a one-line outcome when the user asked ([force]). */
    fun check(context: Context, force: Boolean = false, report: ((String) -> Unit)? = null) {
        val app = context.applicationContext
        val url = UpdateSettings.manifestUrl(app) ?: return // switched off: nothing runs at all
        val prefs = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val now = System.currentTimeMillis()
        if (!force && now - prefs.getLong(KEY_LAST, 0) < INTERVAL_MS) return
        prefs.edit().putLong(KEY_LAST, now).apply()
        scope.launch {
            val manifest = runCatching { fetch(url) }.getOrNull()
            val android = manifest?.get("android") as? JsonObject
            fun str(o: JsonObject?, key: String) = (o?.get(key) as? JsonPrimitive)?.contentOrNull
            val latest = str(manifest, "latest")
            val action = UpdateDecision.decide(BuildConfig.VERSION_NAME, latest, str(manifest, "minimumSupported"),
                if (force) null else UpdateSettings.skippedVersion(app))
            val apk = str(android, "url")
            val sha = str(android, "sha256")
            _offer.value = if (action != UpdateAction.None && latest != null && apk != null && !sha.isNullOrBlank()) {
                Offer(latest, str(manifest, "notes"), apk, sha, forced = action == UpdateAction.Forced)
            } else if (BuildConfig.TEST_BUILD > 0) {
                // A test build also follows the test feed; a real release above still wins.
                val feed = runCatching { fetch(TestFeed.FEED_URL) }.getOrNull()
                TestFeed.offer(feed, BuildConfig.TEST_BUILD, if (force) null else UpdateSettings.skippedVersion(app))
                    ?.let { Offer(it.label, it.notes ?: "A newer test build of Linc.", it.apkUrl, it.sha256, forced = false) }
            } else null
            val message = when {
                manifest == null -> "Couldn't reach the update server — try again later"
                _offer.value != null -> "Linc ${_offer.value!!.version} is available"
                else -> "Linc is up to date"
            }
            report?.let { withContext(Dispatchers.Main) { it(message) } }
        }
    }

    fun skip(context: Context) {
        val o = _offer.value ?: return
        if (o.forced) return
        UpdateSettings.setSkippedVersion(context, o.version)
        _offer.value = null
    }

    /** Download the APK (with progress), check its SHA-256, and hand it to Android's installer. */
    fun install(context: Context) {
        val app = context.applicationContext
        val o = _offer.value ?: return
        if (_progress.value != null) return
        _problem.value = null
        _progress.value = 0f
        scope.launch {
            val dir = File(app.cacheDir, "updates").apply { mkdirs() }
            val file = File(dir, "linc-${o.version.filter { it.isLetterOrDigit() || it in ".-" }}.apk")
            val ok = runCatching { download(o.url, file) { _progress.value = it } }.isSuccess
            _progress.value = null
            when {
                !ok -> _problem.value = "The download didn't finish. Check the connection and try again."
                !sha256(file).equals(o.sha256, ignoreCase = true) -> {
                    file.delete()
                    _problem.value = "The download didn't match the published file, so Linc won't install it."
                }
                else -> withContext(Dispatchers.Main) {
                    when (val r = UpdateInstaller.install(app, file)) {
                        is UpdateInstaller.Result.NeedsPermission -> {
                            _problem.value = r.message
                            runCatching { app.startActivity(r.intent) }
                        }
                        is UpdateInstaller.Result.Failed -> _problem.value = r.message
                        UpdateInstaller.Result.Launched -> Unit
                    }
                }
            }
        }
    }

    private fun fetch(url: String): JsonObject {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 10_000
        c.readTimeout = 10_000
        return c.inputStream.use { json.parseToJsonElement(it.readBytes().decodeToString()).jsonObject }
    }

    private fun download(url: String, file: File, onProgress: (Float) -> Unit) {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 15_000
        c.readTimeout = 30_000
        c.instanceFollowRedirects = true
        val total = c.contentLengthLong
        c.inputStream.use { input ->
            file.outputStream().use { output ->
                val buffer = ByteArray(64 * 1024)
                var read = 0L
                while (true) {
                    val n = input.read(buffer)
                    if (n < 0) break
                    output.write(buffer, 0, n)
                    read += n
                    if (total > 0) onProgress((read.toFloat() / total).coerceIn(0f, 1f))
                }
            }
        }
    }

    private fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val n = input.read(buffer)
                if (n < 0) break
                digest.update(buffer, 0, n)
            }
        }
        return digest.digest().joinToString("") { "%02X".format(it) }
    }
}
