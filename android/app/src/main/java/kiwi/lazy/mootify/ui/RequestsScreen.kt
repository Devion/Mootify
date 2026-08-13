package kiwi.lazy.mootify.ui

import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kiwi.lazy.mootify.data.ApiPlaylist
import kiwi.lazy.mootify.data.ApiRemoteAlbum
import kiwi.lazy.mootify.data.ApiRemoteTrack
import kiwi.lazy.mootify.data.ApiRequest

/**
 * Asking for music that isn't there — the thing that makes this a Mootify client rather than a music
 * player.
 *
 * The shape follows the website's search page because the constraint underneath is the same: Lidarr
 * fetches albums and has no song index, so a request is always "this album, keep one track". Expanding
 * an album to pick a song is a second call, to MusicBrainz, because Lidarr's own lookup returns a
 * track count with no titles.
 */
@Composable
fun RequestsScreen(
    state: MootifyViewModel.RequestState,
    requests: List<ApiRequest>,
    playlists: List<ApiPlaylist>,
    lidarrConfigured: Boolean,
    onQueryChange: (String) -> Unit,
    onSearch: () -> Unit,
    onToggleAlbum: (ApiRemoteAlbum) -> Unit,
    onTargetPlaylist: (String?) -> Unit,
    onRequestAlbum: (ApiRemoteAlbum) -> Unit,
    onRequestTrack: (ApiRemoteAlbum, ApiRemoteTrack) -> Unit,
) {
    if (!lidarrConfigured) {
        EmptyState("Lidarr isn't configured on this server, so nothing new can be fetched.")
        return
    }

    LazyColumn(Modifier.fillMaxSize()) {
        item {
            Column(Modifier.padding(16.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        value = state.query,
                        onValueChange = onQueryChange,
                        singleLine = true,
                        label = { Text("Artist or album") },
                        modifier = Modifier.weight(1f),
                    )
                    Spacer(Modifier.width(8.dp))
                    IconButton(onClick = onSearch, enabled = !state.busy) {
                        Icon(Icons.Filled.Search, contentDescription = "Search Lidarr")
                    }
                }

                Spacer(Modifier.height(8.dp))

                // Where the song lands when it arrives, captured now so completion is silent and
                // automatic — the server appends it for you and rings the cowbell.
                TargetPlaylistPicker(
                    playlists = playlists,
                    selectedId = state.targetPlaylistId,
                    onSelect = onTargetPlaylist,
                )

                if (state.message != null) {
                    Spacer(Modifier.height(8.dp))
                    Text(state.message, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.primary)
                }
            }
        }

        if (state.busy) {
            item {
                Box(Modifier.fillMaxWidth().padding(24.dp), Alignment.Center) { CircularProgressIndicator() }
            }
        }

        items(state.results, key = { it.musicBrainzId ?: it.title }) { album ->
            RemoteAlbumRow(
                album = album,
                expanded = state.expanded == album.musicBrainzId,
                tracklist = if (state.expanded == album.musicBrainzId) state.tracklist else emptyList(),
                loadingTracklist = state.loadingTracklist && state.expanded == album.musicBrainzId,
                onToggle = { onToggleAlbum(album) },
                onRequestAlbum = { onRequestAlbum(album) },
                onRequestTrack = { track -> onRequestTrack(album, track) },
            )
        }

        if (requests.isNotEmpty()) {
            item { SectionHeader("In flight") }

            items(requests, key = { it.id }) { request ->
                Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
                    Text(
                        request.trackTitle ?: request.albumTitle ?: request.query,
                        style = MaterialTheme.typography.bodyLarge,
                    )
                    Text(
                        buildString {
                            append(request.artistName)
                            append(" · ")
                            append(request.status)
                            request.targetPlaylistName?.let { append(" → ").append(it) }
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = if (request.status == "Failed" || request.status == "NotFound") {
                            MaterialTheme.colorScheme.error
                        } else {
                            MaterialTheme.colorScheme.onSurfaceVariant
                        },
                    )
                    request.failureReason?.let {
                        Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
                    }
                }
            }
        }
    }
}

@Composable
private fun TargetPlaylistPicker(
    playlists: List<ApiPlaylist>,
    selectedId: String?,
    onSelect: (String?) -> Unit,
) {
    var open by remember { mutableStateOf(false) }
    val selected = playlists.firstOrNull { it.id == selectedId }

    Box {
        AssistChip(
            onClick = { open = true },
            label = {
                Text(
                    selected?.let { "Add to ${it.name} when it lands" } ?: "Don't add it anywhere",
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            },
        )

        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            DropdownMenuItem(
                text = { Text("Don't add it anywhere") },
                onClick = {
                    open = false
                    onSelect(null)
                },
            )
            playlists.forEach { playlist ->
                DropdownMenuItem(
                    text = { Text(playlist.name) },
                    onClick = {
                        open = false
                        onSelect(playlist.id)
                    },
                )
            }
        }
    }
}

@Composable
private fun RemoteAlbumRow(
    album: ApiRemoteAlbum,
    expanded: Boolean,
    tracklist: List<ApiRemoteTrack>,
    loadingTracklist: Boolean,
    onToggle: () -> Unit,
    onRequestAlbum: () -> Unit,
    onRequestTrack: (ApiRemoteTrack) -> Unit,
) {
    Column(
        Modifier
            .fillMaxWidth()
            .background(
                if (expanded) MaterialTheme.colorScheme.surfaceVariant else MaterialTheme.colorScheme.surface,
            ),
    ) {
        Row(
            Modifier
                .fillMaxWidth()
                .clickable(onClick = onToggle)
                .padding(horizontal = 16.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            AlbumArt(album.coverUrl, Modifier.width(56.dp).height(56.dp))
            Spacer(Modifier.width(12.dp))

            Column(Modifier.weight(1f)) {
                Text(album.title, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    listOfNotNull(
                        album.artistName.takeIf { it.isNotBlank() },
                        album.year?.toString(),
                        album.albumType,
                        "already here".takeIf { album.alreadyInLibrary },
                    ).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }

            TextButton(onClick = onRequestAlbum) { Text("Album") }
        }

        if (expanded) {
            when {
                loadingTracklist -> Box(
                    Modifier.fillMaxWidth().padding(16.dp),
                    Alignment.Center,
                ) { CircularProgressIndicator(Modifier.height(20.dp).width(20.dp), strokeWidth = 2.dp) }

                tracklist.isEmpty() -> Text(
                    // MusicBrainz doesn't have every tracklist, and the album request still works.
                    "No tracklist for this one. Request the whole album instead.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 12.dp),
                )

                else -> Column(Modifier.padding(bottom = 8.dp)) {
                    tracklist.forEach { track ->
                        Row(
                            Modifier
                                .fillMaxWidth()
                                .padding(start = 24.dp, end = 8.dp, top = 2.dp, bottom = 2.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.SpaceBetween,
                        ) {
                            Text(
                                "${track.position}. ${track.title}",
                                style = MaterialTheme.typography.bodyMedium,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f),
                            )
                            Button(
                                onClick = { onRequestTrack(track) },
                                enabled = track.recordingId != null,
                            ) { Text("Get") }
                        }
                    }
                }
            }
        }
    }
}
