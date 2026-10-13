package songlink.remote.data.util

import kotlinx.serialization.KSerializer
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import songlink.remote.data.model.Ack
import songlink.remote.data.model.Art
import songlink.remote.data.model.AudioStart
import songlink.remote.data.model.AudioStop
import songlink.remote.data.model.AuthResult
import songlink.remote.data.model.Cmd
import songlink.remote.data.model.ENVELOPE_FIELDS
import songlink.remote.data.model.ErrorFrame
import songlink.remote.data.model.EventFrame
import songlink.remote.data.model.Frame
import songlink.remote.data.model.Hello
import songlink.remote.data.model.Lyric
import songlink.remote.data.model.PROTOCOL_VERSION
import songlink.remote.data.model.Ping
import songlink.remote.data.model.SearchResults
import songlink.remote.data.model.SrcMeta
import songlink.remote.data.model.State
import songlink.remote.data.model.TYPE_KEY
import kotlinx.serialization.json.intOrNull
import kotlin.math.abs

/**
 * 帧编解码，对齐 C# 端 `src/SongLink.Protocol/Frames.cs:164` 的 `FrameCodec`。
 *
 * 解析语义（与 C# 完全一致）：
 * - 非法 JSON、显式 `v != 1`、未知 `type`、字段异常 → 一律返回 `null`，**不抛**；
 * - 先读 `type` 判别符再按具体类型反序列化，对属性顺序不敏感
 *   （原因同 C# 注释：System.Text.Json 的多态反序列化要求 `type` 出现在对象首个属性，
 *   而第三方客户端常按 `{"v":1,"type":"hello"}` 顺序发帧）。
 *
 * 序列化语义（C# 实际写法，本类手写 canonical writer 以保证字节级一致）：
 * - `"type"` 写在对象第一个属性；
 * - 派生类字段按声明序排在基类信封 `v/seq/id/ts/sig` 之前；
 * - null 字段不写出（`WhenWritingNull`）；
 * - 字符串转义对齐 `JavaScriptEncoder.Default`（非 ASCII 一律写成反斜杠 + `u` + 4 位大写十六进制，
 *   并转义 `" & ' + < > `；`\\ \b \t \n \f \r` 走短转义）；
 * - double 用 .NET 最短往返形式：整数不带 `.0`（`269` 而非 `269.0`），指数形如 `1E+17`。
 *
 * 这些规则不是洁癖：帧签名 [FrameSigner] 就是对 canonical JSON 做 HMAC，
 * 任何一个字节不同两端签名就对不上。
 */
open class FrameCodec {

    /** 反序列化配置：忽略未知键（对齐 C# 默认行为）。 */
    private val reader = Json {
        ignoreUnknownKeys = true
    }

    /** 编码配置：`encodeDefaults` 写出默认值、`explicitNulls = false` 省略 null。 */
    @OptIn(kotlinx.serialization.ExperimentalSerializationApi::class)
    private val writer = Json {
        encodeDefaults = true
        explicitNulls = false
    }

    /** 序列化一帧为 canonical JSON（含 `sig`，若帧上有）。 */
    open fun serialize(frame: Frame): String = write(frame, frame.sig)

    /**
     * 帧签名用的 canonical 形式：与 [serialize] 唯一区别是**一定去掉 `sig`**。
     * 对应 C# `FrameSigner.Compute` 里「临时清空 frame.Sig 再序列化」。
     */
    internal fun canonical(frame: Frame): String = write(frame, null)

