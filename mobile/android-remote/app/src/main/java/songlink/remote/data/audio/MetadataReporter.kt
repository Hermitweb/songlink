package songlink.remote.data.audio

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import songlink.remote.data.model.PlayStatus
import songlink.remote.data.model.SrcMeta
import songlink.remote.data.transport.WsTransport
import java.security.SecureRandom
import java.util.concurrent.TimeUnit
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 上报状态，给 UI 用。
 *
 * 关键设计：**"读不到"必须能被 UI 区分出来**。手机没在放歌（[NoActiveTrack]）和
 * 用户没给通知访问权限（[ReadUnavailable]）是两个完全不同的提示，混成一个"没读到"
 * 会让用户以为功能坏了却找不到原因。
 */
sealed interface MetadataStatus {

    /** 没在跑（未开始串流 / 已停止）。 */
    data object Idle : MetadataStatus

    /** 没连上 Host，这一轮什么都不做。 */
    data object WaitingConnection : MetadataStatus

    /** 读得到，但手机此刻没在放歌 —— 正常状态。 */
    data object NoActiveTrack : MetadataStatus

    /** **读不到**：通知访问权限没开 / 系统会话通道不可用。UI 应提示用户去开权限。 */
    data object ReadUnavailable : MetadataStatus

    /** 最近一次成功上报的曲目。 */
    data class Reporting(val title: String, val artist: String, val artRef: String?) : MetadataStatus

    /** 读取过程出错了（异常已被吞掉，循环继续跑）；[reason] 给 UI 做诊断用。 */
    data class Failed(val reason: String) : MetadataStatus
}

/**
 * 封面上传器。抽成接口是为了让上报逻辑在单测里不碰网络。
 *
 * 契约（Host 侧 `POST /art?name=<ref>`）：
 * ```
 * POST {hostBaseUrl}/art?name=<ref>
 * Content-Type: image/png | image/jpeg | image/webp
 * Body: 原始图片字节
 * 成功 → 200 文本 "ok"
 * ```
 */
fun interface ArtUploader {
    /** 上传成功返回 ref（含扩展名的封面引用）；失败返回 null，**调用方必须照常发 srcMeta**。 */
    suspend fun upload(hostBaseUrl: String, bytes: ByteArray, mime: String): String?
}

/** 生产用上传器：OkHttp POST，失败一律返回 null（绝不向上抛）。 */
class HttpArtUploader(private val client: OkHttpClient = defaultClient()) : ArtUploader {

