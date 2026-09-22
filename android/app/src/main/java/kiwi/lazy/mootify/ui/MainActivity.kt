package kiwi.lazy.mootify.ui

import android.Manifest
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Modifier
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.media3.common.util.UnstableApi
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import kiwi.lazy.mootify.MootifyApp
import kiwi.lazy.mootify.data.ApiTrack
import kiwi.lazy.mootify.playback.MediaId
import kotlinx.coroutines.launch

/**
 * The phone half. Everything here is a view onto the media session and the API; the car half never
 * touches this file, which is why signing in is the only thing the app makes you do on a screen.
 */
@UnstableApi
class MainActivity : ComponentActivity() {

    private lateinit var player: PlayerController

    private val notificationPermission = registerForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { /* Declined only costs the playback notification, so there is nothing to handle. */ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Forced dark, to match the theme. Left to itself, edge-to-edge asks the system for icon
        // colours based on the *system* light/dark setting — which on a phone in light mode means
        // dark status-bar icons drawn over Mootify's ink background, i.e. invisible.
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
        )

        val app = application as MootifyApp
        player = PlayerController(this, app.repository, lifecycleScope)

        // Android 13+ hides the media notification without this. Asked here rather than at first
        // playback, because first playback may well happen in a car with the phone in a pocket.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }

        setContent {
            MootifyTheme {
                MootifyRoot(player)
            }
        }
    }

    override fun onStart() {
        super.onStart()
        player.connect()
    }

    override fun onStop() {
        // The service keeps playing; only this window's handle on it goes away.
        player.release()
        super.onStop()
    }
}

