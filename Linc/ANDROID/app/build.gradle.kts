plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

android {
    namespace = "app.linc.android"
    compileSdk = 35

    defaultConfig {
        applicationId = "app.linc.android"
        // Wireless debugging (QR / pairing-code) requires Android 11 — see docs/DECISIONS.md D-002.
        minSdk = 30
        targetSdk = 35
        // M18 C1: the first production release identity — versionName must match the desktop's
        // InformationalVersion exactly, because M17b's update check compares against it.
        // Numeric four-part versions from 1.0.0.2 on (M20 A2): "1.0.0-beta.2" is only the display
        // name; the update check compares these numbers, and a "-beta.N" suffix would be ignored.
        versionCode = 18
        versionName = "1.0.0.17"
        // Authorship, stated openly (see Provenance.kt and NOTICE); informational only.
        buildConfigField("String", "AUTHOR", "\"deloweProjects\"")
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
        buildConfig = true
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.core)
    implementation(libs.kotlinx.serialization.json)
    debugImplementation(libs.androidx.compose.ui.tooling)
    testImplementation(libs.junit)
}
