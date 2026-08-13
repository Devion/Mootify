package kiwi.lazy.mootify.data

import retrofit2.Response
import retrofit2.http.Body
import retrofit2.http.DELETE
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.PUT
import retrofit2.http.Path
import retrofit2.http.Query

/**
 * `/api/v1`, as Retrofit sees it.
 *
 * Paths are relative with no leading slash so they resolve against a base URL that may include a
 * sub-path — a Mootify reverse-proxied at `https://host/mootify/` is a normal way to run this, and
 * a leading slash would throw that prefix away.
 */
interface MootifyApi {

    // ---- auth -------------------------------------------------------------

    @POST("api/v1/auth/token")
    suspend fun signIn(@Body body: TokenRequest): Response<ApiTokenResponse>

    @GET("api/v1/me")
    suspend fun me(): Response<ApiMe>

    @POST("api/v1/auth/logout")
    suspend fun signOut(): Response<Unit>

    // ---- library ----------------------------------------------------------

    @GET("api/v1/library/artists")
    suspend fun artists(
        @Query("q") query: String? = null,
        @Query("skip") skip: Int = 0,
        @Query("take") take: Int = 200,
    ): Response<ApiPage<ApiArtist>>

    @GET("api/v1/library/artists/{id}")
    suspend fun artist(@Path("id") id: String): Response<ApiArtistDetail>

    @GET("api/v1/library/artists/{id}/tracks")
    suspend fun artistTracks(@Path("id") id: String): Response<List<ApiTrack>>

    @GET("api/v1/library/albums")
    suspend fun albums(
        @Query("artistId") artistId: String? = null,
        @Query("q") query: String? = null,
        @Query("sort") sort: String? = null,
        @Query("skip") skip: Int = 0,
        @Query("take") take: Int = 200,
    ): Response<ApiPage<ApiAlbum>>

    @GET("api/v1/library/albums/{id}")
    suspend fun album(@Path("id") id: String): Response<ApiAlbumDetail>

    @GET("api/v1/library/tracks")
    suspend fun tracks(
        @Query("albumId") albumId: String? = null,
        @Query("artistId") artistId: String? = null,
        @Query("q") query: String? = null,
        @Query("skip") skip: Int = 0,
        @Query("take") take: Int = 200,
    ): Response<ApiPage<ApiTrack>>

    /** One call for a voice search — the car can't afford three round trips before it plays. */
    @GET("api/v1/library/search")
    suspend fun search(
        @Query("q") query: String,
        @Query("take") take: Int = 20,
    ): Response<ApiSearchResults>

    @GET("api/v1/library/stats")
    suspend fun stats(): Response<ApiLibraryStats>

    // ---- playlists --------------------------------------------------------

    @GET("api/v1/playlists")
    suspend fun playlists(): Response<List<ApiPlaylist>>

    @GET("api/v1/playlists/{id}")
    suspend fun playlist(@Path("id") id: String): Response<ApiPlaylistDetail>

    @POST("api/v1/playlists")
    suspend fun createPlaylist(@Body body: CreatePlaylistRequest): Response<CreatedId>

    @POST("api/v1/playlists/{id}/tracks")
    suspend fun addTracks(
        @Path("id") id: String,
        @Body body: AddTracksRequest,
    ): Response<AddedCount>

    @DELETE("api/v1/playlists/{playlistId}/items/{itemId}")
    suspend fun removePlaylistItem(
        @Path("playlistId") playlistId: String,
        @Path("itemId") itemId: String,
    ): Response<Unit>

    // ---- requests ---------------------------------------------------------

    @GET("api/v1/requests")
    suspend fun requests(): Response<List<ApiRequest>>

    @GET("api/v1/requests/search")
    suspend fun searchRemote(@Query("q") query: String): Response<List<ApiRemoteAlbum>>

    @GET("api/v1/requests/albums/{mbid}/tracks")
    suspend fun remoteTracks(@Path("mbid") albumMbid: String): Response<List<ApiRemoteTrack>>

    @POST("api/v1/requests")
    suspend fun createRequest(@Body body: CreateRequestBody): Response<CreatedId>

    // ---- sync -------------------------------------------------------------

    @GET("api/v1/playback")
    suspend fun playback(): Response<ApiPlaybackState>

    @GET("api/v1/playback/queue")
    suspend fun playbackQueue(): Response<List<ApiTrack>>

    @PUT("api/v1/playback")
    suspend fun savePlayback(@Body body: SavePlaybackRequest): Response<Unit>

    @POST("api/v1/plays")
    suspend fun recordPlay(@Body body: RecordPlayRequest): Response<Unit>

    @GET("api/v1/notifications")
    suspend fun notifications(@Query("take") take: Int = 20): Response<ApiNotifications>

    @POST("api/v1/notifications/read-all")
    suspend fun markNotificationsRead(): Response<Unit>
}
