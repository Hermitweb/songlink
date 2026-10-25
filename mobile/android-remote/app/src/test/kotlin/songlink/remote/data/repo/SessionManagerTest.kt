package songlink.remote.data.repo

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import songlink.remote.data.model.AuthResult
import songlink.remote.data.transport.WsTransport

/**
 * [SessionManager] 凭据持久化与自动重连的单元测试（纯 JVM，不依赖设备）。
 *
 * 背景：点歌台的用户是家里长辈，"每次打开 App 都要重输 6 位 PIN" 不可接受。
 * 这里钉住三件事：
 * 1. 连上之后凭据要落盘（否则下次启动无从免 PIN）；
 * 2. 下次启动要自动重连，且**有 token 时不再传 PIN**；
 * 3. token 失效（Host 重启）要退回 PIN 而不是无限重试废凭据。
 */
@OptIn(ExperimentalCoroutinesApi::class)
class SessionManagerTest {

    private lateinit var transport: FakeWsTransport
    private lateinit var repo: NowPlayingRepo
    private lateinit var creds: InMemoryCredentialStore

    private fun newSession(scope: CoroutineScope) =
        SessionManager(transport, repo, creds, scope)

    @Before
    fun setUp() {
        transport = FakeWsTransport()
        repo = NowPlayingRepo()
        creds = InMemoryCredentialStore()
    }

    @Test
    fun `successful connect persists url and token`() = runTest {
        val session = newSession(CoroutineScope(SupervisorJob() + Dispatchers.Unconfined))

        transport.onConnect = { _, _ ->
            Result.success(AuthResult(ref = "h1", ok = true, token = "tok-1"))
        }
        session.onConnectResult(transport.connect("ws://10.0.0.5:51999", "123456"))

        assertEquals("tok-1", creds.token)
        assertEquals("ws://10.0.0.5:51999", creds.url)
    }

    @Test
    fun `rememberPin keeps pin as token fallback`() = runTest {
        val session = newSession(CoroutineScope(SupervisorJob() + Dispatchers.Unconfined))

        session.rememberPin("123456")
        session.rememberPin("   ") // 空白不应覆盖已存的 PIN

        assertEquals("123456", creds.pin)
    }

    @Test
    fun `reset forgets the host so auto reconnect does not kick in`() = runTest {
        val session = newSession(CoroutineScope(SupervisorJob() + Dispatchers.Unconfined))
        transport.onConnect = { _, _ -> Result.success(AuthResult(ok = true, token = "tok-1")) }
        session.onConnectResult(transport.connect("ws://10.0.0.5:51999", "123456"))
        assertTrue(creds.token != null)

        session.reset()

        assertNull("用户主动断开后必须忘掉凭据，否则下次启动会自己连回来", creds.token)
        assertNull(creds.url)
    }

    @Test
    fun `auto reconnect reuses stored token and sends an empty pin`() = runTest {
        // 模拟"上次已经配对过"：只有 url + token，没有 PIN
        creds.url = "ws://10.0.0.5:51999"
        creds.token = "tok-old"
        transport.onConnect = { _, _ -> Result.success(AuthResult(ok = true, token = "tok-new")) }

        val session = newSession(CoroutineScope(SupervisorJob() + UnconfinedTestDispatcher(testScheduler)))
        advanceUntilIdle()

        assertEquals("ws://10.0.0.5:51999", transport.lastUrl)
        assertEquals("有 token 时不该再传 PIN（空串会被转成 null 字段）", "", transport.lastPin)
        val st = session.connState.value as SessionManager.ConnState.Connected
        assertEquals("tok-new", st.token)
    }

    @Test
    fun `auto reconnect falls back to pin when no token was stored`() = runTest {
        creds.url = "ws://10.0.0.5:51999"
        creds.pin = "654321"
        transport.onConnect = { _, _ -> Result.success(AuthResult(ok = true, token = "tok-x")) }

        newSession(CoroutineScope(SupervisorJob() + UnconfinedTestDispatcher(testScheduler)))
        advanceUntilIdle()

        assertEquals("654321", transport.lastPin)
    }

    @Test
    fun `stale token is dropped instead of retried forever`() = runTest {
        creds.url = "ws://10.0.0.5:51999"
        creds.token = "tok-dead"
        // Host 重启 → 旧 token 作废 → 鉴权失败
        transport.onConnect = { _, _ -> Result.failure(IllegalStateException("AUTH_FAILED")) }

        val session = newSession(CoroutineScope(SupervisorJob() + UnconfinedTestDispatcher(testScheduler)))
        advanceUntilIdle()

        assertNull("失效的 token 必须清掉", creds.token)
        assertEquals(
            SessionManager.ConnState.Disconnected,
            session.connState.value
        )
    }

    @Test
    fun `no credentials means no connection attempt`() = runTest {
        newSession(CoroutineScope(SupervisorJob() + UnconfinedTestDispatcher(testScheduler)))
        advanceUntilIdle()

        assertEquals(0, transport.connectCount)
    }

    /** 只桩掉 [WsTransport.connect]，其余沿用真实实现。 */
    private class FakeWsTransport : WsTransport() {
        var connectCount = 0
        var lastUrl: String? = null
        var lastPin: String? = null

        var onConnect: (String, String) -> Result<AuthResult> =
            { _, _ -> Result.success(AuthResult(ref = "h1", ok = true, token = "tok-1")) }

        override suspend fun connect(url: String, pin: String): Result<AuthResult> {
            connectCount++
            lastUrl = url
            lastPin = pin
            // 生产路径的 connectOnce() 会把 url 记进基类字段（[WsTransport.currentUrl] 就是读它），
            // 这里补上同样的副作用，否则 onConnectResult 存不到 url。
            restoreCredentials(url, "")
            return onConnect(url, pin)
        }
    }
}
