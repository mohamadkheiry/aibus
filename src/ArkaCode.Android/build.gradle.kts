buildscript {
    repositories { maven("https://maven.aliyun.com/repository/google"); maven("https://maven.aliyun.com/repository/public"); google(); mavenCentral(); gradlePluginPortal() }
    dependencies {
        classpath("com.android.tools.build:gradle:8.7.3")
        classpath("org.jetbrains.kotlin:kotlin-gradle-plugin:2.0.21")
    }
}
