package songlink.remote.data.model

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.assertFalse
import org.junit.Test
import songlink.remote.Fixtures
import songlink.remote.data.util.FrameCodec

/**
 * 帧模型的**跨语言契约测试**：输入全部来自 C# 端 `FrameCodec.Serialize` 的真实产物。
 *
 * 覆盖：
 * 1. 18 个合法 fixture 全部能解析；
 * 2. **解析后再序列化必须与 C# 原始字节完全一致**（属性顺序 / null 省略 / 转义 / double 格式）；
 * 3. 3 个坏帧全部被拒（返回 null，不抛）；
 * 4. 属性顺序不敏感（`{"v":1,"type":...}` 这种第三方客户端写法）；
 * 5. Kotlin 构造的帧能被自己的 parser 读回。
 */
class FramesTest {

    private val codec = FrameCodec.Default

    // ------------------------------------------------------------- 1. 全部可解析

    @Test
    fun `parses every fixture produced by the C sharp codec`() {
        val failures = mutableListOf<String>()
        Fixtures.GOOD_NAMES.forEach { name ->
            val frame = codec.parse(Fixtures.text(name))
            if (frame == null) failures += "$name -> null"
        }
        assertEquals("以下 fixture 解析失败: $failures", emptyList<String>(), failures)
    }

    @Test
    fun `re-serializing a parsed fixture reproduces the C sharp bytes exactly`() {
        Fixtures.GOOD_NAMES.forEach { name ->
            val original = Fixtures.text(name)
            val frame = codec.parse(original)
            assertNotNull("$name 应能解析", frame)
            assertEquals("$name 序列化字节应与 C# 端完全一致", original, codec.serialize(frame!!))
        }
    }

    // ------------------------------------------------------------- 2. 字段级断言

    @Test
    fun `hello fixture`() {
        val f = codec.parse(Fixtures.text("hello")) as Hello
        assertEquals("remote", f.role)
        assertEquals("Pixel 7", f.device)
        assertEquals("Android 14", f.os)
        assertEquals(listOf("cmd", "state", "lyric", "art"), f.caps)
        assertEquals("123456", f.pin)
        assertEquals(1, f.v)
        assertEquals(1L, f.seq)
        assertEquals("h-001", f.id)
        assertNull(f.token)
        assertNull(f.lastSeq)
    }

    @Test
    fun `hello reconnect fixture carries token and lastSeq`() {
        val f = codec.parse(Fixtures.text("hello_reconnect")) as Hello
        assertEquals("a1b2c3d4e5f6", f.token)
        assertEquals(42L, f.lastSeq)
        assertNull(f.pin)
        assertEquals(listOf("cmd", "state"), f.caps)
    }

    @Test
    fun `authResult ok fixture`() {
        val f = codec.parse(Fixtures.text("authResult_ok")) as AuthResult
        assertTrue(f.ok)
        assertEquals("h-001", f.ref)
        assertEquals("tok-abc-123", f.token)
        assertEquals("hostkey-xyz", f.hostKey)
        assertNull(f.code)
    }

    @Test
    fun `authResult fail fixture`() {
        val f = codec.parse(Fixtures.text("authResult_fail")) as AuthResult
        assertFalse(f.ok)
        assertEquals("AUTH_FAILED", f.code)
        assertNull(f.token)
        assertNull(f.hostKey)
    }

    @Test
    fun `cmd with args decodes unicode escapes`() {
        val f = codec.parse(Fixtures.text("cmd_pick")) as Cmd
        assertEquals("pick", f.action)
        assertEquals("c-001", f.id)
        assertEquals(5L, f.seq)
        val args = f.args!!
        // \u6674\u5929 = 晴天；\u5468\u6770\u4F26 = 周杰伦（标准 JSON 解析还原，不自己写解码）
        assertEquals("晴天", (args["title"] as JsonPrimitive).content)
        assertEquals("周杰伦", (args["artist"] as JsonPrimitive).content)
    }

    @Test
    fun `cmd without args has null args`() {
        val f = codec.parse(Fixtures.text("cmd_next_noargs")) as Cmd
        assertEquals("next", f.action)
        assertNull("C# WhenWritingNull 会省略 null 字段，解析侧应为 null", f.args)
    }

    @Test
    fun `ack fixture`() {
        val f = codec.parse(Fixtures.text("ack_ok")) as Ack
        assertEquals("c-001", f.ref)
        assertTrue(f.ok)
        assertEquals(7L, f.seq)
        assertEquals("a-001", f.id)
    }

