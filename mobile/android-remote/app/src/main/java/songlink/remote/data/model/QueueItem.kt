package songlink.remote.data.model

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * 队列条目 —— 对应 Host `event{queue-updated}` 里的一个元素。
 *
 * Host 侧 `HostServer.BuildQueueEvent()` 下发的是
 * `[{"title":..,"artist":..,"by":..}, ...]`，**没有稳定 id**，所以队列的一切操作
 * （置顶/删除/上下移）都以**下标 index** 表达，UI 必须始终以 Host 广播的顺序为准，
 * 不要在本地缓存一份顺序不同的副本。
 */
data class QueueItem(
    val title: String = "",
    val artist: String = "",
    /** 点歌人；Host 侧缺失时按"匿名"处理。 */
    val by: String = "",
) {
    companion object {
        /** 单个队列元素的安全解析：缺字段/类型不对都退回空串，不让一条脏数据炸掉整个列表。 */
        fun fromJson(o: JsonObject): QueueItem = QueueItem(
            title = o["title"]?.jsonPrimitive?.contentOrNullSafe() ?: "",
            artist = o["artist"]?.jsonPrimitive?.contentOrNullSafe() ?: "",
            by = o["by"]?.jsonPrimitive?.contentOrNullSafe() ?: "",
        )
    }
}

/**
 * 播放历史条目 —— 对应 Host `event{history-list}` 的元素 `{title, artist, playedAt}`。
 *
 * [playedAt] 在 Host 侧是 ISO-8601 字符串（`DateTime.ToString("o")`），
 * 这里保留原始字符串，格式化交给 UI 层做（不同 API level 的解析差异较大）。
 */
data class HistoryItem(
    val title: String = "",
    val artist: String = "",
    val playedAtIso: String = "",
) {
    companion object {
        fun fromJson(o: JsonObject): HistoryItem = HistoryItem(
            title = o["title"]?.jsonPrimitive?.contentOrNullSafe() ?: "",
            artist = o["artist"]?.jsonPrimitive?.contentOrNullSafe() ?: "",
            playedAtIso = o["playedAt"]?.jsonPrimitive?.contentOrNullSafe() ?: "",
        )
    }
}

/**
 * `jsonPrimitive.content` 对 JSON null 会抛异常，而 Host 的队列里 by 字段可能缺失。
 * 这里统一兜底成 null，由调用方再决定默认值。
 */
private fun kotlinx.serialization.json.JsonPrimitive.contentOrNullSafe(): String? =
    runCatching { if (this is kotlinx.serialization.json.JsonNull) null else content }.getOrNull()
