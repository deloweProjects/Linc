package app.linc.android.service

import android.content.Intent
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pure-function coverage for the M10 Part B share-sheet intent-shape decision.
 * [resolveShareUris] is kept dependency-free specifically so this
 * runs on plain JUnit — no Robolectric, no Android framework mocking in this module (mirrors
 * DisplayControlTest's Context-free split). `resolveShareUris` is generic, so String stands in
 * for android.net.Uri here — a real Uri can't be constructed under this unmocked test setup.
 */
class OutgoingActionsTest {

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
