package app.linc.android.service

import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * [PcQuick] (2026-11-03): Home › Control's PC tiles and the PC's notifications. Sends only at
 * ≥ 27, parses what the PC sends, and keeps the notification list in step with posted/removed.
 */
class PcQuickTest {

    private val sent = mutableListOf<Envelope>()
    private val regs = mutableListOf<CompanionOutbox.Registration>()

    private fun register(version: Int) =
        CompanionOutbox.register(version) { sent += it }.also { regs += it }

    @After
    fun tearDown() {
        regs.forEach { CompanionOutbox.release(it) }
        regs.clear()
        PcQuick.reset()
    }

    private fun json(s: String) = Json.parseToJsonElement(s) as JsonObject

    @Test
    fun `nothing goes to a PC older than v27`() {
        register(26)
        PcQuick.refresh()
        PcQuick.set("wifi", true)
        PcQuick.watch(true)
        assertTrue(sent.isEmpty())
    }

    @Test
    fun `set sends the id, and on only when given`() {
        register(32)
        PcQuick.set("nightlight", false)
        PcQuick.set("lock")
        assertEquals(MessageType.PC_QUICK_SET, sent[0].type)
        assertEquals("nightlight", sent[0].payload["id"]!!.jsonPrimitive.content)
        assertEquals("false", sent[0].payload["on"]!!.jsonPrimitive.content)
        assertNull(sent[1].payload["on"])
    }

    @Test
    fun `watch with ask asks, without it doesn't`() {
        register(27)
        PcQuick.watch(true, ask = true)
        PcQuick.watch(false)
        assertEquals("true", sent[0].payload["ask"]!!.jsonPrimitive.content)
        assertNull(sent[1].payload["ask"])
    }

    @Test
    fun `pc_quick_state parses, skipping items without an id`() {
        PcQuick.onState(json("""{"items":[
            {"id":"wifi","label":"Wi-Fi","group":"setting","kind":"toggle","on":true,"available":true},
            {"id":"nightlight","label":"Night light","group":"setting","kind":"toggle","available":false,"note":"Not on this PC"},
            {"label":"no id"},
            {"id":"lock","label":"Lock","group":"shortcut","kind":"action","available":true}],"error":"denied"}"""))
        val items = PcQuick.items.value!!
        assertEquals(listOf("wifi", "nightlight", "lock"), items.map { it.id })
        assertEquals(true, items[0].on)
        assertFalse(items[1].available)
        assertNull(items[2].on)
        assertEquals("denied", PcQuick.error.value)
    }

    @Test
    fun `notes follow posted, removed and dismiss`() {
        register(27)
        PcQuick.onNotes(json("""{"enabled":true,"access":"allowed","items":[
            {"id":"1","app":"Mail","title":"Old","text":"","time":100},
            {"id":"2","app":"Teams","title":"New","text":"hi","time":200}]}"""))
        assertEquals(listOf("2", "1"), PcQuick.notes.value.items.map { it.id })

        PcQuick.onPosted(buildJsonObject { put("id", "3"); put("app", "Edge"); put("title", "Newest"); put("time", 300) })
        assertEquals(listOf("3", "2", "1"), PcQuick.notes.value.items.map { it.id })

        PcQuick.onRemoved(buildJsonObject { put("id", "2") })
        PcQuick.dismiss("1")
        assertEquals(listOf("3"), PcQuick.notes.value.items.map { it.id })
        assertEquals(MessageType.PC_NOTIF_DISMISS, sent.last().type)
        assertEquals("1", sent.last().payload["id"]!!.jsonPrimitive.content)
    }
}
