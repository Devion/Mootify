package kiwi.lazy.mootify.playback

import android.net.Uri
import android.os.Bundle
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.util.UnstableApi
import kiwi.lazy.mootify.data.ApiAlbum
import kiwi.lazy.mootify.data.ApiArtist
import kiwi.lazy.mootify.data.ApiPlaylist
import kiwi.lazy.mootify.data.ApiTrack
import kiwi.lazy.mootify.data.MootifyRepository

/**
 * Turning library objects into what a car draws.
 *
 * The one thing worth knowing here: **artwork is a URL, not bytes.** Android Auto fetches
 * `artworkUri` from its own process, which is why the server serves `/art/album/{id}` without asking
 * for a token — see `ArtEndpoints.cs`. Passing artwork as bytes instead would mean pushing every
 * cover through a parcel with a size limit measured in kilobytes.
 *
 * The content-style extras are the difference between a car showing a wall of album covers and a car
 * showing a list of text. They are read by Android Auto by name; the constants are copied here
 * because they live in the legacy media-compat library this app doesn't depend on.
 */
@UnstableApi
object MediaItems {

    private const val CONTENT_STYLE_BROWSABLE_HINT = "android.media.browse.CONTENT_STYLE_BROWSABLE_HINT"
    private const val CONTENT_STYLE_PLAYABLE_HINT = "android.media.browse.CONTENT_STYLE_PLAYABLE_HINT"
    private const val CONTENT_STYLE_LIST = 1
    private const val CONTENT_STYLE_GRID = 2

    /** Covers deserve a grid; songs read better as a list. */
    private fun styleExtras(browsableAsGrid: Boolean) = Bundle().apply {
        putInt(
            CONTENT_STYLE_BROWSABLE_HINT,
            if (browsableAsGrid) CONTENT_STYLE_GRID else CONTENT_STYLE_LIST,
        )
        putInt(CONTENT_STYLE_PLAYABLE_HINT, CONTENT_STYLE_LIST)
    }

    fun browsable(
        id: MediaId,
        title: String,
        subtitle: String? = null,
        artworkUri: String? = null,
        mediaType: Int = MediaMetadata.MEDIA_TYPE_FOLDER_MIXED,
        gridChildren: Boolean = false,
    ): MediaItem {
        val metadata = MediaMetadata.Builder()
            .setTitle(title)
            .setSubtitle(subtitle)
            .setIsBrowsable(true)
            .setIsPlayable(false)
            .setMediaType(mediaType)
            .setExtras(styleExtras(gridChildren))
            .apply { artworkUri?.let { setArtworkUri(Uri.parse(it)) } }
            .build()

        return MediaItem.Builder()
            .setMediaId(id.encode())
            .setMediaMetadata(metadata)
            .build()
    }

    /**
     * A playable track. The URI is absolute because ExoPlayer needs a real one, and it is only set
     * here — browse items handed to the car deliberately carry no URI, so a stale browse list can't
     * be played against a server the app has since signed out of.
     */
    fun playable(track: ApiTrack, parent: MediaId?, repository: MootifyRepository): MediaItem {
        val metadata = MediaMetadata.Builder()
            .setTitle(track.title)
            .setArtist(track.artistName)
            .setAlbumTitle(track.albumTitle)
            .setAlbumArtist(track.artistName)
            .setTrackNumber(track.trackNumber.takeIf { it > 0 })
            .setDiscNumber(track.discNumber.takeIf { it > 0 })
            .setDurationMs(track.durationMs.takeIf { it > 0 })
            .setRecordingYear(track.year)
            .setIsBrowsable(false)
            .setIsPlayable(true)
            .setMediaType(MediaMetadata.MEDIA_TYPE_MUSIC)
            .apply { repository.absolute(track.artUrl)?.let { setArtworkUri(Uri.parse(it)) } }
            .build()

        return MediaItem.Builder()
            .setMediaId(MediaId.Track(track.id, parent).encode())
            .setUri(repository.absolute(track.streamUrl))
            .setMediaMetadata(metadata)
            .build()
    }

    fun fromPlaylist(playlist: ApiPlaylist): MediaItem = browsable(
        id = MediaId.Playlist(playlist.id),
        title = playlist.name,
        subtitle = buildString {
            append(playlist.trackCount)
            append(if (playlist.trackCount == 1) " song" else " songs")
            playlist.teamName?.let { append(" · ").append(it) }
        },
        mediaType = MediaMetadata.MEDIA_TYPE_PLAYLIST,
    )

    fun fromArtist(artist: ApiArtist): MediaItem = browsable(
        id = MediaId.Artist(artist.id),
        title = artist.name,
        subtitle = "${artist.albumCount} ${if (artist.albumCount == 1) "album" else "albums"}",
        mediaType = MediaMetadata.MEDIA_TYPE_ARTIST,
        gridChildren = true,
    )

    fun fromAlbum(album: ApiAlbum, repository: MootifyRepository): MediaItem = browsable(
        id = MediaId.Album(album.id),
        title = album.title,
        subtitle = listOfNotNull(album.artistName.takeIf { it.isNotBlank() }, album.year?.toString())
            .joinToString(" · "),
        artworkUri = repository.absolute(album.artUrl),
        mediaType = MediaMetadata.MEDIA_TYPE_ALBUM,
    )
}
