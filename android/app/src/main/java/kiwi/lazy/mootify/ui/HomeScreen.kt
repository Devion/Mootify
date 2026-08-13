package kiwi.lazy.mootify.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.PlaylistPlay
import androidx.compose.material.icons.filled.CloudDownload
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Tab
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kiwi.lazy.mootify.data.ApiAlbum
import kiwi.lazy.mootify.data.ApiArtist
import kiwi.lazy.mootify.data.ApiPlaylist
import kiwi.lazy.mootify.data.ApiSearchResults
import kiwi.lazy.mootify.data.ApiTrack

/**
 * The phone's library. Four tabs, matching the four places the car's browse tree goes, so somebody
 * who learned one has learned the other.
 */
@Composable
fun HomeScreen(
    state: MootifyViewModel.LibraryState,
    search: ApiSearchResults,
    searchQuery: String,
    absolute: (String?) -> String?,
    currentTrackId: String?,
    onSearchQuery: (String) -> Unit,
    onOpenPlaylist: (String) -> Unit,
    onOpenArtist: (String) -> Unit,
    onOpenAlbum: (String) -> Unit,
    onOpenRequests: () -> Unit,
    onPlayTrack: (List<ApiTrack>, Int) -> Unit,
) {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    val tabs = listOf("Home", "Playlists", "Albums", "Artists", "Search")

    Column(Modifier.fillMaxSize()) {
        PrimaryTabRow(selectedTabIndex = tab) {
            tabs.forEachIndexed { index, title ->
                Tab(
                    selected = tab == index,
                    onClick = { tab = index },
                    text = { Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                )
            }
        }

        if (state.loading && state.albums.isEmpty() && state.playlists.isEmpty()) {
            Box(Modifier.fillMaxSize(), Alignment.Center) { CircularProgressIndicator() }
            return@Column
        }

        if (state.error != null && state.albums.isEmpty()) {
            Box(Modifier.fillMaxSize().padding(32.dp), Alignment.Center) {
                Text(
                    state.error,
                    textAlign = TextAlign.Center,
                    color = MaterialTheme.colorScheme.error,
                )
            }
            return@Column
        }

        when (tab) {
            0 -> HomeTab(
                state = state,
                absolute = absolute,
                onOpenAlbum = onOpenAlbum,
                onOpenPlaylist = onOpenPlaylist,
                onOpenRequests = onOpenRequests,
            )

            1 -> PlaylistList(state.playlists, onOpenPlaylist)

            2 -> AlbumGrid(state.albums, absolute, onOpenAlbum)

            3 -> ArtistList(state.artists, onOpenArtist)

            4 -> SearchTab(
                query = searchQuery,
                results = search,
                absolute = absolute,
                currentTrackId = currentTrackId,
                onQueryChange = onSearchQuery,
                onOpenAlbum = onOpenAlbum,
                onOpenArtist = onOpenArtist,
                onPlayTrack = onPlayTrack,
            )
        }
    }
}

@Composable
private fun HomeTab(
    state: MootifyViewModel.LibraryState,
    absolute: (String?) -> String?,
    onOpenAlbum: (String) -> Unit,
    onOpenPlaylist: (String) -> Unit,
    onOpenRequests: () -> Unit,
) {
    LazyColumn(Modifier.fillMaxSize()) {
        item {
            SectionHeader("Recently added")
        }

        item {
            LazyRow(
                contentPadding = PaddingValues(horizontal = 16.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                items(state.recent, key = { it.id }) { album ->
                    Column(
                        Modifier
                            .width(140.dp)
                            .clickable { onOpenAlbum(album.id) },
                    ) {
                        AlbumArt(absolute(album.artUrl), Modifier.size(140.dp))
                        Spacer(Modifier.height(6.dp))
                        Text(album.title, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            album.artistName,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
            }
        }

        item { SectionHeader("Playlists") }

        items(state.playlists.take(5), key = { it.id }) { playlist ->
            PlaylistRow(playlist, onOpenPlaylist)
        }

        item {
            SectionHeader("Requests")
        }

        item {
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable(onClick = onOpenRequests)
                    .padding(16.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(Icons.Filled.CloudDownload, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text("Ask for something new", style = MaterialTheme.typography.bodyLarge)
                    Text(
                        state.requests.count { it.isOpen }.let { open ->
                            if (open == 0) "Nothing in flight" else "$open still coming"
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}

@Composable
private fun PlaylistList(playlists: List<ApiPlaylist>, onOpen: (String) -> Unit) {
    if (playlists.isEmpty()) {
        EmptyState("No playlists yet. Make one on the website and it'll show up here.")
        return
    }

    LazyColumn(Modifier.fillMaxSize()) {
        items(playlists, key = { it.id }) { playlist -> PlaylistRow(playlist, onOpen) }
    }
}

@Composable
private fun PlaylistRow(playlist: ApiPlaylist, onOpen: (String) -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable { onOpen(playlist.id) }
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Icons.AutoMirrored.Filled.PlaylistPlay, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(playlist.name, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(
                buildString {
                    append(describeCollection(playlist.trackCount, playlist.durationMs))
                    playlist.teamName?.let { append(" · ").append(it) }
                },
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun AlbumGrid(
    albums: List<ApiAlbum>,
    absolute: (String?) -> String?,
    onOpen: (String) -> Unit,
) {
    if (albums.isEmpty()) {
        EmptyState("Nothing in the library yet. Scan it from the website.")
        return
    }

    LazyVerticalGrid(
        columns = GridCells.Adaptive(150.dp),
        contentPadding = PaddingValues(12.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier.fillMaxSize(),
    ) {
        items(albums, key = { it.id }) { album ->
            Column(Modifier.clickable { onOpen(album.id) }) {
                AlbumArt(absolute(album.artUrl), Modifier.fillMaxWidth().aspectRatio(1f))
                Spacer(Modifier.height(6.dp))
                Text(album.title, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    album.artistName,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

@Composable
private fun ArtistList(artists: List<ApiArtist>, onOpen: (String) -> Unit) {
    if (artists.isEmpty()) {
        EmptyState("No artists yet.")
        return
    }

    LazyColumn(Modifier.fillMaxSize()) {
        items(artists, key = { it.id }) { artist ->
            Column(
                Modifier
                    .fillMaxWidth()
                    .clickable { onOpen(artist.id) }
                    .padding(horizontal = 16.dp, vertical = 12.dp),
            ) {
                Text(artist.name, style = MaterialTheme.typography.bodyLarge)
                Text(
                    "${artist.albumCount} ${if (artist.albumCount == 1) "album" else "albums"} · " +
                        "${artist.trackCount} songs",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun SearchTab(
    query: String,
    results: ApiSearchResults,
    absolute: (String?) -> String?,
    currentTrackId: String?,
    onQueryChange: (String) -> Unit,
    onOpenAlbum: (String) -> Unit,
    onOpenArtist: (String) -> Unit,
    onPlayTrack: (List<ApiTrack>, Int) -> Unit,
) {
    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(
            value = query,
            onValueChange = onQueryChange,
            singleLine = true,
            label = { Text("Search the library") },
            leadingIcon = { Icon(Icons.Filled.Search, contentDescription = null) },
            modifier = Modifier
                .fillMaxWidth()
                .padding(16.dp),
        )

        LazyColumn(Modifier.fillMaxSize()) {
            if (results.tracks.isNotEmpty()) {
                item { SectionHeader("Songs") }
                itemsIndexed(results.tracks) { index, track ->
                    TrackRow(
                        track = track,
                        artUrl = absolute(track.artUrl),
                        isCurrent = track.id == currentTrackId,
                        onClick = { onPlayTrack(results.tracks, index) },
                    )
                }
            }

            if (results.albums.isNotEmpty()) {
                item { SectionHeader("Albums") }
                items(results.albums, key = { "album-${it.id}" }) { album ->
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clickable { onOpenAlbum(album.id) }
                            .padding(horizontal = 16.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        AlbumArt(absolute(album.artUrl), Modifier.size(48.dp))
                        Spacer(Modifier.width(12.dp))
                        Column {
                            Text(album.title, style = MaterialTheme.typography.bodyLarge)
                            Text(
                                album.artistName,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
            }

            if (results.artists.isNotEmpty()) {
                item { SectionHeader("Artists") }
                items(results.artists, key = { "artist-${it.id}" }) { artist ->
                    Text(
                        artist.name,
                        style = MaterialTheme.typography.bodyLarge,
                        modifier = Modifier
                            .fillMaxWidth()
                            .clickable { onOpenArtist(artist.id) }
                            .padding(horizontal = 16.dp, vertical = 12.dp),
                    )
                }
            }

            if (query.isNotBlank() &&
                results.tracks.isEmpty() && results.albums.isEmpty() && results.artists.isEmpty()
            ) {
                item {
                    Column(Modifier.fillMaxWidth().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                        Text("Nothing in the library matches that.", textAlign = TextAlign.Center)
                        Spacer(Modifier.height(8.dp))
                        Text(
                            "The Requests tab can ask Lidarr to fetch it.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            textAlign = TextAlign.Center,
                        )
                    }
                }
            }
        }
    }
}

@Composable
fun EmptyState(message: String) {
    Box(Modifier.fillMaxSize().padding(32.dp), Alignment.Center) {
        Text(
            message,
            textAlign = TextAlign.Center,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}
