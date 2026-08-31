package app.linc.android.service

import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.content.pm.ResolveInfo
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.add
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * The v16 installed-app inventory (M6c, D-058) — the ONLY place the phone touches
 * [PackageManager] for the desktop's Home Apps section.
 *
 * **Launchable apps only.** A raw `getInstalledPackages()` is several hundred framework
 * packages the user can neither recognise nor open, so this queries the `CATEGORY_LAUNCHER`
 * intent instead. System apps are **included**, flagged by [Entry.system] via `FLAG_SYSTEM`,
 * because Camera/Settings/Photos are exactly the ones people reach for; the flag only lets
 * the desktop group or de-emphasise them.
 *
 * `label` is the localised, user-visible name from the package manager. The desktop must
 * never derive a name from the package id, so an entry with no usable label is dropped
 * rather than sent as a package name in disguise. `versionName` is cosmetic and may be
 * absent or empty on some builds.
 *
 * Everything except the two [PackageManager] round-trips lives in pure functions on the
 * companion object, so unit tests can exercise the mapping and the payload shape without an
 * Android runtime (the test classpath has no Robolectric — same arrangement as
 * [DisplayControl]).
 */
object AppInventory {

    /** One launchable package, already mapped off the Android types. */
    data class Entry(
        val packageName: String,
        val label: String,
        val versionName: String?,
        val system: Boolean,
    )

    /** The `apps` reply payload for this phone: `{"apps": [...]}`. */
    fun payload(context: Context): JsonObject = toPayload(launchable(context))

    /**
     * Every package resolving a launcher intent, mapped to [Entry]. Returns an empty list
     * rather than throwing if the package manager query fails — an empty Apps section is a
     * far better outcome than a killed control connection.
     */
    fun launchable(context: Context): List<Entry> = runCatching {
        val pm = context.packageManager
        val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        @Suppress("DEPRECATION")
        val resolved: List<ResolveInfo> = pm.queryIntentActivities(intent, 0)

        // The same package can resolve more than one launcher activity; the desktop wants one
        // row per app, so first-wins by package id.
        val seen = LinkedHashMap<String, Entry>()
        for (info in resolved) {
            val appInfo = info.activityInfo?.applicationInfo ?: continue
            if (seen.containsKey(appInfo.packageName)) continue
            val label = runCatching { info.loadLabel(pm)?.toString() }.getOrNull()
            val versionName = runCatching {
                @Suppress("DEPRECATION")
                pm.getPackageInfo(appInfo.packageName, 0).versionName
            }.getOrNull()
            val entry = mapEntry(appInfo.packageName, label, versionName, appInfo.flags) ?: continue
            seen[entry.packageName] = entry
        }
        seen.values.toList()
    }.getOrDefault(emptyList())

    /**
     * Pure mapping of one package's raw fields to an [Entry], or null when it cannot be
     * represented honestly.
     *
     * - A blank package id, or a blank/absent label, yields null: the contract says the label
     *   is the user-visible localised name, and inventing one from the package id is exactly
     *   what PROTOCOL.md v16 forbids.
     * - A blank or absent `versionName` becomes null — the field is optional and cosmetic, so
     *   an empty string is not worth putting on the wire.
     * - `system` is [ApplicationInfo.FLAG_SYSTEM] on the package's flags.
     */
    fun mapEntry(packageName: String?, label: String?, versionName: String?, flags: Int): Entry? {
        val id = packageName?.trim().orEmpty()
        if (id.isEmpty()) return null
        val name = label?.trim().orEmpty()
        if (name.isEmpty()) return null
        val version = versionName?.trim()?.takeIf { it.isNotEmpty() }
        return Entry(
            packageName = id,
            label = name,
            versionName = version,
            system = (flags and ApplicationInfo.FLAG_SYSTEM) != 0,
        )
    }

    /**
     * Pure shaping of the `apps` payload. Wire order is explicitly not part of the contract
     * (the desktop sorts for display), so entries go out in the order given.
     * `versionName` is omitted entirely when absent rather than sent as null or "".
     */
    fun toPayload(entries: List<Entry>): JsonObject = buildJsonObject {
        put("apps", buildJsonArray {
            for (entry in entries) {
                add(buildJsonObject {
                    put("package", entry.packageName)
                    put("label", entry.label)
                    entry.versionName?.let { put("versionName", it) }
                    put("system", entry.system)
                })
            }
        })
    }
}
