package songlink.remote.ui.panel

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import songlink.remote.R
import songlink.remote.data.model.PlayStatus
import songlink.remote.data.repo.SessionManager

/** 单次校准步长（毫秒）。 */
private const val CALIBRATE_STEP_MS = 500

/**
 * 歌词副屏。
 *
 * **时间基准**：`LyricLine.t` 与 `NowPlaying.pos` 都是**秒**，直接比较即可，不要乘 1000。
 * 全局偏移由 Host 在推送歌词时通过 [songlink.remote.data.model.Lyric.offsetMs] 处理，
 * 所以本页按未偏移的原始秒数渲染，只用 offsetMs 做"校准"的初值与回显。
 *
 * 不包 Scaffold：错误提示由父级统一消费 `session.messages`。
 *
 * @param session 会话管理器（Hilt 单例）
 * @param modifier 布局修饰符，由父级决定内边距等
 * @param autoRequestLyric 切歌后是否自动向 Host 拉一次歌词；关掉后由外部自行调用 `requestLyric`
 */
@Composable
fun LyricsPane(
    session: SessionManager,
    modifier: Modifier = Modifier,
    autoRequestLyric: Boolean = true,
) {
    val lyric by session.repo.lyric.collectAsState()
    val nowPlaying by session.repo.nowPlaying.collectAsState()
    val connState by session.connState.collectAsState()

    val listState = rememberLazyListState()

    // 本地累计偏移：换歌（trackId 变）时以 Host 下发的 offsetMs 重新起算。
    var offsetMs by remember(lyric?.trackId) { mutableIntStateOf(lyric?.offsetMs ?: 0) }

    val lines = lyric?.lines.orEmpty()
    val connected = connState is SessionManager.ConnState.Connected

    // 切歌后自动拉歌词。Host 的 trackId 未必就是歌名，所以按 title+artist 记住"已请求过哪首"，
    // 避免每次 state 广播（pos 在变）都重发一次 getLyric。
    var requestedTrack by remember { mutableStateOf<String?>(null) }
    val trackKey = nowPlaying.title + "|" + nowPlaying.artist
    LaunchedEffect(autoRequestLyric, connected, trackKey) {
        if (autoRequestLyric && connected &&
            nowPlaying.title.isNotBlank() &&
            requestedTrack != trackKey
        ) {
            requestedTrack = trackKey
            session.requestLyric(nowPlaying.title, nowPlaying.artist)
        }
    }

    // 当前行 = 最后一个 t <= pos 的行；整首还没开始（pos < 第一行时间）时停在第一行。
    val currentIndex = remember(lines, nowPlaying.pos) {
        if (lines.isEmpty()) 0 else lines.indexOfLast { it.t <= nowPlaying.pos }.coerceAtLeast(0)
    }

    // 当前行变化时自动滚到可见。
    LaunchedEffect(currentIndex, lines.size) {
        if (lines.isNotEmpty() && currentIndex in lines.indices) {
            listState.animateScrollToItem(currentIndex)
        }
    }

    // 没在播时把歌词淡化，"副屏"感弱一点，也避免静止画面误导成正在唱。
    val active = nowPlaying.status == PlayStatus.PLAYING || nowPlaying.status == PlayStatus.PAUSED

    Column(modifier = modifier.fillMaxSize()) {
        if (lines.isEmpty()) {
            EmptyBlock(
                text = stringResource(R.string.no_lyrics),
                hint = stringResource(R.string.lyrics_page_title),
                modifier = Modifier.weight(1f),
            )
        } else {
            LazyColumn(
                state = listState,
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth(),
                contentPadding = PaddingValues(horizontal = 20.dp, vertical = 24.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                items(count = lines.size, key = { index -> lines[index].t.toString() + "#" + index }) { index ->
                    val isCurrent = index == currentIndex
                    Text(
                        text = lines[index].x.ifBlank { " " },
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = if (isCurrent) FontWeight.Bold else FontWeight.Normal,
                        fontSize = if (isCurrent) 22.sp else 17.sp,
                        textAlign = TextAlign.Center,
                        color = when {
                            !active -> MaterialTheme.colorScheme.onSurfaceVariant
                            isCurrent -> MaterialTheme.colorScheme.primary
                            else -> MaterialTheme.colorScheme.onSurfaceVariant
                        },
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            }
        }

        HorizontalDivider()

        // ---- 偏移校准 ----
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = stringResource(R.string.offset_ms, offsetMs),
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.weight(1f),
            )
            val calibrateEnabled = connected && lines.isNotEmpty() && nowPlaying.title.isNotBlank()
            Button(
                onClick = {
                    offsetMs += CALIBRATE_STEP_MS
                    session.calibrate(offsetMs, nowPlaying.title, nowPlaying.artist)
                },
                enabled = calibrateEnabled,
            ) {
                Text("歌词快 +500ms")
            }
            Spacer(modifier = Modifier.width(8.dp))
            Button(
                onClick = {
                    offsetMs -= CALIBRATE_STEP_MS
                    session.calibrate(offsetMs, nowPlaying.title, nowPlaying.artist)
                },
                enabled = calibrateEnabled,
            ) {
                Text("歌词慢 -500ms")
            }
        }
    }
}
