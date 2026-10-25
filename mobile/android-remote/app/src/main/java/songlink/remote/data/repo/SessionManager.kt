package songlink.remote.data.repo

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import songlink.remote.data.model.Ack
import songlink.remote.data.model.Cmd
import songlink.remote.data.model.ErrorFrame
import songlink.remote.data.model.EventFrame
import songlink.remote.data.model.HistoryItem
import songlink.remote.data.model.PlayStatus
import songlink.remote.data.model.QueueItem
import songlink.remote.data.model.SearchResults
import songlink.remote.data.transport.WsTransport
import songlink.remote.util.Constants
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 会话管理器 —— 点歌台 UI 的唯一数据入口。
 *
 * **为什么需要它**：此前 [NowPlayingRepo] 只负责把 `state`/`lyric` 归并成 StateFlow，
 * 而连接生命周期挂在 `PairViewModel`（配对页的 nav-scoped ViewModel）上。用户一旦离开配对页，
 * 帧收集协程就被 ViewModel 销毁，UI 再也收不到播放状态。本类用 Hilt 单例把
 * 「帧收集 + 连接状态 + 队列 + 命令下发」收敛到 App 生命周期里，与具体页面解耦。
 *
 * **与 [PairViewModel] 的关系**：两者共用同一个 [WsTransport] 单例。
 * `PairViewModel` 继续负责配对表单的校验与握手（它有独立单测），本类不重复实现校验逻辑，
 * 只在拿到帧之后维护会话状态。两者同时收集 `frames` 是安全的 —— SharedFlow 是广播语义，
 * 且往 [NowPlayingRepo] 写的是幂等的快照赋值。
 */
