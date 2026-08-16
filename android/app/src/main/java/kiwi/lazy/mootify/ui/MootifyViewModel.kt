package kiwi.lazy.mootify.ui

import android.app.Application
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.media3.common.util.UnstableApi
import kiwi.lazy.mootify.data.ApiAlbum
import kiwi.lazy.mootify.data.ApiAlbumDetail
import kiwi.lazy.mootify.data.ApiArtist
import kiwi.lazy.mootify.data.ApiArtistDetail
import kiwi.lazy.mootify.data.ApiPlaylist
import kiwi.lazy.mootify.data.ApiPlaylistDetail
import kiwi.lazy.mootify.data.ApiRemoteAlbum
import kiwi.lazy.mootify.data.ApiRemoteTrack
import kiwi.lazy.mootify.data.ApiRequest
import kiwi.lazy.mootify.data.ApiSearchResults
import kiwi.lazy.mootify.data.MootifyRepository
import kiwi.lazy.mootify.MootifyApp
import kiwi.lazy.mootify.data.Session
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

/**
 * State for the phone screens. One view model rather than one per screen: the screens share a
 * repository, a session and a notion of "what went wrong last", and splitting them would mean
 * re-fetching the playlist list every time somebody backs out of a playlist.
 */
@UnstableApi
class MootifyViewModel(application: Application) : AndroidViewModel(application) {

    private val app = application as MootifyApp
    private val repository: MootifyRepository = app.repository

    val session: StateFlow<Session?> = app.sessionStore.current

    /**
     * Relative URLs from the API resolved against the signed-in server, for the image loader. The
     * screens never build a URL themselves — the server's own paths are the only source.
     */
    fun absolute(relative: String?): String? = repository.absolute(relative)

    // ---- sign in ----------------------------------------------------------

    data class LoginState(
        val serverUrl: String = "",
        val username: String = "",
        val password: String = "",
        val busy: Boolean = false,
        val error: String? = null,
    )

    private val _login = MutableStateFlow(LoginState(serverUrl = app.sessionStore.lastServerUrl.value))
    val login: StateFlow<LoginState> = _login

    fun onServerUrlChanged(value: String) { _login.value = _login.value.copy(serverUrl = value, error = null) }
    fun onUsernameChanged(value: String) { _login.value = _login.value.copy(username = value, error = null) }
    fun onPasswordChanged(value: String) { _login.value = _login.value.copy(password = value, error = null) }

    fun signIn() {
        val current = _login.value
        if (current.busy) return

        if (current.serverUrl.isBlank() || current.username.isBlank()) {
            _login.value = current.copy(error = "Server and username, at least.")
            return
        }

        _login.value = current.copy(busy = true, error = null)

        viewModelScope.launch {
            val result = repository.signIn(
                serverUrl = current.serverUrl,
                username = current.username.trim(),
                password = current.password,
                // Names the row on the account's device list, so revoking the right phone is possible.
                deviceName = "${Build.MANUFACTURER} ${Build.MODEL}".trim(),
            )

            _login.value = result.fold(
                onSuccess = { LoginState(serverUrl = current.serverUrl) },
                onFailure = { _login.value.copy(busy = false, error = it.message ?: "Couldn't sign in.") },
            )

            if (result.isSuccess) refreshAll()
        }
    }

    fun signOut() {
        viewModelScope.launch {
            repository.signOut()
            _library.value = LibraryState()
            _login.value = LoginState(serverUrl = app.sessionStore.lastServerUrl.value)
        }
    }

    // ---- library ----------------------------------------------------------

    data class LibraryState(
        val loading: Boolean = false,
        val error: String? = null,
        val playlists: List<ApiPlaylist> = emptyList(),
        val artists: List<ApiArtist> = emptyList(),
        val albums: List<ApiAlbum> = emptyList(),
        val recent: List<ApiAlbum> = emptyList(),
        val requests: List<ApiRequest> = emptyList(),
        val unreadNotifications: Int = 0,
    )

    private val _library = MutableStateFlow(LibraryState())
    val library: StateFlow<LibraryState> = _library

    fun refreshAll() {
        if (!repository.isSignedIn) return

        _library.value = _library.value.copy(loading = true, error = null)

        viewModelScope.launch {
            val playlists = repository.playlists()
            val artists = repository.artists(take = 500)
            val albums = repository.albums(take = 500)
            val recent = repository.albums(recentFirst = true, take = 30)
            val requests = repository.requests()
            val notifications = repository.notifications()

            // One message, from whichever call failed first. Six error strings on one screen tells
            // the user nothing six times.
            val failure = listOf(playlists, artists, albums, recent)
                .firstOrNull { it.isFailure }
                ?.exceptionOrNull()

            _library.value = LibraryState(
                loading = false,
                error = failure?.message,
                playlists = playlists.getOrNull().orEmpty(),
                artists = artists.getOrNull().orEmpty(),
                albums = albums.getOrNull().orEmpty(),
                recent = recent.getOrNull().orEmpty(),
                requests = requests.getOrNull().orEmpty(),
                unreadNotifications = notifications.getOrNull()?.unread ?: 0,
            )
        }
    }

    // ---- details ----------------------------------------------------------

    private val _album = MutableStateFlow<ApiAlbumDetail?>(null)
    val album: StateFlow<ApiAlbumDetail?> = _album

    fun loadAlbum(albumId: String) {
        _album.value = null
        viewModelScope.launch { _album.value = repository.album(albumId).getOrNull() }
    }

    private val _artist = MutableStateFlow<ApiArtistDetail?>(null)
    val artist: StateFlow<ApiArtistDetail?> = _artist

    fun loadArtist(artistId: String) {
        _artist.value = null
        viewModelScope.launch { _artist.value = repository.artist(artistId).getOrNull() }
    }

