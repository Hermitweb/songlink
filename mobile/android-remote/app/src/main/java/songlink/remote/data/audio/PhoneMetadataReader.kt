package songlink.remote.data.audio

import android.app.Notification
import android.content.ComponentName
import android.content.Context
import android.graphics.Bitmap
import android.media.MediaMetadata
import android.media.projection.MediaProjection
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.util.Log
import androidx.annotation.RequiresApi
import androidx.core.app.NotificationManagerCompat
import dagger.Binds
import dagger.Module
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull
import java.io.ByteArrayOutputStream
import java.util.concurrent.atomic.AtomicReference
import javax.inject.Inject
import javax.inject.Singleton
import kotlin.coroutines.resumeWithException
import kotlin.coroutines.suspendCoroutine

/**
 * 手机"当前正在播放"的一条曲目快照。
 *
 * 这是 [PhoneMetadataReader] 的唯一产出，也是 `srcMeta` 帧的数据来源。
 *
 * @param posSec 已播放秒数。读不到会话进度时为 0，由 [MetadataReporter] 自行累加。
 * @param artBytes 封面原始字节；没有封面时为 null（**不是**空数组）。
 * @param artMime 封面 MIME，必须是 `image/png` / `image/jpeg` / `image/webp` 之一（Host 上传端点的契约）。
 */
data class PhoneTrack(
    val title: String,
    val artist: String,
    val album: String? = null,
    val posSec: Double,
    val durSec: Double,
    val artBytes: ByteArray?,
    val artMime: String?,
)

/**
 * 读取"手机此刻正在放什么"的抽象。
 *
 * 之所以抽一层接口：Android 14 与低版本拿数据的**机制完全不同**（MediaProjection 会话令牌 vs
 * 通知监听），而上报逻辑（[MetadataReporter]）不该关心走的是哪条路，单测更不该依赖任何系统服务。
 *
 * 约定：实现方**绝不抛异常**。取不到就返回 null，交给 [MetadataReporter] 决定状态怎么显示。
 */
interface PhoneMetadataReader {

    /** 当前曲目；没有正在播放的内容（或读不到）时返回 null。绝不抛异常。 */
    fun currentTrack(): PhoneTrack?

    /**
     * 读取通道本身是否可用（而不是"此刻有没有在放歌"）。
     *
     * 两者的区别对 UI 很关键：null + `true` = 手机没在放歌（正常）；null + `false` =
     * 用户还没给通知访问权限，**必须提示去开**，不能假装一切正常。
     * 自定义实现（测试用假实现）默认 `true`。
     */
    val readChannelAvailable: Boolean get() = true
}

/**
 * 系统实现：**Android 14+ 优先走 MediaProjection 会话令牌，任何时候都可用通知监听兜底。**
 *
 * ## 两条路径
 * 1. **Android 14 (API 34) +**：`MediaProjection.createCaptureSession()` → `getMediaSessionToken()`
 *    → `MediaSessionManager.getSessions(token)` 拿到本机全部媒体控制器 → 取正在播放的那个。
 *    这条路不需要用户去设置里开通知访问。
 * 2. **通知监听**（[PhoneNotificationListener]）：读 `MediaSessionManager.getActiveSessions(null)`
 *    缓存下来的会话，快照带完整 metadata（歌名/歌手/专辑/时长/进度/封面）。
 *    需要用户在「设置 → 应用 → 通知使用权」里勾选本应用。
 *
 * ## ⚠️ 关于路径 1 的实现方式（重要）
 * `MediaProjection.createCaptureSession()`、`MediaSessionManager.getSessions()` 在 **Android 公开 SDK 里
 * 并不存在**（`android.jar` 只有 `createVirtualDisplay` / `getActiveSessions`，这两个是 `@hide` 的系统 API）。
 * 它们只能通过反射调用，而 Android 9+ 对非 SDK 接口有灰名单限制，**在部分 ROM 上会被拦掉**。
 * 因此这里的取舍是：
 * - 反射拿不到 → **静默退回路径 2**，用户只要开了通知访问，功能完全正常；
 * - [readChannelAvailable] 如实反映"现在到底有没有可用的读取通道"，UI 不会假装成功；
 * - 本机无真机，这条路径**未经真机验证**，不作为唯一依赖。
 *
 * ## 其他降级保护
 * - 主线程调用时不做任何阻塞（直接读通知快照），避免 ANR；
 * - 会话读取有 1.5s 硬超时，系统会话服务卡住也不会拖死上报循环；
 * - 封面一律重编码 + 限边，避免往局域网里塞几 MB 的原图。
 */
