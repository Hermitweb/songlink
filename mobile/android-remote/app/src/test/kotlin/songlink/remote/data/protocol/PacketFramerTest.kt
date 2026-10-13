package songlink.remote.data.protocol

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * [PacketFramer] 与 C# [SongLink.Host.AudioStream.AudioStreamWire] 字节对齐合约测试。
 *
 * 真实跨语言一致性保证：
 *   1. 头部布局: ver(1) | flags(1) | seq(4) | pts(8) | payloadLen(2)  ——16 字节
 *   2. 全部字段大端（网络字节序）
 *   3. payloadLen = 实际 payload 字节数 ——最大 65535
 *   4. flags bit2 = 1 标识 payload 为裸 PCM（C# 端解码侧约定）
 *
 * 注：C# 端 `AudioStreamWire.Parse` 不在本仓库测试范围；这里只确保 Android 封包端 OK。
 */
class PacketFramerTest {

    @Test
    fun assemble_writes_big_endian_header_with_pcm_payload() {
        val pcm = ByteArray(1920) { it.toByte() }   // 1920 B = 48 kHz * 20 ms * mono * 16-bit
        val flags: Byte = (0x01 or 0x02 or 0x04).toByte()

        val pkt = PacketFramer.assemblePcmFrame(
            version = 1,
            flags = flags,
            seq = 1L,
            pts = 48_000L * 20L,   // 一个帧时长的帧号
            pcm = pcm)

        assertEquals(16 + pcm.size, pkt.size)

        // 头部字段逐项校验
        assertEquals(1.toByte(), pkt[0])                             // ver
        assertEquals(flags, pkt[1])                                  // flags
        // seq = 1 (BE: 00 00 00 01)
        assertEquals(0, byteAtAsInt(pkt[2]))
        assertEquals(0, byteAtAsInt(pkt[3]))
        assertEquals(0, byteAtAsInt(pkt[4]))
        assertEquals(1, byteAtAsInt(pkt[5]))
        // pts = 960000 = 0x0000_0000_000E_A600
        // BE 8 bytes: 00 00 00 00 00 0E A6 00
        assertEquals(0, byteAtAsInt(pkt[6]))
        assertEquals(0, byteAtAsInt(pkt[7]))
        assertEquals(0, byteAtAsInt(pkt[8]))
        assertEquals(0, byteAtAsInt(pkt[9]))
        assertEquals(0, byteAtAsInt(pkt[10]))
        assertEquals(0x0E, byteAtAsInt(pkt[11]))
        assertEquals(0xA6.toByte(), pkt[12])
        assertEquals(0, byteAtAsInt(pkt[13]))
        // payloadLen = 1920 = 0x0780 (BE: 07 80)
        assertEquals(0x07, byteAtAsInt(pkt[14]))
        assertEquals(0x80.toByte(), pkt[15])

        // 载荷字节与入参一致
        val payload = pkt.copyOfRange(16, pkt.size)
        assertArrayEquals(pcm, payload)
    }

    @Test
    fun assemble_increments_seq_across_many_frames() {
        // 验证我们没在中间漏 seq 字节
        var seq = 0L
        repeat(100) {
            seq++
            val pkt = PacketFramer.assemblePcmFrame(1, 0x07.toByte(), seq, seq * 960L, ByteArray(100))
            val readBack = ((pkt[2].toInt() and 0xFF) shl 24) or
                ((pkt[3].toInt() and 0xFF) shl 16) or
                ((pkt[4].toInt() and 0xFF) shl 8) or
                (pkt[5].toInt() and 0xFF)
            assertEquals(seq.toInt(), readBack)
        }
    }

    @Test
    fun assemble_rejects_empty_pcm() {
        try {
            PacketFramer.assemblePcmFrame(1, 0, 1L, 0L, ByteArray(0))
            org.junit.Assert.fail("应抛 IllegalArgumentException")
        } catch (e: IllegalArgumentException) {
            assertTrue(e.message?.contains("PCM") == true)
        }
    }

    @Test
    fun assemble_rejects_oversized_payload() {
        val oversize = ByteArray(70_000)
        try {
            PacketFramer.assemblePcmFrame(1, 0, 1L, 0L, oversize)
            org.junit.Assert.fail("应抛 IllegalArgumentException")
        } catch (e: IllegalArgumentException) {
            assertTrue(e.message?.contains("65535") == true)
        }
    }

    private fun byteAtAsInt(b: Byte): Int = b.toInt() and 0xFF
}