    @Test
    fun `state fixture decodes nested nowPlaying`() {
        val f = codec.parse(Fixtures.text("state")) as State
        val now = f.now
        assertEquals("晴天", now.title)
        assertEquals("周杰伦", now.artist)
        assertEquals("叶惠美", now.album)
        assertEquals(61.5, now.pos, 1e-9)
        assertEquals(269.0, now.dur, 1e-9)
        assertEquals(PlayStatus.PLAYING, now.status)
        assertEquals("art-1", now.artRef)
        assertEquals(SourceMode.PC_LOCAL, now.sourceMode)
        assertEquals(8L, f.seq)
    }

    @Test
    fun `lyric fixture`() {
        val f = codec.parse(Fixtures.text("lyric")) as Lyric
        assertEquals("track-77", f.trackId)
        assertEquals(-120, f.offsetMs)
        assertEquals(2, f.lines.size)
        assertEquals(0.0, f.lines[0].t, 1e-9)
        assertEquals("故事的小黄花", f.lines[0].x)
        assertEquals(3.5, f.lines[1].t, 1e-9)
        assertEquals("从出生那年就飘着", f.lines[1].x)
    }

    @Test
    fun `event fixture keeps raw queue array`() {
        val f = codec.parse(Fixtures.text("event_queue")) as EventFrame
        assertEquals("queue-updated", f.name)
        assertNull(f.mode)
        val queue = f.queue!!
        assertEquals(2, queue.size)
        assertEquals("A", (queue[0] as JsonObject)["title"].let { (it as JsonPrimitive).content })
    }

    @Test
    fun `art fixture`() {
        val f = codec.parse(Fixtures.text("art")) as Art
        assertEquals("art-1", f.ref)
        assertEquals("image/png", f.mime)
        assertEquals(20480, f.size)
    }

    @Test
    fun `srcMeta fixture`() {
        val f = codec.parse(Fixtures.text("srcMeta")) as SrcMeta
        assertEquals("流媒体曲目", f.title)
        assertEquals("手机端", f.artist)
        assertEquals(12.0, f.pos, 1e-9)
        assertEquals(200.0, f.dur, 1e-9)
        assertEquals(PlayStatus.PLAYING, f.status)
        assertEquals("art-2", f.artRef)
    }

