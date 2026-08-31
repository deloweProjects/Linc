package app.linc.android.service

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.add
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress
import java.net.NetworkInterface
import java.util.concurrent.atomic.AtomicInteger

// v18 "Instant ADB link-up over a hotspot" (M13b) — the phone-side rules, deliberately free of
// android.* so every one of them runs in a plain JVM unit test. Anything needing SystemProperties,
// NsdManager or a live socket lives in AdbAnnouncer.kt instead.

/** ADB modes on the wire (PROTOCOL.md v18 `mode`). */
object AdbMode {
    const val TLS = "tls"
    const val TCPIP = "tcpip"

    /** Pairing is open but the device is not paired: NOT a port a desktop can connect to. */
    const val PAIRING = "pairing"
}

/**
 * The monotonic generation counter. Incremented on every link event and stamped on all four v18
 * messages; the desktop discards anything whose `gen` is not higher than the highest it has seen.
 *
 * Starts at 0 and [next] returns 1 first, so the very first announcement of a process is `gen: 1`
 * and can never collide with a desktop that seeds its "highest seen" at 0.
 */
object AdbGeneration {
    private val counter = AtomicInteger(0)

    fun next(): Int = counter.incrementAndGet()

    fun current(): Int = counter.get()
}

/** What happened when the companion tried to arm wireless debugging (M13c §2.2). */
enum class AdbArmOutcome {
    /** The setting was written: adbd is coming up. */
    ARMED,

    /** Already on. Nothing was written, and nothing needed to be. */
    ALREADY_ON,

    /** The WRITE_SECURE_SETTINGS grant is not held, so arming is the user's job. */
    DENIED,

    /** The write was attempted and the platform refused it (SecurityException, OEM policy). */
    UNAVAILABLE,
}

/**
 * The self-arming rules (M13c §2.2). Pure, so "we never turn it off behind the user's back" is a
 * property of a function a test can call, not a claim about a code path.
 *
 * **There is deliberately no disarm.** Arming is a user-visible change to their device; the
 * companion may switch it on when the desktop asks, and must never switch it back off. The only
 * value this object will ever write is [ENABLED_VALUE].
 */
object AdbArmRules {

    /** `Settings.Global` key adbd watches for wireless debugging. */
    const val SETTING_NAME = "adb_wifi_enabled"

    /** The only value the companion ever writes. */
    const val ENABLED_VALUE = 1

    fun plan(hasPermission: Boolean, currentlyEnabled: Boolean): AdbArmOutcome = when {
        // Checked first: a phone without the grant must report `denied` rather than attempting
        // the write and letting a SecurityException escape into the control loop.
        !hasPermission -> AdbArmOutcome.DENIED
        currentlyEnabled -> AdbArmOutcome.ALREADY_ON
        else -> AdbArmOutcome.ARMED
    }

    /** True when the plan means "write the setting now". */
    fun writesSetting(outcome: AdbArmOutcome): Boolean = outcome == AdbArmOutcome.ARMED

    /** Plain-language line for the phone's own log and UI. */
    fun describe(outcome: AdbArmOutcome): String = when (outcome) {
        AdbArmOutcome.ARMED -> "Turned wireless debugging on for the PC."
        AdbArmOutcome.ALREADY_ON -> "Wireless debugging was already on."
        AdbArmOutcome.DENIED ->
            "This phone hasn't given Linc permission to turn wireless debugging on by itself. " +
                "Turn it on in Developer options when you want an instant hotspot link."
        AdbArmOutcome.UNAVAILABLE ->
            "This phone refused to let Linc turn wireless debugging on. Turn it on in Developer options instead."
    }
}

/** One of this phone's addresses, as announced in `addrs`. */
data class AdbAddr(val ip: String, val iface: String, val prefix: Int)

/** A port adbd may be reachable on, and how it was found. */
data class AdbDetectedPort(val port: Int, val mode: String, val source: String)

/**
 * Pure decision rules for finding adbd's port. The detection METHODS are ordered by cost in
 * [AdbAnnouncer]; what lives here is the part that can be wrong in a way a unit test can catch.
 */
object AdbPortRules {

    /**
     * System properties, cheapest method first (~1 ms), in the order PROTOCOL.md/M13b give:
     * TLS port, then the tcpip port, then the persisted one. **A miss means "try the next
     * method", never "adbd is down"** — these are not populated on every OEM build.
     */
    val PropertyOrder: List<String> = listOf(
        "service.adb.tls.port",
        "service.adb.tcp.port",
        "persist.adb.tcp.port",
    )

    fun modeForProperty(name: String): String =
        if (name == "service.adb.tls.port") AdbMode.TLS else AdbMode.TCPIP

