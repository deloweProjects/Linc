package app.linc.android.service

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class TestFeedTest {

    private fun feed(text: String) = Json.parseToJsonElement(text).jsonObject

    private val good = feed(
        """{ "build": 43, "name": "linclink-b", "notes": "n",
             "android": { "url": "https://github.com/x/linc-test-43.apk", "sha256": "cd" } }"""
    )

    @Test fun `a newer test build is offered`() {
        val o = TestFeed.offer(good, 42, null)
        assertNotNull(o)
        o!!
        assertEquals(43, o.build)
        assertEquals("Test build 43 (linclink-b)", o.label)
    }

    @Test fun `the same or an older build is not`() {
        assertNull(TestFeed.offer(good, 43, null))
        assertNull(TestFeed.offer(good, 44, null))
    }

    @Test fun `a normal build never reads the feed`() = assertNull(TestFeed.offer(good, 0, null))

    @Test fun `a skipped build stays skipped`() {
        assertNull(TestFeed.offer(good, 42, "Test build 43 (linclink-b)"))
        assertNotNull(TestFeed.offer(good, 42, "Test build 41 (linclink-a)"))
    }

    @Test fun `no hash, no https or no build means nothing`() {
        assertNull(TestFeed.offer(feed("""{ "build": 43, "android": { "url": "https://x/a.apk" } }"""), 42, null))
        assertNull(TestFeed.offer(feed("""{ "build": 43, "android": { "url": "http://x/a.apk", "sha256": "cd" } }"""), 42, null))
        assertNull(TestFeed.offer(feed("""{ "android": { "url": "https://x/a.apk", "sha256": "cd" } }"""), 42, null))
        assertNull(TestFeed.offer(null, 42, null))
    }

    @Test fun `a build number may come as a string`() =
        assertEquals(43, TestFeed.offer(feed("""{ "build": "43", "android": { "url": "https://x/a.apk", "sha256": "cd" } }"""), 42, null)?.build)
}
