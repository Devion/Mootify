package kiwi.lazy.mootify.playback

import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import kiwi.lazy.mootify.data.MootifyRepository
import kiwi.lazy.mootify.data.SavePlaybackRequest
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * Tells the server where we are, so the phone, the car and the website are the same session.
 *
 * The pacing is the whole design. Position could be written on every tick, and then a drive would be
 * a thousand writes to a SQLite file that is also serving the website. Instead:
 *
 * - a periodic save while playing, at [SaveInterval];
 * - an immediate save at the moments that actually matter — a new song, a pause, the end of a queue;
 * - one play event per track, when we leave it, carrying how far it got. That's what makes the
 *   difference between a listen and a skip recordable at all.
 *
 * It is also the "listening along" heartbeat, and deliberately not a second timer next to this one.
 * The media id already carries the list a track was browsed from ([MediaId.Track.parent]), so a save
 * can say "playing track X out of playlist Y" for free. Whether that becomes visible to anybody else
 * is the account's setting on the server — this app reports and does not decide, which is what stops
 * a phone broadcasting after the website has been told to stop.
 *
 * Failures are dropped on purpose. This is telemetry for the user's own benefit; a tunnel is not
 * worth an error message.
 */
@UnstableApi
class PlaybackReporter(
    private val repository: MootifyRepository,
    private val player: Player,
    private val scope: CoroutineScope,
) {

    private var ticker: Job? = null

    /** The track we are counting seconds against, and how far it had got when we last looked. */
    private var currentTrackId: String? = null
    private var lastKnownPositionMs: Long = 0

    private val listener = object : Player.Listener {

        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            // Leaving a song: report the one we were on before adopting the new one, or the event
            // would carry the new track's zero position.
            flushPlay()
            currentTrackId = trackIdOf(mediaItem)
            lastKnownPositionMs = 0
            save()
        }

        override fun onIsPlayingChanged(isPlaying: Boolean) {
            if (isPlaying) {
                startTicking()
            } else {
                stopTicking()
                // A pause is the most likely last thing to happen before the process is killed, so
                // it is the most valuable moment to have written down.
                save()
            }
        }

        override fun onPositionDiscontinuity(
            oldPosition: Player.PositionInfo,
            newPosition: Player.PositionInfo,
            reason: Int,
        ) {
            if (reason == Player.DISCONTINUITY_REASON_SEEK) save()
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            if (playbackState == Player.STATE_ENDED) {
                flushPlay()
                save()
            }
        }
    }

    fun attach() {
        player.addListener(listener)
        currentTrackId = trackIdOf(player.currentMediaItem)
    }

    fun detach() {
        stopTicking()
        flushPlay()
        save()
        player.removeListener(listener)
    }

    private fun startTicking() {
        if (ticker?.isActive == true) return

        ticker = scope.launch {
            while (isActive) {
                delay(SaveInterval)
                remember()
                save()
            }
        }
    }

    private fun stopTicking() {
        remember()
        ticker?.cancel()
        ticker = null
    }

    /**
     * Keeps a copy of the position while the player still has one. By the time a track transition is
     * reported, `player.currentPosition` already belongs to the next song.
     */
    private fun remember() {
        if (trackIdOf(player.currentMediaItem) == currentTrackId) {
            lastKnownPositionMs = player.currentPosition
        }
    }

    private fun flushPlay() {
        val trackId = currentTrackId ?: return
        val played = maxOf(lastKnownPositionMs, positionIfStillCurrent(trackId))

        currentTrackId = null
        lastKnownPositionMs = 0

        if (played < MinimumReportableMs) return

        scope.launch { repository.recordPlay(trackId, played / 1000.0) }
    }

    private fun positionIfStillCurrent(trackId: String): Long =
        if (trackIdOf(player.currentMediaItem) == trackId) player.currentPosition else 0

    private fun save() {
        val queue = (0 until player.mediaItemCount).mapNotNull { trackIdOf(player.getMediaItemAt(it)) }
        val currentId = trackIdOf(player.currentMediaItem)
        val index = player.currentMediaItemIndex.coerceAtLeast(0)
        val positionSeconds = player.currentPosition.coerceAtLeast(0) / 1000.0
        val sourcePlaylistId = playlistIdOf(player.currentMediaItem)

        val repeat = when (player.repeatMode) {
            Player.REPEAT_MODE_ONE -> "One"
            Player.REPEAT_MODE_ALL -> "All"
            else -> "Off"
        }

        scope.launch {
            repository.savePlayback(
                SavePlaybackRequest(
                    currentTrackId = currentId,
                    positionSeconds = positionSeconds,
                    queue = queue,
                    queueIndex = index,
                    shuffleEnabled = player.shuffleModeEnabled,
                    repeat = repeat,
                    sourcePlaylistId = sourcePlaylistId,
                    isPlaying = player.isPlaying,
                ),
            )
        }
    }

    /** Track ids are carried in the media id, which also encodes where the track was browsed from. */
    private fun trackIdOf(item: MediaItem?): String? =
        (MediaId.decode(item?.mediaId) as? MediaId.Track)?.id

    /**
     * The playlist this track was picked out of, if it was. Null for an album, an artist, a search
     * or a resumed queue — and null is the right answer there rather than a missing one: there is no
     * set of people a shared album belongs to, so there is nothing to broadcast it to.
     */
    private fun playlistIdOf(item: MediaItem?): String? =
        ((MediaId.decode(item?.mediaId) as? MediaId.Track)?.parent as? MediaId.Playlist)?.id

    private companion object {
        const val SaveInterval = 20_000L

        /** Below this it was a skip, and the server's play history is better off without it. */
        const val MinimumReportableMs = 5_000L
    }
}