    /** First property in [PropertyOrder] holding a usable port, or null when none does. */
    fun pickPort(properties: Map<String, String?>): AdbDetectedPort? {
        for (name in PropertyOrder) {
            val port = parsePort(properties[name]) ?: continue
            return AdbDetectedPort(port, modeForProperty(name), "property $name")
        }
        return null
    }

    /** Null unless the text is a port in 1..65535. `-1` and `0` are adbd's "not listening". */
    fun parsePort(raw: String?): Int? {
        val port = raw?.trim()?.toIntOrNull() ?: return null
        return if (port in 1..65535) port else null
    }

    /**
     * The NSD service types adbd publishes, mapped to a wire mode. `_adb-tls-pairing._tcp` maps
     * to [AdbMode.PAIRING] rather than to a connectable mode: pairing being open means the
     * desktop is NOT paired, and announcing that port as connectable buys an `unauthorized`.
     */
    fun modeForServiceType(serviceType: String?): String? =
        when (serviceType?.trim()?.trimEnd('.')?.lowercase()) {
            "_adb-tls-connect._tcp" -> AdbMode.TLS
            "_adb._tcp" -> AdbMode.TCPIP
            "_adb-tls-pairing._tcp" -> AdbMode.PAIRING
            else -> null
        }

    fun isConnectableMode(mode: String?): Boolean = mode == AdbMode.TLS || mode == AdbMode.TCPIP

    /**
     * True when a resolved NSD service belongs to THIS phone. Browsing `_adb*._tcp` on a hotspot
     * segment sees every device on it, so without this the phone would happily announce a second
     * device's port as its own.
     */
    fun isOwnAddress(resolved: String?, ownAddresses: List<String>): Boolean {
        val candidate = normalizeAddress(resolved) ?: return false
        return ownAddresses.any { normalizeAddress(it) == candidate }
    }

    /** Strips an IPv6 zone suffix and lower-cases, so `fe80::1%wlan0` and `fe80::1` compare equal. */
    fun normalizeAddress(address: String?): String? {
        val trimmed = address?.trim()?.removePrefix("/")?.takeIf { it.isNotEmpty() } ?: return null
        return trimmed.substringBefore('%').lowercase()
    }
}

/** Collects this phone's own addresses for the `addrs` array. */
object AdbAddresses {

    /**
     * Loopback, unspecified and down interfaces are never worth announcing. Everything else is
     * left in on purpose: the DESKTOP owns the same-subnet filter (PROTOCOL.md v18), and a phone
     * that pre-filtered by guessing which of its interfaces is "the hotspot one" would be
     * guessing with less information than the desktop has.
     */
    fun keep(address: InetAddress): Boolean =
        !address.isLoopbackAddress && !address.isAnyLocalAddress && !address.isMulticastAddress

    fun collect(): List<AdbAddr> {
        val result = mutableListOf<AdbAddr>()
        val interfaces = try {
            NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
        } catch (_: Exception) {
            return result
        }
        for (nic in interfaces) {
            val up = try {
                nic.isUp && !nic.isLoopback
            } catch (_: Exception) {
                false
            }
            if (!up) continue
            for (entry in nic.interfaceAddresses) {
                val address = entry.address ?: continue
                if (!keep(address)) continue
                if (address !is Inet4Address && address !is Inet6Address) continue
                val ip = AdbPortRules.normalizeAddress(address.hostAddress) ?: continue
                result.add(AdbAddr(ip, nic.name ?: "?", entry.networkPrefixLength.toInt()))
            }
        }
        return result
    }
}

/**
 * Pure payload builders for the four v18 messages, so a unit test can check the wire shape
 * without a live connection — the same split [ControlPayloads] uses for v17.
 */
object AdbPayloads {

    /**
     * `verified` is not a formality: it means this phone has just completed a loopback connect
     * to [port]. The L3 link comes up BEFORE adbd re-binds to the new interface, so link-up is
     * not readiness, and the desktop refuses to act on `verified: false`.
     */
    fun announce(
        gen: Int,
        serial: String,
        mode: String,
        port: Int,
        verified: Boolean,
        addrs: List<AdbAddr>,
    ): JsonObject = buildJsonObject {
        put("gen", gen)
        put("serial", serial)
        put("mode", mode)
        put("port", port)
        put("verified", verified)
        put("addrs", buildJsonArray {
            for (addr in addrs) {
                add(buildJsonObject {
                    put("ip", addr.ip)
                    put("iface", addr.iface)
                    put("prefix", addr.prefix)
                })
            }
        })
    }

    fun down(gen: Int, reason: String): JsonObject = buildJsonObject {
        put("gen", gen)
        put("reason", reason)
    }
}
