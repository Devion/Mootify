package kiwi.lazy.mootify

import android.app.Application
import androidx.media3.common.util.UnstableApi
import kiwi.lazy.mootify.data.MootifyRepository
import kiwi.lazy.mootify.data.SessionStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking

/**
 * The service locator, deliberately by hand.
 *
 * A dependency-injection framework would earn its keep in a bigger app; here there are three
 * singletons and one of them has to exist before the media service's first callback, which arrives
 * from Android Auto without an Activity ever having been created. Constructing them in
 * [onCreate] is the whole of it.
 */
@UnstableApi
class MootifyApp : Application() {

    lateinit var sessionStore: SessionStore
        private set

    lateinit var repository: MootifyRepository
        private set

    val applicationScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    override fun onCreate() {
        super.onCreate()

        sessionStore = SessionStore(this)
        repository = MootifyRepository(this, sessionStore)

        // Blocking, and on purpose. Android Auto can bind the media service immediately after the
        // process starts, and a browse callback that runs before the token has been read from disk
        // would answer "not signed in" and show the car an empty library. It is one small file read.
        runBlocking { sessionStore.load() }

        // Then confirm the token is still good, without holding anything up. A revoked device
        // discovers it here rather than three songs into a drive.
        applicationScope.launch {
            if (sessionStore.current.value != null) {
                repository.refreshSession()
            }
        }
    }
}