    @Test
    fun `audio start fixture - type value contains a colon`() {
        val f = codec.parse(Fixtures.text("audio_start")) as AudioStart
        assertEquals("opus", f.codec)
        assertEquals(48000, f.rate)
        assertEquals(2, f.ch)
        assertEquals(20, f.frameMs)
        assertEquals(51000, f.port)
        assertTrue(Fixtures.text("audio_start").startsWith("""{"type":"audio:start""""))
    }

    @Test
    fun `audio stop fixture has only envelope fields`() {
        val f = codec.parse(Fixtures.text("audio_stop")) as AudioStop
        assertEquals(14L, f.seq)
        assertNull(f.id)
        assertNull(f.sig)
    }

    @Test
    fun `error fixture`() {
        val f = codec.parse(Fixtures.text("error")) as ErrorFrame
        assertEquals("c-009", f.ref)
        assertEquals("APP_NOT_FOUND", f.code)
        assertEquals("未找到该 App 进程", f.hint)
    }

    @Test
    fun `ping fixture`() {
        val f = codec.parse(Fixtures.text("ping")) as Ping
        assertEquals(1, f.v)
        assertEquals(16L, f.seq)
    }

    @Test
    fun `searchResults fixture - second item omits album`() {
        val f = codec.parse(Fixtures.text("searchResults")) as SearchResults
        assertEquals("c-003", f.ref)
        assertEquals("晴天", f.query)
        assertEquals(2, f.results.size)
        assertEquals("叶惠美", f.results[0].album)
        assertEquals(269.0, f.results[0].dur!!, 1e-9)
        assertEquals(" Revival", f.results[1].artist)
        assertNull("C# 省略 null album，解析侧应为 null", f.results[1].album)
        assertEquals(245.0, f.results[1].dur!!, 1e-9)
    }

    // ------------------------------------------------------------- 3. 坏帧必须被拒

    @Test
    fun `rejects all three bad fixtures`() {
        Fixtures.BAD_NAMES.forEach { name ->
            assertNull("$name 应被拒绝且不抛异常", codec.parse(Fixtures.text(name)))
        }
    }

    @Test
    fun `rejects frames whose v is not 1`() {
        assertNull(codec.parse("""{"v":0,"type":"ping","seq":1}"""))
        assertNull(codec.parse("""{"v":2,"type":"ping","seq":1}"""))
        assertNull(codec.parse("""{"v":"1","type":"ping","seq":1}"""))
    }

    @Test
    fun `rejects unknown type and non object payloads`() {
        assertNull(codec.parse("""{"v":1,"type":"noSuchFrame","seq":1}"""))
        assertNull(codec.parse("""{"v":1,"seq":1}"""))
        assertNull(codec.parse("""[1,2,3]"""))
        assertNull(codec.parse(""))
    }

    @Test
    fun `rejects frames with wrongly typed fields`() {
        assertNull(codec.parse("""{"v":1,"type":"cmd","action":42}"""))
        assertNull(codec.parse("""{"v":1,"type":"cmd","action":"pick","args":"notAnObject"}"""))
        assertNull(codec.parse("""{"v":1,"type":"searchResults","results":{}}"""))
    }

    // --------------------------------------------------- 4. 顺序不敏感 / 缺省容错

    @Test
    fun `parse is insensitive to property order`() {
        // System.Text.Json 的多态反序列化要求 type 在首个属性，第三方客户端常按下面顺序发帧，
        // 因此 C# 与本实现都是「先读 type 再按具体类型解析」。
        val f = codec.parse("""{"v":1,"seq":1,"type":"ping","id":"x"}""") as Ping
        assertEquals(1L, f.seq)
        assertEquals("x", f.id)
    }

    @Test
    fun `missing v falls back to protocol version 1`() {
        val f = codec.parse("""{"type":"ping","seq":7}""") as Ping
        assertEquals(PROTOCOL_VERSION, f.v)
        assertEquals(7L, f.seq)
    }

    @Test
    fun `unknown extra fields are ignored for forward compatibility`() {
        val f = codec.parse("""{"type":"ping","seq":1,"v":1,"someFutureField":{"a":1}}""") as Ping
        assertEquals(1L, f.seq)
    }

    // ------------------------------------------------------ 5. Kotlin 侧自洽性

    @Test
    fun `kotlin built cmd round trips through the codec`() {
        val args = buildJsonObject {
            put("title", "晴天")
            put("artist", "周杰伦")
        }
        val cmd = Cmd(action = "pick", args = args, seq = 5, id = "c-001")
        val text = codec.serialize(cmd)

        // C# 端实测的字节序：type 首属性，派生字段次之，基类信封最后；
        // 且非 ASCII 一律转成大写 \uXXXX 转义（JavaScriptEncoder.Default），所以这里不是明文中文。
        assertEquals(
            """{"type":"cmd","action":"pick","args":{"title":"\u6674\u5929","artist":"\u5468\u6770\u4F26"},"v":1,"seq":5,"id":"c-001"}""",
            text
        )

        val back = codec.parse(text) as Cmd
        assertEquals(cmd, back)
    }

    @Test
    fun `kotlin built cmd without args omits the args property`() {
        val text = codec.serialize(Cmd(action = "next", seq = 6, id = "c-002"))
        assertEquals("""{"type":"cmd","action":"next","v":1,"seq":6,"id":"c-002"}""", text)
        assertFalse(text.contains("args"))
    }

    @Test
    fun `integral doubles are written without a trailing dot`() {
        // C# System.Text.Json: 269.0 -> 269；kotlinx 默认会写成 269.0，必须由 FrameCodec 修正
        val text = codec.serialize(
            State(now = NowPlaying(pos = 0.0, dur = 269.0, status = PlayStatus.PLAYING), seq = 8)
        )
        assertTrue(text, text.contains(""""pos":0,"""))
        assertTrue(text, text.contains(""""dur":269"""))
        assertTrue(text, text.contains(""""sourceMode":"PC_LOCAL""""))
        assertFalse(text, text.contains(".0"))
    }

    @Test
    fun `raw json args keep their explicit nulls like C sharp does`() {
        // WhenWritingNull 只作用于 @Serializable 类的属性，raw JsonObject 内的 null 要保留
        val args = Json.parseToJsonElement("""{"a":null,"b":1}""") as JsonObject
        val text = codec.serialize(Cmd(action = "x", args = args, seq = 1))
        assertEquals("""{"type":"cmd","action":"x","args":{"a":null,"b":1},"v":1,"seq":1}""", text)
    }

    @Test
    fun `all fourteen frame types are covered by the discriminator mapping`() {
        val frames = listOf(
            Hello(), Cmd(), Ack(), State(), Lyric(), EventFrame(), Art(), SrcMeta(),
            AudioStart(), AudioStop(), ErrorFrame(), Ping(), AuthResult(), SearchResults()
        )
        assertEquals(14, frames.size)
        frames.forEach { frame ->
            val text = codec.serialize(frame)
            val back = codec.parse(text)
            assertEquals(frame::class, back?.let { it::class })
            assertEquals(text, codec.serialize(back!!))
        }
    }
}