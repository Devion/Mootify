# kotlinx.serialization keeps its serializers in generated companions that R8 can't see being used.
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**

-keepclassmembers class kiwi.lazy.mootify.data.** {
    *** Companion;
}
-keepclasseswithmembers class kiwi.lazy.mootify.data.** {
    kotlinx.serialization.KSerializer serializer(...);
}
-keep,includedescriptorclasses class kiwi.lazy.mootify.data.**$$serializer { *; }

# Retrofit builds its implementations from the interface's generic signatures.
-keepattributes Signature
-keep,allowobfuscation,allowshrinking interface retrofit2.Call
-keep,allowobfuscation,allowshrinking class kotlin.coroutines.Continuation

# OkHttp's optional platform hooks.
-dontwarn okhttp3.internal.platform.**
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**
