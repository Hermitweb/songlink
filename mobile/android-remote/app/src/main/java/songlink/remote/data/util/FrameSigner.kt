package songlink.remote.data.util

import songlink.remote.data.model.Frame
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * 每帧 HMAC-SHA256 签名（契约 §6），逐行为对齐 C# 端 `src/SongLink.Protocol/FrameSigner.cs`。
 *
 * ```
 * canonical = FrameCodec 用同一套选项序列化「去掉 sig 的帧」
 * sig      = Convert.ToHexString(HMAC-SHA256(key=token, utf8(canonical)))   // 大写十六进制
 * ```
 *
 * canonical 的字节级一致性由 [FrameCodec] 保证（属性顺序 / null 省略 / 字符串转义 /
 * double 格式都必须与 System.Text.Json 一致），否则两端签名对不上。
 */
object FrameSigner {

    private const val ALGORITHM = "HmacSHA256"

    /** 计算帧签名（大写十六进制），不修改入参帧。 */
    fun compute(key: String, frame: Frame): String {
        val canonical = FrameCodec.Default.canonical(frame)
        val mac = Mac.getInstance(ALGORITHM)
        mac.init(SecretKeySpec(key.toByteArray(Charsets.UTF_8), ALGORITHM))
        val digest = mac.doFinal(canonical.toByteArray(Charsets.UTF_8))
        val sb = StringBuilder(digest.size * 2)
        for (b in digest) {
            val v = b.toInt() and 0xFF
            sb.append(HEX[v shr 4]).append(HEX[v and 0xF])
        }
        return sb.toString()
    }

    /** 返回盖上签名的新帧（原帧不变，与 C# 就地改 `frame.Sig` 的差异仅在不可变性）。 */
    fun sign(key: String, frame: Frame): Frame = frame.withSig(compute(key, frame))

    /** 校验帧自带 `sig`；缺失或不匹配返回 false。比较为定长防时序侧信道。 */
    fun verify(key: String, frame: Frame): Boolean {
        val actual = frame.sig ?: return false
        if (actual.isEmpty()) return false
        return constantTimeEquals(compute(key, frame), actual)
    }

    private fun constantTimeEquals(a: String, b: String): Boolean {
        if (a.length != b.length) return false
        var diff = 0
        for (i in a.indices) {
            diff = diff or (a[i].code xor b[i].code)
        }
        return diff == 0
    }

    private val HEX = "0123456789ABCDEF".toCharArray()
}

/**
 * 就地替换 `sig`。sealed 层级没有通用的 `copy`，由每个帧类型自行提供。
 * 这样 [FrameSigner.sign] 不需要真的改动入参对象（C# 是临时清空再恢复）。
 */
internal fun Frame.withSig(sig: String?): Frame = when (this) {
    is songlink.remote.data.model.Hello -> copy(sig = sig)
    is songlink.remote.data.model.Cmd -> copy(sig = sig)
    is songlink.remote.data.model.Ack -> copy(sig = sig)
    is songlink.remote.data.model.State -> copy(sig = sig)
    is songlink.remote.data.model.Lyric -> copy(sig = sig)
    is songlink.remote.data.model.EventFrame -> copy(sig = sig)
    is songlink.remote.data.model.Art -> copy(sig = sig)
    is songlink.remote.data.model.SrcMeta -> copy(sig = sig)
    is songlink.remote.data.model.AudioStart -> copy(sig = sig)
    is songlink.remote.data.model.AudioStop -> copy(sig = sig)
    is songlink.remote.data.model.ErrorFrame -> copy(sig = sig)
    is songlink.remote.data.model.Ping -> copy(sig = sig)
    is songlink.remote.data.model.AuthResult -> copy(sig = sig)
    is songlink.remote.data.model.SearchResults -> copy(sig = sig)
}