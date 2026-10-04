package dev.songlink.remote.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject

/**
 * SongLink 协议帧的 Kotlin 镜像（与 ../docs/协议契约.md、songlink-frame.schema.json 一一对应）。
 * 用 kotlinx.serialization 多态，classDiscriminator = "type"（在 HostClient 的 Json 配置里设）。
 */
@Serializable
sealed class Frame {
    @SerialName("v") val v: Int = 1
    @SerialName("seq") var seq: Long = 0
    @SerialName("id") var id: String? = null
    @SerialName("ts") var ts: Double? = null
    @SerialName("sig") var sig: String? = null
}

@Serializable
@SerialName("hello")
data class Hello(
    @SerialName("role") val role: String = "remote",
    @SerialName("device") val device: String? = null,
    @SerialName("os") val os: String? = null,
    @SerialName("caps") val caps: List<String> = emptyList(),
    @SerialName("pin") val pin: String? = null,
    @SerialName("token") val token: String? = null,
    @SerialName("lastSeq") val lastSeq: Long? = null,
) : Frame()

@Serializable
@SerialName("authResult")
data class AuthResult(
    @SerialName("ref") val ref: String = "",
    @SerialName("ok") val ok: Boolean = false,
    @SerialName("token") val token: String? = null,
    @SerialName("code") val code: String? = null,
) : Frame()

@Serializable
data class SearchResultItem(
    @SerialName("title") val title: String = "",
    @SerialName("artist") val artist: String = "",
    @SerialName("album") val album: String? = null,
    @SerialName("dur") val dur: Double? = null,
)

@Serializable
@SerialName("searchResults")
data class SearchResults(
    @SerialName("ref") val ref: String = "",
    @SerialName("query") val query: String = "",
    @SerialName("results") val results: List<SearchResultItem> = emptyList(),
) : Frame()

@Serializable
@SerialName("cmd")
data class Cmd(
    @SerialName("action") val action: String,
    @SerialName("args") val args: JsonObject? = null,
) : Frame()

@Serializable
@SerialName("ack")
data class Ack(
    @SerialName("ref") val ref: String = "",
    @SerialName("ok") val ok: Boolean = false,
) : Frame()

@Serializable
data class NowPlaying(
    @SerialName("title") val title: String = "",
    @SerialName("artist") val artist: String = "",
    @SerialName("album") val album: String? = null,
    @SerialName("pos") val pos: Double = 0.0,
    @SerialName("dur") val dur: Double = 0.0,
    @SerialName("status") val status: String = "stopped",
    @SerialName("artRef") val artRef: String? = null,
    @SerialName("sourceMode") val sourceMode: String = "PC_LOCAL",
)

@Serializable
@SerialName("state")
data class State(
    @SerialName("now") val now: NowPlaying = NowPlaying(),
) : Frame()

@Serializable
data class LyricLine(
    @SerialName("t") val t: Double = 0.0,
    @SerialName("x") val x: String = "",
)

@Serializable
@SerialName("lyric")
data class Lyric(
    @SerialName("trackId") val trackId: String = "",
    @SerialName("offsetMs") val offsetMs: Int = 0,
    @SerialName("lines") val lines: List<LyricLine> = emptyList(),
) : Frame()

@Serializable
@SerialName("srcMeta")
data class SrcMeta(
    @SerialName("title") val title: String = "",
    @SerialName("artist") val artist: String = "",
    @SerialName("pos") val pos: Double = 0.0,
    @SerialName("dur") val dur: Double = 0.0,
    @SerialName("status") val status: String = "playing",
    @SerialName("artRef") val artRef: String? = null,
) : Frame()

@Serializable
@SerialName("ping")
class Ping : Frame()

@Serializable
@SerialName("audio:start")
data class AudioStart(
    @SerialName("codec") val codec: String = "opus",
    @SerialName("rate") val rate: Int = 48000,
    @SerialName("ch") val ch: Int = 2,
    @SerialName("frameMs") val frameMs: Int = 20,
    @SerialName("port") val port: Int = 51000,
) : Frame()

@Serializable
@SerialName("audio:stop")
class AudioStop : Frame()
