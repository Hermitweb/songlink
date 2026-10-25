package songlink.remote.ui.history

import android.os.Environment
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.ClearAll
import androidx.compose.material.icons.filled.Download
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import songlink.remote.data.PlaybackHistoryManager
import songlink.remote.data.HistoryExcelExporter
import java.io.File
import java.util.Date
import java.util.Locale

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HistoryPage(
    onBack: () -> Unit
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }
    val historyList by PlaybackHistoryManager.historyList.collectAsState()
    var showExportDialog by remember { mutableStateOf(false) }
var showClearDialog by remember { mutableStateOf(false) }
    var exporting by remember { mutableStateOf(false) }

    /**
     * 真正执行导出：先把 .xlsx 写到 app 专属目录，**只有写入成功才清空本地记录**。
     * 写入失败必须原样保留数据并提示用户，绝不能出现"没拿到文件却把数据删了"。
     */
    fun performExport() {
        val snapshot = historyList
        if (snapshot.isEmpty() || exporting) return
        exporting = true
        scope.launch {
            // 写文件是 IO 操作，放到 Dispatchers.IO；应用专属目录 targetSdk 34 下无需存储权限
            val (ok, fileName) = withContext(Dispatchers.IO) {
                val dir = context.getExternalFilesDir(Environment.DIRECTORY_DOCUMENTS)
                    ?: File(context.filesDir, "documents")
                val file = File(dir, "songlink_history_${timestampTag()}.xlsx")
                val written = runCatching {
                    HistoryExcelExporter().exportToExcel(snapshot, file.absolutePath) && file.exists()
                }.getOrDefault(false)
                written to file.name
            }
            exporting = false
            if (ok) {
                // 导出成功，才清空本地记录
                PlaybackHistoryManager.clearAll()
                snackbarHostState.showSnackbar("已导出 ${snapshot.size} 条记录到 $fileName")
            } else {
                snackbarHostState.showSnackbar("导出失败，记录已保留，请稍后重试")
            }
        }
    }

    Scaffold(
        snackbarHost = { SnackbarHost(snackbarHostState) },
        topBar = {
            TopAppBar(
                title = { Text("播放历史") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.Default.ArrowBack, contentDescription = "返回")
                    }
                },
                actions = {
                    IconButton(onClick = { showExportDialog = true }) {
                        Icon(Icons.Default.Download, contentDescription = "导出 Excel")
                    }

                    IconButton(
                        onClick = { if (historyList.isNotEmpty()) showClearDialog = true },
                        enabled = historyList.isNotEmpty()
                    ) {
                        Icon(Icons.Default.ClearAll, contentDescription = "清空", tint = MaterialTheme.colorScheme.error)
                    }
                }
            )
        }
    ) { paddingValues ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(paddingValues),
            contentAlignment = Alignment.Center
        ) {
            if (historyList.isEmpty()) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(
                        text = "暂无播放记录",
                        style = MaterialTheme.typography.bodyLarge,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(
                        text = "播放歌曲后会自动记录到这里",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            } else {
                LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    items(historyList.reversed(), key = { it.playedAt }) { song ->
                        SongHistoryItem(song)
                    }
                }
            }
        }
    }
    
    // 导出对话框
    if (showExportDialog) {
        ExportConfirmationDialog(
            count = historyList.size,
            onConfirm = {
                showExportDialog = false
                performExport()
            },
            onDismiss = { showExportDialog = false }
        )
    }

    // 清空确认对话框 —— 清空是不可撤销的丢数据操作，必须二次确认（和"导出成功后清空"区分开）
    if (showClearDialog) {
        AlertDialog(
            onDismissRequest = { showClearDialog = false },
            title = { Text("清空播放历史") },
            text = {
                Text(
                    "将删除全部 ${historyList.size} 条本地记录，且无法撤销。\n" +
                        "如果只是想导出备份，请先使用右上角的导出按钮。"
                )
            },
            confirmButton = {
                Button(
                    onClick = {
                        // 先取条数：clearAll 之后再读 historyList.size 会拿到 0
                        val n = historyList.size
                        PlaybackHistoryManager.clearAll()
                        showClearDialog = false
                        scope.launch { snackbarHostState.showSnackbar("已清空 $n 条记录") }
                    }
                ) { Text("确认清空") }
            },
            dismissButton = {
                TextButton(onClick = { showClearDialog = false }) { Text("取消") }
            }
        )
    }
}

/** 文件名时间戳，如 20261018_225300 */
private fun timestampTag(): String =
    java.text.SimpleDateFormat("yyyyMMdd_HHmmss", Locale.getDefault()).format(Date())

@Composable
fun SongHistoryItem(song: songlink.remote.data.PlayedSong) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clickable { /* TODO: 点击查看详情 */ }
    ) {
        Column(modifier = Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = song.title,
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold
                )
            }
            
            Spacer(modifier = Modifier.height(4.dp))
            
            Text(
                text = song.artist,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            
            Spacer(modifier = Modifier.height(4.dp))
            
            Text(
                text = formatTimestamp(song.playedAt),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

private fun formatTimestamp(ms: Long): String {
    val sdf = java.text.SimpleDateFormat("yyyy/MM/dd HH:mm", java.util.Locale.getDefault())
    return sdf.format(java.util.Date(ms))
}

@Composable
fun ExportConfirmationDialog(
    count: Int,
    onConfirm: () -> Unit,
    onDismiss: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("导出播放历史") },
        text = {
            Column {
                Text("即将导出 $count 条播放记录到 Excel 文件")
                Spacer(modifier = Modifier.height(8.dp))
                Text("导出成功后才会清空本地记录，导出失败记录会完整保留", style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = {
            Button(onClick = onConfirm) {
                Text("确认导出")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("取消")
            }
        }
    )
}
