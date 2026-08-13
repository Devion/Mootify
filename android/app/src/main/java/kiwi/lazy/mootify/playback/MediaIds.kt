package kiwi.lazy.mootify.playback

import android.net.Uri

/**
 * The browse tree's addressing scheme.
 *
 * Every node Android Auto ever hands back to us is a string, so the string has to carry enough to
 * answer three questions without any state on our side: what is this, which library object is it,
 * and — for a track — **what list was it picked out of**.
 *
 * That last one is what makes the car behave. When somebody taps the fourth song on an album, Android
 * Auto sends us that one item and nothing else. Without a parent in the id we would start a queue of
 * one, and "next" would end the drive's music. With it, [MootifyLibraryService] can rebuild the album
 * and start at the right index.
 *
 * Format: `type:arg` for nodes, `track:<id>@<parent media id>` for playable leaves. Arguments are
 * URL-encoded, so a search phrase can't smuggle a separator into an id.
 */
sealed interface MediaId {

    data object Root : MediaId

    data object Playlists : MediaId

    data object Artists : MediaId

    data object Albums : MediaId

    data object Recent : MediaId

    /** The stand-in shown when there is no session yet — tapping it explains itself. */
    data object SignIn : MediaId

    data class Playlist(val id: String) : MediaId

    data class Artist(val id: String) : MediaId

    data class Album(val id: String) : MediaId

    data class Search(val query: String) : MediaId

    /** A playable track, and the list it came from so the queue can be rebuilt around it. */
    data class Track(val id: String, val parent: MediaId?) : MediaId

    fun encode(): String = when (this) {
        Root -> "root"
        Playlists -> "playlists"
        Artists -> "artists"
        Albums -> "albums"
        Recent -> "recent"
        SignIn -> "signin"
        is Playlist -> "playlist:${Uri.encode(id)}"
        is Artist -> "artist:${Uri.encode(id)}"
        is Album -> "album:${Uri.encode(id)}"
        is Search -> "search:${Uri.encode(query)}"
        is Track -> "track:${Uri.encode(id)}" + (parent?.let { "@${it.encode()}" } ?: "")
    }

    companion object {
        fun decode(raw: String?): MediaId? {
            if (raw.isNullOrBlank()) return null

            // Split the parent off first: only a track has one, and its own id never contains '@'.
            val at = raw.indexOf('@')
            if (at > 0) {
                val head = decode(raw.substring(0, at)) as? Track ?: return null
                return head.copy(parent = decode(raw.substring(at + 1)))
            }

            val colon = raw.indexOf(':')
            if (colon < 0) {
                return when (raw) {
                    "root" -> Root
                    "playlists" -> Playlists
                    "artists" -> Artists
                    "albums" -> Albums
                    "recent" -> Recent
                    "signin" -> SignIn
                    else -> null
                }
            }

            val type = raw.substring(0, colon)
            val arg = Uri.decode(raw.substring(colon + 1))

            return when (type) {
                "playlist" -> Playlist(arg)
                "artist" -> Artist(arg)
                "album" -> Album(arg)
                "search" -> Search(arg)
                "track" -> Track(arg, parent = null)
                else -> null
            }
        }
    }
}
