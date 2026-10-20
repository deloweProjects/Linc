package app.linc.android

import android.app.Activity
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import app.linc.android.service.LogLevel
import app.linc.android.service.LogStore
import app.linc.android.service.PhoneSource

/**
 * Asks Android once for consent to record what apps play (D-097): Android only grants playback
 * capture through its screen-recording prompt. Linc records no picture, only the sound, and the
 * companion service takes it from here.
 */
class CaptureConsentActivity : ComponentActivity() {

    private val consent = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val data = result.data
        if (result.resultCode == Activity.RESULT_OK && data != null) {
            startForegroundService(PhoneSource.intentToStart(this, result.resultCode, data))
        } else {
            LogStore.log(LogLevel.INFO, "Play everywhere: recording what apps play wasn't allowed")
        }
        finish()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val manager = getSystemService(MediaProjectionManager::class.java)
        val intent = if (Build.VERSION.SDK_INT >= 34) {
            manager.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
        } else manager.createScreenCaptureIntent()
        consent.launch(intent)
    }
}