@Singleton
class SystemPhoneMetadataReader @Inject constructor(
    @ApplicationContext private val ctx: Context,
) : PhoneMetadataReader {

    companion object {
        private const val TAG = "PhoneMetadataReader"

        /** 单次读取的硬上限：系统会话服务无响应时不能把上报循环拖死。 */
        private const val SESSION_READ_TIMEOUT_MS = 1_500L

        /** 封面压缩后的最长边（px）。原图常见 1000+，不缩限传就是往局域网里塞几百 KB。 */
        private const val ART_MAX_PX = 512
        private const val ART_QUALITY = 88
    }

    private val tokenLock = Any()
    private val mainHandler = Handler(Looper.getMainLooper())

    @Volatile
    private var projection: MediaProjection? = null

    @Volatile
    private var projectionCallback: MediaProjection.Callback? = null

    /** API 34+ 的会话令牌；null 表示还没成功拿到过。 */
    @Volatile
    private var sessionToken: ComponentName? = null

    /** 防止每次轮询都去试 createCaptureSession（它只能建一次，重复调会抛）。 */
    @Volatile
    private var tokenResolved = false

    /**
     * 注入 MediaProjection（由已经拿到用户授权的一方调用，见 [PhoneAudioCapturer.captureProjection]）。
     *
     * 传 null 表示解除绑定，会顺带丢掉已拿到的会话令牌。
     * 本项目当前**还没有调用方** —— 需要 `ProjectionService` 在 `getMediaProjection()` 之后补一行，
     * 属于跨文件改动，未在本轮文件所有权范围内。
     */
    fun attachProjection(p: MediaProjection?) {
        synchronized(tokenLock) {
            val old = projectionCallback
            if (old != null) {
                runCatching { projection?.unregisterCallback(old) }
            }
            projection = p
            projectionCallback = null
            sessionToken = null
            // 没注入 projection 时不记"已尝试"，等 ProjectionService 注入后还能再试。
            tokenResolved = p != null
        }
    }

    override val readChannelAvailable: Boolean
        get() = notificationAccessGranted() ||
            (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE && sessionToken != null)

    override fun currentTrack(): PhoneTrack? {
        // Android 14+ 优先会话令牌路径；主线程上直接跳过（它要过一遍 binder，不能阻塞 UI）。
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE &&
            Looper.myLooper() != Looper.getMainLooper()
        ) {
            val token = resolveSessionToken()
            if (token != null) {
                val track = runCatching {
                    runBlocking(Dispatchers.IO) {
                        withTimeoutOrNull(SESSION_READ_TIMEOUT_MS) { trackFromSessionToken(token) }
                    }
                }.getOrNull()
                if (track != null) return track
                // 会话路径暂时读不到（还没开始播放等），继续试通知快照而不是直接放弃。
            }
        }
        return trackFromNotifications()
    }

    // ------------------------------------------------------------ 路径 1：Android 14+

    /** 惰性建立 CaptureSession 并取会话令牌；拿不到就记为已失败，不再反复重试。 */
    @RequiresApi(Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
    private fun resolveSessionToken(): ComponentName? {
        sessionToken?.let { return it }
        if (tokenResolved) return null
        synchronized(tokenLock) {
            sessionToken?.let { return it }
            if (tokenResolved) return null
            val p = projection ?: return null
            val token = runCatching { createCaptureToken(p) }.getOrNull()
            tokenResolved = true
            sessionToken = token
            if (token == null) {
                Log.w(TAG, "MediaProjection.createCaptureSession() 不可用，降级到通知监听通道")
            } else {
                Log.i(TAG, "已拿到媒体会话令牌，走 MediaSession 通道")
            }
            return token
        }
    }

    /**
     * 建立 CaptureSession 并取媒体会话令牌（**全部走反射**，见类注释里的原因）。
     *
     * 系统要求先 `registerCallback` 再 `createCaptureSession`，且同一个 MediaProjection 只能建一次，
     * 所以令牌必须缓存起来复用。
     */
    @RequiresApi(Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
    private fun createCaptureToken(p: MediaProjection): ComponentName? {
        // 回调要留着：反注册可能连带让会话失效。持有它的代价只是一个 projection 的生命周期。
        val cb = object : MediaProjection.Callback() {}
        runCatching { p.registerCallback(cb, mainHandler) }
            .onFailure { Log.w(TAG, "registerCallback 失败: ${it.message}") }
        projectionCallback = cb

        val session = p.javaClass.methods
            .firstOrNull { it.name == "createCaptureSession" && it.parameterCount == 0 }
            ?.invoke(p) ?: return null
        return session.javaClass.methods
            .firstOrNull { it.name == "getMediaSessionToken" && it.parameterCount == 0 }
            ?.invoke(session) as? ComponentName
    }

    @RequiresApi(Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
    private suspend fun trackFromSessionToken(token: ComponentName): PhoneTrack? {
        val msm = ctx.getSystemService(Context.MEDIA_SESSION_SERVICE) as? MediaSessionManager ?: return null
        val controllers = runCatching { suspendGetSessions(msm, token) }.getOrElse {
            Log.w(TAG, "MediaSessionManager.getSessions() 失败（可能被非 SDK 接口限制拦下）: ${it.message}")
            return null
        }
        val best = controllers
            .filter { controllerTitle(it).isNotEmpty() }
            .maxByOrNull { c ->
                // 优先"正在播放"的，其次按播放进度挑一个像真的在播的
                val state = runCatching { c.playbackState?.state }.getOrNull()
                val pos = runCatching { c.playbackState?.position ?: 0L }.getOrDefault(0L)
                val playing = if (state == PlaybackState.STATE_PLAYING) 1_000_000L else 0L
                playing + pos.coerceAtLeast(0L)
            } ?: return null

        val md = runCatching { best.metadata }.getOrNull() ?: return null
        val pos = runCatching { best.playbackState?.position }.getOrNull()
        return buildTrack(md, pos)
    }

    /**
     * `MediaSessionManager.getSessions(token)` 的反射封装。
     *
     * 它在运行时是个 **suspend** 函数，编译后的签名是 `getSessions(ComponentName, Continuation)`，
     * 返回 `Object`；真正的工作是通过传进去的 [kotlin.coroutines.Continuation] 异步完成、结果 resume 回来。
     * 所以直接把 [suspendCoroutine] 的 continuation 递进去调用即可。
     */
    private suspend fun suspendGetSessions(
        msm: MediaSessionManager,
        token: ComponentName,
    ): List<MediaController> = suspendCoroutine { cont ->
        val method = msm.javaClass.methods
            .firstOrNull { it.name == "getSessions" && it.parameterCount == 2 }
        if (method == null) {
            cont.resumeWithException(NoSuchMethodException("MediaSessionManager.getSessions"))
            return@suspendCoroutine
        }
        try {
            @Suppress("UNCHECKED_CAST")
            method.invoke(msm, token, cont) as List<MediaController>
        } catch (e: Throwable) {
            cont.resumeWithException(e)
        }
    }

    // -------------------------------------------------------- 路径 2：通知监听（降级）

    private fun notificationAccessGranted(): Boolean = runCatching {
        // androidx.core 1.12 里只有静态方法，没有实例方法。
        NotificationManagerCompat.getEnabledListenerPackages(ctx).contains(ctx.packageName)
    }.getOrDefault(false)

    /**
     * 降级路径：从通知监听缓存的会话快照构造。
     *
     * 播放中的进度会随时间自己往前走，这里按"上次快照时间到现在"补一次，
     * 否则那个几乎不动的 position 会让面板上的进度条一直卡在原地。
     */
    private fun trackFromNotifications(): PhoneTrack? {
        val s = PhoneNotificationListener.pickActive() ?: return null
        if (s.title.isBlank()) return null
        val elapsed = (System.currentTimeMillis() - s.updatedAt).coerceAtLeast(0L)
        val posMs = if (s.state == PlaybackState.STATE_PLAYING) s.positionMs + elapsed else s.positionMs

        // 有会话元数据就按完整路径来（含封面）；只有 EXTRA_* 文本时时长/封面都没有，如实留空。
        val md = s.metadata
        return if (md != null) {
            buildTrack(md, posMs)
        } else {
            PhoneTrack(
                title = s.title,
                artist = s.artist,
                album = s.album,
                posSec = posMs.coerceAtLeast(0L) / 1000.0,
                durSec = s.durSec,
                artBytes = null,
                artMime = null,
            )
        }
    }

    // ------------------------------------------------------------------ 公共构造

    private fun controllerTitle(c: MediaController): String = runCatching {
        c.metadata?.getString(MediaMetadata.METADATA_KEY_TITLE)?.trim().orEmpty()
    }.getOrDefault("")

    /**
     * MediaMetadata → [PhoneTrack]。
     *
     * **解析失败一律返回 null**（缺 title 是硬伤：没有 title 的 srcMeta 对 Host 毫无意义，
     * 只会把面板上的歌名刷成空）。
     *
     * 注意播放态（暂停/缓冲）不往 [PhoneTrack] 里放 —— 那张表的契约里没有这个字段，
     * `srcMeta.status` 由 [MetadataReporter] 按"串流期间 = playing"填。
     */
    private fun buildTrack(md: MediaMetadata, positionMs: Long?): PhoneTrack? {
        val title = md.getString(MediaMetadata.METADATA_KEY_TITLE)?.trim().orEmpty()
        if (title.isEmpty()) return null

        val artist = firstNonBlank(
            md.getString(MediaMetadata.METADATA_KEY_ARTIST),
            md.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST),
            md.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE),
        )
        val album = md.getString(MediaMetadata.METADATA_KEY_ALBUM)?.trim()?.takeIf { it.isNotEmpty() }
        val durMs = md.getLong(MediaMetadata.METADATA_KEY_DURATION)
        val art = artImageOf(md)

        return PhoneTrack(
            title = title,
            artist = artist,
            album = album,
            posSec = (positionMs ?: 0L).coerceAtLeast(0L) / 1000.0,
            durSec = durMs.coerceAtLeast(0L) / 1000.0,
            artBytes = art?.first,
            artMime = art?.second,
        )
    }

    /**
     * 取封面字节并统一重编码成 JPEG：限边 + 质量压缩，避免原图动辄几百 KB。
     *
     * `MediaMetadata` 只公开了 `getBitmap()`（bundle 里的裸字节取不到），所以认
     * `METADATA_KEY_ART` 与 `METADATA_KEY_ALBUM_ART` 两个键。
     */
    private fun artImageOf(md: MediaMetadata): Pair<ByteArray, String>? {
        val bitmap = md.getBitmap(MediaMetadata.METADATA_KEY_ART)
            ?: md.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
            ?: return null
        return compressArt(bitmap)
    }

    private fun compressArt(src: Bitmap): Pair<ByteArray, String>? = runCatching {
        val scaled = scaleDown(src, ART_MAX_PX)
        val out = ByteArrayOutputStream()
        scaled.compress(Bitmap.CompressFormat.JPEG, ART_QUALITY, out)
        if (scaled !== src) scaled.recycle()
        val bytes = out.toByteArray()
        if (bytes.isEmpty()) null else bytes to MIME_JPEG
    }.getOrNull()

    private fun scaleDown(src: Bitmap, maxPx: Int): Bitmap {
        val longest = maxOf(src.width, src.height)
        if (longest <= maxPx) return src
        val ratio = maxPx.toDouble() / longest
        return Bitmap.createScaledBitmap(
            src,
            (src.width * ratio).toInt().coerceAtLeast(1),
            (src.height * ratio).toInt().coerceAtLeast(1),
            true,
        )
    }
}

