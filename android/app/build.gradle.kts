plugins {
    // AGP 9 brings its own Kotlin support, so org.jetbrains.kotlin.android is not applied here —
    // applying it fails the build outright. The compiler plugins below are still ours to declare.
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

android {
    namespace = "kiwi.lazy.mootify"
    // Dictated by the AndroidX versions in the catalog: core-ktx 1.19 refuses to be compiled against
    // anything older. Compiling against 37 is not the same as targeting it — see targetSdk below.
    compileSdk = 37

    defaultConfig {
        applicationId = "kiwi.lazy.mootify"
        // Android Auto's phone half needs nothing exotic; 24 is a floor that keeps the
        // notification and foreground-service code paths from needing legacy branches.
        minSdk = 24
        // Deliberately behind compileSdk: targetSdk opts the app in to a platform's new runtime
        // behaviour, and there is nothing in 37's that this app wants and no way to test it here.
        targetSdk = 36
        versionCode = 1
        versionName = "0.1"

        // The default the login screen offers. Set with -PmootifyServerUrl=… at build time; the
        // app remembers whatever it actually signed in against, so this is a convenience only.
        buildConfigField(
            "String",
            "DEFAULT_SERVER_URL",
            "\"${project.findProperty("mootifyServerUrl") ?: "https://moo.lazy.kiwi"}\"",
        )

        // Declared here rather than in strings.xml so the debug build can override it below —
        // a resValue and a strings.xml entry of the same name is a duplicate-resource error.
        resValue("string", "app_name", "Mootify")
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
        debug {
            applicationIdSuffix = ".debug"
            // So a debug build and the real one can sit side by side on the same phone without
            // Android Auto showing two identically named media apps.
            resValue("string", "app_name", "Mootify (debug)")
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
        // Off by default in AGP 9. Needed for the app_name resValue above, which is how the debug
        // build renames itself.
        resValues = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    lint {
        // Same reason, for the release lint pass: @UnstableApi propagates to every caller of a class
        // that uses it, and lintVitalRelease would otherwise fail the build over a deliberate choice.
        disable += "UnsafeOptInUsageError"
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }
}

// The Kotlin plugin's own block rather than the `android { kotlinOptions }` shim, which AGP 9 removed.
//
// No `-opt-in=androidx.media3.common.util.UnstableApi` here: that marker is a Java annotation, which
// the Kotlin compiler doesn't recognise as an opt-in requirement ("is not an opt-in requirement
// marker"). The classes that touch unstable Media3 APIs carry @UnstableApi themselves, and the lint
// check below is the part that had to be told.
kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.activity.compose)

    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons)
    implementation(libs.androidx.navigation.compose)
    debugImplementation(libs.androidx.compose.ui.tooling)

    // Media3: the player, the session that Android Auto talks to, and an HTTP data source that
    // can carry our bearer token. These three versions move together.
    implementation(libs.androidx.media3.exoplayer)
    implementation(libs.androidx.media3.session)
    implementation(libs.androidx.media3.datasource.okhttp)
    implementation(libs.androidx.media3.ui)

    // MediaLibraryService's callbacks are ListenableFuture-based, so Guava is not optional.
    implementation(libs.guava)
    implementation(libs.kotlinx.coroutines.guava)

    implementation(libs.okhttp)
    implementation(libs.okhttp.logging)
    implementation(libs.retrofit)
    implementation(libs.retrofit.serialization)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.androidx.datastore.preferences)
    // Coil 3 splits the image loader from its network layer; without the OkHttp fetcher it can read
    // files and resources but not the http(s) URLs the server hands out for cover art.
    implementation(libs.coil.compose)
    implementation(libs.coil.network.okhttp)

    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
}