    /** 解析一帧；坏帧返回 `null`。 */
    open fun parse(text: String): Frame? {
        return try {
            val root = reader.parseToJsonElement(text)
            val obj = root as? JsonObject ?: return null

            // v 显式存在且 != 1 → 拒（契约 §5）。缺失则沿用默认值 1。
            // 必须要求它是 JSON number：C# 的 GetValue<int>() 遇到字符串/布尔会抛异常 → 返回 null，
            // 而 kotlinx 默认允许 "1" 转 Int，这里显式收紧才能与 C# 一致。
            val version = obj["v"]
            if (version != null) {
                val p = version.asPrimitive()
                if (p == null || p.isString || p.intOrNull != PROTOCOL_VERSION) return null
            }

            val type = obj[TYPE_KEY]?.asPrimitive()?.asTypeString() ?: return null
            val serializer = serializerFor(type) ?: return null
            reader.decodeFromJsonElement(serializer, obj)
        } catch (e: Exception) {
            // 任何字段 / 类型异常都视为坏帧，交由上层回 BAD_PROTO（不吞连接）
            null
        }
    }

    // ---------------------------------------------------------------- serialize

    private fun write(frame: Frame, sig: String?): String {
        val encoded = writer.encodeToJsonElement(Frame.serializer(), frame) as JsonObject

        val sb = StringBuilder()
        sb.append('{')
        var first = true

        // 1) 判别符固定第一个属性
        first = appendKey(sb, first, TYPE_KEY)
        appendString(sb, typeOf(frame))

        // 2) 派生类字段：保持 kotlinx 编码给出的声明序，跳过信封字段
        for ((key, value) in encoded) {
            if (key == TYPE_KEY || key in ENVELOPE_FIELDS) continue
            if (value is JsonNull) continue
            first = appendKey(sb, first, key)
            appendElement(sb, value)
        }

        // 3) 基类信封字段：v / seq / id / ts，null 不写出
        for (key in ENVELOPE_FIELDS) {
            if (key == "sig") continue
            val value = encoded[key] ?: continue
            if (value is JsonNull) continue
            first = appendKey(sb, first, key)
            appendElement(sb, value)
        }

        // 4) sig 排最后（对齐 cmd_signed.json 的实测字节序）
        if (sig != null) {
            appendKey(sb, first, "sig")
            appendString(sb, sig)
        }

        sb.append('}')
        return sb.toString()
    }

    private fun appendKey(sb: StringBuilder, first: Boolean, key: String): Boolean {
        if (!first) sb.append(',')
        appendString(sb, key)
        sb.append(':')
        return false
    }

    private fun appendElement(sb: StringBuilder, element: JsonElement) {
        when (element) {
            is JsonNull -> sb.append("null")
            is JsonObject -> {
                sb.append('{')
                var first = true
                for ((key, value) in element) {
                    if (!first) sb.append(',')
                    first = false
                    appendString(sb, key)
                    sb.append(':')
                    appendElement(sb, value)
                }
                sb.append('}')
            }
            is JsonArray -> {
                sb.append('[')
                var first = true
                for (item in element) {
                    if (!first) sb.append(',')
                    first = false
                    appendElement(sb, item)
                }
                sb.append(']')
            }
            is JsonPrimitive -> appendPrimitive(sb, element)
        }
    }

    private fun appendPrimitive(sb: StringBuilder, primitive: JsonPrimitive) {
        if (primitive.isString) {
            appendString(sb, primitive.content)
            return
        }
        val raw = primitive.content
        // 整数（含超出 double 精度的 Long，如大 seq）原样输出，避免精度丢失
        if (raw.toLongOrNull() != null) {
            sb.append(raw)
            return
        }
        val decimal = raw.toDoubleOrNull()
        if (decimal == null) {
            // true / false：JsonPrimitive 的 content 就是字面量
            sb.append(raw)
            return
        }
        sb.append(formatDouble(decimal))
    }

    /**
     * 字符串转义，严格对齐 C# `JavaScriptEncoder.Default`（规则实测自 C# 端 `FrameCodec.Serialize`）：
     * 非 ASCII（含代理对）一律写成反斜杠 + `u` + 4 位大写十六进制；`" & ' + < > 反引号` 转义；
     * `\\ \b \t \n \f \r` 用短转义；其余可打印 ASCII 原样输出。
     */
    private fun appendString(sb: StringBuilder, value: String) {
        sb.append('"')
        for (ch in value) {
            when (ch) {
                '\\' -> sb.append("\\\\")
                '\b' -> sb.append("\\b")
                '\t' -> sb.append("\\t")
                '\n' -> sb.append("\\n")
                '\u000C' -> sb.append("\\f")
                '\r' -> sb.append("\\r")
                '"', '&', '\'', '+', '<', '>', '`' -> appendUnicodeEscape(sb, ch)
                else -> if (ch.code in 0x20..0x7E) sb.append(ch) else appendUnicodeEscape(sb, ch)
            }
        }
        sb.append('"')
    }

