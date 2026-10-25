package songlink.remote.data.audio

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
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
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import songlink.remote.data.model.AuthResult
import songlink.remote.data.model.Frame
import songlink.remote.data.model.Hello
import songlink.remote.data.model.PlayStatus
import songlink.remote.data.model.SrcMeta
import songlink.remote.data.transport.WsTransport
import songlink.remote.data.util.FrameCodec
import java.util.concurrent.CopyOnWriteArrayList

/**
 * [MetadataReporter] 的单元测试（纯 JVM，不依赖 Android 系统服务）。
 *
 * 这条链路是"手机正在放的歌显示到电脑面板"的**最后一公里**：电脑侧 [SongLink remote → Host]
 * 已经能处理 `srcMeta`，缺的就是手机这一帧。所以这里钉住的是三件事：
 * 1. **不刷屏**：同一首歌只能发一帧（每秒发一帧会把 Host 反复唤醒去刷歌词）；
 * 2. **不丢歌**：封面传不上去、读取抛异常，都不能把整条元数据丢掉，更不能让循环死掉；
 * 3. **不假装成功**：读不到且通道不可用时必须报 `ReadUnavailable`，让 UI 去提示开权限。
 *
 * 连接态用的是真实的 WebSocket 握手（[MockWebServer]），而不是给 [WsTransport] 打桩 ——
 * `isConnected` 是只读属性，桩不出来，假装连上等于没测"未连接不发"。
 */
@OptIn(ExperimentalCoroutinesApi::class)
class MetadataReporterTest {

    private lateinit var server: MockWebServer
    private val codec = FrameCodec.Default

