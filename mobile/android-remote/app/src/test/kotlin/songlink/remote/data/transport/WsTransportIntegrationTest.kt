package songlink.remote.data.transport

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.onSubscription
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import songlink.remote.Fixtures
import songlink.remote.data.model.AuthResult
import songlink.remote.data.model.Frame
import songlink.remote.data.model.Hello
import songlink.remote.data.model.Ping
import songlink.remote.data.model.State
import songlink.remote.data.util.FrameCodec
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference

/**
 * WsTransport 集成测试。
 *
 * 用 okhttp 官方 [MockWebServer]（`MockResponse.withWebSocketUpgrade`）做真实的 WebSocket
 * 握手 —— 手写 mock server 算不对 `Sec-WebSocket-Accept`，测出来的不是真实行为。
 *
 * 这里刻意用 [runBlocking] + 真实时钟而不是 `runTest`：传输层有 OkHttp 回调线程和真实的
 * 退避 `delay`，跑在虚拟时间下反而测不准。
 */
class WsTransportIntegrationTest {

    private lateinit var server: MockWebServer
    private lateinit var transport: WsTransport

    /** 第几次 WebSocket 升级请求（每次握手 +1）。 */
    private val connections = AtomicInteger(0)

    private val codec = FrameCodec.Default

    @Before
    fun setUp() {
        connections.set(0)
        server = MockWebServer()
        server.start()
        transport = newTransport(autoReconnect = false)
    }

    @After
    fun tearDown() {
        runCatching { transport.shutdown() }
        runCatching { server.shutdown() }
    }

    // ------------------------------------------------------------- 握手 + 鉴权

    @Test
    fun `connect sends hello then becomes connected after authResult`() = runBlocking {
        val firstText = AtomicReference<String>()
        serveHost { ws, text ->
            firstText.compareAndSet(null, text)
            val hello = codec.parse(text) as? Hello ?: return@serveHost
            if (hello.role != "remote") return@serveHost
            ws.send(
                codec.serialize(
                    AuthResult(ref = hello.id.orEmpty(), ok = true, token = "tok-1", hostKey = "hk-1", seq = 2)
                )
            )
        }

        val result = transport.connect(wsUrl(), "123456")

        assertTrue("鉴权应成功，实际: ${result.exceptionOrNull()}", result.isSuccess)
        assertEquals("tok-1", result.getOrThrow().token)
        assertEquals("hk-1", transport.currentHostKey())
        assertTrue(transport.isConnected)

        val hello = codec.parse(firstText.get()) as? Hello
        assertNotNull("连上后第一帧必须是 hello", hello)
        assertEquals("remote", hello!!.role)
        assertEquals("Pixel 7 Test", hello.device)
        assertEquals("Android 14 Test", hello.os)
        assertEquals(listOf("cmd", "state"), hello.caps)
        assertEquals("123456", hello.pin)
        assertEquals(1, hello.v)
        assertEquals(1L, hello.seq)
        assertNotNull("hello 应带 id 供 authResult 回填 ref", hello.id)
        assertNull("首次配对不应带 token", hello.token)
        assertNull("首次配对不应带 lastSeq", hello.lastSeq)
    }

    @Test
    fun `reconnect hello carries token and lastSeq instead of pin`() = runBlocking {
        // 第 1 次：PIN 配对成功
        serveHost { ws, text ->
            val hello = codec.parse(text) as? Hello ?: return@serveHost
            ws.send(codec.serialize(AuthResult(ref = hello.id.orEmpty(), ok = true, token = "tok-1", seq = 2)))
        }
        assertTrue(transport.connect(wsUrl(), "123456").isSuccess)
        assertNotNull(transport.handleTextMessage(Fixtures.text("state"))) // 记下 lastSeq = 8
        assertEquals(8L, transport.lastSeq)

        // 第 2 次：重连，应带 token + lastSeq 而不是 PIN
        val secondHello = AtomicReference<String>()
        connections.set(0)
        serveHost { ws, text ->
            if (connections.get() == 1) {
                secondHello.compareAndSet(null, text)
                val hello = codec.parse(text) as? Hello ?: return@serveHost
                ws.send(codec.serialize(AuthResult(ref = hello.id.orEmpty(), ok = true, seq = 9)))
            }
        }
        assertTrue(transport.connect(wsUrl(), "").isSuccess)

        val hello = codec.parse(secondHello.get()) as? Hello
        assertNotNull(hello)
        assertEquals("tok-1", hello!!.token)
        assertEquals(8L, hello.lastSeq)
        assertNull("已持有 token 时不再带 PIN", hello.pin)
    }

    @Test
    fun `failed authResult surfaces as failure and leaves the transport disconnected`() = runBlocking {
        serveHost { ws, text ->
            val hello = codec.parse(text) as? Hello ?: return@serveHost
            ws.send(
                codec.serialize(AuthResult(ref = hello.id.orEmpty(), ok = false, code = "AUTH_FAILED", seq = 2))
            )
        }

        val result = transport.connect(wsUrl(), "000000")

        assertTrue(result.isFailure)
        assertTrue(result.exceptionOrNull() is AuthFailedException)
        assertEquals("AUTH_FAILED", (result.exceptionOrNull() as AuthFailedException).code)
        assertFalse(transport.isConnected)
    }

    @Test
    fun `auth timeout is reported as failure instead of hanging forever`() {
        // 服务端升级后不回 authResult，应在 authTimeoutMs 后失败
        serveHost { _, _ -> }
        val impatient = newTransport(autoReconnect = false, authTimeoutMs = 300)
        try {
            val r = runBlocking { impatient.connect(wsUrl(), "123456") }
            assertTrue(r.isFailure)
            assertFalse(impatient.isConnected)
        } finally {
            impatient.shutdown()
        }
    }