@Singleton
class SessionManager internal constructor(
    private val ws: WsTransport,
    val repo: NowPlayingRepo,
    /** 落盘的配对凭据，用于进程重启后免 PIN 重连。 */
    private val creds: CredentialStore,
    /** 帧收集协程的作用域。生产用 [Dispatchers.Default]；单测传入测试调度器才能确定性驱动。 */
    private val scope: CoroutineScope,
) {
    /** Hilt 走这个次构造函数 —— 注意不要给 [CoroutineScope] 加构造参数，否则 Dagger 找不到绑定。 */
    @Inject
    constructor(ws: WsTransport, repo: NowPlayingRepo, creds: CredentialStore) :
        this(ws, repo, creds, CoroutineScope(SupervisorJob() + Dispatchers.Default))

    /** 连接状态；UI 据此决定显示「未连接 / 连接中 / 已连接 / 失败」。 */
    sealed interface ConnState {
        data object Disconnected : ConnState
        data object Connecting : ConnState
        data class Connected(val token: String? = null, val hostKey: String? = null) : ConnState
        data class Failed(val reason: String) : ConnState
    }

    /** 一次性消息（Host 侧错误、指令失败等）。 */
    data class UiMessage(val text: String, val isError: Boolean = true)

    /** Host 的 HTTP 基址，用于拼 /art/{ref}；未连接时为 null。 */
    val hostBaseUrl: String? get() = ws.hostBaseUrl

    /**
     * Host 的纯主机名/IP（不含协议与端口），例如 `192.168.1.5`。
     *
     * 音频串流要把 UDP 打到这台机器上，而目标就是用户在配对时填的那台电脑 ——
     * **不是手机自己的 IP**。以前 StreamViewModel 用 `getLocalIpAddress()` 取本机 IP
     * 当目标，等于把音频发回自己，是个方向性错误；这里给出唯一正确的来源。
     */
    val hostAddress: String?
        get() = ws.currentUrl?.let { url ->
            runCatching {
                val withoutScheme = url.substringAfter("://")
                withoutScheme.substringBefore('/').substringBefore(':').ifBlank { null }
            }.getOrNull()
        } ?: creds.url?.let { url ->
            runCatching {
                url.substringAfter("://").substringBefore('/').substringBefore(':').ifBlank { null }
            }.getOrNull()
        }

    private val _connState = MutableStateFlow<ConnState>(ConnState.Disconnected)
    val connState: StateFlow<ConnState> = _connState.asStateFlow()

    private val _queue = MutableStateFlow<List<QueueItem>>(emptyList())
    val queue: StateFlow<List<QueueItem>> = _queue.asStateFlow()

    private val _history = MutableStateFlow<List<HistoryItem>>(emptyList())
    val history: StateFlow<List<HistoryItem>> = _history.asStateFlow()

    /** 搜索结果，带 replay(1)：切页回来仍能看到最近一次结果。 */
    private val _searchResults = MutableStateFlow<SearchResults?>(null)
    val searchResults: StateFlow<SearchResults?> = _searchResults.asStateFlow()

    private val _trending = MutableStateFlow<List<songlink.remote.data.model.SearchResultItem>>(emptyList())
    val trending: StateFlow<List<songlink.remote.data.model.SearchResultItem>> = _trending.asStateFlow()

    private val _messages = MutableSharedFlow<UiMessage>(extraBufferCapacity = 32)
    val messages: SharedFlow<UiMessage> = _messages.asSharedFlow()

    /**
     * 在途指令 id → action。
     *
     * Host 对每条 `cmd` 都会回 `ack{ref, ok}`，但 `ack` 里不带 action，
     * 靠这张表才能把「失败了」对应回「是哪条操作失败了」，从而给出可读提示。
     */
    private val inflight = ConcurrentHashMap<String, String>()

    init {
        scope.launch {
            ws.frames.collect { frame -> onFrame(frame) }
        }
        // 连接态以传输层为准。这里同时覆盖两种情况：
        // 1) 静默断链（WiFi 漂移 / 电脑休眠）→ 转 Disconnected，UI 不再假装"已连接"；
        // 2) 退避重连成功 → 自动转回 Connected，用户无需手动重连。
        scope.launch {
            ws.connected.collect { ok ->
                _connState.value = when {
                    ok -> ConnState.Connected(ws.currentToken(), ws.currentHostKey())
                    _connState.value is ConnState.Connecting -> _connState.value
                    _connState.value is ConnState.Failed -> _connState.value
                    else -> ConnState.Disconnected
                }
            }
        }
        // 进程重启后自动重连：优先用落盘的 token 免 PIN；
        // token 失效（Host 重启过）则退回 PIN。都拿不到就保持未连接，等用户去配对页。
        scope.launch { autoReconnect() }
    }

    /**
     * 用上次保存的凭据尝试自动连上点歌台。
     *
     * 点歌台的用户是家里长辈，"每次打开 App 都要重输 6 位 PIN" 不可接受，
     * 所以这里在会话建立时就自动尝试一次，连上了用户直接就能点歌。
     */
    private suspend fun autoReconnect() {
        val url = creds.url?.takeIf { it.isNotBlank() } ?: return
        val token = creds.token?.takeIf { it.isNotBlank() }
        val pin = creds.pin?.takeIf { it.isNotBlank() }
        if (token == null && pin == null) return

        _connState.value = ConnState.Connecting
        if (token != null) ws.restoreCredentials(url, token)

        val result = ws.connect(url, if (token != null) "" else pin!!)
        if (result.isFailure && token != null) {
            // 多半是 Host 重启导致 token 作废（AuthManager 的 token 表在内存里）：
            // 清掉废凭据让用户重新配一次，而不是每次启动都重试一轮再失败。
            creds.token = null
            _connState.value = ConnState.Disconnected
        } else {
            onConnectResult(result)
        }
    }

    // ------------------------------------------------------------------ 帧处理

    private fun onFrame(frame: songlink.remote.data.model.Frame) {
        when (frame) {
            // state / lyric / searchResults 统一交给 NowPlayingRepo 归并，
            // 本类只额外维护队列与会话状态。
            is songlink.remote.data.model.State -> repo.onFrame(frame)

            is SearchResults -> {
                repo.onFrame(frame)
                if (frame.query == "trending") _trending.value = frame.results
                else _searchResults.value = frame
            }

            is songlink.remote.data.model.Lyric -> repo.onFrame(frame)

            is EventFrame -> onEvent(frame)

            is Ack -> {
                val action = inflight.remove(frame.ref)
                if (!frame.ok && action != null) {
                    emit(UiMessage(failureText(action), isError = true))
                }
            }

            is ErrorFrame -> emit(UiMessage(errorText(frame), isError = true))

            else -> repo.onFrame(frame)
        }
    }

    private fun onEvent(e: EventFrame) {
        when (e.name) {
            "queue-updated" -> _queue.value = e.queue.orEmpty().mapNotNull { el ->
                (el as? JsonObject)?.let { QueueItem.fromJson(it) }
            }

            "history-list" -> _history.value = e.queue.orEmpty().mapNotNull { el ->
                (el as? JsonObject)?.let { HistoryItem.fromJson(it) }
            }

            // queue-shuffled / queue-unshuffled 之后 Host 会紧接着广播 queue-updated，
            // 所以这里什么都不用做 —— 注意不要去发"queue.list"，Host 并没有这个 action，
            // 发了会收到 ok=false 的 ack 并弹出无意义的错误提示。
            "queue-shuffled", "queue-unshuffled" -> Unit

            // 连接态由 ws.connected 流统一驱动，这里不再重复判断
            else -> Unit
        }
    }

    // ------------------------------------------------------------------ 连接

    /**
     * 建立连接。真正的校验（ws:// 前缀、6 位 PIN）由 `PairViewModel` 负责，
     * 这里只负责把结果落到会话状态上，并在**成功后把凭据落盘**，
     * 好让下次打开 App 能自动重连（见 [autoReconnect]）。
     */
    fun onConnectResult(result: Result<songlink.remote.data.model.AuthResult>) {
        _connState.value = result.fold(
            onSuccess = { auth ->
                ws.currentUrl?.let { creds.url = it }
                auth.token?.let { creds.token = it }
                ConnState.Connected(auth.token, auth.hostKey)
            },
            onFailure = { ConnState.Failed(it.message ?: "连接失败") },
        )
    }

    fun onConnecting() {
        _connState.value = ConnState.Connecting
    }

    /**
     * 记下这次配对用的 PIN，作为 token 失效时的兜底。
     *
     * Host 重启后 [AuthManager] 的 token 表会清空，旧 token 全部作废，
     * 这时若能退回 PIN 就能自动重连，不必把用户重新赶到配对页。
     *
     * 注意：token 与 PIN 都以明文存在 SharedPreferences 里。对家用局域网点歌台可接受
     * （与 Web 面板存在 localStorage 的做法一致），但不要因此把它用在公网暴露的场景。
     */
    fun rememberPin(pin: String) {
        if (pin.isNotBlank()) creds.pin = pin
    }

    /** 断开连接并清空会话内的一切派生状态，避免 UI 继续显示上一台主机的歌。 */
    fun reset() {
        ws.disconnect()
        repo.reset()
        _connState.value = ConnState.Disconnected
        _queue.value = emptyList()
        _history.value = emptyList()
        _searchResults.value = null
        _trending.value = emptyList()
        inflight.clear()
        // 用户是主动断开的 → 忘掉这台主机，否则下次启动 [autoReconnect] 会又连回来，
        // 用户明明点了断开却发现它自己又连上了。
        creds.clear()
    }

    // ------------------------------------------------------------------ 传输控制

    fun play() = sendCmd("play", null)
    fun pause() = sendCmd("pause", null)

    /** 按当前播放态决定发 play 还是 pause。 */
    fun toggle() {
        if (repo.nowPlaying.value.status == PlayStatus.PLAYING) pause() else play()
    }

    fun next() = sendCmd("next", null)
    fun prev() = sendCmd("prev", null)

    /** 音量 0.0~1.0，Host 侧会做裁剪。 */
    fun setVolume(level: Double) {
        sendCmd("volume", buildJsonObject { put("level", level.coerceIn(0.0, 1.0)) })
    }

    /** 立即播放某一首（会打断当前播放）。 */
    fun pick(title: String, artist: String) {
        sendCmd("pick", buildJsonObject {
            put("title", title)
            put("artist", artist)
        })
    }

    /** 歌词偏移校准，毫秒，可正可负。 */
    fun calibrate(offsetMs: Int, title: String, artist: String) {
        sendCmd("calibrate", buildJsonObject {
            put("offsetMs", offsetMs)
            put("title", title)
            put("artist", artist)
        })
    }

    // ------------------------------------------------------------------ 点歌 / 队列

    /** 加入队列尾部；[by] 是点歌人昵称，用于「谁点的歌」展示。 */
    fun addToQueue(title: String, artist: String, by: String) {
        sendCmd("queue.add", buildJsonObject {
            put("title", title)
            put("artist", artist)
            put("by", by.ifBlank { "手机" })
        })
    }

    fun removeFromQueue(index: Int) {
        sendCmd("queue.remove", buildJsonObject { put("index", index) })
    }

    /** 把 [index] 位置的曲目提到队首。 */
    fun topInQueue(index: Int) {
        sendCmd("queue.top", buildJsonObject { put("index", index) })
    }

    /** 上移/下移一格。 */
    fun moveInQueue(from: Int, to: Int) {
        if (to < 0) return
        sendCmd("queue.move", buildJsonObject {
            put("from", from)
            put("to", to)
        })
    }

    fun clearQueue() = sendCmd("queue.clear", null)
    fun shuffleQueue() = sendCmd("queue.shuffle", null)

    // ------------------------------------------------------------------ 查询

    fun search(kw: String) {
        val k = kw.trim()
        if (k.isEmpty()) return
        sendCmd("search", buildJsonObject { put("kw", k) })
    }

    fun requestTrending(limit: Int = 20) {
        sendCmd("trending", buildJsonObject { put("limit", limit) })
    }

    fun requestHistory(limit: Int = 20) {
        sendCmd("history.list", buildJsonObject { put("limit", limit) })
    }

    fun requestLyric(title: String, artist: String) {
        if (title.isBlank()) return
        sendCmd("getLyric", buildJsonObject {
            put("title", title)
            put("artist", artist)
        })
    }

    /**
     * 下发一条 `cmd`。返回是否成功进入发送队列。
     *
     * 断线时直接丢弃并提示，避免用户对着没反应的界面反复点击。
     */
    private fun sendCmd(action: String, args: JsonObject?): Boolean {
        if (!ws.isConnected) {
            emit(UiMessage("还没连上点歌台", isError = true))
            return false
        }
        val id = ws.nextId("c")
        inflight[id] = action
        val sent = ws.send(Cmd(id = id, action = action, args = args, seq = ws.nextSeq()))
        if (!sent) inflight.remove(id)
        return sent
    }

    private fun emit(m: UiMessage) {
        _messages.tryEmit(m)
    }

    private companion object {
        /** 把 action 翻成用户看得懂的话；`ack.ok=false` 时用。 */
        fun failureText(action: String): String = when (action) {
            "queue.add" -> "加入队列失败"
            "queue.remove" -> "删除失败"
            "queue.top" -> "置顶失败"
            "queue.move" -> "调整顺序失败"
            "queue.clear" -> "清空队列失败"
            "pick" -> "点歌失败，请确认电脑上的音乐 App 正在运行"
            "volume" -> "调节音量失败"
            "search" -> "搜索失败"
            "getLyric" -> "获取歌词失败"
            else -> "操作失败：$action"
        }

        /** Host 侧 `error.code` → 用户文案；未知 code 退回到 hint/code 本身。 */
        fun errorText(f: ErrorFrame): String {
            val hint = f.hint?.takeIf { it.isNotBlank() }
            return when (f.code) {
                "DUPLICATE" -> hint ?: "这首歌已经在队列里了"
                "PICK_UNSUPPORTED" -> "当前音源不支持点歌，换用本地音乐试试"
                "APP_NOT_FOUND" -> "没找到电脑上的音乐 App，请先把它打开"
                "PATH_INVALID" -> hint ?: "文件夹路径无效"
                "SCAN_FAILED" -> hint ?: "扫描文件夹失败"
                else -> hint ?: ("操作失败：" + f.code)
            }
        }
    }
}
