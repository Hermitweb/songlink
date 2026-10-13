plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.kapt)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.hilt.android)
}

android {
    namespace = "songlink.remote"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.songlink.remote"
        minSdk = 26
        targetSdk = 34
        versionCode = 1
        versionName = "1.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        vectorDrawables {
            useSupportLibrary = true
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
    }

    composeOptions {
        // 必须与 Kotlin 1.9.22 匹配（Kotlin 2.0+ 改用 org.jetbrains.kotlin.plugin.compose 插件）
        kotlinCompilerExtensionVersion = libs.versions.composeCompiler.get()
    }

    packaging {
        resources {
            excludes += "/META-INF/{AL2.0,LGPL2.1}"
        }
    }

    testOptions {
        unitTests {
            // JVM 单元测试里 SavedStateHandle / android.util.Log 等会碰到 android.jar 桩方法，
            // 返回默认值而不是抛 "not mocked"，否则纯 JVM 测试无法运行。
            isReturnDefaultValues = true
        }
    }
}

kapt {
    correctErrorTypes = true
}

dependencies {
    // Core Android
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.viewmodel.savedstate)
    implementation(libs.androidx.activity.compose)

    // Compose BOM
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.ui)
    implementation(libs.androidx.ui.graphics)
    implementation(libs.androidx.ui.tooling.preview)
    implementation(libs.androidx.material3)
    implementation(libs.androidx.material.icons.extended)
    implementation(libs.androidx.navigation.compose)
    debugImplementation(libs.androidx.ui.tooling)

    // Hilt
    implementation(libs.hilt.android)
    kapt(libs.hilt.compiler)
    implementation(libs.hilt.navigation.compose)

    // 权限
    implementation(libs.accompanist.permissions)

    // 二维码
    implementation(libs.zxing.android.embedded)
    // tflite 之前是 OCR 引擎占位，但下游 16 MB 的 libtensorflowlite_jni.so（4 ABI）无对应
    // 源码调用——本轮彻底移除；OCR 用 zxing + 视觉字符匹配即可。
    // implementation(libs.tensorflow.lite)  // 已移除

    // 网络 / JSON
    implementation(libs.okhttp)
    implementation(libs.kotlinx.serialization.json)

    // Excel 导出改用纯 JDK 手写 .xlsx（Java.util.zip），见 data/PlaybackHistory.kt；
    // 之前 Apache POI 5.2.3 占 classes.dex ~25 MB 的体积，但实际只被 stub 占用，本轮移除
    // implementation(libs.poi)                // 已移除
    // implementation(libs.poi.ooxml)         // 已移除

    // Coroutines
    implementation(libs.kotlinx.coroutines.android)

    // Testing
    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(libs.okhttp.mockwebserver)
    androidTestImplementation(libs.androidx.junit)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
    androidTestImplementation(libs.hilt.navigation.compose)
    debugImplementation(libs.androidx.compose.ui.test.manifest)
}