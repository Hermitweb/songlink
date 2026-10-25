package songlink.remote.ui.panel

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import songlink.remote.R
import songlink.remote.data.model.SearchResultItem
import songlink.remote.data.repo.SessionManager
import java.util.Locale
import kotlin.math.floor

/** 点歌人昵称：手机端发起的点歌统一用它，[SessionManager.addToQueue] 内部对空白另有兜底。 */
private const val REMOTE_NICKNAME = "手机"

/**
 * 热门推荐的"伪 query"。
 *
 * Host 对热门也回 `searchResults` 帧，只是 `query == "trending"`；
 * [SessionManager] 靠这个值把热门分流到 `trending` 而不是 `searchResults`，
 * 所以 UI 判定"是否处于搜索结果态"时必须排除它。
 */
private const val QUERY_TRENDING = "trending"

/** 未搜索且热门也还没拉到数据时的引导文案。 */
private const val SEARCH_GUIDE = "搜索歌名或歌手开始点歌，也可以直接点下面的热门"

/**
 * 点歌页：搜索 + 热门推荐。
 *
 * 数据全部来自 [SessionManager] 的 StateFlow，本页**不包 Scaffold** ——
 * Snackbar 由父级页面统一消费 `session.messages`，本页只负责内容与空态/错误态文案。
 *
 * @param session 会话管理器（Hilt 单例）
 * @param modifier 布局修饰符，由父级决定内边距等
 */
@Composable
fun SearchPane(
    session: SessionManager,
    modifier: Modifier = Modifier,
) {
    var keyword by remember { mutableStateOf("") }

    val connState by session.connState.collectAsState()
    val trending by session.trending.collectAsState()
    val results by session.searchResults.collectAsState()

    // 首次进入拉一次热门；热门 query 固定为 "trending"，由 SessionManager 分流到 trending 列表。
    LaunchedEffect(Unit) {
        session.requestTrending()
    }

    val connected = connState is SessionManager.ConnState.Connected
    // 只有真正由用户关键词发起的搜索才算"搜索结果态"，热门单独走下面的 chip 行。
    val searchResults = results?.takeIf { it.query != QUERY_TRENDING }
    val searched = searchResults != null

    Column(modifier = modifier.fillMaxSize()) {
        // ---- 搜索输入区 ----
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            OutlinedTextField(
                value = keyword,
                onValueChange = { keyword = it },
                modifier = Modifier.weight(1f),
                singleLine = true,
                label = { Text(stringResource(R.string.search_hint)) },
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = {
                    val kw = keyword.trim()
                    if (kw.isNotEmpty()) session.search(kw)
                }),
            )
            Spacer(modifier = Modifier.width(8.dp))
            Button(
                onClick = {
                    val kw = keyword.trim()
                    if (kw.isNotEmpty()) session.search(kw)
                },
                enabled = connected,
            ) {
                Text(stringResource(R.string.search_button))
            }
        }

        HorizontalDivider()

        // ---- 搜索结果 / 热门推荐 ----
        val listModifier = Modifier
            .weight(1f)
            .fillMaxWidth()

        when {
            // 搜索过但一条没命中
            searched && searchResults!!.results.isEmpty() -> EmptyBlock(
                text = stringResource(R.string.no_results),
                hint = stringResource(R.string.hint_enter_song_name),
                modifier = listModifier,
            )

            // 搜索结果列表
            searched -> LazyColumn(
                modifier = listModifier,
                contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                items(searchResults!!.results, key = { it.title + "|" + it.artist }) { item ->
                    ResultRow(
                        item = item,
                        connected = connected,
                        onAdd = { session.addToQueue(item.title, item.artist, REMOTE_NICKNAME) },
                        onPlay = { session.pick(item.title, item.artist) },
                    )
                }
            }

            // 还没搜索，热门也空 —— 引导用户先搜索
            trending.isEmpty() -> EmptyBlock(
                text = stringResource(R.string.hint_enter_song_name),
                hint = SEARCH_GUIDE,
                modifier = listModifier,
            )

            // 热门推荐 chip 行
            else -> LazyColumn(
                modifier = listModifier,
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                item(key = "trending-header") {
                    Column(modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 12.dp)) {
                        Text(
                            text = "热门推荐",
                            style = MaterialTheme.typography.titleSmall,
                            fontWeight = FontWeight.Bold,
                        )
                        Text(
                            text = "点一下直接加入队列",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                item(key = "trending-row") {
                    LazyRow(
                        contentPadding = PaddingValues(horizontal = 16.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        items(trending, key = { it.title + "|" + it.artist }) { item ->
                            FilterChip(
                                selected = false,
                                enabled = connected,
                                onClick = { session.addToQueue(item.title, item.artist, REMOTE_NICKNAME) },
                                label = {
                                    Text(
                                        text = if (item.artist.isBlank()) item.title
                                        else item.title + " · " + item.artist,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                    )
                                },
                            )
                        }
                    }
                }
            }
        }
    }
}

/**
 * 一条搜索结果：歌名 / 歌手 / 时长 + 加入队列 + 立即播放。
 *
 * @param onAdd 加入队列尾部
 * @param onPlay 立即播放（会打断当前播放）
 */
@Composable
private fun ResultRow(
    item: SearchResultItem,
    connected: Boolean,
    onAdd: () -> Unit,
    onPlay: () -> Unit,
) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(start = 12.dp, end = 4.dp, top = 8.dp, bottom = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = item.title,
                    style = MaterialTheme.typography.titleSmall,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    text = item.artist.ifBlank { "未知歌手" },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                // 来源标记：多源搜索后让用户一眼看出点下去会被送到哪
                // （本地库 / 网易云 / QQ音乐 / 酷狗），与 Web 面板保持一致
                item.source?.takeIf { it.isNotBlank() && it != "local" }?.let { src ->
                    Text(
                        text = sourceLabel(src),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(top = 2.dp),
                    )
                }
            }
            Text(
                text = formatDuration(item.dur),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            IconButton(onClick = onAdd, enabled = connected) {
                Icon(Icons.Default.Add, contentDescription = "加入队列")
            }
            IconButton(onClick = onPlay, enabled = connected) {
                Icon(Icons.Default.PlayArrow, contentDescription = "立即播放")
            }
        }
    }
}

/**
 * 搜索来源的展示名。与 Web 面板 `SOURCE_LABEL` 保持一致。
 */
internal fun sourceLabel(source: String): String = when (source) {
    "netease" -> "网易云"
    "qq" -> "QQ音乐"
    "kugou" -> "酷狗"
    "local" -> "本地"
    else -> source
}

/**
 * 通用空态块：主文案 + 一行引导。
 *
 * 点歌页的"无结果 / 没搜过"两种空态共用，避免留下空白页。
 */
@Composable
internal fun EmptyBlock(
    text: String,
    hint: String? = null,
    modifier: Modifier = Modifier,
) {
    Box(modifier = modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(
            modifier = Modifier.padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(
                text = text,
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (!hint.isNullOrBlank()) {
                Spacer(modifier = Modifier.height(8.dp))
                Text(
                    text = hint,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/**
 * 秒 → `m:ss`。
 *
 * @param dur 秒数；null（Host 未下发时长）显示 `--:--`
 */
internal fun formatDuration(dur: Double?): String {
    if (dur == null || dur <= 0.0) return "--:--"
    val total = floor(dur).toLong()
    return String.format(Locale.getDefault(), "%d:%02d", total / 60, total % 60)
}
