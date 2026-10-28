import java.net.HttpURLConnection
import java.net.URI
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.util.Properties

plugins {
    id("com.android.application")
    kotlin("android")
    kotlin("plugin.serialization")
    id("org.jetbrains.kotlin.plugin.compose")
}

// The Android wrapper's bundled extractor can lag behind YouTube changes.
// Override its raw resource with a reviewed, checksum-pinned official zipimport.
val ytDlpPin = Properties().apply {
    rootProject.file("yt-dlp.properties").inputStream().use(::load)
}
val ytDlpVersion = ytDlpPin.getProperty("version")
val ytDlpSha256 = ytDlpPin.getProperty("sha256")
require(Regex("\\d{4}\\.\\d{2}\\.\\d{2}").matches(ytDlpVersion))
require(Regex("[a-f0-9]{64}").matches(ytDlpSha256))
val runtimeResources = layout.buildDirectory.dir("generated/ytDlpResources")
fun digest(file: File): String = MessageDigest.getInstance("SHA-256").let { hash ->
    file.inputStream().use { input ->
        val buffer = ByteArray(65536)
        while (true) { val read = input.read(buffer); if (read < 0) break; hash.update(buffer, 0, read) }
    }
    hash.digest().joinToString("") { "%02x".format(it) }
}
val prepareBundledYtDlp by tasks.registering {
    inputs.file(rootProject.file("yt-dlp.properties"))
    val destination = runtimeResources.map { it.file("raw/ytdlp") }
    outputs.file(destination)
    outputs.upToDateWhen { destination.get().asFile.let { it.isFile && digest(it) == ytDlpSha256 } }
    doLast {
        val target = destination.get().asFile
        target.parentFile.mkdirs()
        val temporary = File.createTempFile(".ytdlp-", ".tmp", target.parentFile)
        val connection = URI("https://github.com/yt-dlp/yt-dlp/releases/download/$ytDlpVersion/yt-dlp").toURL().openConnection() as HttpURLConnection
        connection.connectTimeout = 15000
        connection.readTimeout = 15000
        val deadline = System.nanoTime() + 120_000_000_000L
        try {
            connection.inputStream.use { input -> temporary.outputStream().use { output ->
                val buffer = ByteArray(65536)
                var total = 0L
                while (true) {
                    check(System.nanoTime() < deadline) { "yt-dlp download timed out" }
                    val read = input.read(buffer)
                    if (read < 0) break
                    total += read
                    check(total <= 16 * 1024 * 1024) { "yt-dlp download exceeds 16 MiB" }
                    output.write(buffer, 0, read)
                }
            } }
            check(digest(temporary) == ytDlpSha256) { "Official yt-dlp SHA-256 mismatch" }
            Files.move(temporary.toPath(), target.toPath(), StandardCopyOption.REPLACE_EXISTING)
        } finally { connection.disconnect(); temporary.delete() }
    }
}
tasks.named("preBuild").configure { dependsOn(prepareBundledYtDlp) }

android {
    namespace = "ru.bespalov.videodownloader"
    compileSdk = 35

    defaultConfig {
        applicationId = "ru.bespalov.videodownloader"
        minSdk = 26
        targetSdk = 35
        versionCode = 2
        versionName = "1.0.1"
        buildConfigField("String", "YTDLP_VERSION", "\"$ytDlpVersion\"")
        buildConfigField("String", "YTDLP_SHA256", "\"$ytDlpSha256\"")
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        ndk {
            abiFilters += listOf("arm64-v8a", "x86_64")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
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
    sourceSets.getByName("main").assets.apply {
        setSrcDirs(listOf(rootProject.file("../collection-plugins")))
        exclude("**/__pycache__/**", "**/*.pyc")
    }
    sourceSets.getByName("main").res.srcDir(runtimeResources)
    packaging {
        jniLibs.useLegacyPackaging = true
        resources.excludes += setOf("META-INF/AL2.0", "META-INF/LGPL2.1")
    }
    testOptions {
        unitTests.isReturnDefaultValues = true
        animationsDisabled = true
    }
    lint {
        abortOnError = true
        checkReleaseBuilds = true
    }
}

dependencies {
    implementation(project(":core"))
    implementation(platform("androidx.compose:compose-bom:2025.03.00"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.7")
    implementation("androidx.lifecycle:lifecycle-viewmodel-ktx:2.8.7")
    implementation("androidx.documentfile:documentfile:1.0.1")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("io.coil-kt:coil-compose:2.7.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.1")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.8.0")
    implementation("io.github.junkfood02.youtubedl-android:library:0.18.1")
    implementation("io.github.junkfood02.youtubedl-android:ffmpeg:0.18.1")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.10.1")
    androidTestImplementation(platform("androidx.compose:compose-bom:2025.03.00"))
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test:core-ktx:1.6.1")
    androidTestImplementation("androidx.test.ext:junit-ktx:1.2.1")
    androidTestImplementation("androidx.test.uiautomator:uiautomator:2.3.0")
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    debugImplementation("androidx.compose.ui:ui-tooling")
    debugImplementation("androidx.compose.ui:ui-test-manifest")
}
