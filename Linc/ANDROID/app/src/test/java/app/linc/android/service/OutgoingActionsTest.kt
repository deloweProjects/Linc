package app.linc.android.service

import android.content.Intent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pure-function coverage for the M10 Part B share-sheet bounds and intent-shape decisions.
 * [ShareSizeLimits.check] and [resolveShareUris] are kept dependency-free specifically so this
 * runs on plain JUnit — no Robolectric, no Android framework mocking in this module (mirrors
 * DisplayControlTest's Context-free split). `resolveShareUris` is generic, so String stands in
 * for android.net.Uri here — a real Uri can't be constructed under this unmocked test setup.
 */
class OutgoingActionsTest {

    // ---- ShareSizeLimits.check: single-item bound (100 MB) ----

    @Test
    fun `single item just under the 100 MB limit is accepted`() {
        assertNull(ShareSizeLimits.check(listOf(ShareSizeLimits.MAX_SINGLE_ITEM_BYTES - 1)))
    }

    @Test
    fun `single item exactly at the 100 MB limit is accepted`() {
        assertNull(ShareSizeLimits.check(listOf(ShareSizeLimits.MAX_SINGLE_ITEM_BYTES)))
    }

    @Test
    fun `single item just over the 100 MB limit is refused`() {
        val message = ShareSizeLimits.check(listOf(ShareSizeLimits.MAX_SINGLE_ITEM_BYTES + 1))
        assertTrue(message != null && message.contains("100 MB"))
    }

    // ---- ShareSizeLimits.check: total bound (250 MB) across a multi-item selection ----
    // Three items are used so each stays at-or-under the 100 MB per-item cap while the SUM
    // straddles the 250 MB total boundary — two items pinned at the per-item cap (allowed) plus
    // a third that supplies the remainder, so only the total rule is exercised here.
    private val singleCap = ShareSizeLimits.MAX_SINGLE_ITEM_BYTES
    private val exactRemainder = ShareSizeLimits.MAX_TOTAL_BYTES - 2 * singleCap

    @Test
    fun `selection totalling just under the 250 MB limit is accepted`() {
        val sizes = listOf(singleCap, singleCap, exactRemainder - 1)
        assertEquals(ShareSizeLimits.MAX_TOTAL_BYTES - 1, sizes.sum())
        assertNull(ShareSizeLimits.check(sizes))
    }

    @Test
    fun `selection totalling exactly the 250 MB limit is accepted`() {
        val sizes = listOf(singleCap, singleCap, exactRemainder)
        assertEquals(ShareSizeLimits.MAX_TOTAL_BYTES, sizes.sum())
        assertNull(ShareSizeLimits.check(sizes))
    }

    @Test
    fun `selection totalling just over the 250 MB limit is refused`() {
        val sizes = listOf(singleCap, singleCap, exactRemainder + 1)
        assertEquals(ShareSizeLimits.MAX_TOTAL_BYTES + 1, sizes.sum())
        val message = ShareSizeLimits.check(sizes)
        assertTrue(message != null && message.contains("250 MB"))
    }

    @Test
    fun `a single-item refusal takes priority even when the total would also be over`() {
        // An oversized single item always violates the per-item bound first, regardless of
        // what the running total is — the message should name the 100 MB limit, not the 250 MB one.
        val message = ShareSizeLimits.check(listOf(ShareSizeLimits.MAX_SINGLE_ITEM_BYTES + 1))
        assertTrue(message != null && message.contains("100 MB") && !message.contains("250 MB"))
    }

    @Test
    fun `empty selection is accepted`() {
        assertNull(ShareSizeLimits.check(emptyList()))
    }

    // ---- resolveShareUris: single vs. multiple intent shape ----

    @Test
    fun `ACTION_SEND resolves to the single item only`() {
        assertEquals(listOf("uri-a"), resolveShareUris(Intent.ACTION_SEND, "uri-a", listOf("uri-b", "uri-c")))
    }

    @Test
    fun `ACTION_SEND with no single extra resolves to empty`() {
        assertEquals(emptyList<String>(), resolveShareUris(Intent.ACTION_SEND, null, listOf("uri-b")))
    }

    @Test
    fun `ACTION_SEND_MULTIPLE resolves to the full list, ignoring any single extra`() {
        assertEquals(
            listOf("uri-a", "uri-b"),
            resolveShareUris(Intent.ACTION_SEND_MULTIPLE, "ignored", listOf("uri-a", "uri-b")),
        )
    }

    @Test
    fun `ACTION_SEND_MULTIPLE with a null list resolves to empty`() {
        assertEquals(emptyList<String>(), resolveShareUris(Intent.ACTION_SEND_MULTIPLE, "ignored", null))
    }

    @Test
    fun `an unrelated or null action resolves to empty`() {
        assertEquals(emptyList<String>(), resolveShareUris(Intent.ACTION_VIEW, "uri-a", listOf("uri-b")))
        assertEquals(emptyList<String>(), resolveShareUris(null, "uri-a", listOf("uri-b")))
    }
}