    // --------------------------------------------------------------- 入站派发

    @Test
    fun `valid frames reach the frames flow`() = runBlocking {
        serveHost { ws, text ->
            when (val frame = codec.parse(text)) {
                is Hello -> ws.send(
                    codec.serialize(AuthResult(ref = frame.id.orEmpty(), ok = true, seq = 2))
                )
                is Ping -> ws.send(Fixtures.text("state"))
                else -> Unit
            }
        }

        withFrameCollector { received ->
            assertTrue(transport.connect(wsUrl(), "123456").isSuccess)
            assertTrue("send ping 应成功", transport.ping())

            val state = withTimeout(5_000) {
                var found: Frame? = null
                while (found !is State) {
                    found = received.receive()
                }
                found
            }
            assertEquals(8L, state!!.seq)
            assertEquals("晴天", (state as State).now.title)
            assertEquals(8L, transport.lastSeq)
        }
    }

    @Test
    fun `bad frames are ignored without breaking the connection`() = runBlocking {
        serveHost { ws, text ->
            val hello = codec.parse(text) as? Hello ?: return@serveHost
            // 先发一个坏帧（fixture bad_not_json），再正常鉴权
            ws.send("this is not json")
            ws.send(codec.serialize(AuthResult(ref = hello.id.orEmpty(), ok = true, seq = 2)))
        }

        withFrameCollector { received ->
            assertTrue("坏帧不应影响鉴权", transport.connect(wsUrl(), "123456").isSuccess)

            // 坏帧被丢弃，流里第一条应当是合法帧
            val first = withTimeout(5_000) { received.receive() }
            assertTrue("坏帧不应出现在帧流里，实际: $first", first is AuthResult)
            assertTrue(transport.isConnected)
        }
    }

    @Test
    fun `handleTextMessage rejects every bad fixture without throwing`() {
        Fixtures.BAD_NAMES.forEach { name ->
            assertNull("$name 应被丢弃", transport.handleTextMessage(Fixtures.text(name)))
        }
        assertEquals(0L, transport.lastSeq)
    }

    // --------------------------------------------------------------- 退避重连

    @Test
    fun `backoff grows exponentially and is capped`() {
        val t = newTransport(autoReconnect = true, reconnectBaseDelayMs = 50, reconnectMaxDelayMs = 500)
        try {
            assertEquals(50L, t.backoffDelayMs(1))
            assertEquals(100L, t.backoffDelayMs(2))
            assertEquals(200L, t.backoffDelayMs(3))
            assertEquals(400L, t.backoffDelayMs(4))
            assertEquals(500L, t.backoffDelayMs(5))
            assertEquals(500L, t.backoffDelayMs(50))
        } finally {
            t.shutdown()
        }
    }

    @Test
    fun `reconnects after the peer drops the connection`() = runBlocking {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                connections.incrementAndGet()
                // 升级后立刻关闭，逼客户端走重连路径
                return MockResponse().withWebSocketUpgrade(object : WebSocketListener() {
                    override fun onOpen(webSocket: WebSocket, response: Response) {
                        webSocket.close(1000, "bye")
                    }
                })
            }
        }
        val retrying = newTransport(
            autoReconnect = true,
            reconnectBaseDelayMs = 50,
            reconnectMaxDelayMs = 100,
            authTimeoutMs = 2_000,
        )
        try {
            retrying.connect(wsUrl(), "123456")

            val deadline = System.currentTimeMillis() + 8_000
            while (System.currentTimeMillis() < deadline && connections.get() < 3) {
                delay(20)
            }
            assertTrue(
                "断线后应至少再连 2 次，实际连接次数=${connections.get()}",
                connections.get() >= 3
            )

            retrying.disconnect()
            val afterDisconnect = connections.get()
            delay(500)
            assertEquals("disconnect() 后不应继续重连", afterDisconnect, connections.get())
        } finally {
            retrying.shutdown()
        }
    }

    // ------------------------------------------------------------------ 工具

    private fun newTransport(
        autoReconnect: Boolean,
        reconnectBaseDelayMs: Long = 1_000L,
        reconnectMaxDelayMs: Long = 5_000L,
        authTimeoutMs: Long = 3_000L,
    ) = WsTransport(
        client = OkHttpClient(),
        codec = codec,
        device = "Pixel 7 Test",
        osVersion = "Android 14 Test",
        capabilities = listOf("cmd", "state"),
        autoReconnect = autoReconnect,
        reconnectBaseDelayMs = reconnectBaseDelayMs,
        reconnectMaxDelayMs = reconnectMaxDelayMs,
        authTimeoutMs = authTimeoutMs,
    )

    /** 每次连接都升级为 WebSocket，并把客户端发来的文本帧交给 [handler]。 */
    private fun serveHost(handler: (WebSocket, String) -> Unit) {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                connections.incrementAndGet()
                return MockResponse().withWebSocketUpgrade(object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        handler(webSocket, text)
                    }
                })
            }
        }
    }

    /** 订阅 [WsTransport.frames] 后执行 [body]，保证订阅先于服务端推送建立。 */
    private suspend fun withFrameCollector(body: suspend (Channel<Frame>) -> Unit) {
        val received = Channel<Frame>(Channel.UNLIMITED)
        val subscribed = CompletableDeferred<Unit>()
        val collector = CoroutineScope(Dispatchers.Default).launch {
            transport.frames.onSubscription { subscribed.complete(Unit) }.collect { received.trySend(it) }
        }
        try {
            subscribed.await()
            body(received)
        } finally {
            collector.cancel()
        }
    }

    private fun wsUrl(): String = "ws://${server.hostName}:${server.port}/"
}