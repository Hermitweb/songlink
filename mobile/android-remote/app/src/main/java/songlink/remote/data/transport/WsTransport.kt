package songlink.remote.data.transport

import android.os.Build
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import songlink.remote.data.model.AuthResult
import songlink.remote.data.model.Frame
import songlink.remote.data.model.Hello
import songlink.remote.data.model.Ping
import songlink.remote.data.util.FrameCodec
import songlink.remote.util.Constants
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

/**
 * WebSocket 传输层（契约 §1：局域网主通道 = WebSocket，文本帧一条 message 一帧）。
 *
 * 握手流程（契约 §3.1 / §6）：连上 → 发 `hello`(role=remote, device, os, caps, pin 或 token)
 * → 等 `authResult`；只有 `ok=true` 才置 [isConnected]（连接级鉴权门）。
 *
 * 断线后按指数退避重连，重连时带上 `hello.token`（免再输 PIN）与 `hello.lastSeq`（请求补发）。
 *
 * 设计说明：类与 [connect]/[send]/[disconnect] 都刻意留 `open`，单元测试可以
 * `object : WsTransport(httpClient, codec) {}` 覆写行为；生产路径直接 `WsTransport()`。
 * [OkHttpClient] 与 [FrameCodec] 都可注入，测试用 MockWebServer 的 client + 自定义 codec。
 */
