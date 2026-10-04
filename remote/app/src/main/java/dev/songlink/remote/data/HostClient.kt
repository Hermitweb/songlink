package dev.songlink.remote.data

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

/**
 * 与 PC Host 的 WebSocket 客户端（对应协议契约）。
 * 收帧走 [frames] SharedFlow；发指令用 sendCmd/sendHello。
 */
class HostClient(private val scope: CoroutineScope) {

    companion object {
        val Json = Json {
            classDiscriminator = "type"
            ignoreUnknownKeys = true
            encodeDefaults = true   // 必须为 true:否则基类默认字段 v 被省略,.NET Host 会判 BAD_PROTO(#2)
        }
    }

    private val client = OkHttpClient()
    private var socket: WebSocket? = null
    private var seq = 0L
    private val nextSeq: Long get() = ++seq

    private val _frames = MutableSharedFlow<Frame>(
        replay = 1, extraBufferCapacity = 64, onBufferOverflow = BufferOverflow.DROP_OLDEST
    )
    val frames: SharedFlow<Frame> = _frames.asSharedFlow()

    private val _connected = MutableSharedFlow<Boolean>(replay = 1)
    val connected: SharedFlow<Boolean> = _connected.asSharedFlow()

    fun connect(url: String) {
        val request = Request.Builder().url(url).build()
        socket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                _connected.tryEmit(true)
            }
            override fun onMessage(webSocket: WebSocket, text: String) {
                runCatching { Json.decodeFromString<Frame>(text) }.getOrNull()?.let { _frames.tryEmit(it) }
            }
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                _connected.tryEmit(false)
            }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                _connected.tryEmit(false)
            }
        })
    }

    private fun send(frame: Frame) {
        frame.seq = nextSeq
        socket?.send(Json.encodeToString(Frame.serializer(), frame))
    }

    fun sendHello(device: String, caps: List<String>) =
        send(Hello(role = "remote", device = device, os = "Android", caps = caps))

    fun sendCmd(action: String, argsJson: String? = null) {
        val args = argsJson?.let { Json.parseToJsonElement(it) as? kotlinx.serialization.json.JsonObject }
        send(Cmd(action = action, args = args, id = "c$nextSeq"))
    }

    /** PHONE_STREAM：把手机正在放的元数据上报给 Host（架构 §14）。 */
    fun sendSrcMeta(title: String, artist: String, pos: Double, dur: Double) =
        send(SrcMeta(title = title, artist = artist, pos = pos, dur = dur, status = "playing"))

    fun close() { socket?.close(1000, "bye") }
}
