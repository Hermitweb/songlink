package songlink.remote

/**
 * 跨语言 fixture 读取器。
 *
 * `app/src/test/resources/fixtures` 下的 21 个 JSON 由 C# 端真实的 `FrameCodec.Serialize` 产出
 * （生成器 `tools/SongLink.FixtureGen`，重新生成：
 * `dotnet run --project tools/SongLink.FixtureGen -- mobile/android-remote/app/src/test/resources/fixtures`）。
 * 这些文件是 Android 端与 C# 端能不能真的对上的硬证据。
 */
object Fixtures {

    private const val DIR = "fixtures"

    /** 合法帧 fixture（C# FrameCodec.Serialize 的真实输出）。 */
    val GOOD_NAMES = listOf(
        "hello",
        "hello_reconnect",
        "authResult_ok",
        "authResult_fail",
        "cmd_pick",
        "cmd_next_noargs",
        "cmd_signed",
        "ack_ok",
        "state",
        "lyric",
        "event_queue",
        "art",
        "srcMeta",
        "audio_start",
        "audio_stop",
        "error",
        "ping",
        "searchResults",
    )

    /** 坏帧 fixture：两端都必须拒绝。 */
    val BAD_NAMES = listOf(
        "bad_not_json",
        "bad_unknown_type",
        "bad_version",
    )

    /** 全部 fixture（21 个）。 */
    val ALL_NAMES = GOOD_NAMES + BAD_NAMES

    /** 读取 fixture 原文（不做任何规范化）。 */
    fun text(name: String): String {
        val resource = "$DIR/$name.json"
        val stream = Fixtures::class.java.classLoader?.getResourceAsStream(resource)
            ?: error(
                "fixture 缺失: $resource；请先用 tools/SongLink.FixtureGen 重新生成到 " +
                    "app/src/test/resources/fixtures"
            )
        return stream.use { it.readBytes().toString(Charsets.UTF_8) }
    }
}