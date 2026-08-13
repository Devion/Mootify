package kiwi.lazy.mootify.playback

import android.net.Uri
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.Timeline
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSpec
import androidx.media3.datasource.cache.CacheDataSource
import androidx.media3.datasource.cache.CacheWriter
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.isActive
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException
import kotlin.coroutines.coroutineContext

/**
 * Pulls the next few songs onto disk before they're needed, so a tunnel between two tracks is
 * silence the player never notices.
 *
 * ExoPlayer already buffers ahead into the *current* track and a little way into the next one; this
 * exists because "a little way into the next one" is a few seconds, and the gap in a car is minutes.
 * It writes into the same [CacheDataSource] cache the player reads through, so a prefetched track is
 * indistinguishable from one that was played before — there is no second store and no handoff.
 *
 * Three bounds, and they are the design, because the alternative is a phone quietly pulling a
 * gigabyte off somebody's data plan the moment they open a 600-song playlist:
 *
 * - **[TracksAhead] songs**, never the queue. A playlist is prefetched a rolling three tracks deep,
 *   re-aimed every time the current song changes.
 * - **[PerTrackBytes] per song and [WindowBytes] per pass**, counted against bytes actually pulled
 *   off the network — a song already on disk costs nothing and doesn't consume the budget. A long
 *   track is cached as a prefix and the rest streams normally, which is the same outcome, later.
 * - **The current song comes first.** Prefetching competes for the very bandwidth the thing playing
 *   right now is short of, so nothing starts until the current track is [CurrentBufferedPercent]
 *   buffered — or [MaxWaitMs] has passed and it plainly isn't going to be.
 *
 * Failures are silent on purpose. Every one of them means "this song will stream the ordinary way",
 * which is exactly what would have happened without this class.
 */
@UnstableApi
class MediaPrefetcher(
    private val dataSourceFactory: CacheDataSource.Factory,
    private val player: Player,
    private val scope: CoroutineScope,
) {

    private var job: Job? = null

    private val listener = object : Player.Listener {

        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) = schedule()

        // Covers the queue being replaced under us — tapping an album in the car arrives this way.
        override fun onTimelineChanged(timeline: Timeline, reason: Int) = schedule()

        override fun onShuffleModeEnabledChanged(shuffleModeEnabled: Boolean) = schedule()

        override fun onIsPlayingChanged(isPlaying: Boolean) {
            if (isPlaying) schedule()
        }
    }

    fun attach() {
        player.addListener(listener)
        schedule()
    }

    fun detach() {
        player.removeListener(listener)
        job?.cancel()
        job = null
    }

    /**
     * Cancelling the previous pass is what keeps a run of skips from stacking up downloads: the
     * writer for the track we no longer care about is torn down with the job that owns it (see
     * [cache]), so the bytes stop arriving rather than merely being ignored.
     */
    private fun schedule() {
        job?.cancel()

        job = scope.launch {
            waitForTheCurrentSong()

            val targets = upcoming()
            if (targets.isEmpty()) return@launch

            withContext(Dispatchers.IO) { fill(targets) }
        }
    }

    /** Runs on the player's thread, which is what [scope] is. */
    private suspend fun waitForTheCurrentSong() {
        var waited = 0L

        while (coroutineContext.isActive && waited < MaxWaitMs) {
            // A paused player has nothing to be starved of, so there is nothing to wait for.
            if (!player.isPlaying) return
            if (player.bufferedPercentage >= CurrentBufferedPercent) return

            delay(RecheckMs)
            waited += RecheckMs
        }
    }

    /**
     * The next [TracksAhead] songs in play order — shuffle included, since the car's shuffle button
     * changes what "next" means and prefetching the printed order would then be wrong.
     *
     * Walked with [Player.REPEAT_MODE_OFF] deliberately: under repeat-all a short queue would
     * otherwise wrap round onto songs already on disk and spend the window re-confirming them.
     */
    private fun upcoming(): List<Uri> {
        val timeline = player.currentTimeline
        if (timeline.isEmpty) return emptyList()

        val uris = mutableListOf<Uri>()
        var index = player.currentMediaItemIndex

        repeat(TracksAhead) {
            index = timeline.getNextWindowIndex(index, Player.REPEAT_MODE_OFF, player.shuffleModeEnabled)
            if (index == C.INDEX_UNSET) return uris

            // Browse items carry no URI by design; only a resolved, playable one does.
            player.getMediaItemAt(index).localConfiguration?.uri?.let(uris::add)
        }

        return uris
    }

    private suspend fun fill(uris: List<Uri>) {
        var remaining = WindowBytes

        for (uri in uris) {
            if (remaining <= 0) return
            coroutineContext.ensureActive()

            remaining -= cache(uri, minOf(remaining, PerTrackBytes))
        }
    }

    /**
     * Caches up to [limit] bytes of one track, and returns how many of them came off the network.
     *
     * Two things are load-bearing. The byte cap is enforced by cancelling the writer from its own
     * progress callback, because a [DataSpec] with an explicit length would have to be right about
     * the size of a file we haven't fetched yet. And the writer is tied to the coroutine's job:
     * `CacheWriter.cache()` is a blocking call, so cancelling the coroutine alone would let it run
     * to completion in the background — the completion handler is what actually stops it.
     */
    private suspend fun cache(uri: Uri, limit: Long): Long {
        var fetched = 0L
        lateinit var writer: CacheWriter

        writer = CacheWriter(
            dataSourceFactory.createDataSource(),
            DataSpec.Builder().setUri(uri).build(),
            /* temporaryBuffer = */ null,
        ) { _, _, newBytesCached ->
            fetched += newBytesCached
            if (fetched >= limit) writer.cancel()
        }

        val cancellation = coroutineContext.job.invokeOnCompletion { writer.cancel() }

        return try {
            writer.cache()
            fetched
        } catch (_: IOException) {
            // Cancelled at the cap, or the network went away. Both mean: it streams normally later.
            fetched
        } catch (_: InterruptedException) {
            fetched
        } finally {
            cancellation.dispose()
        }
    }

    private companion object {
        const val TracksAhead = 3
        const val PerTrackBytes = 12L * 1024 * 1024
        const val WindowBytes = 24L * 1024 * 1024

        const val CurrentBufferedPercent = 85
        const val RecheckMs = 4_000L
        const val MaxWaitMs = 60_000L
    }
}