// TopAppBar is still an experimental Material3 API; the opt-in says so and nothing more.
@OptIn(ExperimentalMaterial3Api::class)
@UnstableApi
@Composable
private fun MootifyRoot(player: PlayerController) {
    val viewModel: MootifyViewModel = viewModel()
    val session by viewModel.session.collectAsStateWithLifecycle()
    val playerState by player.state.collectAsStateWithLifecycle()
    val navController = rememberNavController()
    val snackbar = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()

    val toast by viewModel.toast.collectAsStateWithLifecycle()

    LaunchedEffect(toast) {
        toast?.let {
            snackbar.showSnackbar(it)
            viewModel.toastShown()
        }
    }

    // Signing in — or a token being revoked out from under us — changes which half of the app exists.
    LaunchedEffect(session?.token) {
        if (session != null) viewModel.refreshAll()
    }

    if (session == null) {
        val login by viewModel.login.collectAsStateWithLifecycle()

        LoginScreen(
            state = login,
            onServerUrl = viewModel::onServerUrlChanged,
            onUsername = viewModel::onUsernameChanged,
            onPassword = viewModel::onPasswordChanged,
            onSubmit = viewModel::signIn,
        )
        return
    }

    val library by viewModel.library.collectAsStateWithLifecycle()
    val search by viewModel.search.collectAsStateWithLifecycle()
    val searchQuery by viewModel.searchQuery.collectAsStateWithLifecycle()
    val backStackEntry by navController.currentBackStackEntryAsState()
    val route = backStackEntry?.destination?.route

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(titleFor(route, session?.displayName)) },
                navigationIcon = {
                    if (route != null && route != Routes.Home) {
                        IconButton(onClick = { navController.popBackStack() }) {
                            Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                        }
                    }
                },
                actions = {
                    IconButton(onClick = viewModel::refreshAll) {
                        Icon(Icons.Filled.Refresh, contentDescription = "Refresh")
                    }
                    IconButton(onClick = viewModel::signOut) {
                        Icon(Icons.AutoMirrored.Filled.Logout, contentDescription = "Sign out")
                    }
                },
            )
        },
        bottomBar = {
            NowPlayingBar(
                state = playerState,
                onToggle = player::togglePlay,
                onNext = player::next,
                onOpen = { navController.navigate(Routes.NowPlaying) },
            )
        },
        snackbarHost = { SnackbarHost(snackbar) },
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            NavHost(navController = navController, startDestination = Routes.Home) {

                composable(Routes.Home) {
                    HomeScreen(
                        state = library,
                        search = search,
                        searchQuery = searchQuery,
                        absolute = viewModel::absolute,
                        currentTrackId = playerState.trackId,
                        onSearchQuery = viewModel::onSearchQueryChanged,
                        onOpenPlaylist = { navController.navigate(Routes.playlist(it)) },
                        onOpenArtist = { navController.navigate(Routes.artist(it)) },
                        onOpenAlbum = { navController.navigate(Routes.album(it)) },
                        onOpenRequests = { navController.navigate(Routes.Requests) },
                        onPlayTrack = { tracks, index ->
                            player.tap(tracks, index, MediaId.Search(searchQuery))
                        },
                    )
                }

                composable(Routes.AlbumPattern) { entry ->
                    val albumId = entry.arguments?.getString("albumId").orEmpty()
                    val album by viewModel.album.collectAsStateWithLifecycle()

                    LaunchedEffect(albumId) { viewModel.loadAlbum(albumId) }

                    AlbumScreen(
                        detail = album,
                        playlists = library.playlists,
                        absolute = viewModel::absolute,
                        currentTrackId = playerState.trackId,
                        onPlay = { tracks, index -> player.play(tracks, index, MediaId.Album(albumId)) },
                        onTapTrack = { tracks, index -> player.tap(tracks, index, MediaId.Album(albumId)) },
                        onShuffle = { tracks -> playShuffled(player, tracks, MediaId.Album(albumId)) },
                        onAddToPlaylist = viewModel::addToPlaylist,
                    )
                }

                composable(Routes.ArtistPattern) { entry ->
                    val artistId = entry.arguments?.getString("artistId").orEmpty()
                    val artist by viewModel.artist.collectAsStateWithLifecycle()

                    LaunchedEffect(artistId) { viewModel.loadArtist(artistId) }

                    ArtistScreen(
                        detail = artist,
                        absolute = viewModel::absolute,
                        onOpenAlbum = { navController.navigate(Routes.album(it)) },
                        onPlayAll = {
                            scope.launch {
                                player.play(viewModel.artistTracks(artistId), 0, MediaId.Artist(artistId))
                            }
                        },
                        onShuffleAll = {
                            scope.launch {
                                playShuffled(player, viewModel.artistTracks(artistId), MediaId.Artist(artistId))
                            }
                        },
                    )
                }

                composable(Routes.PlaylistPattern) { entry ->
                    val playlistId = entry.arguments?.getString("playlistId").orEmpty()
                    val playlist by viewModel.playlist.collectAsStateWithLifecycle()

                    LaunchedEffect(playlistId) { viewModel.loadPlaylist(playlistId) }

                    PlaylistScreen(
                        state = playlist,
                        absolute = viewModel::absolute,
                        currentTrackId = playerState.trackId,
                        onPlay = { tracks, index -> player.play(tracks, index, MediaId.Playlist(playlistId)) },
                        onTapTrack = { tracks, index -> player.tap(tracks, index, MediaId.Playlist(playlistId)) },
                        onShuffle = { tracks -> playShuffled(player, tracks, MediaId.Playlist(playlistId)) },
                        onRemove = { itemId -> viewModel.removeFromPlaylist(playlistId, itemId) },
                    )
                }

                composable(Routes.Requests) {
                    val requestState by viewModel.requestState.collectAsStateWithLifecycle()

                    RequestsScreen(
                        state = requestState,
                        requests = library.requests,
                        playlists = library.playlists,
                        soulseekConfigured = session?.soulseekConfigured == true,
                        onQueryChange = viewModel::onRequestQueryChanged,
                        onSearch = viewModel::searchRemote,
                        onTargetPlaylist = viewModel::onTargetPlaylistChanged,
                        onRequestFile = viewModel::requestFile,
                        onCancelRequest = viewModel::cancelRequest,
                    )
                }

                composable(Routes.NowPlaying) {
                    NowPlayingScreen(
                        state = playerState,
                        onToggle = player::togglePlay,
                        onNext = player::next,
                        onPrevious = player::previous,
                        onSeek = player::seekTo,
                        onShuffle = player::toggleShuffle,
                        onRepeat = player::cycleRepeat,
                    )
                }
            }
        }
    }
}

/**
 * Shuffle means "start somewhere random with shuffle on", not "hand the player a pre-shuffled list" —
 * the second one can't be turned back off without losing the queue.
 */
@UnstableApi
private fun playShuffled(player: PlayerController, tracks: List<ApiTrack>, parent: MediaId?) {
    if (tracks.isEmpty()) return

    player.play(tracks, tracks.indices.random(), parent)
    if (!player.state.value.shuffle) player.toggleShuffle()
}

private object Routes {
    const val Home = "home"
    const val Requests = "requests"
    const val NowPlaying = "nowplaying"

    const val AlbumPattern = "album/{albumId}"
    const val ArtistPattern = "artist/{artistId}"
    const val PlaylistPattern = "playlist/{playlistId}"

    fun album(id: String) = "album/$id"
    fun artist(id: String) = "artist/$id"
    fun playlist(id: String) = "playlist/$id"
}

private fun titleFor(route: String?, displayName: String?): String = when (route) {
    Routes.Requests -> "Requests"
    Routes.NowPlaying -> "Now playing"
    Routes.AlbumPattern -> "Album"
    Routes.ArtistPattern -> "Artist"
    Routes.PlaylistPattern -> "Playlist"
    else -> displayName?.takeIf { it.isNotBlank() }?.let { "Mootify · $it" } ?: "Mootify"
}