/** Host 封面上传端点只认这三种 MIME。 */
internal const val MIME_JPEG = "image/jpeg"
internal const val MIME_PNG = "image/png"

/**
 * 通知监听服务：读取"当前曲目"的主力通道（同时也是 Android 14 反射路径失败后的兜底）。
 *
 * 系统只在用户于「设置 → 应用 → 通知使用权」里勾选本应用后才会绑定本服务；没绑定时
 * 快照缓存是空的，[PhoneMetadataReader.readChannelAvailable] 会据此返回 false，让 UI 去提示用户，
 * **而不是假装"手机上没在放歌"**。
 *
 * 本服务只负责把会话快照缓存下来，曲目解析发生在读取侧，
 * 所以即使解析逻辑有 bug，也污染不到系统回调线程。
 */
class PhoneNotificationListener : NotificationListenerService() {

    companion object {
        private const val TAG = "PhoneNotifListener"

        /** 快照在内存里的有效期。播放器换了歌但没发 remove 时，靠它兜底。 */
        private const val SNAPSHOT_TTL_MS = 30 * 60 * 1000L

        /** 播放态优先，其次取最近更新的一条。 */
        private val snapshot = AtomicReference<Snapshot?>(null)

        @Volatile
        private var connected = false

        /** 调试/诊断用：本进程里通知监听是否已连上。 */
        val isConnected: Boolean get() = connected

        /** 选一条"当前活跃"的会话；没有或已过期返回 null。整个过程不抛异常。 */
        internal fun pickActive(): Snapshot? {
            val s = snapshot.get() ?: return null
            if (System.currentTimeMillis() - s.updatedAt >= SNAPSHOT_TTL_MS) return null
            return s
        }
    }

