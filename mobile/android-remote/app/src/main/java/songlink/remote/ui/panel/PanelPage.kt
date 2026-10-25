package songlink.remote.ui.panel

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.components.SingletonComponent
import songlink.remote.R
import songlink.remote.data.model.PlayStatus
import songlink.remote.data.repo.SessionManager

/**
 * Hilt 入口：把单例 [SessionManager] 取给 Compose 使用。
 *
 * [SessionManager] 是 @Singleton 而不是 ViewModel，而点歌台是一个多页面共享会话的场景，
 * 所以用 EntryPoint 拿而不是 hiltViewModel()。
 */
@EntryPoint
@InstallIn(SingletonComponent::class)
interface SessionEntryPoint {
    fun sessionManager(): SessionManager
}

/** 在任意 composable 里取到全局会话。 */
@Composable
fun rememberSessionManager(): SessionManager {
    val context = LocalContext.current
    return remember(context) {
        EntryPointAccessors
            .fromApplication(context.applicationContext, SessionEntryPoint::class.java)
            .sessionManager()
    }
}

/** 点歌台三个内容页的标签。 */
private enum class PanelTab {
    SEARCH, QUEUE, LYRICS
}

/**
 * 点歌台主界面 —— 手机端的点歌中枢。
 *
 * 结构：顶部「现在播放」常驻卡（任何标签下都能看到正在放什么 + 上下首/播放暂停），
 * 下方三个标签页分别是 [SearchPane] 点歌、[QueuePane] 队列、[LyricsPane] 歌词副屏。
 *
 * 未连接时不渲染内容，而是给出「去连接」的引导 —— 与其让人对着没反应的界面点，
 * 不如明确告诉用户第一步该做什么。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PanelPage(
    modifier: Modifier = Modifier,
    onGoPair: () -> Unit,
    onOpenStream: () -> Unit = {},
    onOpenHistory: () -> Unit = {}
) {
    val session = rememberSessionManager()
    val conn by session.connState.collectAsState()
    val now by session.repo.nowPlaying.collectAsState()

    val snackbarHostState = remember { SnackbarHostState() }

    // 统一消费 Host 侧的错误 / 指令失败，三个内容页就不必各自弹提示了
    LaunchedEffect(Unit) {
        session.messages.collect { msg ->
            snackbarHostState.currentSnackbarData?.dismiss()
            snackbarHostState.showSnackbar(msg.text)
        }
    }

    // 进来先拉一次热门与历史，避免首屏空空
    LaunchedEffect(Unit) {
        session.requestTrending()
        session.requestHistory()
    }

    var tab by remember { mutableStateOf(PanelTab.SEARCH) }

    Scaffold(
        modifier = modifier,
        snackbarHost = { SnackbarHost(snackbarHostState) },
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.pick_page_title)) },
                actions = {
                    ConnectionPill(state = conn)
                    IconButton(onClick = onOpenHistory) {
                        Icon(Icons.Default.History, contentDescription = "播放历史")
                    }
                    IconButton(onClick = onOpenStream) {
                        Icon(Icons.Default.GraphicEq, contentDescription = "音频串流")
                    }
                }
            )
        },
        bottomBar = {
            TabRow(selectedTabIndex = tab.ordinal) {
                PanelTab.entries.forEach { t ->
                    Tab(
                        selected = tab == t,
                        onClick = { tab = t },
                        text = {
                            Text(
                                when (t) {
                                    PanelTab.SEARCH -> "点歌"
                                    PanelTab.QUEUE -> "队列"
                                    PanelTab.LYRICS -> "歌词"
                                }
                            )
                        },
                        icon = {
                            Icon(
                                when (t) {
                                    PanelTab.SEARCH -> Icons.Default.Search
                                    PanelTab.QUEUE -> Icons.Default.QueueMusic
                                    PanelTab.LYRICS -> Icons.Default.Lyrics
                                },
                                contentDescription = null
                            )
                        }
                    )
                }
            }
        }
    ) { padding ->
        Box(modifier = Modifier.padding(padding).fillMaxSize()) {
            when (conn) {
                is SessionManager.ConnState.Disconnected,
                is SessionManager.ConnState.Failed -> DisconnectedPrompt(
                    reason = (conn as? SessionManager.ConnState.Failed)?.reason,
                    onGoPair = onGoPair
                )

                else -> Column(modifier = Modifier.fillMaxSize()) {
                    NowPlayingCard(session = session, now = now)
                    HorizontalDivider()
                    when (tab) {
                        PanelTab.SEARCH -> SearchPane(session = session, modifier = Modifier.fillMaxSize())
                        PanelTab.QUEUE -> QueuePane(session = session, modifier = Modifier.fillMaxSize())
                        PanelTab.LYRICS -> LyricsPane(session = session, modifier = Modifier.fillMaxSize())
                    }
                }
            }
        }
    }
}

/**
 * 顶栏连接状态小药丸。
 *
 * 用颜色区分三态，让用户不点进设置页也能看出「现在这台机器连上了没有」。
 */
