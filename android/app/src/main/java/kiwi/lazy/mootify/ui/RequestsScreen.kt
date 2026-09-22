package kiwi.lazy.mootify.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kiwi.lazy.mootify.data.ApiPlaylist
import kiwi.lazy.mootify.data.ApiRequest
import kiwi.lazy.mootify.data.ApiSoulseekFile

@Composable
fun RequestsScreen(
    state: MootifyViewModel.RequestState,
    requests: List<ApiRequest>,
    playlists: List<ApiPlaylist>,
    soulseekConfigured: Boolean,
    onQueryChange: (String) -> Unit,
    onSearch: () -> Unit,
    onTargetPlaylist: (String?) -> Unit,
    onRequestFile: (ApiSoulseekFile) -> Unit,
    onCancelRequest: (ApiRequest) -> Unit,
) {
    if (!soulseekConfigured) {
        EmptyState("Soulseek isn't configured on this server, so nothing new can be fetched.")
        return
    }

    LazyColumn(Modifier.fillMaxSize()) {
        item {
            Column(Modifier.padding(16.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        value = state.query, onValueChange = onQueryChange, singleLine = true,
                        label = { Text("Song, album or artist") }, modifier = Modifier.weight(1f),
                    )
                    Spacer(Modifier.width(8.dp))
                    IconButton(onClick = onSearch, enabled = !state.busy) {
                        Icon(Icons.Filled.Search, contentDescription = "Search Soulseek")
                    }
                }
                Spacer(Modifier.height(8.dp))
                TargetPlaylistPicker(playlists, state.targetPlaylistId, onTargetPlaylist)
                Text(
                    "Live recordings and bootlegs are excluded.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                state.message?.let {
                    Spacer(Modifier.height(8.dp))
                    Text(it, color = MaterialTheme.colorScheme.primary)
                }
            }
        }

        if (state.busy) item {
            Box(Modifier.fillMaxWidth().padding(24.dp), Alignment.Center) { CircularProgressIndicator() }
        }

        items(state.results, key = { it.resultId }) { file ->
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(Modifier.weight(1f)) {
                    Text(file.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(
                        listOfNotNull(
                            file.extension.uppercase().takeIf { it.isNotBlank() },
                            file.bitRate?.let { "$it kbps" },
                            formatSize(file.size),
                            if (file.hasFreeUploadSlot) "free slot" else "queue ${file.queueLength}",
                        ).joinToString(" · "),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Text(file.folder, style = MaterialTheme.typography.labelSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                Button(onClick = { onRequestFile(file) }) { Text("Get") }
            }
        }

        if (requests.isNotEmpty()) {
            item { SectionHeader("Requests") }
            items(requests, key = { it.id }) { request ->
                Row(
                    Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp, top = 8.dp, bottom = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(request.trackTitle ?: request.albumTitle ?: request.query)
                        Text(
                            buildString {
                                append(request.status)
                                request.targetPlaylistName?.let { append(" → ").append(it) }
                            },
                            style = MaterialTheme.typography.bodySmall,
                            color = if (request.status in setOf("Failed", "NotFound")) MaterialTheme.colorScheme.error
                            else MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        request.failureReason?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
                    }
                    IconButton(onClick = { onCancelRequest(request) }) {
                        Icon(Icons.Filled.Close, contentDescription = if (request.isOpen) "Cancel" else "Remove")
                    }
                }
            }
        }
    }
}

@Composable
private fun TargetPlaylistPicker(playlists: List<ApiPlaylist>, selectedId: String?, onSelect: (String?) -> Unit) {
    var open by remember { mutableStateOf(false) }
    val selected = playlists.firstOrNull { it.id == selectedId }
    Box {
        AssistChip(onClick = { open = true }, label = { Text(selected?.let { "Add to ${it.name}" } ?: "Don't add to a playlist") })
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            DropdownMenuItem(text = { Text("Don't add to a playlist") }, onClick = { open = false; onSelect(null) })
            playlists.forEach { playlist ->
                DropdownMenuItem(text = { Text(playlist.name) }, onClick = { open = false; onSelect(playlist.id) })
            }
        }
    }
}

private fun formatSize(bytes: Long): String = if (bytes < 1024 * 1024) "%.1f KB".format(bytes / 1024.0)
else "%.1f MB".format(bytes / 1024.0 / 1024.0)
