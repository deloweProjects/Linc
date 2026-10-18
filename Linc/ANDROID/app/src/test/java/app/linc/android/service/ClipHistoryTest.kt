package app.linc.android.service

import java.time.LocalDate
import java.time.ZoneOffset
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** The clipboard history's rules (D-083). */
class ClipHistoryTest {

    private fun add(items: List<ClipItem>, text: String, at: Long = 0, cap: Int = 500, fromPhone: Boolean = true) =
        ClipHistory.add(items, text, fromPhone, at, sent = false, cap = cap)

    @Test
    fun `newest first, and a repeat moves to the top instead of duplicating`() {
        var h = add(emptyList(), "a", 1)
        h = add(h, "b", 2)
        h = add(h, "a", 3)
        assertEquals(listOf("a", "b"), h.map { it.text })
        assertEquals(3L, h.first().at)
    }

    @Test
    fun `blank text is ignored`() {
        assertEquals(emptyList<ClipItem>(), add(emptyList(), "   "))
    }

    @Test
    fun `the cap evicts the oldest unpinned items, never pinned ones`() {
        var h = emptyList<ClipItem>()
        for (i in 1..5) h = add(h, "t$i", i.toLong())
        h = ClipHistory.pin(h, h.last().id, true) // pin the oldest, "t1"
        h = add(h, "t6", 6, cap = 3)
        assertEquals(listOf("t6", "t5", "t1"), h.map { it.text })
        assertTrue(h.last().pinned)
    }

    @Test
    fun `a repeat keeps its pin and its id`() {
        var h = add(emptyList(), "keep", 1)
        val id = h.first().id
        h = ClipHistory.pin(h, id, true)
        h = add(h, "other", 2)
        h = add(h, "keep", 3)
        assertEquals(id, h.first().id)
        assertTrue(h.first().pinned)
    }

    @Test
    fun `clear keeps pinned items unless asked`() {
        var h = add(add(emptyList(), "a", 1), "b", 2)
        h = ClipHistory.pin(h, h.first { it.text == "a" }.id, true)
        assertEquals(listOf("a"), ClipHistory.clear(h).map { it.text })
        assertEquals(emptyList<ClipItem>(), ClipHistory.clear(h, includePinned = true))
    }

    @Test
    fun `delete removes one item`() {
        val h = add(add(emptyList(), "a", 1), "b", 2)
        assertEquals(listOf("a"), ClipHistory.delete(h, h.first().id).map { it.text })
    }

    @Test
    fun `search is a case-insensitive substring match`() {
        val h = add(add(emptyList(), "Hello World", 1), "bye", 2)
        assertEquals(listOf("Hello World"), ClipHistory.search(h, "world").map { it.text })
        assertEquals(2, ClipHistory.search(h, "  ").size)
    }

    @Test
    fun `groups put pinned first, then days newest first`() {
        val zone = ZoneOffset.UTC
        val day = 86_400_000L
        val now = LocalDate.now(zone).atStartOfDay(zone).toInstant().toEpochMilli() + 3_600_000
        var h = add(emptyList(), "old", now - 3 * day)
        h = add(h, "yesterday", now - day)
        h = add(h, "today", now)
        h = ClipHistory.pin(h, h.first { it.text == "old" }.id, true)
        val groups = ClipHistory.groups(h, zone)
        assertEquals(listOf("Pinned", "Today", "Yesterday"), groups.map { it.first })
        assertEquals(listOf("old"), groups.first().second.map { it.text })
    }

    @Test
    fun `history sizes are 100, 500 and 2000, default 500`() {
        assertEquals(listOf(100, 500, 2_000), ClipHistory.SIZES)
        assertEquals(500, ClipHistory.DEFAULT_SIZE)
    }
}
