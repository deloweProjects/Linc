package app.linc.android.service

enum class UpdateAction { None, Optional, Forced }

/**
 * M17b A — the same rule as the desktop's UpdateDecision.cs. Pure; no Context, no network.
 *
 * SAFETY RULE: anything malformed or missing degrades to [UpdateAction.None], never to Forced.
 * A typo in the backend manifest must not be able to brick every installed copy.
 */
object UpdateDecision {

    fun decide(installed: String?, latest: String?, minimumSupported: String?, skipped: String?): UpdateAction {
        val have = parse(installed) ?: return UpdateAction.None
        val min = parse(minimumSupported)
        // Forced ignores `skipped` on purpose — see the C# twin.
        if (min != null && compare(have, min) < 0) return UpdateAction.Forced
        val newest = parse(latest) ?: return UpdateAction.None
        if (compare(have, newest) >= 0) return UpdateAction.None
        val skip = parse(skipped)
        if (skip != null && compare(skip, newest) == 0) return UpdateAction.None
        return UpdateAction.Optional
    }

    fun parse(text: String?): IntArray? {
        val core = text?.trim()?.trimStart('v', 'V')?.split('-', '+')?.firstOrNull()
            ?.takeIf { it.isNotEmpty() } ?: return null
        val chunks = core.split('.')
        if (chunks.isEmpty() || chunks.size > 4) return null
        val out = IntArray(4)
        chunks.forEachIndexed { i, c ->
            val v = c.toIntOrNull() ?: return null
            if (v < 0) return null
            out[i] = v
        }
        return out
    }

    fun compare(a: IntArray, b: IntArray): Int {
        for (i in 0 until 4) if (a[i] != b[i]) return if (a[i] < b[i]) -1 else 1
        return 0
    }
}
