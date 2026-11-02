import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("org.jetbrains.kotlin.jvm") version "2.4.20"
    id("org.jetbrains.intellij.platform") version "2.19.0"
}

group = "dev.spyglass"
version = providers.gradleProperty("pluginVersion").get()

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        val local = providers.gradleProperty("platformLocalPath").orNull
        if (!local.isNullOrBlank()) local(local)
        else rider(providers.gradleProperty("platformVersion")) { useInstaller = false }
    }
    // Unit tests run the IntelliJ-independent client (dev.spyglass.rider.hub) against a fake hub.
    testImplementation(platform("org.junit:junit-bom:5.14.2"))
    testImplementation("org.junit.jupiter:junit-jupiter")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
    testImplementation("org.java-websocket:Java-WebSocket:1.6.0")
}

java {
    sourceCompatibility = JavaVersion.VERSION_21
    targetCompatibility = JavaVersion.VERSION_21
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_21)
    }
}

intellijPlatform {
    pluginConfiguration {
        version = providers.gradleProperty("pluginVersion")
        ideaVersion {
            sinceBuild = providers.gradleProperty("pluginSinceBuild")
            untilBuild = provider { null }
        }
        changeNotes = providers.gradleProperty("pluginVersion").map { v ->
            "<p>$v: see <a href=\"https://github.com/AIDotNet/Spyglass/releases\">the release notes</a>.</p>"
        }
    }
    // JetBrains Marketplace. The first version must be uploaded by hand on plugins.jetbrains.com (it is reviewed);
    // later ones: PUBLISH_TOKEN=… ./gradlew publishPlugin -PpluginVersion=x.y.z (release.yml does this on v* tags).
    publishing {
        token = providers.environmentVariable("PUBLISH_TOKEN")
    }
    buildSearchableOptions = false
}

tasks.test {
    useJUnitPlatform()
    // The real-hub test (RealHubTest) runs only when SPYGLASS_REPO points at a built Spyglass checkout.
    environment("SPYGLASS_REPO", System.getenv("SPYGLASS_REPO") ?: "")
    testLogging {
        events("passed", "skipped", "failed")
        showStandardStreams = System.getenv("SPYGLASS_TEST_VERBOSE") == "1"
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
}
