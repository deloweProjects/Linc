package app.linc.android.service

import kotlinx.coroutines.channels.Channel

/**
 * "Connect to the PC now." Anything that knows the PC is close — an NFC tap on the desk tag,
 * the user opening Linc — pokes this, and [PresenceClient] skips the rest of its backoff and
 * dials immediately. Conflated: ten pokes while it is already dialling are one dial.
 */
object ReconnectSignal {
    private val channel = Channel<String>(Channel.CONFLATED)

    fun poke(reason: String) {
        channel.trySend(reason)
    }

    internal suspend fun await(): String = channel.receive()
}