@Composable
private fun ConnectionPill(state: SessionManager.ConnState) {
    val (label, bg, fg) = when (state) {
        is SessionManager.ConnState.Connected ->
            Triple("已连接", MaterialTheme.colorScheme.primaryContainer, MaterialTheme.colorScheme.onPrimaryContainer)

        is SessionManager.ConnState.Connecting ->
            Triple("连接中", MaterialTheme.colorScheme.surfaceVariant, MaterialTheme.colorScheme.onSurfaceVariant)

        else ->
            Triple("未连接", MaterialTheme.colorScheme.errorContainer, MaterialTheme.colorScheme.onErrorContainer)
    }
    Box(
        modifier = Modifier
            .padding(end = 8.dp)
            .clip(CircleShape)
            .background(bg)
            .padding(horizontal = 10.dp, vertical = 4.dp)
    ) {
        Text(label, style = MaterialTheme.typography.labelSmall, color = fg)
    }
}

/** 未连接时的引导页：说清楚现在缺什么、下一步点什么。 */
@Composable
private fun DisconnectedPrompt(reason: String?, onGoPair: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize().padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Icon(
            Icons.Default.LinkOff,
            contentDescription = null,
            modifier = Modifier.size(64.dp),
            tint = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Spacer(Modifier.height(16.dp))
        Text(
            "还没连上点歌台",
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold
        )
        Spacer(Modifier.height(8.dp))
        Text(
            reason ?: "请先连接运行在电脑上的 Host，再来点歌",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Spacer(Modifier.height(24.dp))
        Button(onClick = onGoPair) {
            Icon(Icons.Default.Link, contentDescription = null)
            Spacer(Modifier.width(8.dp))
            Text("去连接")
        }
    }
}

/**
 * 「现在播放」常驻卡：封面占位 + 歌名歌手 + 进度 + 播放控制。
 *
 * 进度条**只做展示** —— Host 协议目前没有 `seek` action，适配器接口 `IPlayerAdapter`
 * 也没有 Seek 方法，所以这里刻意不给它任何可点击的暗示，避免用户点了没反应。
 */
@Composable
private fun NowPlayingCard(session: SessionManager, now: songlink.remote.data.model.NowPlaying) {
    val isPlaying = now.status == PlayStatus.PLAYING
    Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            // 封面：Host 侧封面引用走 HTTP /art/{ref}；取不到时 ArtImage 内部回落到占位图标
            ArtImage(
                artRef = now.artRef,
                baseUrl = session.hostBaseUrl,
                contentDescription = "专辑封面",
                modifier = Modifier
                    .size(64.dp)
                    .clip(MaterialTheme.shapes.medium)
            )

            Spacer(Modifier.width(12.dp))

            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = now.title.ifBlank { "暂无播放内容" },
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(Modifier.height(2.dp))
                Text(
                    text = now.artist.ifBlank { "—" },
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(Modifier.height(4.dp))
                Text(
                    text = "${fmtTime(now.pos)} / ${fmtTime(now.dur)}",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }

        Spacer(Modifier.height(10.dp))

        LinearProgressIndicator(
            progress = { if (now.dur > 0) (now.pos / now.dur).toFloat().coerceIn(0f, 1f) else 0f },
            modifier = Modifier.fillMaxWidth().height(4.dp).clip(CircleShape)
        )

        Spacer(Modifier.height(4.dp))

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = { session.prev() }) {
                Icon(Icons.Default.SkipPrevious, contentDescription = stringResource(R.string.prev_button))
            }
            FilledIconButton(
                onClick = { session.toggle() },
                modifier = Modifier.size(56.dp)
            ) {
                Icon(
                    if (isPlaying) Icons.Default.Pause else Icons.Default.PlayArrow,
                    contentDescription = stringResource(
                        if (isPlaying) R.string.pause_button else R.string.play_button
                    ),
                    modifier = Modifier.size(32.dp)
                )
            }
            IconButton(onClick = { session.next() }) {
                Icon(Icons.Default.SkipNext, contentDescription = stringResource(R.string.next_button))
            }
        }

        Spacer(Modifier.height(4.dp))

        // 音量：Host 侧 volume 指令做 0.0~1.0 的裁剪，这里直接把百分比换算过去。
        // 拖动时本地先动、松手再发，避免每个像素都打一发指令。
        var vol by remember { mutableFloatStateOf(1f) }
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = {
                val next = if (vol <= 0f) 0.6f else 0f
                vol = next
                session.setVolume(next.toDouble())
            }) {
                Icon(
                    if (vol <= 0f) Icons.Default.VolumeOff else Icons.Default.VolumeUp,
                    contentDescription = stringResource(R.string.volume_title)
                )
            }
            Slider(
                value = vol,
                onValueChange = { vol = it },
                onValueChangeFinished = { session.setVolume(vol.toDouble()) },
                valueRange = 0f..1f,
                modifier = Modifier.weight(1f)
            )
            Text(
                text = "${(vol * 100).toInt()}",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.width(28.dp)
            )
        }
    }
}

/** 秒 → `m:ss`。负数与 NaN 一律归零，避免把 `0:NaN` 显示给用户。 */
private fun fmtTime(seconds: Double): String {
    if (seconds.isNaN() || seconds.isInfinite() || seconds <= 0) return "0:00"
    val s = seconds.toInt()
    return "${s / 60}:${(s % 60).toString().padStart(2, '0')}"
}
