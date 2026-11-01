package app.linc.android

import android.app.Activity
import android.os.Bundle
import app.linc.android.service.PcHotspotJoin

/**
 * The "Join your PC's hotspot" notification's tap: Android only shows its join dialog for an app
 * on screen, so this invisible window asks for the network and leaves.
 */
class JoinPcHotspotActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        PcHotspotJoin.requestNow(this)
        finish()
    }
}
