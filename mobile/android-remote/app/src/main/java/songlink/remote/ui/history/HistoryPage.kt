package songlink.remote.ui.history

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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import songlink.remote.data.PlaybackHistoryManager
import songlink.remote.data.HistoryExcelExporter

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HistoryPage(
    onBack: () -> Unit
) {
    val historyList by PlaybackHistoryManager.historyList.collectAsState()
    var showExportDialog by remember { mutableStateOf(false) }
    
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("📊 播放历史") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.Default.ArrowBack, contentDescription = "返回")
                    }
                },
                actions = {
                    IconButton(onClick = { showExportDialog = true }) {
                        Icon(Icons.Default.Download, contentDescription = "导出 Excel")
                    }
                    
                    IconButton(onClick = { 
                        if (historyList.isNotEmpty()) {
                            PlaybackHistoryManager.clearAll()
                        }
                    }) {
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
                PlaybackHistoryManager.clearAll()
                showExportDialog = false
            },
            onDismiss = { showExportDialog = false }
        )
    }
}

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
                Text("导出后将自动清空本地记录", style = MaterialTheme.typography.bodySmall)
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
