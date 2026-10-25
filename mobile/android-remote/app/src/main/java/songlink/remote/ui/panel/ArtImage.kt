package songlink.remote.ui.panel

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.util.LruCache
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.util.concurrent.TimeUnit

/**
 * 专辑封面图。取不到就安静地画一个占位图标，**绝不影响点歌**。
 *
 * ## 为什么不用 Coil / Glide / Picasso
 *
 * 本项目为了压 APK 体积已经主动砍掉了两个大依赖：Apache POI（省约 25 MB classes.dex）
 * 与 tflite（省约 16 MB libtensorflowlite_jni.so）。在同一个 codebase 里再引入一个
 * 图片加载库，等于把刚省下来的体积又还回去 —— 而封面图的实际需求非常小：
 * 一次同步 GET、一张内存缓存的 Bitmap而已。因此这里直接复用**已经在依赖里**的
 * OkHttp（`libs.okhttp`，WsTransport 也用它），不新增任何 gradle 依赖。
 *
 * ## 行为
 * - [artRef] 为空 / [baseUrl] 为空 / HTTP 失败 / 字节不是图片 → 显示居中的 `MusicNote` 占位；
 * - 缓存键是 `baseUrl + artRef`，避免切页或重组时重复下载；
 * - 加载由 `LaunchedEffect(artRef, baseUrl)` 驱动，换歌时自动取消上一次请求，不留野协程。
 *
 * @param baseUrl 形如 `http://192.168.1.5:51999` 的 Host HTTP 基址；为 null 时直接显示占位。
 * @param artRef  Host `ArtStore` 里的封面引用（如 `abc123.png`）；Host 侧已校验只含 `[A-Za-z0-9._-]`。
 */
@Composable
fun ArtImage(
    artRef: String?,
    baseUrl: String?,
    modifier: Modifier = Modifier,
    contentDescription: String? = null,
) {
    // 首次组合就先命中缓存，避免「先闪占位图再闪封面」的抖动
    var bitmap by remember(artRef, baseUrl) {
        mutableStateOf(loadFromCache(artRef, baseUrl))
    }

    LaunchedEffect(artRef, baseUrl) {
        val ref = artRef?.trim().orEmpty()
        val base = baseUrl?.trim().orEmpty().trimEnd('/')
        if (ref.isEmpty() || base.isEmpty()) {
            bitmap = null
            return@LaunchedEffect
        }
        bitmap = loadArt(base, ref)
    }

    val shown = bitmap
    if (shown != null && !shown.isRecycled) {
        Image(
            bitmap = shown.asImageBitmap(),
            contentDescription = contentDescription,
            contentScale = ContentScale.Crop,
            modifier = modifier,
        )
    } else {
        // 占位分支：保留同样的 modifier（尺寸/圆角），所以取不到图时也不会塌成空白
        Box(
            modifier = modifier.background(MaterialTheme.colorScheme.surfaceVariant),
            contentAlignment = Alignment.Center
        ) {
            Icon(
                imageVector = Icons.Default.MusicNote,
                contentDescription = contentDescription,
                tint = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

// ---------------------------------------------------------------------- 加载实现

/** 同步 GET 一次封面并解码；任何异常都回落到 null（占位图）。 */
private suspend fun loadArt(baseUrl: String, ref: String): Bitmap? = withContext(Dispatchers.IO) {
    runCatching {
        cacheGet(cacheKey(baseUrl, ref))?.let { return@runCatching it }

        val url = "$baseUrl/art/${Uri.encode(ref)}"
        val bytes = client.newCall(Request.Builder().url(url).build()).execute().use { resp ->
            if (!resp.isSuccessful) null else resp.body?.bytes()
        } ?: return@runCatching null

        val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
        if (bmp != null) cachePut(cacheKey(baseUrl, ref), bmp)
        bmp
    }.getOrElse { e ->
        // 协程被取消不是「失败」，让它照常传播，否则会把取消吞掉
        if (e is CancellationException) throw e
        null
    }
}

private fun cacheKey(baseUrl: String?, artRef: String?): String =
    (baseUrl?.trim()?.trimEnd('/').orEmpty()) + "|" + (artRef?.trim().orEmpty())

private fun loadFromCache(artRef: String?, baseUrl: String?): Bitmap? {
    if (artRef.isNullOrBlank() || baseUrl.isNullOrBlank()) return null
    return cacheGet(cacheKey(baseUrl, artRef))
}

private fun cacheGet(key: String): Bitmap? = artCache.get(key)

private fun cachePut(key: String, bmp: Bitmap) {
    artCache.put(key, bmp)
}

private val artCache: LruCache<String, Bitmap> = run {
    // 约 1/8 可用堆，按 KB 计；Android 的 LruCache.sizeOf 返回的量纲要和 maxSize 一致
    val maxKb = (Runtime.getRuntime().maxMemory() / 1024 / 8).coerceAtLeast(1024L)
    object : LruCache<String, Bitmap>(maxKb.toInt()) {
        override fun sizeOf(key: String, value: Bitmap): Int =
            (value.allocationByteCount / 1024).coerceAtLeast(1).toInt()
    }
}

private val client: OkHttpClient by lazy {
    OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(8, TimeUnit.SECONDS)
        .callTimeout(10, TimeUnit.SECONDS)
        .build()
}
