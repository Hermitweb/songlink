package songlink.remote

import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * 关键配置的回归测试（纯 JVM，不依赖 Android 运行时）。
 *
 * 这里钉死的两个配置都属于「删掉之后不会编译失败、只会静默坏掉」的类型：
 *
 * 1. **明文流量放行**：targetSdk=34 且缺少 `network_security_config.xml` 时，
 *    Android 默认禁止明文，OkHttp 建连 ws:// 直接抛
 *    `CLEARTEXT communication to ... not permitted` —— 整个 App 连不上 Host，
 *    但界面只是一句「连接失败」，排查成本极高。
 * 2. **相机权限**：扫码页依赖 `android.permission.CAMERA`，删掉后 zxing 会静默失败。
 *
 * 断言方式是直接读仓库里的源文件文本 —— 配置写错时能在 `./gradlew test` 阶段就红。
 */
class CriticalConfigTests {

    // ------------------------------------------------------------ 明文流量放行

    @Test
    fun `network security config permits cleartext traffic`() {
        val xml = readAppFile("src/main/res/xml/network_security_config.xml")

        assertTrue(
            "network_security_config.xml 必须显式放行明文，否则连不上局域网的 Host",
            xml.contains("""cleartextTrafficPermitted="true"""")
        )
    }

    @Test
    fun `manifest references the network security config`() {
        val manifest = readAppFile("src/main/AndroidManifest.xml")

        assertTrue(
            "AndroidManifest 的 <application> 必须引用 networkSecurityConfig，否则明文配置不生效",
            manifest.contains("""android:networkSecurityConfig="@xml/network_security_config"""")
        )
    }

    // ------------------------------------------------------------------ 相机权限

    @Test
    fun `manifest declares the camera permission`() {
        val manifest = readAppFile("src/main/AndroidManifest.xml")

        assertTrue(
            "扫码页依赖 CAMERA 权限，删掉后扫码会静默失败",
            manifest.contains("android.permission.CAMERA")
        )
    }

    // -------------------------------------------------------------------- 工具

    /** Gradle 单测的工作目录是模块目录（`.../android-remote/app`），逐级上溯找到带 settings 的仓库根。 */
    private fun readAppFile(relativeToApp: String): String {
        val root = repoRoot()
        val file = File(File(root, "app"), relativeToApp)
        assertTrue("找不到文件：${file.absolutePath}", file.isFile)
        return file.readText()
    }

    private fun repoRoot(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null) {
            if (File(dir, "settings.gradle.kts").isFile) return dir
            dir = dir.parentFile
        }
        throw AssertionError("未能从 ${File("").absolutePath} 上溯到含 settings.gradle.kts 的目录")
    }
}