    override suspend fun upload(hostBaseUrl: String, bytes: ByteArray, mime: String): String? {
        val base = hostBaseUrl.trim().trimEnd('/')
        if (base.isEmpty() || bytes.isEmpty()) return null
        val contentType = mime.takeIf { it in SUPPORTED_MIME } ?: MIME_JPEG

        return try {
            withContext(Dispatchers.IO) {
                val ref = newRef(contentType)
                // ref 只含 [A-Za-z0-9._-]，无需转义
                val request = Request.Builder()
                    .url("$base/art?name=$ref")
                    .post(bytes.toRequestBody(contentType.toMediaTypeOrNull()))
                    .build()
                client.newCall(request).execute().use { resp ->
                    if (!resp.isSuccessful) {
                        null
                    } else {
                        // Host 回 "ok" 才算成功；别的内容（错误页 / 空体）一律当作失败
                        val body = runCatching { resp.body?.string()?.trim() }.getOrNull()
                        if (body.equals("ok", ignoreCase = true)) ref else null
                    }
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (_: Throwable) {
            null
        }
    }

    private companion object {
        /** Host 侧封面上传端点接受的 MIME。 */
        val SUPPORTED_MIME = setOf("image/png", MIME_JPEG, "image/webp")

        /**
         * 生成 `phone_<16位hex>.<ext>` 形式的 ref。
         *
         * 只能出现 `[A-Za-z0-9._-]` —— Host 侧会用它拼文件路径，多一个斜杠就是一次路径穿越。
         */
        fun newRef(mime: String): String {
            val buf = ByteArray(8)
            SecureRandom().nextBytes(buf)
            val hex = buf.joinToString("") { "%02x".format(it) }
            val ext = when (mime) {
                "image/png" -> "png"
                "image/webp" -> "webp"
                else -> "jpg"
            }
            return "phone_$hex.$ext"
        }

        fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(8, TimeUnit.SECONDS)
            .callTimeout(10, TimeUnit.SECONDS)
            .build()
    }
}

/**
 * 串流期间的手机曲目上报器（契约 §3.8 `srcMeta`）。
 *
 * 电脑侧已经就绪：Host 收到 `srcMeta` 就会 `SetPhoneNowPlaying` 并广播 `state`，
 * Web 面板随之自动去取歌词、加载封面。**这条链路缺的只是手机这一帧。**
 *
 * ## 为什么"只有变了才发"
 * 轮询每秒一次，但同一首歌会连着几百秒。若每秒都发一帧 `srcMeta`，Host 会被无意义地
 * 反复唤醒去刷歌词/封面，而歌词内容并不会变。所以比对 title/artist/dur/封面之后，
 * **没变就一帧都不发**。
 *
 * ## 失败处理
 * 封面上传失败 / 读取抛异常 / 发送失败，**一律不中断轮询**：能降级的降级（丢 artRef），
 * 不能降级的落到 [status]。唯一的硬要求是这个协程不能死 —— 死了手机上的歌名就永远不会再更新。
 *
 * ## 未连接
 * [WsTransport.isConnected] 为 false 时本轮直接跳过（不读元数据也不发帧）。
 * 注意这里是"跳过本轮"而不是"停掉整个循环"：断线后点歌台还会自动重连，
 * 真停了循环就再也不会自动恢复上报。
 */
@Singleton
class MetadataReporter internal constructor(
    private val reader: PhoneMetadataReader,
    private val ws: WsTransport,
    /** 轮询协程的作用域。生产用 [Dispatchers.Default]；单测传测试调度器才能确定性驱动。 */
    private val scope: CoroutineScope,
    /** 封面上传器，单测注入假实现。 */
    private val uploader: ArtUploader,
    /** 轮询间隔（毫秒）。 */
    private val pollIntervalMs: Long = 1_000L,
) {

    /** Hilt 走这个次构造函数 —— 不要给 [CoroutineScope] / [ArtUploader] 加进参数，否则 Dagger 找不到绑定。 */
    @Inject
    constructor(reader: PhoneMetadataReader, ws: WsTransport) :
        this(
            reader,
            ws,
            CoroutineScope(SupervisorJob() + Dispatchers.Default),
            HttpArtUploader(),
        )

    private val _status = MutableStateFlow<MetadataStatus>(MetadataStatus.Idle)

    /** 最近一次上报状态，给 UI 用（如"读取失败，请开启通知访问"）。 */
    val status: StateFlow<MetadataStatus> = _status.asStateFlow()

    private var job: Job? = null

    /** 已经成功发出去的曲目指纹；null = 还没发过（或上一轮没发成功）。 */
    @Volatile
    private var lastSentKey: TrackKey? = null

    /** 已经上传过封面的曲目指纹与结果。同一首曲目的封面**只上传一次**，切歌才重新传。 */
    @Volatile
    private var artKey: TrackKey? = null

    @Volatile
    private var artRef: String? = null

    /** 读不到会话进度时的估算锚点。 */
    @Volatile
    private var posAnchorSec = 0.0

    @Volatile
    private var posAnchorMs = 0L

    /** 开始轮询上报。重复调用无副作用。 */
    fun start() {
        if (job?.isActive == true) return
        job = scope.launch {
            while (isActive) {
                pollOnce()
                delay(pollIntervalMs)
            }
        }
    }

    /** 停止轮询，并清空"已上报"状态（下次 start() 会把当前曲目重新报一遍）。 */
    fun stop() {
        job?.cancel()
        job = null
        forgetTrack()
        _status.value = MetadataStatus.Idle
    }

    /**
     * 跑一轮：读一次、必要时发一帧。
     *
     * 抽成可见方法是为了让单测能确定性地驱动同一段逻辑，而不必依赖真实时钟。
     */
    internal suspend fun pollOnce() {
        try {
            if (!ws.isConnected) {
                _status.value = MetadataStatus.WaitingConnection
                return
            }

            // 读取一律放到 IO 上：Android 14 路径要过一遍系统会话服务。
            val track = withContext(Dispatchers.IO) { reader.currentTrack() }
            if (track == null) {
                _status.value = if (reader.readChannelAvailable) {
                    MetadataStatus.NoActiveTrack
                } else {
                    MetadataStatus.ReadUnavailable
                }
                // 忘了"当前曲目"：等下一首开始播时会重新上报（而不是因为上一首的指纹被跳过）
                forgetTrack()
                return
            }

            val key = TrackKey.of(track)
            val changed = key != lastSentKey
            if (!changed) {
                _status.value = MetadataStatus.Reporting(track.title, track.artist, artRef)
                return
            }
            if (lastSentKey == null) {
                // 首帧或重新开始：进度锚点重置
                posAnchorSec = 0.0
                posAnchorMs = 0L
            }

            val ref = resolveArtRef(track, key)
            val frame = SrcMeta(
                title = track.title,
                artist = track.artist,
                pos = estimatePos(track),
                dur = track.durSec,
                // PhoneTrack 不携带播放态（见 PhoneMetadataReader 的定义），串流期间按 playing 上报；
                // 暂停时 Host 侧由音频流本身体现。
                status = PlayStatus.PLAYING,
                artRef = ref,
                seq = ws.nextSeq(),
                id = ws.nextId("m"),
            )
            if (ws.send(frame)) {
                lastSentKey = key
                _status.value = MetadataStatus.Reporting(track.title, track.artist, ref)
            } else {
                // 没发出去（socket 已死）：下一轮重试。封面结果留着，不重复上传。
                lastSentKey = null
            }
        } catch (e: CancellationException) {
            throw e
        } catch (t: Throwable) {
            // 任何异常都只落到 status 上，循环必须活着。
            _status.value = MetadataStatus.Failed(t.message ?: t::class.java.simpleName)
        }
    }

    /**
     * 封面 ref。
     *
     * 同一曲目只上传一次；**上传失败也只是丢掉 artRef，绝不因此丢掉整条元数据** ——
     * 歌名和歌词比封面重要得多。
     */
    private suspend fun resolveArtRef(track: PhoneTrack, key: TrackKey): String? {
        val bytes = track.artBytes
        if (bytes == null || bytes.isEmpty()) return null
        if (artKey == key) return artRef

        val base = ws.hostBaseUrl
        if (base.isNullOrBlank()) return null

        val mime = track.artMime?.takeIf {
            it == "image/png" || it == MIME_JPEG || it == "image/webp"
        } ?: MIME_JPEG
        val ref = try {
            uploader.upload(base, bytes, mime)
        } catch (e: CancellationException) {
            throw e
        } catch (_: Throwable) {
            null
        }
        // 成功失败都记：同一首曲子不再反复上传（网络不通时每秒重试只会刷屏）
        artKey = key
        artRef = ref
        return ref
    }

    /**
     * 进度估算。
     *
     * 读得到会话进度（MediaSession / 通知 position）就用它；读不到就按"上次记录到现在"
     * 累加 —— 宁可差一两秒，也不要一个永远为 0 的进度条。
     */
    private fun estimatePos(track: PhoneTrack): Double {
        val now = System.currentTimeMillis()
        if (track.posSec > 0.0) {
            posAnchorSec = track.posSec
            posAnchorMs = now
            return track.posSec
        }
        val prev = posAnchorMs
        posAnchorMs = now
        if (prev == 0L) return posAnchorSec
        val delta = (now - prev) / 1000.0
        posAnchorSec = (posAnchorSec + delta).coerceAtLeast(0.0)
        return posAnchorSec
    }

    /** 清掉"当前曲目"相关的缓存（停止上报 / 读不到曲目时调用）。 */
    private fun forgetTrack() {
        lastSentKey = null
        artKey = null
        artRef = null
        posAnchorSec = 0.0
        posAnchorMs = 0L
    }
}

/**
 * 曲目指纹：判断"是不是换歌了"。
 *
 * 只比 title/artist/dur/封面字节，**不比进度** —— 进度每秒都在变，拿它比就等于每秒发一帧。
 */
private data class TrackKey(
    val title: String,
    val artist: String,
    val durSec: Double,
    val art: Int,
) {
    companion object {
        fun of(t: PhoneTrack): TrackKey = TrackKey(
            title = t.title,
            artist = t.artist,
            durSec = t.durSec,
            art = t.artBytes?.contentHashCode() ?: 0,
        )
    }
}
