plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "dev.wanwei.androidlab"
    compileSdk = 36
    buildToolsVersion = "36.0.0"
    defaultConfig {
        applicationId = "dev.wanwei.androidlab"
        minSdk = 26
        targetSdk = 36
        versionCode = 7
        versionName = "0.2.5"
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildTypes {
        release { isMinifyEnabled = false }
    }
}
kotlin { jvmToolchain(17) }

dependencies { testImplementation("junit:junit:4.13.2") }
