package kiwi.lazy.mootify.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * The wire types, mirroring `src/Mootify/Endpoints/Api/ApiContracts.cs`.
 *
 * Three conventions come from the server and are worth knowing before editing anything here:
 *
 * - Durations are milliseconds (`Long`). The server refuses to send a .NET TimeSpan because
 *   `"00:03:41.2340000"` is a parsing job for every client.
 * - URLs (`streamUrl`, `artUrl`) are **relative**. The server sits behind a reverse proxy and does
 *   not reliably know its own public name, so resolving them against the base URL is this app's
 *   job — see [MootifyRepository.absolute].
 * - Enums arrive as names, not numbers, so a build of this app that predates a new server enum
 *   member fails to match rather than silently picking the wrong one.
 *
 * Every field the app doesn't need is simply absent: `ignoreUnknownKeys` is on, so the server can
 * grow without this file changing.
 */
@Serializable
data class ApiUser(
    val id: String,
    val displayName: String,
    val isAdmin: Boolean = false,
)

@Serializable
data class ApiServerInfo(
    val name: String = "Mootify",
    val apiVersion: Int = 1,
    val lidarrConfigured: Boolean = false,
    val registrationOpen: Boolean = false,
    val maxPageSize: Int = 500,
)

@Serializable
data class TokenRequest(
    val username: String,
    val password: String,
    val deviceName: String,
)

@Serializable
data class ApiTokenResponse(
    val token: String,
    val expiresAt: String? = null,
    val user: ApiUser,
    val server: ApiServerInfo,
)

@Serializable
data class ApiMe(
    val user: ApiUser,
    val server: ApiServerInfo,
)

@Serializable
data class ApiArtist(
    val id: String,
    val name: String,
    val sortName: String = "",
    val albumCount: Int = 0,
    val trackCount: Int = 0,
)

@Serializable
data class ApiAlbum(
    val id: String,
    val title: String,
    val artistId: String,
    val artistName: String,
    val year: Int? = null,
    val trackCount: Int = 0,
    val durationMs: Long = 0,
    val artUrl: String,
)

@Serializable
data class ApiTrack(
    val id: String,
    val title: String,
    val artistId: String,
    val artistName: String,
    val albumId: String,
    val albumTitle: String,
    val year: Int? = null,
    val trackNumber: Int = 0,
    val discNumber: Int = 0,
    val durationMs: Long = 0,
    val bitrate: Int = 0,
    val streamUrl: String,
    val artUrl: String,
)

@Serializable
data class ApiPlaylist(
    val id: String,
    val name: String,
    val description: String? = null,
    val trackCount: Int = 0,
    val durationMs: Long = 0,
    val teamId: String? = null,
    val teamName: String? = null,
    val isTeamPlaylist: Boolean = false,
)

@Serializable
data class ApiPlaylistItem(
    val id: String,
    val track: ApiTrack,
    val addedAt: String? = null,
)

/**
 * A playlist header plus **one page** of its contents.
 *
 * [items] used to be the whole list, and browsing a 200-track playlist in the car re-fetched all
 * 200 rows for every twenty it drew — once per page, on a phone, over mobile data. It is now an
 * [ApiPage]; [ApiPage.total] is the length of the playlist, so a pager needs no second call.
 *
 * [trackCount] and [durationMs] describe the playlist rather than the page, which is what the
 * header wants — summing the page would say "12 songs" about a list of 200.
 *
 * Server API version 2. Older servers send `items` as a bare array and this will fail to parse,
 * which is the intended failure: showing the first hundred as though they were all of them is worse.
 */
@Serializable
data class ApiPlaylistDetail(
    val id: String,
    val name: String,
    val description: String? = null,
    val teamId: String? = null,
    val teamName: String? = null,
    val canEdit: Boolean = false,
    val canDelete: Boolean = false,
    val trackCount: Int = 0,
    val durationMs: Long = 0,
    val items: ApiPage<ApiPlaylistItem> = ApiPage(),
)

@Serializable
data class ApiPage<T>(
    val total: Int = 0,
    val skip: Int = 0,
    val take: Int = 0,
    val items: List<T> = emptyList(),
)

@Serializable
data class ApiArtistDetail(
    val artist: ApiArtist,
    val albums: List<ApiAlbum> = emptyList(),
)

