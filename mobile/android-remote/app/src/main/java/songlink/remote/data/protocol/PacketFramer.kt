package songlink.remote.data.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 协议 §4 PHONE_STREAM 帧封/拆（与 C# [SongLink.Host.AudioStream.AudioStreamWire] 字节对齐）。
 *
 * 帧格式（大端 / 网络字节序）：
 *   ver(1) | flags(1) | seq(4) | pts(8) | payloadLen(2) | payload...
 *
 * 客户端（Android 手机）：用作 outgoing 封包
 * 服务端（PC）：用 [SongLink.Host.AudioStream.AudioStreamWire.Parse]
 *
 * 注意：Android 端的 AudioRecord 送 16-bit PCM（48 kHz / mono / 20 ms = 1920 B/帧），
 * flags bit2 = [PhoneAudioCapturer.FLAG_PCM_RAW] 标识。Host 端解码时按此标记直传。
 */
object PacketFramer {

    /** 帧头部大小：1+1+4+8+2 = 16 字节。 */
    const val HEADER_SIZE = 16

    /** 拼装一帧完整 datagram（包含头部 + 裸 PCM 载荷）。 */
    fun assemblePcmFrame(
        version: Byte,
        flags: Byte,
        seq: Long,
        pts: Long,
        pcm: ByteArray
    ): ByteArray {
        require(pcm.isNotEmpty()) { "PCM 载荷不能为空" }
        require(pcm.size <= 0xFFFF) { "单帧 PCM 不能超过 65535 字节 (实际 ${pcm.size})" }

        val out = ByteBuffer.allocate(HEADER_SIZE + pcm.size).order(ByteOrder.BIG_ENDIAN)
        out.put(version)
        out.put(flags)
        out.putInt(seq.toInt())
        out.putLong(pts)
        out.putShort(pcm.size.toShort())
        out.put(pcm)
        return out.array()
    }
}
