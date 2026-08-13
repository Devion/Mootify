package kiwi.lazy.mootify.playback

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.HttpDataSource
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Gets the music going again after the signal drops.
 *
 * ExoPlayer retries a failing load a few times inside the media source and then gives up, and giving
 * up means [Player.STATE_IDLE] and a silent car until somebody picks up the phone. That's the right
 * default for a video app on a sofa and the wrong one for a motorway. Calling `prepare()` again
 * resumes from the position that failed, keeping `playWhenReady`, so recovery is invisible when it
 * works.
 *
 * Two stages, because the two failures are different lengths:
 *
 * - **A blip** — a tunnel, a cell handover — is [MaxAttempts] re-prepares on a doubling backoff.
 * - **A real outage** costs nothing to wait out, so once the retries are spent it stops trying and
 *   registers for the network coming back instead. A dead zone that lasts ten minutes then resumes
 *   the moment there's signal, rather than having exhausted its attempts in the first thirty seconds.
 *
 * **Only transient errors are retried.** A revoked token answers 401 and the session is cleared by
 * the auth interceptor; re-preparing into that would be a loop that spends battery to be told no
 * four more times. So HTTP status decides: 5xx and the two "come back later" codes are worth another
 * go, and everything else is left to surface.
 */
@UnstableApi
class NetworkRecovery(
    context: Context,
    private val player: Player,
    private val scope: CoroutineScope,
) {

    private val connectivity = context.getSystemService(ConnectivityManager::class.java)

    private var attempts = 0
    private var retry: Job? = null
    private var waiting: ConnectivityManager.NetworkCallback? = null

    private val listener = object : Player.Listener {

        override fun onPlayerError(error: PlaybackException) {
            if (!isTransient(error)) return

            if (attempts < MaxAttempts) {
                retryIn(backoffMs(attempts))
                attempts++
            } else {
                awaitNetwork()
            }
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            // Audio is coming out again: whatever went wrong is over, and the next problem deserves
            // its own full set of attempts rather than the tail of this one's.
            if (playbackState == Player.STATE_READY) settle()
        }

        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            attempts = 0
        }
    }

    fun attach() {
        player.addListener(listener)
    }

    fun detach() {
        player.removeListener(listener)
        settle()
    }

    private fun retryIn(delayMs: Long) {
        retry?.cancel()

        retry = scope.launch {
            delay(delayMs)
            prepare()
        }
    }

    /**
     * Registering for the default network rather than polling. The callback arrives on a system
     * thread, so the player is touched back on [scope], which is the thread that owns it.
     */
    private fun awaitNetwork() {
        val manager = connectivity ?: return
        if (waiting != null) return

        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                scope.launch {
                    attempts = 0
                    stopWaiting()
                    prepare()
                }
            }
        }

        waiting = callback

        // A phone in flight mode can refuse the registration outright, and a player that is already
        // stopped is not made worse by that.
        runCatching { manager.registerDefaultNetworkCallback(callback) }
            .onFailure { waiting = null }
    }

    private fun stopWaiting() {
        val callback = waiting ?: return
        waiting = null
        runCatching { connectivity?.unregisterNetworkCallback(callback) }
    }

    private fun settle() {
        attempts = 0
        retry?.cancel()
        retry = null
        stopWaiting()
    }

    /** Only from IDLE: a player that recovered on its own in the meantime must not be restarted. */
    private fun prepare() {
        if (player.playbackState == Player.STATE_IDLE) player.prepare()
    }

    private fun isTransient(error: PlaybackException): Boolean {
        val status = generateSequence(error.cause) { it.cause }
            .filterIsInstance<HttpDataSource.InvalidResponseCodeException>()
            .firstOrNull()
            ?.responseCode

        if (status != null) return status >= 500 || status in RetryableStatuses

        return when (error.errorCode) {
            PlaybackException.ERROR_CODE_IO_UNSPECIFIED,
            PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
            PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT,
            -> true

            else -> false
        }
    }

    private fun backoffMs(attempt: Int): Long = minOf(1_000L shl attempt, MaxBackoffMs)

    private companion object {
        const val MaxAttempts = 4
        const val MaxBackoffMs = 15_000L

        /** 408 Request Timeout and 429 Too Many Requests. Everything else worth a retry is 5xx. */
        val RetryableStatuses = setOf(408, 429)
    }
}
