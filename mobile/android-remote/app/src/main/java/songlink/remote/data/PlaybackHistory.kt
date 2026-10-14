package songlink.remote.data

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.io.File
import java.io.FileOutputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

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
/// Excel 导出工具 —— 纯 JDK 手写最小 .xlsx（zip + 4 个 XML，无 POI 依赖）。
/// .xlsx 本质是 [Content_Types].xml + _rels + xl/{workbook, _rels, worksheets/sheet1}.xml 的 zip，
/// 这里只用 java.util.zip.ZipOutputStream，所以 APK 不依赖 Apache POI。
/// </summary>
class HistoryExcelExporter {
    private val dateFormat = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.getDefault())

    /// <summary>导出到本地存储</summary>
    fun exportToExcel(historyList: List<PlayedSong>, filePath: String): Boolean {
        return try {
            val file = File(filePath)
            file.parentFile?.mkdirs()

            ZipOutputStream(FileOutputStream(file)).use { zip ->
                zip.putNextEntry(ZipEntry("[Content_Types].xml"))
                zip.write(CONTENT_TYPES_XML.toByteArray(Charsets.UTF_8))

                zip.putNextEntry(ZipEntry("_rels/.rels"))
                zip.write(ROOT_RELS_XML.toByteArray(Charsets.UTF_8))

                zip.putNextEntry(ZipEntry("xl/workbook.xml"))
                zip.write(WORKBOOK_XML.toByteArray(Charsets.UTF_8))

                zip.putNextEntry(ZipEntry("xl/_rels/workbook.xml.rels"))
                zip.write(WORKBOOK_RELS_XML.toByteArray(Charsets.UTF_8))

                zip.putNextEntry(ZipEntry("xl/worksheets/sheet1.xml"))
                zip.write(buildSheetXml(historyList).toByteArray(Charsets.UTF_8))

                // 补 zip 收尾标记
                zip.closeEntry()
            }
            true
        } catch (e: Exception) {
            e.printStackTrace()
            false
        }
    }

    private fun buildSheetXml(history: List<PlayedSong>): String {
        val sb = StringBuilder(1024 + history.size * 80)
        sb.append(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" +
            """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""" +
            """<sheetData>"""
        )
        appendRow(sb, "时间", "歌曲", "艺人", "专辑", "时长(秒)")
        history.forEachIndexed { i, song ->
            appendRow(
                sb,
                dateFormat.format(Date(song.playedAt)),
                song.title,
                song.artist,
                song.album ?: "",
                song.durationSec.toString(),
                rowIndex = (i + 2)
            )
        }
        sb.append("</sheetData></worksheet>")
        return sb.toString()
    }

    private fun appendRow(
        sb: StringBuilder, vararg cells: String, rowIndex: Int = 1
    ) {
        sb.append("""<row r="$rowIndex">""")
        cells.forEachIndexed { idx, value ->
            val col = columnLetter(idx + 1)
            sb.append("""<c r="$col$rowIndex" t="inlineStr">""")
            sb.append("""<is><t xml:space="preserve">""")
            sb.append(xmlEscape(value))
            sb.append("""</t></is>""")
            sb.append("</c>")
        }
        sb.append("</row>")
    }

    private fun columnLetter(idx: Int): String {
        // 1..26 -> A..Z
        return ('A' + idx - 1).toString()
    }

    private fun xmlEscape(s: String): String {
        if (s.indexOfAny(charArrayOf('<', '>', '&', '"', '\'')) < 0) return s
        return s.replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
            .replace("\"", "&quot;")
            .replace("'", "&apos;")
    }

    /// <summary>生成预览文本</summary>
    fun generatePreviewText(count: Int): String {
        return """
            SongLink 播放历史记录导出报告

            总计歌曲数:$count
            导出时间:${dateFormat.format(Date())}

            文件格式：.xlsx (无 POI 依赖的纯 JDK 实现)
            包含列：时间 | 歌曲 | 艺人 | 专辑 | 时长
        """.trimIndent()
    }

    companion object {
        private val CONTENT_TYPES_XML = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
        """.trimIndent()

        private val ROOT_RELS_XML = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
        """.trimIndent()

        private val WORKBOOK_XML = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="History" sheetId="1" r:id="rId1"/>
              </sheets>
            </workbook>
        """.trimIndent()

        private val WORKBOOK_RELS_XML = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
        """.trimIndent()
    }
}

