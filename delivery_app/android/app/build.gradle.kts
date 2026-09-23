plugins {
    id("com.android.application")
    id("kotlin-android")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

android {
    namespace = "com.smart.delivery"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = "30.0.16138531"

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }

    kotlinOptions {
        jvmTarget = JavaVersion.VERSION_11.toString()
    }

        val whitelabelAppId = System.getenv("APPLICATION_ID")
    val defaultAppId = "com.smart.delivery"
    val finalAppId = if (whitelabelAppId != null && whitelabelAppId.isNotBlank()) whitelabelAppId else defaultAppId

    val whitelabelAppName = System.getenv("WHITELABEL_APP_NAME")
        ?: (if (System.getenv("APP_NAME") != null && System.getenv("APP_NAME") != "Gradle") System.getenv("APP_NAME") else null)
    val defaultAppName = "Smart Delivery"
    val finalAppName = if (whitelabelAppName != null && whitelabelAppName.isNotBlank()) whitelabelAppName else defaultAppName

    defaultConfig {
        // Unique Application ID
        applicationId = finalAppId
        resValue("string", "app_name", finalAppName)
        minSdk = flutter.minSdkVersion
        targetSdk = flutter.targetSdkVersion
        versionCode = flutter.versionCode
        versionName = flutter.versionName
        multiDexEnabled = true
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.getByName("debug")
            isMinifyEnabled = false
            isShrinkResources = false
            proguardFiles(
                getDefaultProguardFile("proguard-android.txt"),
                "proguard-rules.pro"
            )
        }
    }
}

flutter {
    source = "../.."
}


