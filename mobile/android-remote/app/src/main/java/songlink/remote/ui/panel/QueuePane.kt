package songlink.remote.ui.panel

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.DeleteSweep
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material.icons.filled.VerticalAlignTop
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import songlink.remote.R
import songlink.remote.data.model.QueueItem
import songlink.remote.data.repo.SessionManager

/**
 * 队列页：查看 / 排序 / 置顶 / 删除。
 *
 * **索引即真值**：Host 下发的队列元素没有稳定 id，所有队列操作都以下标表达。
 * 因此本页直接对 `session.queue` 的当前下标发指令，**不在本地维护第二份顺序副本** ——
 * 顺序一律以 Host 广播的 `queue-updated` 为准，本地排序只会在广播回来前短暂不一致。
 *
 * 不包 Scaffold：错误提示由父级统一消费 `session.messages`。
 *
 * @param session 会话管理器（Hilt 单例）
 * @param modifier 布局修饰符，由父级决定内边距等
 */
@Composable
fun QueuePane(
    session: SessionManager,
    modifier: Modifier = Modifier,
) {
    val queue by session.queue.collectAsState()
    val connState by session.connState.collectAsState()
    val nowPlaying by session.repo.nowPlaying.collectAsState()

    var showClearDialog by remember { mutableStateOf(false) }

    val connected = connState is SessionManager.ConnState.Connected
    val topLabel = stringResource(R.string.queue_top_button)

    Column(modifier = modifier.fillMaxSize()) {
        // ---- 标题行：数量 + 打乱 / 清空 ----
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(start = 16.dp, end = 4.dp, top = 8.dp, bottom = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = stringResource(R.string.queue_count, queue.size),
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.Bold,
                modifier = Modifier.weight(1f),
            )
            IconButton(
                onClick = { session.shuffleQueue() },
                enabled = connected && queue.isNotEmpty(),
            ) {
                Icon(Icons.Default.Shuffle, contentDescription = "随机打乱")
            }
            IconButton(
                onClick = { showClearDialog = true },
                enabled = connected && queue.isNotEmpty(),
            ) {
                Icon(
                    imageVector = Icons.Default.DeleteSweep,
                    contentDescription = stringResource(R.string.queue_clear_button),
                    tint = MaterialTheme.colorScheme.error,
                )
            }
        }

        HorizontalDivider()

        // ---- 队列列表 ----
        if (queue.isEmpty()) {
            EmptyBlock(
                text = stringResource(R.string.queue_empty),
                hint = stringResource(R.string.hint_enter_song_name),
                modifier = Modifier.weight(1f),
            )
        } else {
            LazyColumn(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth(),
                contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                itemsIndexed(queue, key = { index, _ -> index }) { index, item ->
                    QueueRow(
                        index = index,
                        total = queue.size,
                        item = item,
                        topLabel = topLabel,
                        // title + artist 都对得上才算"正在播"，任一为空都当不匹配，避免误高亮。
                        playing = nowPlaying.title.isNotBlank() &&
                            nowPlaying.title == item.title &&
                            nowPlaying.artist == item.artist,
                        onMoveUp = { session.moveInQueue(index, index - 1) },
                        onMoveDown = { session.moveInQueue(index, index + 1) },
                        onTop = { session.topInQueue(index) },
                        onRemove = { session.removeFromQueue(index) },
                    )
                }
            }
        }
    }

    // ---- 清空二次确认：不可撤销，必须确认 ----
    if (showClearDialog) {
        AlertDialog(
            onDismissRequest = { showClearDialog = false },
            title = { Text(stringResource(R.string.queue_clear_button)) },
            text = { Text("将清空队列中的 ${queue.size} 首歌曲，该操作无法撤销。") },
            confirmButton = {
                TextButton(onClick = {
                    showClearDialog = false
                    session.clearQueue()
                }) {
                    Text("确认清空")
                }
            },
            dismissButton = {
                TextButton(onClick = { showClearDialog = false }) { Text("取消") }
            },
        )
    }
}

/**
 * 队列中的一行。
 *
 * @param index 当前下标，**直接作为 Host 协议里的 index 使用**
 * @param total 队列长度，用于把首尾的上移/下移按钮置灰
 * @param playing 正在播放则整行高亮
 */
@Composable
private fun QueueRow(
    index: Int,
    total: Int,
    item: QueueItem,
    topLabel: String,
    playing: Boolean,
    onMoveUp: () -> Unit,
    onMoveDown: () -> Unit,
    onTop: () -> Unit,
    onRemove: () -> Unit,
) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = if (playing) {
            CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer)
        } else {
            CardDefaults.cardColors()
        },
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(start = 4.dp, end = 4.dp, top = 6.dp, bottom = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = (index + 1).toString(),
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(horizontal = 8.dp),
            )
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = item.title,
                    style = MaterialTheme.typography.titleSmall,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        text = item.artist.ifBlank { "未知歌手" },
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        text = " · " + (if (item.by.isBlank()) topLabel else "点歌人：" + item.by),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
            IconButton(onClick = onMoveUp, enabled = index > 0) {
                Icon(Icons.Default.KeyboardArrowUp, contentDescription = "上移")
            }
            IconButton(onClick = onMoveDown, enabled = index < total - 1) {
                Icon(Icons.Default.KeyboardArrowDown, contentDescription = "下移")
            }
            IconButton(onClick = onTop, enabled = index != 0) {
                Icon(Icons.Default.VerticalAlignTop, contentDescription = topLabel)
            }
            IconButton(onClick = onRemove) {
                Icon(
                    imageVector = Icons.Default.DeleteSweep,
                    contentDescription = "删除",
                    tint = MaterialTheme.colorScheme.error,
                )
            }
        }
    }
}
