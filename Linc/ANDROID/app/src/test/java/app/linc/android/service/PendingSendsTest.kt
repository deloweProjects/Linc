package app.linc.android.service

import app.linc.android.service.PendingSends.Item
import org.junit.Assert.assertEquals
import org.junit.Test

class PendingSendsTest {
    private val now = 1_000_000_000L

    @Test fun keeps_order() {
        var q = emptyList<Item>()
        q = PendingSends.added(q, Item(PendingSends.URL, "https://a", now), now)
        q = PendingSends.added(q, Item(PendingSends.TEXT, "b", now + 1), now + 1)
        assertEquals(listOf("https://a", "b"), q.map { it.text })
    }

    @Test fun only_the_newest_clip_is_kept() {
        var q = emptyList<Item>()
        q = PendingSends.added(q, Item(PendingSends.CLIP, "old", now), now)
        q = PendingSends.added(q, Item(PendingSends.TEXT, "note", now + 1), now + 1)
        q = PendingSends.added(q, Item(PendingSends.CLIP, "new", now + 2), now + 2)
        assertEquals(listOf("note", "new"), q.map { it.text })
    }

    @Test fun capped_at_twenty_oldest_dropped() {
        var q = emptyList<Item>()
        repeat(25) { q = PendingSends.added(q, Item(PendingSends.TEXT, "t$it", now + it), now + it) }
        assertEquals(20, q.size)
        assertEquals("t5", q.first().text)
    }

    @Test fun older_than_a_day_is_dropped() {
        val q = listOf(Item(PendingSends.TEXT, "stale", now), Item(PendingSends.TEXT, "fresh", now + 1))
        assertEquals(listOf("fresh"), PendingSends.fresh(q, now + 24 * 60 * 60 * 1000L + 1).map { it.text })
    }
}
