package kiwi.lazy.mootify.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Album
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import coil3.compose.SubcomposeAsyncImage
import kiwi.lazy.mootify.data.ApiTrack
import java.util.Locale

/**
 * Cover art. Never authenticated — `/art/album/{id}` is open by design (the car fetches it from its
 * own process), which conveniently means the image loader needs no special client here either.
 */
@Composable
fun AlbumArt(
    url: String?,
    modifier: Modifier = Modifier,
    corner: Int = 8,
) {
    val shape = RoundedCornerShape(corner.dp)

    if (url == null) {
        Box(
            modifier
                .clip(shape)
                .background(MaterialTheme.colorScheme.surfaceVariant),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                Icons.Filled.Album,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f),
            )
        }
        return
    }

    SubcomposeAsyncImage(
        model = url,
        contentDescription = null,
        contentScale = ContentScale.Crop,
        modifier = modifier.clip(shape).background(MaterialTheme.colorScheme.surfaceVariant),
        // Albums without art are common in a library assembled by hand, so the placeholder is the
        // normal case rather than an error state.
        error = {
            Box(Modifier.background(MaterialTheme.colorScheme.surfaceVariant), Alignment.Center) {
                Icon(
                    Icons.Filled.Album,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f),
                )
            }
        },
        loading = {
            Box(Modifier.background(MaterialTheme.colorScheme.surfaceVariant))
        },
    )
}

@Composable
fun TrackRow(
    track: ApiTrack,
    artUrl: String?,
    isCurrent: Boolean,
    onClick: () -> Unit,
    onMenu: (() -> Unit)? = null,
    showArt: Boolean = true,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (showArt) {
            AlbumArt(artUrl, Modifier.size(48.dp))
            Spacer(Modifier.width(12.dp))
        } else {
            Text(
                text = track.trackNumber.takeIf { it > 0 }?.toString() ?: "–",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.width(28.dp),
            )
        }

        Column(Modifier.weight(1f)) {
            Text(
                track.title,
                style = MaterialTheme.typography.bodyLarge,
                fontWeight = if (isCurrent) FontWeight.Bold else FontWeight.Normal,
                color = if (isCurrent) MaterialTheme.colorScheme.primary else Color.Unspecified,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                listOfNotNull(
                    track.artistName.takeIf { it.isNotBlank() },
                    track.albumTitle.takeIf { it.isNotBlank() && showArt },
                ).joinToString(" · "),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        Text(
            formatDuration(track.durationMs),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )

        if (onMenu != null) {
            IconButton(onClick = onMenu) {
                Icon(Icons.Filled.MoreVert, contentDescription = "More")
            }
        }
    }
}

@Composable
fun SectionHeader(text: String, trailing: @Composable (() -> Unit)? = null) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(start = 16.dp, end = 8.dp, top = 20.dp, bottom = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(
            text.uppercase(Locale.getDefault()),
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.primary,
        )
        trailing?.invoke()
    }
}

/** m:ss, or h:mm:ss for the album-length things. Nothing rounds up to "0:60". */
fun formatDuration(ms: Long): String {
    if (ms <= 0) return "–:––"

    val totalSeconds = ms / 1000
    val hours = totalSeconds / 3600
    val minutes = (totalSeconds % 3600) / 60
    val seconds = totalSeconds % 60

    return if (hours > 0) {
        String.format(Locale.US, "%d:%02d:%02d", hours, minutes, seconds)
    } else {
        String.format(Locale.US, "%d:%02d", minutes, seconds)
    }
}

/** "12 songs · 47 min" — the subtitle every list of music wants. */
fun describeCollection(trackCount: Int, durationMs: Long): String {
    val songs = "$trackCount ${if (trackCount == 1) "song" else "songs"}"
    if (durationMs <= 0) return songs

    val minutes = durationMs / 60_000
    return if (minutes < 60) {
        "$songs · $minutes min"
    } else {
        "$songs · ${minutes / 60} h ${minutes % 60} min"
    }
}