@Serializable
data class ApiAlbumDetail(
    val album: ApiAlbum,
    val tracks: List<ApiTrack> = emptyList(),
)

@Serializable
data class ApiSearchResults(
    val artists: List<ApiArtist> = emptyList(),
    val albums: List<ApiAlbum> = emptyList(),
    val tracks: List<ApiTrack> = emptyList(),
)

@Serializable
data class ApiLibraryStats(
    val artists: Int = 0,
    val albums: Int = 0,
    val tracks: Int = 0,
    val durationMs: Long = 0,
    val lastAddedAt: String? = null,
)

@Serializable
data class ApiRemoteAlbum(
    val musicBrainzId: String? = null,
    val title: String,
    val artistName: String = "",
    val artistMusicBrainzId: String? = null,
    val year: Int? = null,
    val albumType: String? = null,
    val coverUrl: String? = null,
    val alreadyInLibrary: Boolean = false,
)

@Serializable
data class ApiRemoteTrack(
    val position: Int = 0,
    val title: String,
    val recordingId: String? = null,
    val durationMs: Long? = null,
)

@Serializable
data class ApiRequest(
    val id: String,
    val kind: String = "Album",
    val status: String = "Pending",
    val query: String = "",
    val artistName: String = "",
    val albumTitle: String? = null,
    val trackTitle: String? = null,
    val targetPlaylistId: String? = null,
    val targetPlaylistName: String? = null,
    val failureReason: String? = null,
    val createdAt: String? = null,
    val completedAt: String? = null,
) {
    /** Matches `Request.IsOpen` on the server: anything that hasn't finished one way or another. */
    val isOpen: Boolean get() = status !in setOf("Available", "NotFound", "Failed")
}

@Serializable
data class CreateRequestBody(
    val albumMusicBrainzId: String?,
    val kind: String,
    val trackTitle: String? = null,
    val recordingMusicBrainzId: String? = null,
    val targetPlaylistId: String? = null,
    /** The term that produced this album, so the server can re-find it if its cache expired. */
    val searchTerm: String? = null,
)

@Serializable
data class ApiNotification(
    val id: String,
    val type: String = "Info",
    val title: String = "",
    val body: String = "",
    val url: String? = null,
    val createdAt: String? = null,
    val readAt: String? = null,
)

@Serializable
data class ApiNotifications(
    val unread: Int = 0,
    val items: List<ApiNotification> = emptyList(),
)

@Serializable
data class ApiPlaybackState(
    val currentTrackId: String? = null,
    val positionSeconds: Double = 0.0,
    val queue: List<String> = emptyList(),
    val queueIndex: Int = 0,
    val shuffleEnabled: Boolean = false,
    val repeat: String = "Off",
    val updatedAt: String? = null,
)

/**
 * Where we are, and — when the current track came out of a playlist — which one.
 *
 * [sourcePlaylistId] and [isPlaying] are what make this call double as the "listening along"
 * heartbeat. The app always reports them; whether that becomes visible to anybody is the account's
 * setting on the server, not this app's decision. That is deliberate: a per-device switch would
 * mean a phone still broadcasting after the website had been told to stop.
 */
@Serializable
data class SavePlaybackRequest(
    val currentTrackId: String?,
    val positionSeconds: Double,
    val queue: List<String>,
    val queueIndex: Int,
    val shuffleEnabled: Boolean,
    val repeat: String,
    val sourcePlaylistId: String? = null,
    val isPlaying: Boolean = false,
)

@Serializable
data class RecordPlayRequest(
    val trackId: String,
    val secondsPlayed: Double,
)

@Serializable
data class CreatePlaylistRequest(val name: String, val teamId: String? = null)

@Serializable
data class AddTracksRequest(val trackIds: List<String>)

@Serializable
data class CreatedId(val id: String)

@Serializable
data class AddedCount(val added: Int = 0)

/** What the server sends instead of a body when it refuses something. */
@Serializable
data class ApiError(
    val error: String? = null,
    // Results.Problem uses ProblemDetails, which spells the message "detail".
    @SerialName("detail") val detail: String? = null,
    val title: String? = null,
) {
    val message: String? get() = error ?: detail ?: title
}
