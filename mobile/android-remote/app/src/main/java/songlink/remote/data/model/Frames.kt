package songlink.remote.data.model

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonClassDiscriminator
import kotlinx.serialization.json.JsonObject

/**
 * SongLink 通信帧模型 —— 逐字段对齐 `src/SongLink.Protocol/Frames.cs`。
 *
 * 权威来源：
 * - `src/SongLink.Protocol/Frames.cs`（C# 端的 Frame 类型层级 + FrameCodec）
 * - `docs/schema/songlink-frame.schema.json`（每种 type 的必填字段与枚举）
 * - `docs/协议契约.md` §2 信封 / §3 帧类型
 *
 * 序列化契约要点（与 C# System.Text.Json 保持一致，否则两端签名对不上）：
 * - 判别符固定为 `"type"`，与 C# `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]` 一致；
 * - null 字段不写出（C# `DefaultIgnoreCondition = WhenWritingNull`，靠 FrameCodec 的
 *   `explicitNulls = false` 实现）；
 * - 属性顺序 = `type` → 派生类字段（声明序）→ 基类字段 `v/seq/id/ts/sig`
 *   （C# System.Text.Json 多态序列化的实际写法，kotlinx 默认把基类排前面，故由 FrameCodec 重排）。
 *
 * 各数据类的构造参数顺序刻意与 C# 属性声明顺序一致：派生字段在前、信封字段在后，
 * 这样重排只依赖「哪些是信封字段」，不依赖具体类型。
 */

/** 判别符字段名，与 C# `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]` 对齐。 */
const val TYPE_KEY = "type"

/** 协议主版本，契约 §2 固定为 1。 */
const val PROTOCOL_VERSION = 1

/** 基类信封字段，序列化时统一排到对象末尾（顺序即本列表顺序）。 */
val ENVELOPE_FIELDS = listOf("v", "seq", "id", "ts", "sig")

/** [NowPlaying.status] 枚举（契约 §3.4 / schema）。 */
object PlayStatus {
    const val PLAYING = "playing"
    const val PAUSED = "paused"
    const val STOPPED = "stopped"
    const val BUFFERING = "buffering"
}

/** [NowPlaying.sourceMode] 枚举（契约 §3.4）。 */
object SourceMode {
    const val PC_LOCAL = "PC_LOCAL"
    const val PHONE_STREAM = "PHONE_STREAM"
}

/**
 * 帧信封基类。14 种具体帧见下方；C# 侧同名抽象类见 `Frames.cs:26`。
 *
 * 注意 C# 的 `ts` 是 `double?`，这里保持 `Double?`，不要改成 Long。
 */
@OptIn(ExperimentalSerializationApi::class)
@Serializable
@JsonClassDiscriminator(TYPE_KEY)
sealed class Frame {
    /** 协议主版本，固定 1。 */
    abstract val v: Int

    /** 发送方单调递增序号，用于断线补发与去重（契约 §2/§7）。 */
    abstract val seq: Long

    /** 请求 ID，供 ack / error / authResult 回填 `ref`。 */
    abstract val id: String?

    /** 发送方 Unix 毫秒时间戳（可选）。 */
    abstract val ts: Double?

    /** HMAC-SHA256(token, canonical(本帧去 sig)) 大写十六进制（契约 §6）。 */
    abstract val sig: String?
}

