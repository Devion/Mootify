package kiwi.lazy.mootify.data

import android.content.Context
import androidx.media3.common.util.UnstableApi
import androidx.media3.database.StandaloneDatabaseProvider
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.cache.CacheDataSource
import androidx.media3.datasource.cache.LeastRecentlyUsedCacheEvictor
import androidx.media3.datasource.cache.SimpleCache
import androidx.media3.datasource.okhttp.OkHttpDataSource
// Jake Wharton's converter, not Retrofit's own: the coordinate is
// com.jakewharton.retrofit:retrofit2-kotlinx-serialization-converter, and its package is namespaced
// to match. Square's later in-house converter lives at retrofit2.converter.kotlinx.serialization,
// which is the same file name in a different package and the obvious way to get this wrong.
import com.jakewharton.retrofit2.converter.kotlinx.serialization.asConverterFactory
import kotlinx.serialization.json.Json
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Response
import retrofit2.Retrofit
import java.io.File
import java.util.concurrent.TimeUnit

/**
 * One HTTP client for everything: the JSON API and the audio.
 *
 * That is the point of it. The bearer token is attached by an interceptor rather than passed around,
 * so ExoPlayer's data source — handed this same client — authenticates its range requests without
 * anybody remembering to set a header on it. Two clients would mean two places for the token to go
 * missing, and the one that goes missing is always the one playing music in a tunnel.
 *
 * Marked [UnstableApi] because the caching data source and the OkHttp bridge are Media3 APIs still
 * declared as such; the annotation is the opt-in, not a warning about this code.
 */
@UnstableApi
object Http {

    private const val MediaCacheBytes = 512L * 1024 * 1024

    val json = Json {
        ignoreUnknownKeys = true
        explicitNulls = false
        coerceInputValues = true
    }

    /**
     * Adds the token, and notices when the server stops accepting it. A 401 on any call means the
     * device was revoked, the account was banned, or the token expired — all of which the client
     * treats the same way: forget it and ask for a password again.
     */
    private class AuthInterceptor(private val session: SessionStore) : Interceptor {
        override fun intercept(chain: Interceptor.Chain): Response {
            val token = session.current.value?.token

            val request = if (token.isNullOrBlank()) {
                chain.request()
            } else {
                chain.request().newBuilder()
                    .header("Authorization", "Bearer $token")
                    .build()
            }

            val response = chain.proceed(request)

            if (response.code == 401 && !token.isNullOrBlank()) {
                session.invalidateInMemory()
            }

            return response
        }
    }

    fun client(session: SessionStore): OkHttpClient = OkHttpClient.Builder()
        .addInterceptor(AuthInterceptor(session))
        // Generous read timeout: this is streaming audio over whatever signal the car has, and a
        // short timeout turns a slow tunnel into a stopped song.
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    fun retrofit(baseUrl: String, client: OkHttpClient): Retrofit = Retrofit.Builder()
        .baseUrl(baseUrl)
        .client(client)
        .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
        .build()

    @Volatile
    private var mediaCache: SimpleCache? = null

    @Synchronized
    private fun mediaCache(context: Context): SimpleCache {
        mediaCache?.let { return it }

        val created = SimpleCache(
            File(context.cacheDir, "media"),
            LeastRecentlyUsedCacheEvictor(MediaCacheBytes),
            StandaloneDatabaseProvider(context),
        )

        mediaCache = created
        return created
    }

    /**
     * A read-through disk cache in front of the network, so a track played twice is fetched once and
     * a dropped signal mid-song doesn't necessarily stop it. Kept modest: a phone that fills its own
     * storage with somebody's discography is a support call.
     *
     * The return type is the concrete [CacheDataSource.Factory] rather than [DataSource.Factory]
     * because `MediaPrefetcher` needs a [CacheDataSource] to hand to a `CacheWriter`, and it has to
     * be *this* factory: two factories over the same directory would be two caches by any useful
     * definition, and the player would never see what the prefetcher wrote.
     */
    fun dataSourceFactory(context: Context, client: OkHttpClient): CacheDataSource.Factory =
        CacheDataSource.Factory()
            .setCache(mediaCache(context))
            .setUpstreamDataSourceFactory(OkHttpDataSource.Factory(client))
            // A cache that can't be written to should mean a slower player, not a broken one.
            .setFlags(CacheDataSource.FLAG_IGNORE_CACHE_ON_ERROR)
}
