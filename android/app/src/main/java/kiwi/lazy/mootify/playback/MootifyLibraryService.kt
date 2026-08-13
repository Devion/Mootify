package kiwi.lazy.mootify.playback

import android.app.PendingIntent
import android.content.Intent
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.exoplayer.upstream.DefaultLoadErrorHandlingPolicy
import androidx.media3.session.DefaultMediaNotificationProvider
import androidx.media3.session.LibraryResult
import androidx.media3.session.MediaLibraryService
import androidx.media3.session.MediaSession
import com.google.common.collect.ImmutableList
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.SettableFuture
import kiwi.lazy.mootify.MootifyApp
import kiwi.lazy.mootify.R
import kiwi.lazy.mootify.data.ApiTrack
import kiwi.lazy.mootify.data.MootifyRepository
import kiwi.lazy.mootify.ui.MainActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * The car. Also the phone's player, the notification, the headset buttons and Assistant — Media3
 * routes all of them through one session, which is why this app has exactly one player and no
 * component keeps its own idea of what is playing.
 *
 * Four things in here are the difference between "it appears in Android Auto" and "it works in a car":
 *
 * 1. **The browse tree is fetched, not cached.** A head unit asks for children the moment it connects
 *    and expects an answer in a second or two, so every call is one HTTP request and nothing is
 *    pre-warmed. The server pages; so does this.
 *
 * 2. **Tapping a song plays the list it was in.** Android Auto sends the single item that was tapped
 *    and nothing else. [onSetMediaItems] rebuilds the album or playlist around it and starts at the
 *    right index — otherwise "next" ends the music.
 *
 * 3. **Browse items carry no playback URI.** Only [MediaItems.playable] sets one, resolved against
 *    the server this session is signed into. A car holding a browse list from a previous sign-in
 *    therefore can't play against a server that no longer knows it.
 *
 * 4. **Resumption asks the server where we were.** The car requests playback resumption on connect,
 *    before any Activity has existed; the answer comes from Mootify's saved playback state, so
 *    getting in the car continues what was playing in the kitchen.
 *
 * The player is then built for a bad connection rather than a good one, in three pieces that are
 * only useful together: a buffer sized to hold whole songs ([bufferForPatchySignal]), a rolling
 * read-ahead onto disk ([MediaPrefetcher]), and getting going again when a load finally fails
 * ([NetworkRecovery]). The first two are why the third rarely runs.
 */