    private fun appendUnicodeEscape(sb: StringBuilder, ch: Char) {
        val code = ch.code
        sb.append("\\u")
        sb.append(HEX[(code shr 12) and 0xF])
        sb.append(HEX[(code shr 8) and 0xF])
        sb.append(HEX[(code shr 4) and 0xF])
        sb.append(HEX[code and 0xF])
    }

    /** double 格式化，对齐 .NET「最短往返」：整数不带 `.0`，指数统一 `E+NN` / `E-NN`。 */
    private fun formatDouble(value: Double): String {
        if (value.isNaN() || value.isInfinite()) {
            // C# System.Text.Json 对非有限值直接抛错，这里同样不产出非法 JSON
            throw IllegalArgumentException("double 不可序列化: $value")
        }
        val abs = abs(value)
        val text = value.toString()
        if (abs != 0.0 && (abs >= 1e17 || abs < 1e-5)) {
            val e = text.indexOf('E')
            if (e >= 0) {
                var mantissa = text.substring(0, e)
                if (mantissa.endsWith(".0")) mantissa = mantissa.dropLast(2)
                val exp = text.substring(e + 1)
                val negative = exp.startsWith("-")
                val digits = exp.removePrefix("-").removePrefix("+").trimStart('0')
                    .ifEmpty { "0" }.padStart(2, '0')
                return "${mantissa}E${if (negative) "-" else "+"}$digits"
            }
        }
        return if (text.endsWith(".0")) text.dropLast(2) else text
    }

    // -------------------------------------------------------------------- 映射

    /** type 值 → 具体反序列化器（覆盖 C# `FrameCodec.Parse` 的 switch 全部分支）。 */
    private fun serializerFor(type: String): KSerializer<out Frame>? = when (type) {
        "hello" -> Hello.serializer()
        "cmd" -> Cmd.serializer()
        "ack" -> Ack.serializer()
        "state" -> State.serializer()
        "lyric" -> Lyric.serializer()
        "event" -> EventFrame.serializer()
        "art" -> Art.serializer()
        "srcMeta" -> SrcMeta.serializer()
        "audio:start" -> AudioStart.serializer()
        "audio:stop" -> AudioStop.serializer()
        "error" -> ErrorFrame.serializer()
        "ping" -> Ping.serializer()
        "authResult" -> AuthResult.serializer()
        "searchResults" -> SearchResults.serializer()
        else -> null
    }

    /** 帧实例 → type 值。 */
    private fun typeOf(frame: Frame): String = when (frame) {
        is Hello -> "hello"
        is Cmd -> "cmd"
        is Ack -> "ack"
        is State -> "state"
        is Lyric -> "lyric"
        is EventFrame -> "event"
        is Art -> "art"
        is SrcMeta -> "srcMeta"
        is AudioStart -> "audio:start"
        is AudioStop -> "audio:stop"
        is ErrorFrame -> "error"
        is Ping -> "ping"
        is AuthResult -> "authResult"
        is SearchResults -> "searchResults"
    }

    private fun JsonElement.asPrimitive(): JsonPrimitive? =
        if (this is JsonPrimitive && this !is JsonNull) this else null

    private fun JsonPrimitive.asTypeString(): String? = content.takeUnless { it == "null" }

    companion object {
        /** 默认（无状态）编解码器。 */
        val Default: FrameCodec = FrameCodec()

        private val HEX = "0123456789ABCDEF".toCharArray()
    }
}