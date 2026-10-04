// SongLink Remote (Android) — app 模块构建脚本（脚手架，Android Studio 打开后可跑）
// 说明：本仓库开发环境无 Android SDK，此文件为源码交付；用 Android Studio（含 SDK）打开 remote/ 即可编译。
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.serialization")
}

android {
    namespace = "dev.songlink.remote"
    compileSdk = 34

    defaultConfig {
        applicationId = "dev.songlink.remote"
        minSdk = 29          // AudioPlaybackCapture 需 Android 10+
        targetSdk = 34
        versionCode = 1
        versionName = "0.1-m1"
    }

    buildFeatures { compose = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.09.03")
    implementation(composeBom)
    implementation("androidx.activity:activity-compose:1.9.2")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.6")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
    implementation("io.coil-kt:coil-compose:2.7.0")   // 加载 Host 推送的封面
    // 后续 M2.5 音频串流：androidx.media3 或 MediaCodec 内置 Opus（无需第三方）
}
