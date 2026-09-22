package kiwi.lazy.mootify.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kiwi.lazy.mootify.BuildConfig
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

/**
 * Which server, and who we are on it.
 *
 * [current] exists alongside the DataStore flow because the OkHttp interceptor that attaches the
 * bearer token runs on a network thread and cannot suspend. Reading DataStore there would mean
 * blocking inside every request — including every range request of a stream in a moving car. So the
 * session is mirrored into memory and the flow drives the UI.
 */
private val Context.sessionDataStore: DataStore<Preferences> by preferencesDataStore(name = "mootify-session")

data class Session(
    val serverUrl: String,
    val token: String,
    val userId: String,
    val displayName: String,
    val soulseekConfigured: Boolean,
)

class SessionStore(private val context: Context) {

    private object Keys {
        val ServerUrl = stringPreferencesKey("server_url")
        val Token = stringPreferencesKey("token")
        val UserId = stringPreferencesKey("user_id")
        val DisplayName = stringPreferencesKey("display_name")
        val SoulseekConfigured = booleanPreferencesKey("soulseek_configured")
    }

    private val _current = MutableStateFlow<Session?>(null)

    /** Readable without suspending, for the interceptor and the media service. */
    val current: StateFlow<Session?> = _current

    /**
     * The last server anyone typed, so re-signing-in after a token expiry doesn't ask again. Falls
     * back to the URL baked in at build time.
     */
    private val _lastServerUrl = MutableStateFlow(BuildConfig.DEFAULT_SERVER_URL)
    val lastServerUrl: StateFlow<String> = _lastServerUrl

    val sessions: Flow<Session?> = context.sessionDataStore.data.map { it.toSession() }

    /** Called once at startup, before anything asks [current] for an answer. */
    suspend fun load() {
        val preferences = context.sessionDataStore.data.first()
        _current.value = preferences.toSession()
        preferences[Keys.ServerUrl]?.let { if (it.isNotBlank()) _lastServerUrl.value = it }
    }

    suspend fun save(serverUrl: String, response: ApiTokenResponse) {
        val normalized = normalize(serverUrl)

        context.sessionDataStore.edit {
            it[Keys.ServerUrl] = normalized
            it[Keys.Token] = response.token
            it[Keys.UserId] = response.user.id
            it[Keys.DisplayName] = response.user.displayName
            it[Keys.SoulseekConfigured] = response.server.soulseekConfigured
        }

        _lastServerUrl.value = normalized
        _current.value = Session(
            serverUrl = normalized,
            token = response.token,
            userId = response.user.id,
            displayName = response.user.displayName,
            soulseekConfigured = response.server.soulseekConfigured,
        )
    }

    /** Refreshed from `/me`, so a Soulseek service configured later appears without signing in again. */
    suspend fun updateServerInfo(info: ApiServerInfo) {
        context.sessionDataStore.edit { it[Keys.SoulseekConfigured] = info.soulseekConfigured }
        _current.value = _current.value?.copy(soulseekConfigured = info.soulseekConfigured)
    }

    /**
     * Forgets the token but keeps the server URL: the next sign-in is almost always the same person
     * on the same server, and retyping a URL on a phone is a small misery.
     */
    suspend fun clear() {
        val serverUrl = _current.value?.serverUrl ?: _lastServerUrl.value

        context.sessionDataStore.edit {
            it.clear()
            it[Keys.ServerUrl] = serverUrl
        }

        _current.value = null
    }

    /**
     * Dropped from the interceptor when the server rejects the token, which can happen on any
     * thread and must not suspend. The UI notices through [current] and shows the login screen; the
     * DataStore write happens when someone next signs in or out.
     */
    fun invalidateInMemory() {
        _current.value = null
    }

    private fun Preferences.toSession(): Session? {
        val serverUrl = this[Keys.ServerUrl]
        val token = this[Keys.Token]

        if (serverUrl.isNullOrBlank() || token.isNullOrBlank()) return null

        return Session(
            serverUrl = serverUrl,
            token = token,
            userId = this[Keys.UserId].orEmpty(),
            displayName = this[Keys.DisplayName].orEmpty(),
            soulseekConfigured = this[Keys.SoulseekConfigured] ?: false,
        )
    }

    companion object {
        /**
         * Retrofit demands a base URL ending in a slash, and people type `moo.lazy.kiwi` without a
         * scheme. Assume HTTPS in that case — a self-hosted server on a plain-HTTP LAN address has
         * to be typed with `http://`, which is the right way round for a password to travel.
         */
        fun normalize(raw: String): String {
            var url = raw.trim()
            if (url.isEmpty()) return url
            if (!url.startsWith("http://") && !url.startsWith("https://")) url = "https://$url"
            if (!url.endsWith("/")) url = "$url/"
            return url
        }
    }
}
