package kiwi.lazy.mootify.data

import android.content.Context
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.cache.CacheDataSource
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import retrofit2.Response
import java.io.IOException

/**
 * Everything the app knows how to ask a Mootify for.
 *
 * Two jobs beyond forwarding calls:
 *
 * 1. **Rebinding.** The server URL is typed by a person, and Retrofit wants it at construction time.
 *    So the client is rebuilt whenever the session's URL changes, rather than smuggling a host
 *    rewrite into an interceptor where nothing would explain it later.
 * 2. **Absolute URLs.** The API returns `/media/…` and `/art/album/…` because a proxied server
 *    doesn't know its own public name. Everything a player or an image loader is handed has to be
 *    absolute, and this is the only place that stitches the two halves together.
 *
 * Failures come back as [Result] rather than exceptions: a car with no signal is an expected state,
 * not an error condition worth unwinding a call stack for.
 */
@UnstableApi
class MootifyRepository(
    private val context: Context,
    val session: SessionStore,
) {

    val httpClient: OkHttpClient = Http.client(session)

    private var boundUrl: String? = null
    private var api: MootifyApi? = null

    /**
     * One cache-backed factory, shared. The player reads through it and `MediaPrefetcher` writes
     * through it, which only works because it is the same object — hence the concrete type rather
     * than `DataSource.Factory`.
     */
    val dataSourceFactory: CacheDataSource.Factory by lazy { Http.dataSourceFactory(context, httpClient) }

    /** The API bound to the current server, or null when nobody is signed in. */
    @Synchronized
    private fun api(): MootifyApi? {
        val serverUrl = session.current.value?.serverUrl ?: return null

        if (serverUrl != boundUrl || api == null) {
            api = Http.retrofit(serverUrl, httpClient).create(MootifyApi::class.java)
            boundUrl = serverUrl
        }

        return api
    }

    /**
     * A one-off client for a server nobody has signed into yet. Sign-in can't go through [api]
     * because [api] needs a session, and the session is what sign-in produces.
     */
    private fun apiFor(serverUrl: String): MootifyApi =
        Http.retrofit(SessionStore.normalize(serverUrl), httpClient).create(MootifyApi::class.java)

    val isSignedIn: Boolean get() = session.current.value != null

    /** Resolves a relative URL from the API against the server this session is signed into. */
    fun absolute(relative: String?): String? {
        if (relative.isNullOrBlank()) return null
        if (relative.startsWith("http://") || relative.startsWith("https://")) return relative

        val base = session.current.value?.serverUrl ?: return null
        return base.trimEnd('/') + "/" + relative.trimStart('/')
    }

    // ---- auth -------------------------------------------------------------

    suspend fun signIn(serverUrl: String, username: String, password: String, deviceName: String):
        Result<ApiTokenResponse> = withContext(Dispatchers.IO) {
        call { apiFor(serverUrl).signIn(TokenRequest(username, password, deviceName)) }
            .onSuccess { session.save(serverUrl, it) }
    }

    /**
     * Confirms the stored token still works and refreshes what the server can do. Called at startup:
     * finding out on the motorway that a token was revoked last week is worse than finding out in the
     * kitchen.
     */
    suspend fun refreshSession(): Result<ApiMe> = withContext(Dispatchers.IO) {
        call { api()?.me() }.onSuccess { session.updateServerInfo(it.server) }
    }

    suspend fun signOut() = withContext(Dispatchers.IO) {
        // Best effort: the point is to revoke the token server-side, but a device with no signal
        // still has to be able to sign out locally.
        runCatching { api()?.signOut() }
        session.clear()
    }

    // ---- library ----------------------------------------------------------

    suspend fun artists(
        query: String? = null,
        skip: Int = 0,
        take: Int = 400,
    ): Result<List<ApiArtist>> = page { api()?.artists(query = query, skip = skip, take = take) }

    suspend fun artist(artistId: String): Result<ApiArtistDetail> =
        io { api()?.artist(artistId) }

    suspend fun artistTracks(artistId: String): Result<List<ApiTrack>> =
        io { api()?.artistTracks(artistId) }

    suspend fun albums(
        artistId: String? = null,
        query: String? = null,
        recentFirst: Boolean = false,
        skip: Int = 0,
        take: Int = 400,
    ): Result<List<ApiAlbum>> = page {
        api()?.albums(
            artistId = artistId,
            query = query,
            sort = if (recentFirst) "recent" else null,
            skip = skip,
            take = take,
        )
    }

    suspend fun album(albumId: String): Result<ApiAlbumDetail> = io { api()?.album(albumId) }

    suspend fun search(query: String, take: Int = 20): Result<ApiSearchResults> =
        io { api()?.search(query, take) }

    suspend fun stats(): Result<ApiLibraryStats> = io { api()?.stats() }

    // ---- playlists --------------------------------------------------------

    suspend fun playlists(): Result<List<ApiPlaylist>> = io { api()?.playlists() }

    suspend fun playlist(playlistId: String): Result<ApiPlaylistDetail> =
        io { api()?.playlist(playlistId) }

    suspend fun createPlaylist(name: String, teamId: String? = null): Result<String> =
        io { api()?.createPlaylist(CreatePlaylistRequest(name, teamId)) }.map { it.id }

    suspend fun addTracks(playlistId: String, trackIds: List<String>): Result<Int> =
        io { api()?.addTracks(playlistId, AddTracksRequest(trackIds)) }.map { it.added }

    suspend fun removePlaylistItem(playlistId: String, itemId: String): Result<Unit> =
        io { api()?.removePlaylistItem(playlistId, itemId) }

    // ---- requests ---------------------------------------------------------

    suspend fun requests(): Result<List<ApiRequest>> = io { api()?.requests() }

    suspend fun searchRemote(query: String): Result<List<ApiRemoteAlbum>> =
        io { api()?.searchRemote(query) }

    suspend fun remoteTracks(albumMbid: String): Result<List<ApiRemoteTrack>> =
        io { api()?.remoteTracks(albumMbid) }

    suspend fun requestAlbum(
        album: ApiRemoteAlbum,
        searchTerm: String,
        targetPlaylistId: String?,
    ): Result<String> = io {
        api()?.createRequest(
            CreateRequestBody(
                albumMusicBrainzId = album.musicBrainzId,
                kind = "Album",
                targetPlaylistId = targetPlaylistId,
                searchTerm = searchTerm,
            ),
        )
    }.map { it.id }

    /**
     * One song. Lidarr still fetches the whole album — it can't do otherwise — and the recording id
     * is what picks this track out of it once it lands.
     */
    suspend fun requestTrack(
        album: ApiRemoteAlbum,
        track: ApiRemoteTrack,
        searchTerm: String,
        targetPlaylistId: String?,
    ): Result<String> = io {
        api()?.createRequest(
            CreateRequestBody(
                albumMusicBrainzId = album.musicBrainzId,
                kind = "Track",
                trackTitle = track.title,
                recordingMusicBrainzId = track.recordingId,
                targetPlaylistId = targetPlaylistId,
                searchTerm = searchTerm,
            ),
        )
    }.map { it.id }

    // ---- sync -------------------------------------------------------------

    suspend fun playbackState(): Result<ApiPlaybackState> = io { api()?.playback() }

    suspend fun playbackQueue(): Result<List<ApiTrack>> = io { api()?.playbackQueue() }

    suspend fun savePlayback(body: SavePlaybackRequest): Result<Unit> =
        io { api()?.savePlayback(body) }

    suspend fun recordPlay(trackId: String, secondsPlayed: Double): Result<Unit> =
        io { api()?.recordPlay(RecordPlayRequest(trackId, secondsPlayed)) }

    suspend fun notifications(): Result<ApiNotifications> = io { api()?.notifications() }

    suspend fun markNotificationsRead(): Result<Unit> = io { api()?.markNotificationsRead() }

    // ---- plumbing ---------------------------------------------------------

    private suspend fun <T> io(block: suspend () -> Response<T>?): Result<T> =
        withContext(Dispatchers.IO) { call(block) }

    private suspend fun <T> page(block: suspend () -> Response<ApiPage<T>>?): Result<List<T>> =
        io(block).map { it.items }

    /**
     * Turns a Retrofit response into a [Result] with a message worth showing someone. The server
     * answers refusals as `{"error": …}` or as ProblemDetails' `{"detail": …}`, and both are written
     * to be read by a person — "You have 5 requests still in flight" beats "HTTP 400".
     */
    private suspend fun <T> call(block: suspend () -> Response<T>?): Result<T> = try {
        val response = block() ?: return Result.failure(NotSignedIn())

        if (response.isSuccessful) {
            val body = response.body()

            @Suppress("UNCHECKED_CAST")
            when {
                body != null -> Result.success(body)
                // 204 on a Response<Unit> is a success with nothing in it.
                response.code() == 204 -> Result.success(Unit as T)
                else -> Result.failure(IOException("The server sent an empty answer."))
            }
        } else {
            Result.failure(ApiException(response.code(), errorMessage(response)))
        }
    } catch (e: IOException) {
        Result.failure(e)
    } catch (e: Exception) {
        Result.failure(e)
    }

    private fun errorMessage(response: Response<*>): String {
        val raw = runCatching { response.errorBody()?.string() }.getOrNull()

        val parsed = raw
            ?.takeIf { it.isNotBlank() && it.trimStart().startsWith("{") }
            ?.let { runCatching { Http.json.decodeFromString<ApiError>(it) }.getOrNull() }
            ?.message

        return parsed ?: when (response.code()) {
            401 -> "Signed out. Sign in again."
            403 -> "You're not allowed to do that."
            404 -> "That's not there any more."
            503 -> "The server isn't ready for that yet."
            else -> "The server said no (${response.code()})."
        }
    }
}

class ApiException(val code: Int, message: String) : IOException(message)

class NotSignedIn : IOException("Not signed in.")
