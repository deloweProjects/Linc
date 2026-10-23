package app.linc.android.service

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull

/**
 * The test feed (owner: "update instead of reinstalling"): test-build.yml uploads each test build
 * to the pre-release [FEED_TAG] with `test-update.json` beside it. A test build ([mine] > 0) is
 * offered a newer test build; normal Linc never reads the feed. Pure, so it is unit-tested; the
 * PC's twin is `Services/TestChannel.cs`.
 */
object TestFeed {

    const val FEED_TAG = "linc-test-feed"
    const val FEED_URL = "https://github.com/deloweProjects/Linc/releases/download/$FEED_TAG/test-update.json"

    data class Build(val build: Int, val name: String?, val notes: String?, val apkUrl: String, val sha256: String) {
        val label: String get() = label(build, name)
    }

    fun label(build: Int, name: String?): String =
        if (name.isNullOrBlank()) "Test build $build" else "Test build $build ($name)"

    /** The newer test build to offer a test build numbered [mine], or null. */
    fun offer(feed: JsonObject?, mine: Int, skipped: String?): Build? {
        if (mine <= 0 || feed == null) return null
        fun str(o: JsonObject?, key: String) = (o?.get(key) as? JsonPrimitive)?.contentOrNull
        val primitive = feed["build"] as? JsonPrimitive ?: return null
        val build = primitive.intOrNull ?: primitive.contentOrNull?.toIntOrNull() ?: return null
        if (build <= mine) return null
        val android = feed["android"] as? JsonObject
        val url = str(android, "url")?.takeIf { it.startsWith("https://") } ?: return null
        val sha = str(android, "sha256")?.takeIf { it.isNotBlank() } ?: return null
        val name = str(feed, "name")
        if (skipped == label(build, name)) return null
        return Build(build, name, str(feed, "notes"), url, sha)
    }
}