    override fun onListenerConnected() {
        connected = true
        refreshActiveSessions()
    }

    override fun onListenerDisconnected() {
        connected = false
        snapshot.set(null)
    }

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        refreshActiveSessions()
        // 没有可读会话时才退化到通知自带字段（少数播放器只填 EXTRA_*）。
        if (sbn != null && snapshot.get()?.title.isNullOrBlank()) {
            snapshot.set(sbn.fallbackSnapshot())
        }
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification?) {
        refreshActiveSessions()
    }

    /**
     * 读系统全部活跃媒体会话并缓存快照。
     *
     * `getActiveSessions(null)` 是**公开 API**，但只有 NotificationListenerService 能调（别的身份会抛
     * SecurityException），所以必须在这里调、而不是在读取侧调。
     */
    private fun refreshActiveSessions() {
        if (!connected) return
        val msm = runCatching {
            getSystemService(Context.MEDIA_SESSION_SERVICE) as? MediaSessionManager
        }.getOrNull() ?: return
        val controllers = runCatching { msm.getActiveSessions(null) }.getOrElse {
            Log.w(TAG, "getActiveSessions 失败: ${it.message}")
            return
        }
        val best = controllers
            .mapNotNull { controllerSnapshot(it) }
            .maxByOrNull { s ->
                val playing = if (s.state == PlaybackState.STATE_PLAYING) 1_000_000_000L else 0L
                playing + s.updatedAt
            } ?: return
        snapshot.set(best)
    }

    private fun controllerSnapshot(c: MediaController): Snapshot? {
        val md = runCatching { c.metadata }.getOrNull() ?: return null
        val title = runCatching { md.getString(MediaMetadata.METADATA_KEY_TITLE)?.trim().orEmpty() }
            .getOrDefault("")
        if (title.isEmpty()) return null
        val state = runCatching { c.playbackState?.state }.getOrNull()
        val pos = runCatching { c.playbackState?.position }.getOrNull()
        return Snapshot(
            metadata = md,
            title = title,
            artist = firstNonBlank(
                md.getString(MediaMetadata.METADATA_KEY_ARTIST),
                md.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST),
            ),
            album = md.getString(MediaMetadata.METADATA_KEY_ALBUM)?.trim()?.takeIf { it.isNotEmpty() },
            durSec = md.getLong(MediaMetadata.METADATA_KEY_DURATION).coerceAtLeast(0L) / 1000.0,
            state = state ?: PlaybackState.STATE_NONE,
            positionMs = pos ?: 0L,
            updatedAt = System.currentTimeMillis(),
        )
    }

    /** 通知里只有 EXTRA_* 时的退化快照：没有精确时长，也没有封面。 */
    private fun StatusBarNotification.fallbackSnapshot(): Snapshot? {
        val extras = runCatching { notification?.extras }.getOrNull() ?: return null
        val title = runCatching { extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()?.trim().orEmpty() }
            .getOrDefault("")
        if (title.isEmpty()) return null
        return Snapshot(
            metadata = null,
            title = title,
            artist = runCatching { extras.getCharSequence(Notification.EXTRA_TEXT)?.toString()?.trim().orEmpty() }
                .getOrDefault(""),
            album = runCatching { extras.getCharSequence(Notification.EXTRA_SUB_TEXT)?.toString()?.trim() }
                .getOrNull()?.takeIf { it.isNotEmpty() },
            durSec = 0.0,
            state = PlaybackState.STATE_PLAYING,
            positionMs = 0L,
            updatedAt = postTime,
        )
    }
}

/**
 * 一条媒体会话的快照（已规范化，[title] 一定非空）。
 *
 * [metadata] 有值时是完整的 [MediaMetadata]（可取封面与精确时长）；为 null 表示这条通知
 * 只填了 EXTRA_* 文本，此时 [durSec] 可能为 0、也没有封面。
 */
internal data class Snapshot(
    val metadata: MediaMetadata?,
    val title: String,
    val artist: String,
    val album: String?,
    val durSec: Double,
    val state: Int,
    val positionMs: Long,
    val updatedAt: Long,
)

/**
 * Hilt 绑定：接口 → 系统实现。放在本文件里是因为本轮的文件所有权只允许动这一处。
 */
@Module
@InstallIn(SingletonComponent::class)
abstract class MetadataReaderModule {
    @Binds
    @Singleton
    abstract fun bindPhoneMetadataReader(impl: SystemPhoneMetadataReader): PhoneMetadataReader
}

/** 取第一个非空白字符串；全空时返回空串（不是 null，调用方统一按"没有"处理）。 */
private fun firstNonBlank(vararg values: String?): String =
    values.firstOrNull { !it.isNullOrBlank() }?.trim().orEmpty()
