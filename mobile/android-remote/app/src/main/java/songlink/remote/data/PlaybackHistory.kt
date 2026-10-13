package songlink.remote.data

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/// <summary>
/// 播放历史记录（M2.5 P2 增强）
/// </summary>
data class PlayedSong(
    val title: String,
    val artist: String,
    val album: String?,
    val playedAt: Long = System.currentTimeMillis(),
    val durationSec: Int = 180 // 默认时长
)

object PlaybackHistoryManager {
    private val _historyList = MutableStateFlow<List<PlayedSong>>(emptyList())
    val historyList: StateFlow<List<PlayedSong>> get() = _historyList
    
    fun addSong(title: String, artist: String, album: String? = null, durationSec: Int = 180) {
        val song = PlayedSong(
            title = title,
            artist = artist,
            album = album,
            durationSec = durationSec
        )
        
        // 保持最多 100 条记录
        val current = _historyList.value.toMutableList()
        current.add(song)
        if (current.size > 100) {
            current.removeAt(0)
        }
        
        _historyList.value = current
    }
    
    fun clearAll() {
        _historyList.value = emptyList()
    }
    
    fun getTotalSongsCount(): Int = _historyList.value.size
}

/// <summary>
/// Excel 导出工具
/// </summary>
class HistoryExcelExporter {
    private val dateFormat = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.getDefault())
    
    /// <summary>导出到本地存储</summary>
    fun exportToExcel(historyList: List<PlayedSong>, filePath: String): Boolean {
        return try {
            // Apache POI 实现 Excel 导出逻辑
            // TODO: 实现实际的文件写入操作
            println("Exporting ${historyList.size} songs to $filePath")
            
            // 模拟导出成功
            true
        } catch (e: Exception) {
            e.printStackTrace()
            false
        }
    }
    
    /// <summary>生成预览文本</summary>
    fun generatePreviewText(count: Int): String {
        return """
            📊 SongLink 播放历史记录导出报告
            
            总计歌曲数：$count
            导出时间：${dateFormat.format(Date())}
            
            文件格式：.xlsx
            包含列：时间 | 歌曲 | 艺人 | 专辑 | 时长
        """.trimIndent()
    }
}
