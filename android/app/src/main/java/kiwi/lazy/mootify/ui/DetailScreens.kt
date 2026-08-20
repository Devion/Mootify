package kiwi.lazy.mootify.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.PlaylistAdd
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kiwi.lazy.mootify.data.ApiAlbumDetail
import kiwi.lazy.mootify.data.ApiArtistDetail
import kiwi.lazy.mootify.data.ApiPlaylist
import kiwi.lazy.mootify.data.ApiPlaylistDetail
import kiwi.lazy.mootify.data.ApiTrack

/**
 * Album, artist and playlist. All three end in the same two verbs — play this in order, or shuffle it
 * — because that is what a list of songs is for.
 */
@Composable
fun AlbumScreen(
    detail: ApiAlbumDetail?,
    playlists: List<ApiPlaylist>,
    absolute: (String?) -> String?,
    currentTrackId: String?,
    onPlay: (List<ApiTrack>, Int) -> Unit,
    // Separate from onPlay on purpose: tapping the row you are listening to should not restart it,
    // but pressing the header's Play button should. See PlayerController.tap.
    onTapTrack: (List<ApiTrack>, Int) -> Unit,
    onShuffle: (List<ApiTrack>) -> Unit,
    onAddToPlaylist: (playlistId: String, trackIds: List<String>) -> Unit,
) {
    if (detail == null) {
        Loading()
        return
    }

    LazyColumn(Modifier.fillMaxSize()) {
        item {
            Column(Modifier.padding(16.dp)) {
                Row {
                    AlbumArt(absolute(detail.album.artUrl), Modifier.size(120.dp))
                    Spacer(Modifier.width(16.dp))
                    Column {
                        Text(detail.album.title, style = MaterialTheme.typography.titleLarge)
                        Text(
                            detail.album.artistName,
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        detail.album.year?.let {
                            Text(
                                it.toString(),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        Spacer(Modifier.height(4.dp))
                        Text(
                            describeCollection(detail.album.trackCount, detail.album.durationMs),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }

                Spacer(Modifier.height(12.dp))

                PlayButtons(
                    onPlay = { onPlay(detail.tracks, 0) },
                    onShuffle = { onShuffle(detail.tracks) },
                    onAddAll = { playlistId -> onAddToPlaylist(playlistId, detail.tracks.map { it.id }) },
                    playlists = playlists,
                )
            }
        }

        itemsIndexed(detail.tracks, key = { _, track -> track.id }) { index, track ->
            TrackRowWithMenu(
                track = track,
                artUrl = null,
                showArt = false,
                isCurrent = track.id == currentTrackId,
                playlists = playlists,
                onClick = { onTapTrack(detail.tracks, index) },
                onAddToPlaylist = { playlistId -> onAddToPlaylist(playlistId, listOf(track.id)) },
            )
        }
    }
}

@Composable
fun ArtistScreen(
    detail: ApiArtistDetail?,
    absolute: (String?) -> String?,
    onOpenAlbum: (String) -> Unit,
    onPlayAll: () -> Unit,
    onShuffleAll: () -> Unit,
) {
    if (detail == null) {
        Loading()
        return
    }

    LazyColumn(Modifier.fillMaxSize()) {
        item {
            Column(Modifier.padding(16.dp)) {
                Text(detail.artist.name, style = MaterialTheme.typography.headlineSmall)
                Text(
                    "${detail.artist.albumCount} albums · ${detail.artist.trackCount} songs",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.height(12.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = onPlayAll) {
                        Icon(Icons.Filled.PlayArrow, contentDescription = null)
                        Spacer(Modifier.width(4.dp))
                        Text("Play all")
                    }
                    OutlinedButton(onClick = onShuffleAll) {
                        Icon(Icons.Filled.Shuffle, contentDescription = null)
                        Spacer(Modifier.width(4.dp))
                        Text("Shuffle")
                    }
                }
            }
        }

        item { SectionHeader("Albums") }

        items(detail.albums, key = { it.id }) { album ->
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable { onOpenAlbum(album.id) }
                    .padding(horizontal = 16.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                AlbumArt(absolute(album.artUrl), Modifier.size(56.dp))
                Spacer(Modifier.width(12.dp))
                Column {
                    Text(album.title, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(
                        listOfNotNull(album.year?.toString(), "${album.trackCount} songs").joinToString(" · "),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}

/**
 * Unlike the album and artist screens, this one is fed a list that arrives in pieces — the server
 * pages playlists, and the view model fills the rest in behind the first page. Two consequences are
 * visible here: the header counts come from the playlist ([ApiPlaylistDetail.trackCount]) rather
 * than from the rows on screen, and Play and Shuffle wait for [MootifyViewModel.PlaylistUi.complete]
 * so neither can quietly queue a prefix of a long list.
 */
@Composable
fun PlaylistScreen(
    state: MootifyViewModel.PlaylistUi?,
    absolute: (String?) -> String?,
    currentTrackId: String?,
    onPlay: (List<ApiTrack>, Int) -> Unit,
    // See AlbumScreen: the rows toggle, the header's Play button restarts.
    onTapTrack: (List<ApiTrack>, Int) -> Unit,
    onShuffle: (List<ApiTrack>) -> Unit,
    onRemove: (itemId: String) -> Unit,
) {
    if (state == null) {
        Loading()
        return
    }

    val detail = state.detail
    val tracks = state.tracks
    val ready = state.complete && tracks.isNotEmpty()

    LazyColumn(Modifier.fillMaxSize()) {
        item {
            Column(Modifier.padding(16.dp)) {
                Text(detail.name, style = MaterialTheme.typography.headlineSmall)
                Text(
                    buildString {
                        append(describeCollection(detail.trackCount, detail.durationMs))
                        detail.teamName?.let { append(" · ").append(it) }
                        if (!state.complete) append(" · loading…")
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.height(12.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = { onPlay(tracks, 0) }, enabled = ready) {
                        Icon(Icons.Filled.PlayArrow, contentDescription = null)
                        Spacer(Modifier.width(4.dp))
                        Text("Play")
                    }
                    OutlinedButton(onClick = { onShuffle(tracks) }, enabled = ready) {
                        Icon(Icons.Filled.Shuffle, contentDescription = null)
                        Spacer(Modifier.width(4.dp))
                        Text("Shuffle")
                    }
                }
            }
        }

        if (detail.trackCount == 0) {
            item { EmptyState("Nothing in here yet.") }
        }

        itemsIndexed(state.items, key = { _, item -> item.id }) { index, item ->
            var menuOpen by remember { mutableStateOf(false) }

            Box {
                TrackRow(
                    track = item.track,
                    artUrl = absolute(item.track.artUrl),
                    isCurrent = item.track.id == currentTrackId,
                    // Tapping a row plays from the rows that have arrived. That is always a
                    // prefix of the real order, so the index is right even mid-fill.
                    onClick = { onTapTrack(tracks, index) },
                    onMenu = if (detail.canEdit) ({ menuOpen = true }) else null,
                )

                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    DropdownMenuItem(
                        text = { Text("Remove from playlist") },
                        onClick = {
                            menuOpen = false
                            onRemove(item.id)
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun PlayButtons(
    onPlay: () -> Unit,
    onShuffle: () -> Unit,
    onAddAll: (String) -> Unit,
    playlists: List<ApiPlaylist>,
) {
    var menuOpen by remember { mutableStateOf(false) }

    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
        Button(onClick = onPlay) {
            Icon(Icons.Filled.PlayArrow, contentDescription = null)
            Spacer(Modifier.width(4.dp))
            Text("Play")
        }
        OutlinedButton(onClick = onShuffle) {
            Icon(Icons.Filled.Shuffle, contentDescription = null)
            Spacer(Modifier.width(4.dp))
            Text("Shuffle")
        }

        Box {
            OutlinedButton(onClick = { menuOpen = true }, enabled = playlists.isNotEmpty()) {
                Icon(Icons.AutoMirrored.Filled.PlaylistAdd, contentDescription = "Add to playlist")
            }
            DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                playlists.forEach { playlist ->
                    DropdownMenuItem(
                        text = { Text(playlist.name) },
                        onClick = {
                            menuOpen = false
                            onAddAll(playlist.id)
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun TrackRowWithMenu(
    track: ApiTrack,
    artUrl: String?,
    showArt: Boolean,
    isCurrent: Boolean,
    playlists: List<ApiPlaylist>,
    onClick: () -> Unit,
    onAddToPlaylist: (String) -> Unit,
) {
    var menuOpen by remember { mutableStateOf(false) }

    Box {
        TrackRow(
            track = track,
            artUrl = artUrl,
            isCurrent = isCurrent,
            onClick = onClick,
            onMenu = if (playlists.isEmpty()) null else ({ menuOpen = true }),
            showArt = showArt,
        )

        DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
            playlists.forEach { playlist ->
                DropdownMenuItem(
                    text = { Text("Add to ${playlist.name}") },
                    onClick = {
                        menuOpen = false
                        onAddToPlaylist(playlist.id)
                    },
                )
            }
        }
    }
}

@Composable
private fun Loading() {
    Box(Modifier.fillMaxSize(), Alignment.Center) { CircularProgressIndicator() }
}
