package dev.songlink.remote.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import dev.songlink.remote.data.HostClient
import dev.songlink.remote.data.Lyric
import dev.songlink.remote.data.NowPlaying
import dev.songlink.remote.data.State

/**
 * 副屏：显示当前曲目 + 按 pos 高亮滚动歌词 + 传输控制。
 * 数据来自 HostClient.frames（State/Lyric 帧）。这是"点歌台副屏歌词显示"的落地。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NowPlayingScreen(client: HostClient) {
    var now by remember { mutableStateOf(NowPlaying()) }
    var lyric by remember { mutableStateOf(Lyric()) }

    LaunchedEffect(client) {
        client.frames.collect { f ->
            when (f) {
                is State -> now = f.now
                is Lyric -> lyric = f
                else -> {}
            }
        }
    }

    val currentIdx = remember(now.pos, lyric) {
        lyric.lines.indexOfLast { it.t <= now.pos }.coerceAtLeast(0)
    }

    Scaffold(topBar = { TopAppBar(title = { Text(if (now.sourceMode == "PHONE_STREAM") "手机串流" else "点歌台") }) }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().padding(16.dp)) {
            Text(now.title.ifBlank { "未在播放" }, fontSize = 28.sp, fontWeight = FontWeight.Bold)
            Text(now.artist, fontSize = 18.sp, color = Color.Gray)
            Spacer(Modifier.height(16.dp))

            LazyColumn(Modifier.weight(1f)) {
                itemsIndexed(lyric.lines) { i, line ->
                    Text(
                        line.x,
                        fontSize = if (i == currentIdx) 22.sp else 16.sp,
                        fontWeight = if (i == currentIdx) FontWeight.Bold else FontWeight.Normal,
                        color = if (i == currentIdx) MaterialTheme.colorScheme.primary else Color.Gray,
                        modifier = Modifier.padding(vertical = 6.dp)
                    )
                }
            }

            Spacer(Modifier.height(12.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Button(onClick = { client.sendCmd("prev") }) { Text("上一首") }
                Button(onClick = { client.sendCmd(if (now.status == "playing") "pause" else "play") }) {
                    Text(if (now.status == "playing") "暂停" else "播放")
                }
                Button(onClick = { client.sendCmd("next") }) { Text("下一首") }
            }
        }
    }
}
