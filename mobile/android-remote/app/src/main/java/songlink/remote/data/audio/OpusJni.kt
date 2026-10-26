package songlink.remote.data.audio

/**
 * Opus 编码器 JNI 封装（libopus 源码 NDK 现编，见 app/src/main/cpp 与 third_party/opus）。
 *
 * 失败语义：[load] / [create] 返回 false/0 时，调用方（[PhoneAudioCapturer]）自动回退
 * 裸 PCM 直通（flags bit2=1，Host 已支持）——没有 NDK 的机器 APK 照样能出、链路照样通。
 */
object OpusJni {

    private const val LIB = "songlink_opus"
    private var loaded: Boolean? = null

    external fun nativeCreateEncoder(sampleRate: Int, channels: Int, bitrateBps: Int, frameMs: Int): Long
    external fun nativeEncode(handle: Long, pcm: ShortArray, frameSize: Int, out: ByteArray, outCap: Int): Int
    external fun nativeDestroyEncoder(handle: Long)

    val isAvailable: Boolean
        get() = load()

    /** 尝试加载 native 库；结果缓存。false = 没有 NDK 产物，走 PCM 回退。 */
    fun load(): Boolean = loaded ?: run {
        try {
            System.loadLibrary(LIB)
            true
        } catch (_: Throwable) {
            false
        }.also { loaded = it }
    }

    /** 建编码器；返回 0 = 失败。 */
    fun create(sampleRate: Int, channels: Int, bitrateBps: Int, frameMs: Int): Long {
        if (!load()) return 0
        return try {
            nativeCreateEncoder(sampleRate, channels, bitrateBps, frameMs)
        } catch (_: Throwable) {
            0
        }
    }

    /** 编码一帧 PCM → Opus 包（写到 [out]，返回包字节数；负值 = 错误）。 */
    fun encode(handle: Long, pcm: ShortArray, frameSize: Int, out: ByteArray): Int {
        if (handle == 0L) return -1
        return try {
            nativeEncode(handle, pcm, frameSize, out, out.size)
        } catch (_: Throwable) {
            -1
        }
    }

    fun destroy(handle: Long) {
        if (handle == 0L) return
        try { nativeDestroyEncoder(handle) } catch (_: Throwable) {}
    }
}
