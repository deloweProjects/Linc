package app.linc.android

import java.security.MessageDigest
import org.junit.Assert.assertEquals
import org.junit.Test

class ProvenanceTest {
    @Test
    fun fingerprintIsTheSha256OfTheStatement() {
        val hash = MessageDigest.getInstance("SHA-256").digest(Provenance.STATEMENT.toByteArray(Charsets.UTF_8))
        assertEquals(Provenance.FINGERPRINT, hash.joinToString("") { "%02x".format(it) })
    }

    @Test
    fun buildConfigAgrees() {
        assertEquals(Provenance.AUTHOR, BuildConfig.AUTHOR)
    }

    @Test
    fun bannerNamesTheAuthorAndSource() {
        assertEquals(
            "Linc 1.0.0.17 by deloweProjects — https://github.com/deloweProjects/Linc",
            Provenance.banner("1.0.0.17"),
        )
    }
}