    @Before
    fun setUp() {
        server = MockWebServer()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse =
                MockResponse().withWebSocketUpgrade(object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        val hello = codec.parse(text) as? Hello ?: return@onMessage
                        webSocket.send(
                            codec.serialize(
                                AuthResult(ref = hello.id.orEmpty(), ok = true, token = "tok-1", seq = 2)
                            )
                        )
                    }
                })
        }
        server.start()
    }

    @After
    fun tearDown() {
        runCatching { server.shutdown() }
    }

    // ------------------------------------------------------------- 只发一次 / 换歌再发

    @Test
    fun `an unchanged track is reported exactly once`() = runTest {
        val ws = connectedTransport()
        val reporter = newReporter(ws, FakeReader(track = track()))

        reporter.pollOnce()
        reporter.pollOnce()
        reporter.pollOnce()

        val metas = ws.srcMetas()
        assertEquals("同一首歌重复轮询只能发一帧，实际 ${metas.size} 帧", 1, metas.size)
        assertEquals("晴天", metas[0].title)
        assertEquals("周杰伦", metas[0].artist)
        assertEquals(269.0, metas[0].dur, 0.001)
        assertEquals(PlayStatus.PLAYING, metas[0].status)
        assertTrue("seq 必须单调递增", metas[0].seq > 0)
        assertNotNull("id 应供 Host 回包对齐", metas[0].id)
    }

    @Test
    fun `switching tracks sends a new srcMeta`() = runTest {
        val ws = connectedTransport()
        val reader = FakeReader(track = track())
        val reporter = newReporter(ws, reader)

        reporter.pollOnce()
        reader.track = track(title = "稻香")
        reporter.pollOnce()
        reporter.pollOnce()

        val metas = ws.srcMetas()
        assertEquals(2, metas.size)
        assertEquals("晴天", metas[0].title)
        assertEquals("稻香", metas[1].title)
        assertTrue("seq 必须单调递增", metas[1].seq > metas[0].seq)
    }

    // ------------------------------------------------------------------------ 封面

    @Test
    fun `cover is uploaded once and its ref travels in srcMeta`() = runTest {
        val ws = connectedTransport()
        val uploader = FakeUploader(result = "phone_0123456789abcdef.jpg")
        val bytes = byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 1, 2, 3)
        val reader = FakeReader(track = track(artBytes = bytes))
        val reporter = newReporter(ws, reader, uploader)

        reporter.pollOnce()
        val first = ws.srcMetas().single()
        assertEquals(1, uploader.calls)
        assertEquals("phone_0123456789abcdef.jpg", first.artRef)
        assertEquals(MIME_JPEG, uploader.lastMime)
        assertTrue("上传的必须是原始字节", uploader.lastBytes!!.contentEquals(bytes))
        assertEquals("上传要打 Host 的 HTTP 端点而不是 WebSocket", server.url("/").toString().trimEnd('/'), uploader.lastBaseUrl)

        reporter.pollOnce()
        assertEquals("同一首歌的封面只上传一次", 1, uploader.calls)
        assertEquals(1, ws.srcMetas().size)
    }

    @Test
    fun `a failed upload still reports the track without artRef`() = runTest {
        val ws = connectedTransport()
        val uploader = FakeUploader(result = null)
        val reader = FakeReader(track = track(artBytes = byteArrayOf(1, 2, 3)))
        val reporter = newReporter(ws, reader, uploader)

        reporter.pollOnce()

        val meta = ws.srcMetas().single()
        assertNull("封面传失败就只是没有封面，不能丢掉整条元数据", meta.artRef)
        assertEquals("晴天", meta.title)
        assertEquals("上传确实发生过一次", 1, uploader.calls)
    }

    // ------------------------------------------------------------------- 未连接 / 异常

    @Test
    fun `nothing is sent while the transport is disconnected`() = runTest {
        val ws = CollectingWs() // 故意不握手
        val reader = FakeReader(track = track())
        val reporter = newReporter(ws, reader)

        reporter.pollOnce()

        assertEquals(0, ws.sent.size)
        assertEquals(0, reader.calls)
        assertEquals(MetadataStatus.WaitingConnection, reporter.status.value)
    }

    @Test
    fun `a throwing reader does not kill the loop and shows up in status`() = runTest {
        val ws = connectedTransport()
        val reader = FakeReader(error = IllegalStateException("媒体会话服务不可用"))
        val reporter = newReporter(ws, reader)

        reporter.pollOnce()
        assertEquals(0, ws.sent.size)
        val failed = reporter.status.value
        assertTrue("读取异常要落到 status，实际 ${reporter.status.value}", failed is MetadataStatus.Failed)
        assertEquals("媒体会话服务不可用", (failed as MetadataStatus.Failed).reason)

        // 恢复正常后循环还能继续上报 —— 这才是"循环活着"的含义
        reader.error = null
        reader.track = track()
        reporter.pollOnce()
        assertEquals(1, ws.srcMetas().size)
    }

    @Test
    fun `an unavailable read channel is surfaced instead of faking success`() = runTest {
        val ws = connectedTransport()
        val reader = FakeReader(track = null, available = false)
        val reporter = newReporter(ws, reader)

        reporter.pollOnce()

        assertEquals(0, ws.sent.size)
        assertEquals(
            "没给通知访问权限时必须报 ReadUnavailable，UI 才能提示去开权限",
            MetadataStatus.ReadUnavailable,
            reporter.status.value,
        )

        // 通道正常、只是没在放歌 → 是另一个状态，不能混为一谈
        reader.available = true
        reporter.pollOnce()
        assertEquals(MetadataStatus.NoActiveTrack, reporter.status.value)
    }

    // --------------------------------------------------------------- 轮询生命周期

    @Test
    fun `the polling loop keeps running but stays quiet on an unchanged track`() = runBlocking {
        val ws = connectedTransport()
        val reader = FakeReader(track = track())
        // 用真实线程 + 真实时钟：pollOnce 内部要过 Dispatchers.IO，
        // 虚拟时间下它与测试线程的交接不可靠，这里不赌那个时序。
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val reporter = MetadataReporter(reader, ws, scope, FakeUploader(null), pollIntervalMs = 40L)

        reporter.start()
        reporter.start() // 重复 start 不应起第二个循环
        Thread.sleep(400)
        reporter.stop()
        Thread.sleep(150)

        assertTrue("轮询期间应该读到过多次，实际 ${reader.calls}", reader.calls >= 2)
        assertEquals("8 轮轮询只应发出 1 帧", 1, ws.srcMetas().size)
        val callsAtStop = reader.calls
        Thread.sleep(150)
        assertEquals("stop() 之后不该再读", callsAtStop, reader.calls)

        scope.cancel()
    }

    // ------------------------------------------------------------------------- 工具

    private fun newReporter(
        ws: WsTransport,
        reader: PhoneMetadataReader,
        uploader: ArtUploader = FakeUploader(result = null),
        scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined),
    ) = MetadataReporter(reader, ws, scope, uploader, pollIntervalMs = 1_000L)

    /** 真实握手出来的连接：`isConnected` 是只读属性，不走真握手就没法测"连上才发"。 */
    private fun connectedTransport(): CollectingWs = runBlocking {
        val ws = CollectingWs()
        val result = ws.connect("ws://${server.hostName}:${server.port}/", "123456")
        assertTrue("测试前置：握手应成功，实际 ${result.exceptionOrNull()}", result.isSuccess)
        assertTrue(ws.isConnected)
        ws
    }

    private fun track(
        title: String = "晴天",
        artist: String = "周杰伦",
        durSec: Double = 269.0,
        posSec: Double = 12.0,
        artBytes: ByteArray? = null,
    ) = PhoneTrack(
        title = title,
        artist = artist,
        album = null,
        posSec = posSec,
        durSec = durSec,
        artBytes = artBytes,
        artMime = artBytes?.let { MIME_JPEG },
    )

    private fun CollectingWs.srcMetas(): List<SrcMeta> = sent.filterIsInstance<SrcMeta>()

    /** 只桩掉 [WsTransport.send]，连接握手走真实 WebSocket。 */
    private class CollectingWs : WsTransport(
        client = OkHttpClient(),
        autoReconnect = false,
        authTimeoutMs = 5_000,
    ) {
        val sent = CopyOnWriteArrayList<Frame>()
        override fun send(frame: Frame): Boolean {
            sent.add(frame)
            return true
        }
    }

    private class FakeReader(
        var track: PhoneTrack? = null,
        var available: Boolean = true,
        var error: Throwable? = null,
    ) : PhoneMetadataReader {
        var calls = 0
        override fun currentTrack(): PhoneTrack? {
            calls++
            error?.let { throw it }
            return track
        }

        override val readChannelAvailable: Boolean get() = available
    }

    private class FakeUploader(private val result: String?) : ArtUploader {
        var calls = 0
        var lastMime: String? = null
        var lastBytes: ByteArray? = null
        var lastBaseUrl: String? = null

        override suspend fun upload(hostBaseUrl: String, bytes: ByteArray, mime: String): String? {
            calls++
            lastBaseUrl = hostBaseUrl
            lastBytes = bytes
            lastMime = mime
            return result
        }
    }
}
