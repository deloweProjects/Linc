package app.linc.android.service

import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Pure-function coverage for [ControlPayloads] (v17, M4b, D-042): every action shapes the
 * payload the protocol spec requires, and — the safety-relevant part — `confirm: true` is
 * present on shutdown/restart and absent everywhere else.
 */
class ControlPayloadsTest {

    @Test
    fun `lock carries only the action`() {
        val payload = ControlPayloads.lock()
        assertEquals("lock", payload["action"]!!.jsonPrimitive.content)
        assertNull(payload["confirm"])
    }

    @Test
    fun `sleep carries only the action, no confirm (not destructive)`() {
        val payload = ControlPayloads.sleep()
        assertEquals("sleep", payload["action"]!!.jsonPrimitive.content)
        assertNull(payload["confirm"])
    }

    @Test
    fun `shutdown always carries confirm true`() {
        val payload = ControlPayloads.shutdown()
        assertEquals("shutdown", payload["action"]!!.jsonPrimitive.content)
        assertEquals(true, payload["confirm"]!!.jsonPrimitive.content.toBoolean())
    }

    @Test
    fun `restart always carries confirm true`() {
        val payload = ControlPayloads.restart()
        assertEquals("restart", payload["action"]!!.jsonPrimitive.content)
        assertEquals(true, payload["confirm"]!!.jsonPrimitive.content.toBoolean())
    }

    @Test
    fun `volumeUp with no step omits the field`() {
        val payload = ControlPayloads.volumeUp()
        assertEquals("volume.up", payload["action"]!!.jsonPrimitive.content)
        assertNull(payload["step"])
    }

    @Test
    fun `volumeUp with a step includes it`() {
        val payload = ControlPayloads.volumeUp(step = 3)
        assertEquals("3", payload["step"]!!.jsonPrimitive.content)
    }

    @Test
    fun `volumeDown with a step includes it`() {
        val payload = ControlPayloads.volumeDown(step = 2)
        assertEquals("volume.down", payload["action"]!!.jsonPrimitive.content)
        assertEquals("2", payload["step"]!!.jsonPrimitive.content)
    }

    @Test
    fun `volumeSet carries the level`() {
        val payload = ControlPayloads.volumeSet(42)
        assertEquals("volume.set", payload["action"]!!.jsonPrimitive.content)
        assertEquals("42", payload["level"]!!.jsonPrimitive.content)
    }

    @Test
    fun `volumeMute with no 'on' omits the field (toggle)`() {
        val payload = ControlPayloads.volumeMute()
        assertEquals("volume.mute", payload["action"]!!.jsonPrimitive.content)
        assertNull(payload["on"])
    }

    @Test
    fun `volumeMute with 'on' explicit includes it`() {
        val payload = ControlPayloads.volumeMute(on = true)
        assertEquals(true, payload["on"]!!.jsonPrimitive.content.toBoolean())
    }

    @Test
    fun `brightnessSet carries the level`() {
        val payload = ControlPayloads.brightnessSet(75)
        assertEquals("brightness.set", payload["action"]!!.jsonPrimitive.content)
        assertEquals("75", payload["level"]!!.jsonPrimitive.content)
    }

    @Test
    fun `only the two destructive actions carry confirm`() {
        val nonDestructive = listOf(
            ControlPayloads.lock(),
            ControlPayloads.sleep(),
            ControlPayloads.volumeUp(),
            ControlPayloads.volumeDown(),
            ControlPayloads.volumeSet(50),
            ControlPayloads.volumeMute(),
            ControlPayloads.brightnessSet(50),
        )
        nonDestructive.forEach { assertNull(it["confirm"]) }

        listOf(ControlPayloads.shutdown(), ControlPayloads.restart()).forEach {
            assertEquals(true, it["confirm"]!!.jsonPrimitive.content.toBoolean())
        }
    }
}
