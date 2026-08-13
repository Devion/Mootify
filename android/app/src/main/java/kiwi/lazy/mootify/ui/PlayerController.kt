package kiwi.lazy.mootify.ui

import android.content.ComponentName
import android.content.Context
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import kiwi.lazy.mootify.data.ApiTrack
import kiwi.lazy.mootify.data.MootifyRepository
import kiwi.lazy.mootify.playback.MediaId
import kiwi.lazy.mootify.playback.MediaItems
import kiwi.lazy.mootify.playback.MootifyLibraryService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.guava.await
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

data class PlayerUiState(
    val connected: Boolean = false,
    val isPlaying: Boolean = false,
    val title: String? = null,
    val artist: String? = null,
    val artUri: String? = null,
    val trackId: String? = null,
    val positionMs: Long = 0,
    val durationMs: Long = 0,
    val shuffle: Boolean = false,
    val repeatMode: Int = Player.REPEAT_MODE_OFF,
    val hasNext: Boolean = false,
    val hasPrevious: Boolean = false,
) {
    val hasSomethingLoaded: Boolean get() = title != null
}

/**
 * The phone UI's handle on playback — a [MediaController] pointed at the same session Android Auto
 * uses.
 *
 * Deliberately not its own player. Two players in one app means two things holding audio focus and a
 * play bar that disagrees with the notification; the website has the same rule about
 * `PlayerService` owning playback state, and this is the mobile version of it.
 */
@UnstableApi
class PlayerController(
    private val context: Context,
    private val repository: MootifyRepository,
    private val scope: CoroutineScope,
) {

    private val _state = MutableStateFlow(PlayerUiState())
    val state: StateFlow<PlayerUiState> = _state

    private var controller: MediaController? = null
    private var ticker: Job? = null

    private val listener = object : Player.Listener {
        override fun onEvents(player: Player, events: Player.Events) = publish()
    }

    fun connect() {
        if (controller != null) return

        scope.launch {
            val token = SessionToken(context, ComponentName(context, MootifyLibraryService::class.java))

            val connected = runCatching { MediaController.Builder(context, token).buildAsync().await() }
                .getOrNull() ?: return@launch

            controller = connected
            connected.addListener(listener)
            publish()
            startTicking()
        }
    }

    fun release() {
        ticker?.cancel()
        controller?.removeListener(listener)
        controller?.release()
        controller = null
        _state.value = PlayerUiState()
    }

    // ---- commands ---------------------------------------------------------

    /**
     * Plays a list from a given position. The whole list goes to the session rather than one track,
     * so "next" works and the queue the user can see is the queue that exists.
     */
    fun play(tracks: List<ApiTrack>, index: Int = 0, parent: MediaId? = null) {
        val target = controller ?: return
        if (tracks.isEmpty()) return

        val items = tracks.map { MediaItems.playable(it, parent, repository) }

        target.setMediaItems(items, index.coerceIn(0, items.lastIndex), 0)
        target.prepare()
        target.play()
    }

    /** Appends to whatever is playing, or starts playing if nothing is. */
    fun queue(tracks: List<ApiTrack>, parent: MediaId? = null) {
        val target = controller ?: return
        if (tracks.isEmpty()) return

        val items: List<MediaItem> = tracks.map { MediaItems.playable(it, parent, repository) }

        if (target.mediaItemCount == 0) {
            play(tracks, 0, parent)
        } else {
            target.addMediaItems(items)
        }
    }

    fun togglePlay() {
        val target = controller ?: return

        if (target.isPlaying) {
            target.pause()
        } else {
            // A player that reached the end of its queue needs a seek before it will play again.
            if (target.playbackState == Player.STATE_ENDED) target.seekTo(0, 0)
            target.play()
        }
    }

    fun next() {
        controller?.seekToNextMediaItem()
    }

    fun previous() {
        val target = controller ?: return
        // Restart the song if we're past the first few seconds — what every other player does.
        if (target.currentPosition > 3_000 || !target.hasPreviousMediaItem()) {
            target.seekTo(0)
        } else {
            target.seekToPreviousMediaItem()
        }
    }

    fun seekTo(positionMs: Long) {
        controller?.seekTo(positionMs)
    }

    fun toggleShuffle() {
        val target = controller ?: return
        target.shuffleModeEnabled = !target.shuffleModeEnabled
    }

    fun cycleRepeat() {
        val target = controller ?: return
        target.repeatMode = when (target.repeatMode) {
            Player.REPEAT_MODE_OFF -> Player.REPEAT_MODE_ALL
            Player.REPEAT_MODE_ALL -> Player.REPEAT_MODE_ONE
            else -> Player.REPEAT_MODE_OFF
        }
    }

    // ---- state ------------------------------------------------------------

    private fun startTicking() {
        ticker?.cancel()
        ticker = scope.launch {
            while (isActive) {
                // Only while it matters: a progress bar nobody is watching move doesn't need waking
                // the UI thread five times a second.
                if (controller?.isPlaying == true) publish()
                delay(500)
            }
        }
    }

    private fun publish() {
        val target = controller

        if (target == null) {
            _state.value = PlayerUiState()
            return
        }

        val metadata = target.mediaMetadata

        _state.value = PlayerUiState(
            connected = true,
            isPlaying = target.isPlaying,
            title = metadata.title?.toString(),
            artist = metadata.artist?.toString() ?: metadata.albumTitle?.toString(),
            artUri = metadata.artworkUri?.toString(),
            trackId = (MediaId.decode(target.currentMediaItem?.mediaId) as? MediaId.Track)?.id,
            positionMs = target.currentPosition.coerceAtLeast(0),
            durationMs = target.duration.takeIf { it > 0 } ?: 0,
            shuffle = target.shuffleModeEnabled,
            repeatMode = target.repeatMode,
            hasNext = target.hasNextMediaItem(),
            hasPrevious = target.hasPreviousMediaItem(),
        )
    }
}
