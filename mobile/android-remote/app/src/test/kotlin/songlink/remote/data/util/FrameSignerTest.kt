package songlink.remote.data.util

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import songlink.remote.Fixtures
import songlink.remote.data.model.Ack
import songlink.remote.data.model.Cmd
import songlink.remote.data.model.Frame
import songlink.remote.data.model.NowPlaying
import songlink.remote.data.model.PlayStatus
import songlink.remote.data.model.State

/**
 * 帧签名（契约 §6）测试。
 *
 * 最重要的一条：`cmd_signed.json` 是 C# 端用 key=`fixture-key-123` 真签出来的，
 * Kotlin 必须复算出**完全一样**的 sig，否则两端验签必然失败。
 */
class FrameSignerTest {

    private val codec = FrameCodec.Default

    @Test
    fun `reproduces the C sharp signature of cmd_signed fixture`() {
        val frame = codec.parse(Fixtures.text("cmd_signed")) as Cmd
        assertEquals(
            "Kotlin 端复算的 sig 必须与 C# 端一致",
            "6987F3267624FA263D1248D8E52E1BD3E869C72F899EA4F8865ABEED3F1A55C8",
            FrameSigner.compute("fixture-key-123", frame)
        )
    }

    @Test
    fun `canonical form excludes sig so compute is stable`() {
        val signed = codec.parse(Fixtures.text("cmd_signed")) as Cmd
        val unsigned = signed.copy(sig = null)

        assertEquals(
            """{"type":"cmd","action":"toggle","v":1,"seq":18,"id":"c-004"}""",
            codec.serialize(unsigned)
        )
        // 帧上有没有 sig 都不影响复算结果（对应 C# Compute 里临时清空再恢复）
        assertEquals(FrameSigner.compute("k", unsigned), FrameSigner.compute("k", signed))
        assertEquals(FrameSigner.compute("k", unsigned), FrameSigner.compute("k", signed.copy(sig="DEADBEEF")))
    }

    @Test
    fun `verify accepts the matching key and rejects everything else`() {
        val frame = codec.parse(Fixtures.text("cmd_signed")) as Cmd
        assertTrue(FrameSigner.verify("fixture-key-123", frame))
        assertFalse(FrameSigner.verify("wrong-key", frame))
        assertFalse("缺失 sig 应返回 false", FrameSigner.verify("fixture-key-123", frame.copy(sig = null)))
        assertFalse("空 sig 应返回 false", FrameSigner.verify("fixture-key-123", frame.copy(sig = "")))
    }

    @Test
    fun `sign produces a frame that verifies and keeps the original untouched`() {
        val original: Frame = Cmd(action = "toggle", seq = 18, id = "c-004")
        val signed = FrameSigner.sign("fixture-key-123", original)

        assertEquals("签名不应就地修改入参帧（C# 临时清空后恢复）", null, original.sig)
        assertNotNull(signed.sig)
        assertTrue(FrameSigner.verify("fixture-key-123", signed))
        assertEquals(Fixtures.text("cmd_signed"), codec.serialize(signed))
    }

    @Test
    fun `signature is upper case hex of 32 bytes`() {
        val sig = FrameSigner.compute("any-key", Ack(ref = "c-001", ok = true, seq = 7))
        assertEquals(64, sig.length)
        assertTrue(sig, sig.all { it in "0123456789ABCDEF" })
    }

    @Test
    fun `different keys and different frames yield different signatures`() {
        val a: Frame = Ack(ref = "c-001", ok = true, seq = 7)
        val b: Frame = Ack(ref = "c-002", ok = true, seq = 7)
        assertFalse(FrameSigner.compute("k1", a) == FrameSigner.compute("k2", a))
        assertFalse(FrameSigner.compute("k1", a) == FrameSigner.compute("k1", b))
    }

    @Test
    fun `signing works for frames carrying unicode payload`() {
        // canonical 里的非 ASCII 必须按 C# JavaScriptEncoder.Default 转成 \uXXXX，
        // 否则含中文的 cmd（如 pick）两端签名必然对不上。
        val frame = State(
            now = NowPlaying(title = "晴天", artist = "周杰伦", pos = 61.5, dur = 269.0, status = PlayStatus.PLAYING),
            seq = 8
        )
        val canonical = codec.serialize(frame)
        assertTrue(canonical, canonical.contains("""\u6674\u5929"""))
        assertTrue(canonical, canonical.contains("""\u5468\u6770\u4F26"""))
        assertEquals(64, FrameSigner.compute("k", frame).length)
    }
}