    private val _playlist = MutableStateFlow<ApiPlaylistDetail?>(null)
    val playlist: StateFlow<ApiPlaylistDetail?> = _playlist

    fun loadPlaylist(playlistId: String) {
        _playlist.value = null
        viewModelScope.launch { _playlist.value = repository.playlist(playlistId).getOrNull() }
    }

    fun removeFromPlaylist(playlistId: String, itemId: String) {
        viewModelScope.launch {
            repository.removePlaylistItem(playlistId, itemId)
                .onSuccess { loadPlaylist(playlistId) }
                .onFailure { showToast(it.message) }
        }
    }

    fun addToPlaylist(playlistId: String, trackIds: List<String>) {
        viewModelScope.launch {
            repository.addTracks(playlistId, trackIds).fold(
                onSuccess = { added ->
                    showToast(
                        when {
                            added == 0 -> "Already in there."
                            added == 1 -> "Added."
                            else -> "Added $added songs."
                        },
                    )
                },
                onFailure = { showToast(it.message) },
            )
        }
    }

    suspend fun artistTracks(artistId: String) = repository.artistTracks(artistId).getOrNull().orEmpty()

    // ---- library search ---------------------------------------------------

    private val _search = MutableStateFlow(ApiSearchResults())
    val search: StateFlow<ApiSearchResults> = _search

    private val _searchQuery = MutableStateFlow("")
    val searchQuery: StateFlow<String> = _searchQuery

    private var searchJob: Job? = null

    fun onSearchQueryChanged(query: String) {
        _searchQuery.value = query
        searchJob?.cancel()

        if (query.isBlank()) {
            _search.value = ApiSearchResults()
            return
        }

        searchJob = viewModelScope.launch {
            // Typing "cowbell" is seven keystrokes and one search, not seven.
            delay(250)
            _search.value = repository.search(query, take = 30).getOrNull() ?: ApiSearchResults()
        }
    }

    // ---- requests ---------------------------------------------------------

    data class RequestState(
        val query: String = "",
        val busy: Boolean = false,
        val results: List<ApiRemoteAlbum> = emptyList(),
        val expanded: String? = null,
        val tracklist: List<ApiRemoteTrack> = emptyList(),
        val loadingTracklist: Boolean = false,
        val targetPlaylistId: String? = null,
        val message: String? = null,
    )

    private val _requestState = MutableStateFlow(RequestState())
    val requestState: StateFlow<RequestState> = _requestState

    fun onRequestQueryChanged(value: String) {
        _requestState.value = _requestState.value.copy(query = value)
    }

    fun onTargetPlaylistChanged(playlistId: String?) {
        _requestState.value = _requestState.value.copy(targetPlaylistId = playlistId)
    }

    fun searchRemote() {
        val query = _requestState.value.query.trim()
        if (query.isBlank()) return

        _requestState.value = _requestState.value.copy(busy = true, message = null, expanded = null)

        viewModelScope.launch {
            repository.searchRemote(query).fold(
                onSuccess = { _requestState.value = _requestState.value.copy(busy = false, results = it) },
                onFailure = {
                    _requestState.value = _requestState.value.copy(busy = false, message = it.message)
                },
            )
        }
    }

    /**
     * Expanding an album fetches its tracklist from MusicBrainz through the server. Lidarr's album
     * lookup has a track count and no titles, which is why picking one song needs a second call.
     */
    fun toggleAlbum(album: ApiRemoteAlbum) {
        val mbid = album.musicBrainzId ?: return
        val state = _requestState.value

        if (state.expanded == mbid) {
            _requestState.value = state.copy(expanded = null, tracklist = emptyList())
            return
        }

        _requestState.value = state.copy(expanded = mbid, tracklist = emptyList(), loadingTracklist = true)

        viewModelScope.launch {
            val tracks = repository.remoteTracks(mbid).getOrNull().orEmpty()
            _requestState.value = _requestState.value.copy(tracklist = tracks, loadingTracklist = false)
        }
    }

    fun requestAlbum(album: ApiRemoteAlbum) {
        val state = _requestState.value

        viewModelScope.launch {
            repository.requestAlbum(album, state.query.trim(), state.targetPlaylistId).fold(
                onSuccess = { message("Requested ${album.title}. You'll get a cowbell when it lands.") },
                onFailure = { message(it.message) },
            )
            refreshRequests()
        }
    }

    fun requestTrack(album: ApiRemoteAlbum, track: ApiRemoteTrack) {
        val state = _requestState.value

        viewModelScope.launch {
            repository.requestTrack(album, track, state.query.trim(), state.targetPlaylistId).fold(
                onSuccess = { message("Requested \"${track.title}\".") },
                onFailure = { message(it.message) },
            )
            refreshRequests()
        }
    }

    /**
     * Takes a request back off the list. Cancelling the last one wanted off a release also stops
     * Lidarr chasing it — the server works that out, because it's the one that knows who else is
     * still waiting on the same album.
     */
    fun cancelRequest(request: ApiRequest) {
        viewModelScope.launch {
            repository.cancelRequest(request.id).fold(
                onSuccess = { message(null) },
                onFailure = { message(it.message) },
            )
            refreshRequests()
        }
    }

    private fun refreshRequests() {
        viewModelScope.launch {
            _library.value = _library.value.copy(requests = repository.requests().getOrNull().orEmpty())
        }
    }

    private fun message(text: String?) {
        _requestState.value = _requestState.value.copy(message = text)
    }

    // ---- one-line feedback ------------------------------------------------

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast

    private fun showToast(text: String?) {
        _toast.value = text ?: return
    }

    fun toastShown() {
        _toast.value = null
    }
}