/** `hello`：握手 / 能力协商 / 鉴权（契约 §3.1）。 */
@Serializable
data class Hello(
    val role: String = "remote",
    val device: String? = null,
    val os: String? = null,
    val caps: List<String> = emptyList(),
    /** 首次配对：Host 显示的 6 位 PIN。 */
    val pin: String? = null,
    /** 重连：携带上次下发的令牌。 */
    val token: String? = null,
    /** 重连补发：客户端已见的最大 seq（契约 §7）。 */
    val lastSeq: Long? = null,
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** `authResult`：鉴权回执，Host→Remote（契约 §3.1b）。 */
@Serializable
data class AuthResult(
    val ref: String = "",
    val ok: Boolean = false,
    val token: String? = null,
    /** 开启签名模式时下发的 Host 出站帧验签密钥。 */
    val hostKey: String? = null,
    /** 失败原因码，如 `AUTH_FAILED`。 */
    val code: String? = null,
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** `searchResults` 的一条命中（契约 §3.2b）。不是帧。 */
@Serializable
data class SearchResultItem(
    val title: String = "",
    val artist: String = "",
    val album: String? = null,
    /** 时长（秒）。 */
    val dur: Double? = null,
    /**
     * 结果来自哪个搜索源："local" / "netease" / "qq" / "kugou"。
     * 与 C# 侧 `SearchResultItem.Source` 对应 —— 面板据此显示来源，
     * 点歌时 Host 侧也据此路由到对应播放策略。
     *
     * 刻意可空：宿主按"null 字段不写出"序列化，没赋值时 JSON 里**不会**出现这个键，
     * 保证与 C# 端产出的字节完全一致（非空默认值会把空串也写出去，破坏契约）。
     */
    val source: String? = null,
    /** 该平台内部的歌曲 ID（网易云 id / QQ songid / 酷狗 hash）。 */
    val sourceId: String? = null,
)

/** `searchResults`：搜索结果，Host→Remote（契约 §3.2b）。 */
@Serializable
data class SearchResults(
    val ref: String = "",
    val query: String = "",
    val results: List<SearchResultItem> = emptyList(),
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** `cmd`：指令，Remote→Host（契约 §3.2）。[args] 的形状由 action 决定，故用原始 JSON 对象。 */
@Serializable
data class Cmd(
    val action: String = "",
    val args: JsonObject? = null,
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** `ack`：回执，Host→Remote（契约 §3.3）。 */
@Serializable
data class Ack(
    val ref: String = "",
    val ok: Boolean = false,
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** 当前播放真值。嵌入 `state.now`，本身不是帧（`Frames.cs:82`）。 */
@Serializable
data class NowPlaying(
    val title: String = "",
    val artist: String = "",
    val album: String? = null,
    /** 进度（秒）。 */
    val pos: Double = 0.0,
    /** 总时长（秒）。 */
    val dur: Double = 0.0,
    val status: String = PlayStatus.STOPPED,
    /** 封面引用，配合 `art` 帧经 HTTP `GET /art/{ref}` 拉取。 */
    val artRef: String? = null,
    val sourceMode: String = SourceMode.PC_LOCAL,
)

/** `state`：当前播放真值广播，Host→Remote（契约 §3.4）。 */
@Serializable
data class State(
    val now: NowPlaying = NowPlaying(),
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** 歌词单行：`t` 秒、`x` 文本。不是帧。 */
@Serializable
data class LyricLine(
    val t: Double = 0.0,
    val x: String = "",
)

/** `lyric`：整首歌词，换曲才推（契约 §3.5）。 */
@Serializable
data class Lyric(
    val trackId: String = "",
    /** 全局毫秒校正，可为负。 */
    val offsetMs: Int = 0,
    val lines: List<LyricLine> = emptyList(),
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** `event`：队列 / 音源模式变更（契约 §3.6）。[queue] 为原始 JSON 数组。 */
@Serializable
data class EventFrame(
    val name: String = "",
    val mode: String? = null,
    val queue: JsonArray? = null,
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** `art`：封面引用（非内联图片，契约 §3.7）。 */
@Serializable
data class Art(
    val ref: String = "",
    val mime: String? = null,
    val size: Int = 0,
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** `srcMeta`：串流模式手机上报的曲目元数据，Remote→Host（契约 §3.8）。 */
@Serializable
data class SrcMeta(
    val title: String = "",
    val artist: String = "",
    val pos: Double = 0.0,
    val dur: Double = 0.0,
    val status: String = PlayStatus.PLAYING,
    val artRef: String? = null,
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** `audio:start`：串流会话参数协商（契约 §3.9）。type 值里带冒号。 */
@Serializable
data class AudioStart(
    val codec: String = "opus",
    val rate: Int = 48000,
    val ch: Int = 2,
    val frameMs: Int = 20,
    val port: Int = 51000,
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** `audio:stop`：结束串流会话（契约 §3.9）。除信封外无字段。 */
@Serializable
data class AudioStop(
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** `error`：错误回执（契约 §3.10）。`code` 取值见 schema。 */
@Serializable
data class ErrorFrame(
    val ref: String? = null,
    val code: String = "",
    val hint: String? = null,
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()

/** `ping`：心跳（契约 §1，间隔 3s）。除信封外无字段。 */
@Serializable
data class Ping(
    override val v: Int = PROTOCOL_VERSION,
    override val seq: Long = 0,
    override val id: String? = null,
    override val ts: Double? = null,
    override val sig: String? = null,
) : Frame()