open class WsTransport(
    private val client: OkHttpClient = defaultHttpClient(),
    private val codec: FrameCodec = FrameCodec.Default,
    private val scope: CoroutineScope = defaultScope(),
    private val device: String = defaultDeviceName(),
    private val osVersion: String = defaultOsVersion(),
    private val capabilities: List<String> = Constants.DEFAULT_CAPABILITIES,
    private val autoReconnect: Boolean = true,
    private val reconnectBaseDelayMs: Long = 1_000L,
    private val reconnectMaxDelayMs: Long = 30_000L,
    private val authTimeoutMs: Long = 5_000L,
) {

    /** 合法入站帧流（坏帧已在 [handleTextMessage] 过滤）。 */
    private val _frames = MutableSharedFlow<Frame>(extraBufferCapacity = 64)
    val frames: SharedFlow<Frame> = _frames.asSharedFlow()

    /** 最近一次鉴权回执（含 token / hostKey）。 */
    private val _authResult = MutableStateFlow<AuthResult?>(null)
    val authResult: StateFlow<AuthResult?> = _authResult.asStateFlow()

    /**
     * socket 已建立且鉴权通过。鉴权前 Host 不会推任何广播，故此时才算「连上」。
     *
     * 以 StateFlow 暴露：WiFi 漂移 / 电脑休眠这类"静默断链"必须让 UI 立刻看到，
     * 否则点歌台会一直显示"已连接"，而用户发出的每条指令都石沉大海。
     */
    private val _connected = MutableStateFlow(false)
    val connected: StateFlow<Boolean> = _connected.asStateFlow()

    /** [connected] 的同步读取，供命令下发等非 Compose 场景使用。 */
    val isConnected: Boolean get() = _connected.value

    /** 已见最大 seq，重连时填进 `hello.lastSeq`（契约 §7）。 */
    @Volatile
    var lastSeq: Long = 0
        private set

    @Volatile
    private var token: String? = null

    @Volatile
    private var hostKey: String? = null

    @Volatile
    private var socket: WebSocket? = null

    @Volatile
    private var lastUrl: String? = null

    /**
     * 当前连接的 Host 基址，形如 `http://192.168.1.5:51999`（ws/wss 已转成 http/https）。
     *
     * 封面图要经 `GET /art/{ref}` 拉取，那是 HTTP 端点而不是 WebSocket 端点，
     * 所以这里从 [lastUrl] 派生而不是直接复用。未连接时返回 null。
     */
    val hostBaseUrl: String?
        get() = lastUrl?.trimEnd('/')?.let {
            when {
                it.startsWith("wss://") -> "https://" + it.removePrefix("wss://")
                it.startsWith("ws://") -> "http://" + it.removePrefix("ws://")
                else -> null
            }
        }

    @Volatile
    private var lastPin: String? = null

    @Volatile
    private var reconnecting: Boolean = false

    private var reconnectJob: Job? = null

    /** 应用层心跳协程；断开时取消。 */
    private var heartbeatJob: Job? = null
    private val idSeq = AtomicLong(0)
    private val seqSeq = AtomicLong(0)

    /** 连接代号：手动 connect() 会让它自增，从而让在跑的重连循环自行退出。 */
    private val generation = AtomicLong(0)

    /** 下一个发送方 seq（契约 §2 要求单调递增）。 */
    fun nextSeq(): Long = seqSeq.incrementAndGet()

    /** 生成一个本端唯一的请求 ID，供 `cmd.id` / `hello.id` 使用。 */
    fun nextId(prefix: String = "r"): String = "$prefix-${idSeq.incrementAndGet()}"

    // ------------------------------------------------------------------ 连接

    /**
     * 建立连接并完成鉴权。返回 [AuthResult] 表示鉴权通过，失败返回 [Result.failure]。
     * 鉴权超时按 [authTimeoutMs]（默认 5s，契约 §7 要求 ack < 1.5s 量级）。
     */
    open suspend fun connect(url: String, pin: String): Result<AuthResult> {
        generation.incrementAndGet()
        return connectOnce(url, pin)
    }

    /** 真正的握手流程；重连循环复用它而不递增 [generation]，否则会把自己判成过期任务。 */
    private suspend fun connectOnce(url: String, pin: String): Result<AuthResult> {
        closeSocket()
        lastUrl = url
        lastPin = pin

        val auth = CompletableDeferred<AuthResult>()
        val helloId = nextId("h")
        val hello = Hello(
            role = Constants.ROLE_REMOTE,
            device = device,
            os = osVersion,
            caps = capabilities,
            pin = pin.ifEmpty { null },
            token = token,
            lastSeq = if (token != null) lastSeq else null,
            seq = nextSeq(),
            id = helloId,
        )

        val ws = try {
            client.newWebSocket(Request.Builder().url(url).build(), object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    webSocket.send(codec.serialize(hello))
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    val frame = handleTextMessage(text)
                    if (frame is AuthResult && !auth.isCompleted) auth.complete(frame)
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(NORMAL_CLOSURE, null)
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    if (!auth.isCompleted) auth.completeExceptionally(
                        TransportException("连接在鉴权完成前关闭 (code=$code $reason)")
                    )
                    onDisconnected()
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    if (!auth.isCompleted) auth.completeExceptionally(
                        TransportException("连接失败: ${t.message}", t)
                    )
                    onDisconnected()
                }
            })
        } catch (e: Exception) {
            return Result.failure(TransportException("无法建立连接 $url: ${e.message}", e))
        }
        socket = ws

        return try {
            val result = withTimeout(authTimeoutMs) { auth.await() }
            _authResult.value = result
            if (result.ok) {
                _connected.value = true
                startHeartbeat()
                result.token?.let { token = it }
                result.hostKey?.let { hostKey = it }
                Result.success(result)
            } else {
                closeSocket()
                Result.failure(AuthFailedException(result.code ?: "AUTH_FAILED"))
            }
        } catch (e: Exception) {
            closeSocket()
            Result.failure(
                if (e is TransportException) e else TransportException("鉴权超时或中断: ${e.message}", e)
            )
        }
    }

    /** 主动断开并停止自动重连。 */
    open fun disconnect() {
        generation.incrementAndGet()
        reconnectJob?.cancel()
        reconnectJob = null
        reconnecting = false
        closeSocket()
        _authResult.value = null
        token = null
        hostKey = null
    }

    /** 发送一帧；socket 未就绪返回 false。 */
    open fun send(frame: Frame): Boolean = socket?.send(codec.serialize(frame)) ?: false

    /** 发送心跳（契约 §1：3s 一次）。 */
    fun ping(): Boolean = send(Ping(seq = nextSeq()))

    /** 鉴权通过后拿到的长期令牌，重连时用。 */
    fun currentToken(): String? = token

    /**
     * 进程重启后恢复上次配对成功的凭据。
     *
     * token 只存在内存里，进程一死就没了，导致用户每次打开 App 都要重输 PIN。
     * 调用本方法把落盘的 token 灌回来，随后 `connect(url, "")` 发出的 hello 帧就会带上它
     * （见 [connectOnce] 里的 `token = token`），从而实现**免 PIN 重连**。
     * PIN 传空串即可 —— `pin.ifEmpty { null }` 会让 null 的 pin 字段不出现在帧里。
     */
    fun restoreCredentials(url: String, token: String) {
        lastUrl = url
        if (token.isNotBlank()) this.token = token
    }

    /** 当前连接的 WebSocket 地址（未连接为 null），供上层持久化使用。 */
    val currentUrl: String? get() = lastUrl

    /** 签名模式下 Host 出站广播帧的验签密钥。 */
    fun currentHostKey(): String? = hostKey

    // -------------------------------------------------------------- 入站处理

    /**
     * 解码并派发一条文本帧。坏帧（非法 JSON / 未知 type / 版本不符）返回 `null`，
     * 按契约 §5「未知 type 必须忽略」处理，不抛也不断连。
     */
    open fun handleTextMessage(text: String): Frame? {
        val frame = codec.parse(text) ?: return null
        if (frame.seq > lastSeq) lastSeq = frame.seq
        _frames.tryEmit(frame)
        return frame
    }

    private fun closeSocket() {
        stopHeartbeat()
        _connected.value = false
        socket?.close(NORMAL_CLOSURE, "bye")
        socket = null
    }

    private fun onDisconnected() {
        stopHeartbeat()
        _connected.value = false
        socket = null
        scheduleReconnect()
    }

    /**
     * 应用层心跳（契约 §1：3s 一次）。
     *
     * 和 OkHttp 的 `pingInterval` 分工：
     * - OkHttp 发的是**协议层 WebSocket PING**，用来判定 TCP 链路是否已死（应对 WiFi 漂移 / 电脑休眠）；
     * - 这里发的是**应用层 `ping` 帧**，用来保活 NAT / 防火墙上的 UDP 映射，并让 Host 侧看到我们还活着。
     *
     * `ping()` 返回 false 说明 socket 已经没了，此时立刻走 [onDisconnected] 触发退避重连，
     * 而不是继续对着一个死 socket 空转。
     */
    private fun startHeartbeat() {
        if (!autoReconnect) return
        heartbeatJob?.cancel()
        heartbeatJob = scope.launch {
            while (isActive && _connected.value) {
                delay(HEARTBEAT_INTERVAL_MS)
                if (!_connected.value) break
                if (!ping()) {
                    onDisconnected()
                    break
                }
            }
        }
    }

    private fun stopHeartbeat() {
        heartbeatJob?.cancel()
        heartbeatJob = null
    }

    /** 指数退避：base * 2^(n-1)，上限 [reconnectMaxDelayMs]（1-based attempt）。 */
    internal fun backoffDelayMs(attempt: Int): Long =
        minOf(reconnectBaseDelayMs shl minOf(attempt - 1, 20), reconnectMaxDelayMs)

    /** 指数退避重连。 */
    private fun scheduleReconnect() {
        if (!autoReconnect || reconnecting) return
        val url = lastUrl ?: return
        val pin = lastPin ?: return
        reconnecting = true
        reconnectJob?.cancel()
        reconnectJob = scope.launch {
            val gen = generation.get()
            var attempt = 0
            try {
                while (isActive && autoReconnect && gen == generation.get()) {
                    attempt++
                    delay(backoffDelayMs(attempt))
                    // 期间用户可能已经手动重连过（或主动断开），此时本循环作废
                    if (!isActive || !autoReconnect || gen != generation.get()) break
                    if (connectOnce(url, pin).isSuccess) break
                }
            } finally {
                reconnecting = false
            }
        }
    }

    /** 释放内部协程作用域（App 退出/测试收尾用）。 */
    fun shutdown() {
        disconnect()
        scope.cancel()
    }

    companion object {
        private const val NORMAL_CLOSURE = 1000

        /** 应用层心跳间隔（契约 §1：ping 每 3s 一次）。 */
        private const val HEARTBEAT_INTERVAL_MS = 3_000L

        /**
         * OkHttp 协议层 PING 间隔。
         *
         * 没有它，TCP 静默断开（手机离开 WiFi 覆盖、电脑休眠、路由器换信道）不会被察觉——
         * socket 在两端看来都还"开着"，但消息已经发不出去了。OkHttp 发 PING 后若下一个间隔
         * 内收不到 PONG，会主动关闭 socket，从而触发本类的退避重连。
         * 20s 是个平衡点：比 TCP 自身的失效判定快，又不会给路由器添乱。
         */
        private const val WS_PING_INTERVAL_SEC = 20L

        private val sharedHttpClient: OkHttpClient by lazy {
            OkHttpClient.Builder()
                .connectTimeout(10, TimeUnit.SECONDS)
                .readTimeout(0, TimeUnit.MILLISECONDS) // 长连接不读超时
                .retryOnConnectionFailure(true)
                .pingInterval(WS_PING_INTERVAL_SEC, TimeUnit.SECONDS)
                .build()
        }

        private fun defaultHttpClient(): OkHttpClient = sharedHttpClient

        private fun defaultScope(): CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

        private fun defaultDeviceName(): String {
            val parts = listOfNotNull(
                runCatching { Build.MANUFACTURER }.getOrNull(),
                runCatching { Build.MODEL }.getOrNull(),
            ).filter { it.isNotBlank() && it != "null" }
            return parts.joinToString(" ").ifEmpty { "Android" }
        }

        private fun defaultOsVersion(): String {
            val release = runCatching { Build.VERSION.RELEASE }.getOrNull()
            if (release.isNullOrBlank() || release == "null") return "Android"
            val sdk = runCatching { Build.VERSION.SDK_INT }.getOrNull() ?: 0
            return "Android $release (API $sdk)"
        }
    }
}

/** 传输层故障（连接/超时/协议层不可用）。 */
open class TransportException(message: String, cause: Throwable? = null) : Exception(message, cause)

/** 鉴权被 Host 拒绝（`authResult.ok=false`）。 */
class AuthFailedException(val code: String) : TransportException("鉴权失败: $code")