@UnstableApi
class MootifyLibraryService : MediaLibraryService() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private lateinit var repository: MootifyRepository
    private lateinit var player: ExoPlayer
    private lateinit var librarySession: MediaLibrarySession
    private lateinit var reporter: PlaybackReporter
    private lateinit var prefetcher: MediaPrefetcher
    private lateinit var recovery: NetworkRecovery

    override fun onCreate() {
        super.onCreate()

        repository = (application as MootifyApp).repository

        player = ExoPlayer.Builder(this)
            // The authenticated, caching data source. This is where the bearer token reaches the
            // audio stream. The retry policy is raised well above the default three because the
            // failure this app actually sees is a car losing signal for ten seconds, not a broken
            // URL — and every retry that succeeds is a song that didn't stop.
            .setMediaSourceFactory(
                DefaultMediaSourceFactory(repository.dataSourceFactory)
                    .setLoadErrorHandlingPolicy(DefaultLoadErrorHandlingPolicy(LoadRetries)),
            )
            .setLoadControl(bufferForPatchySignal())
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(C.USAGE_MEDIA)
                    .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                    .build(),
                /* handleAudioFocus = */ true,
            )
            // Pause when the headphones are pulled out, and hold the network awake while playing —
            // both are what a music app is expected to do and neither is the default.
            .setHandleAudioBecomingNoisy(true)
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()

        librarySession = MediaLibrarySession.Builder(this, player, LibraryCallback())
            .setSessionActivity(openAppIntent())
            .build()

        setMediaNotificationProvider(
            DefaultMediaNotificationProvider.Builder(this)
                .setChannelId(PlaybackChannelId)
                .setChannelName(R.string.playback_channel_name)
                .build()
                .apply { setSmallIcon(R.drawable.ic_notification) },
        )

        reporter = PlaybackReporter(repository, player, scope).also { it.attach() }
        prefetcher = MediaPrefetcher(repository.dataSourceFactory, player, scope).also { it.attach() }
        recovery = NetworkRecovery(this, player, scope).also { it.attach() }
    }

    /**
     * Buffering sized for a car rather than for a sofa.
     *
     * The defaults hold about 50 seconds ahead, which is a reasonable trade when the network is a
     * home wifi and a bad one when it's a phone at 70mph. These numbers say: given the chance, read
     * five minutes ahead — in practice the whole of the current song, so losing signal in the middle
     * of one is inaudible.
     *
     * [DefaultLoadControl.Builder.setPrioritizeTimeOverSizeThresholds] is what makes the durations
     * mean anything; without it the byte threshold stops loading long before five minutes of audio.
     * The byte target is the real ceiling, and it is a memory figure, not a disk one — the disk side
     * is `MediaPrefetcher` and the cache's own 512MB.
     */
    private fun bufferForPatchySignal(): DefaultLoadControl = DefaultLoadControl.Builder()
        .setBufferDurationsMs(
            /* minBufferMs = */ 120_000,
            /* maxBufferMs = */ 300_000,
            // Unchanged: how long a song takes to start is what the user feels on every tap.
            /* bufferForPlaybackMs = */ 2_500,
            // Raised from five seconds. A stall means the network is bad *now*, so resuming on
            // barely any audio buys a few seconds of music and then stalls again; the second gap
            // is the one that sounds broken.
            /* bufferForPlaybackAfterRebufferMs = */ 8_000,
        )
        .setTargetBufferBytes(TargetBufferBytes)
        .setPrioritizeTimeOverSizeThresholds(true)
        // So a nudge backwards on the wheel replays from memory instead of refetching.
        .setBackBuffer(/* backBufferDurationMs = */ 30_000, /* retainBackBufferFromKeyframe = */ true)
        .build()

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaLibrarySession =
        librarySession

    /**
     * Swiping the app away shouldn't stop the car. Media3's default behaviour stops the service when
     * the task is removed, which is right for a paused player and wrong for one that is playing
     * through a head unit the user is still looking at.
     */
    override fun onTaskRemoved(rootIntent: Intent?) {
        if (!player.playWhenReady || player.mediaItemCount == 0) {
            stopSelf()
        }
    }

    override fun onDestroy() {
        recovery.detach()
        prefetcher.detach()
        reporter.detach()
        librarySession.release()
        player.release()
        scope.cancel()
        super.onDestroy()
    }

    private fun openAppIntent(): PendingIntent = PendingIntent.getActivity(
        this,
        0,
        Intent(this, MainActivity::class.java),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    // ---- the browse tree --------------------------------------------------

    private inner class LibraryCallback : MediaLibrarySession.Callback {

        override fun onGetLibraryRoot(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            params: LibraryParams?,
        ): ListenableFuture<LibraryResult<MediaItem>> = immediate(
            LibraryResult.ofItem(
                MediaItems.browsable(
                    id = MediaId.Root,
                    title = getString(R.string.app_name),
                    mediaType = MediaMetadata.MEDIA_TYPE_FOLDER_MIXED,
                ),
                params,
            ),
        )

        override fun onGetChildren(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            parentId: String,
            page: Int,
            pageSize: Int,
            params: LibraryParams?,
        ): ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> = future {
            val parent = MediaId.decode(parentId)
                ?: return@future LibraryResult.ofError(LibraryResult.RESULT_ERROR_BAD_VALUE)

            if (!repository.isSignedIn) {
                // A car can't show a login form, so it gets one row that says where to go.
                return@future LibraryResult.ofItemList(
                    ImmutableList.of(
                        MediaItems.browsable(MediaId.SignIn, getString(R.string.browse_sign_in)),
                    ),
                    params,
                )
            }

            val children = childrenOf(parent, page, pageSize)
                ?: return@future LibraryResult.ofError(LibraryResult.RESULT_ERROR_IO)

            LibraryResult.ofItemList(ImmutableList.copyOf(children), params)
        }

        override fun onGetItem(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            mediaId: String,
        ): ListenableFuture<LibraryResult<MediaItem>> = future {
            val item = itemFor(MediaId.decode(mediaId))
            if (item == null) {
                LibraryResult.ofError(LibraryResult.RESULT_ERROR_BAD_VALUE)
            } else {
                LibraryResult.ofItem(item, null)
            }
        }

        /**
         * Voice search. The car calls this, then asks for the results separately, so the work is done
         * here and handed to [onGetSearchResult] through the server's own search — which is one
         * request answering artists, albums and tracks at once for exactly this reason.
         */
        override fun onSearch(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            query: String,
            params: LibraryParams?,
        ): ListenableFuture<LibraryResult<Void>> = future {
            val results = searchItems(query)
            session.notifySearchResultChanged(browser, query, results.size, params)
            LibraryResult.ofVoid()
        }

        override fun onGetSearchResult(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            query: String,
            page: Int,
            pageSize: Int,
            params: LibraryParams?,
        ): ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> = future {
            val results = searchItems(query)
            LibraryResult.ofItemList(ImmutableList.copyOf(paged(results, page, pageSize)), params)
        }

        /**
         * Everything that reaches the player passes through here. Items arriving from a browse list
         * or from Assistant have a media id and no URI; this fills them in, and expands anything
         * browsable (an album, a playlist, an artist) into the songs it contains.
         */
        override fun onAddMediaItems(
            mediaSession: MediaSession,
            controller: MediaSession.ControllerInfo,
            mediaItems: MutableList<MediaItem>,
        ): ListenableFuture<MutableList<MediaItem>> = future {
            resolve(mediaItems).toMutableList()
        }

        /**
         * The tap-a-song-in-an-album case. [startIndex] arrives as INDEX_UNSET with a single item, so
         * the queue is rebuilt from that song's parent and the index is where it sits in it.
         */
        override fun onSetMediaItems(
            mediaSession: MediaSession,
            controller: MediaSession.ControllerInfo,
            mediaItems: MutableList<MediaItem>,
            startIndex: Int,
            startPositionMs: Long,
        ): ListenableFuture<MediaSession.MediaItemsWithStartPosition> = future {
            val single = mediaItems.singleOrNull()
            val parsed = MediaId.decode(single?.mediaId)

            if (single != null && parsed is MediaId.Track && parsed.parent != null) {
                val siblings = tracksOf(parsed.parent)

                if (siblings != null && siblings.isNotEmpty()) {
                    val index = siblings.indexOfFirst { it.id == parsed.id }.coerceAtLeast(0)
                    val queue = siblings.map { MediaItems.playable(it, parsed.parent, repository) }

                    return@future MediaSession.MediaItemsWithStartPosition(queue, index, startPositionMs)
                }
            }

            val resolved = resolve(mediaItems)
            MediaSession.MediaItemsWithStartPosition(
                resolved,
                startIndex.takeIf { it != C.INDEX_UNSET && it < resolved.size } ?: 0,
                startPositionMs,
            )
        }

        /**
         * Asked when the car (or a media button) wants to continue where we left off, potentially
         * before anything else has happened in this process. The answer is the queue Mootify saved —
         * which the website and any other device write to as well.
         */
        override fun onPlaybackResumption(
            mediaSession: MediaSession,
            controller: MediaSession.ControllerInfo,
            isForPlayback: Boolean,
        ): ListenableFuture<MediaSession.MediaItemsWithStartPosition> = future {
            val state = repository.playbackState().getOrNull()
                ?: throw IllegalStateException("No playback state to resume.")

            val tracks = repository.playbackQueue().getOrNull().orEmpty()
            if (tracks.isEmpty()) throw IllegalStateException("Nothing in the saved queue.")

            val items = tracks.map { MediaItems.playable(it, null, repository) }
            val index = state.queueIndex.coerceIn(0, items.lastIndex)

            MediaSession.MediaItemsWithStartPosition(
                items,
                index,
                (state.positionSeconds * 1000).toLong().coerceAtLeast(0),
            )
        }
    }

    // ---- resolving --------------------------------------------------------

    /**
     * Fills in playback URIs, expands browsable items, and turns a voice query into songs.
     * Anything it can't make sense of is dropped rather than passed on as an item that would fail at
     * playback time.
     */
    private suspend fun resolve(items: List<MediaItem>): List<MediaItem> = items.flatMap { item ->
        // "Play Nirvana on Mootify": Assistant sends an item with a search query and no id.
        val query = item.requestMetadata.searchQuery
        if (!query.isNullOrBlank()) {
            return@flatMap searchTracks(query).map { MediaItems.playable(it, MediaId.Search(query), repository) }
        }

        val id = MediaId.decode(item.mediaId) ?: return@flatMap emptyList()

        when (id) {
            is MediaId.Track ->
                // Already playable if it came from us with a URI; otherwise look it up.
                if (item.localConfiguration != null) {
                    listOf(item)
                } else {
                    trackById(id, id.parent)?.let { listOf(it) }.orEmpty()
                }

            else -> tracksOf(id).orEmpty().map { MediaItems.playable(it, id, repository) }
        }
    }

    private suspend fun trackById(id: MediaId.Track, parent: MediaId?): MediaItem? {
        // There is no "get one track" endpoint, and there doesn't need to be: a track is always
        // reachable through the list it belongs to, and that list is what we want anyway.
        val siblings = parent?.let { tracksOf(it) } ?: return null
        val track = siblings.firstOrNull { it.id == id.id } ?: return null
        return MediaItems.playable(track, parent, repository)
    }

    /** The songs behind a browse node, or null when the server couldn't be reached. */
    private suspend fun tracksOf(id: MediaId): List<ApiTrack>? = when (id) {
        is MediaId.Album -> repository.album(id.id).map { it.tracks }.getOrNull()
        is MediaId.Artist -> repository.artistTracks(id.id).getOrNull()
        is MediaId.Playlist -> repository.playlist(id.id).map { detail -> detail.items.map { it.track } }.getOrNull()
        is MediaId.Search -> searchTracks(id.query)
        is MediaId.Track -> id.parent?.let { tracksOf(it) }
        else -> emptyList()
    }

    private suspend fun searchTracks(query: String): List<ApiTrack> {
        val results = repository.search(query, take = 50).getOrNull() ?: return emptyList()

        // Songs first, then whatever an album or artist match implies. "Play Nevermind" and "play
        // Nirvana" both have to end in audio, not in a list nobody can tap while driving.
        if (results.tracks.isNotEmpty()) return results.tracks

        results.albums.firstOrNull()?.let { album ->
            repository.album(album.id).getOrNull()?.let { return it.tracks }
        }

        results.artists.firstOrNull()?.let { artist ->
            repository.artistTracks(artist.id).getOrNull()?.let { return it }
        }

        return emptyList()
    }

    // ---- children ---------------------------------------------------------

    private suspend fun childrenOf(parent: MediaId, page: Int, pageSize: Int): List<MediaItem>? =
        when (parent) {
            MediaId.Root -> listOf(
                MediaItems.browsable(MediaId.Playlists, getString(R.string.browse_playlists), mediaType = MediaMetadata.MEDIA_TYPE_FOLDER_PLAYLISTS),
                MediaItems.browsable(MediaId.Recent, getString(R.string.browse_recent), gridChildren = true, mediaType = MediaMetadata.MEDIA_TYPE_FOLDER_ALBUMS),
                MediaItems.browsable(MediaId.Albums, getString(R.string.browse_albums), gridChildren = true, mediaType = MediaMetadata.MEDIA_TYPE_FOLDER_ALBUMS),
                MediaItems.browsable(MediaId.Artists, getString(R.string.browse_artists), mediaType = MediaMetadata.MEDIA_TYPE_FOLDER_ARTISTS),
            )

            MediaId.Playlists -> repository.playlists().getOrNull()
                ?.let { paged(it, page, pageSize).map(MediaItems::fromPlaylist) }

            // Artists and albums are the two lists that can be thousands long, and they are the two
            // the server pages for us — so these ask for the page the car asked for rather than
            // pulling the library down and slicing it.
            MediaId.Artists -> repository.artists(skip = page * size(pageSize), take = size(pageSize))
                .getOrNull()
                ?.map(MediaItems::fromArtist)

            MediaId.Albums -> repository.albums(skip = page * size(pageSize), take = size(pageSize))
                .getOrNull()
                ?.map { album -> MediaItems.fromAlbum(album, repository) }

            MediaId.Recent -> repository.albums(
                recentFirst = true,
                skip = page * size(pageSize),
                take = size(pageSize),
            ).getOrNull()
                ?.map { album -> MediaItems.fromAlbum(album, repository) }

            is MediaId.Artist -> repository.artist(parent.id).getOrNull()
                ?.let { paged(it.albums, page, pageSize).map { album -> MediaItems.fromAlbum(album, repository) } }

            is MediaId.Album -> repository.album(parent.id).getOrNull()
                ?.let { detail ->
                    paged(detail.tracks, page, pageSize).map { MediaItems.playable(it, parent, repository) }
                }

            is MediaId.Playlist -> repository.playlist(parent.id).getOrNull()
                ?.let { detail ->
                    paged(detail.items.map { it.track }, page, pageSize)
                        .map { MediaItems.playable(it, parent, repository) }
                }

            is MediaId.Search -> paged(searchItems(parent.query), page, pageSize)

            MediaId.SignIn -> emptyList()

            is MediaId.Track -> emptyList()
        }

    private suspend fun itemFor(id: MediaId?): MediaItem? = when (id) {
        null -> null
        MediaId.Root -> MediaItems.browsable(MediaId.Root, getString(R.string.app_name))
        MediaId.Playlists -> MediaItems.browsable(id, getString(R.string.browse_playlists))
        MediaId.Artists -> MediaItems.browsable(id, getString(R.string.browse_artists))
        MediaId.Albums -> MediaItems.browsable(id, getString(R.string.browse_albums))
        MediaId.Recent -> MediaItems.browsable(id, getString(R.string.browse_recent))
        MediaId.SignIn -> MediaItems.browsable(id, getString(R.string.browse_sign_in))
        is MediaId.Search -> MediaItems.browsable(id, id.query)
        is MediaId.Album -> repository.album(id.id).getOrNull()
            ?.let { MediaItems.fromAlbum(it.album, repository) }
        is MediaId.Artist -> repository.artist(id.id).getOrNull()
            ?.let { MediaItems.fromArtist(it.artist) }
        is MediaId.Playlist -> repository.playlists().getOrNull()
            ?.firstOrNull { it.id == id.id }
            ?.let(MediaItems::fromPlaylist)
        is MediaId.Track -> trackById(id, id.parent)
    }

    /** Search results as browse items: songs to play, plus the albums and artists that matched. */
    private suspend fun searchItems(query: String): List<MediaItem> {
        val results = repository.search(query, take = 30).getOrNull() ?: return emptyList()
        val parent = MediaId.Search(query)

        return buildList {
            results.tracks.forEach { add(MediaItems.playable(it, parent, repository)) }
            results.albums.forEach { add(MediaItems.fromAlbum(it, repository)) }
            results.artists.forEach { add(MediaItems.fromArtist(it)) }
        }
    }

    /**
     * Local paging, for lists the server hands over whole (a playlist, an album's tracks). Done in
     * Long arithmetic because a browser asking for Integer.MAX_VALUE items is a real thing, and
     * `page * pageSize` in Int would come back negative.
     */
    private fun <T> paged(items: List<T>, page: Int, pageSize: Int): List<T> {
        val take = size(pageSize)
        val from = page.coerceAtLeast(0).toLong() * take

        if (from >= items.size) return emptyList()

        val start = from.toInt()
        return items.subList(start, minOf(start + take, items.size))
    }

    /** A page size worth asking the server for. It clamps too; this keeps the arithmetic sane. */
    private fun size(pageSize: Int): Int = if (pageSize <= 0) 100 else pageSize.coerceAtMost(200)

    // ---- futures ----------------------------------------------------------

    /**
     * Media3's callbacks are ListenableFuture-based and the work behind them is suspending. This is
     * the bridge, kept deliberately small: launch on the session's scope, complete the future, and
     * let a failure become the future's exception rather than a crash on the main thread.
     */
    private fun <T> future(block: suspend () -> T): ListenableFuture<T> {
        val future = SettableFuture.create<T>()

        scope.launch {
            try {
                future.set(block())
            } catch (t: Throwable) {
                future.setException(t)
            }
        }

        return future
    }

    private fun <T> immediate(value: T): ListenableFuture<T> = Futures.immediateFuture(value)

    private companion object {
        const val PlaybackChannelId = "mootify_playback"

        /** Media3's default is 3, which is about four seconds of trying before a song gives up. */
        const val LoadRetries = 8

        /** In memory, and reached only by a long track: five minutes of MP3 is nearer 12MB. */
        const val TargetBufferBytes = 24 * 1024 * 1024
    }
}
