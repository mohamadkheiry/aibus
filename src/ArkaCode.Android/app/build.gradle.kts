plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }

android {
    namespace = "ir.arka.arkacode"
    compileSdk = 35
    buildToolsVersion = "35.0.0"
    defaultConfig { applicationId = "ir.arka.arkacode"; minSdk = 26; targetSdk = 35; versionCode = 1; versionName = "1.0.0" }
    buildTypes { release { isMinifyEnabled = false; proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro") } }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
}
