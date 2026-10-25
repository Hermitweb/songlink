package songlink.remote.data.repo

import android.content.Context
import android.content.SharedPreferences
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 配对凭据的持久化存储。
 *
 * **为什么需要它**：[WsTransport] 内部的 token 只存在内存里，进程一死就没了，
 * 于是 App 每次被系统回收后重新打开，用户都得**重新手输 6 位 PIN**。
 * 点歌台面向的是家里长辈，"每次开 App 都要重敲 PIN" 是不可接受的体验缺陷。
 *
 * 存 token 而不是 PIN 的原因（契约 §6）：Host 的 `AuthManager.IssueToken` 签发的是长期凭据，
 * 而 PIN 每次 Host 重启都会随机变化。token 失效时（Host 重启 → 内存 token 表清空），
 * 我们会退回手输 PIN，见 [SessionManager] 的降级逻辑。
 *
 * 抽成接口是为了让单元测试能用内存实现，不依赖 Android Context。
 */
interface CredentialStore {
    /** 上次连通的 Host WebSocket 地址，形如 `ws://192.168.1.5:51999`。 */
    var url: String?

    /** Host 签发的长期 token，优先用它重连。 */
    var token: String?

    /** 兜底保存的 PIN —— 仅在 token 不可用时使用。 */
    var pin: String?

    /** 忘记本机（用户主动断开连接时调用），下次启动就不要再自动连了。 */
    fun clear()
}

/** 生产实现：落到 SharedPreferences。 */
@Singleton
class PrefsCredentialStore @Inject constructor(
    @ApplicationContext app: Context
) : CredentialStore {

    private val prefs: SharedPreferences =
        app.getSharedPreferences("songlink.credentials", Context.MODE_PRIVATE)

    override var url: String?
        get() = prefs.getString(KEY_URL, null)
        set(value) = prefs.edit().putString(KEY_URL, value).apply()

    override var token: String?
        get() = prefs.getString(KEY_TOKEN, null)
        set(value) = prefs.edit().putString(KEY_TOKEN, value).apply()

    override var pin: String?
        get() = prefs.getString(KEY_PIN, null)
        set(value) = prefs.edit().putString(KEY_PIN, value).apply()

    override fun clear() {
        prefs.edit().remove(KEY_URL).remove(KEY_TOKEN).remove(KEY_PIN).apply()
    }

    private companion object {
        const val KEY_URL = "url"
        const val KEY_TOKEN = "token"
        const val KEY_PIN = "pin"
    }
}

/** 单元测试用：纯内存，不碰 Android。 */
class InMemoryCredentialStore : CredentialStore {
    override var url: String? = null
    override var token: String? = null
    override var pin: String? = null
    override fun clear() {
        url = null; token = null; pin = null
    }
}
