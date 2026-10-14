package songlink.remote.data.audio

import android.annotation.SuppressLint
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.projection.MediaProjection
import android.os.Build
import android.util.Log
import androidx.annotation.RequiresApi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import songlink.remote.data.protocol.PacketFramer
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * 手机音频采集 → §4 PHONE_STREAM 帧 → UDP 推送给 Host（架构 §3.9 audio:start / 契约 §4）。
 *
 * 真机启动路径（架构 §14 phone→host 链路）：
 *   1. UI Activity 启动 [MediaProjectionManager.createScreenCaptureIntent] 拿授权
 *   2. 系统返回 [MediaProjection] token，UI 把它交给 [captureProjection]
 *   3. 调用 [startCapture]——真使用 [AudioPlaybackCaptureConfiguration]（API 29+）
 *      或 [AudioFormat.CHANNEL_IN_MONO] + VOICE_CALL（API 26-28 fallback）
 *   4. 本类按 §4 帧格式封 UDP datagram，48 kHz / 单声道 / 20 ms / 1920 B/帧
 *
 * NOTE 当前 commit 限制:
 *  - **载荷是裸 16-bit PCM**（不是 Opus），用 flags bit2 = 1 标识，见 [FLAG_PCM_RAW]
 *  - 真正的 Opus 编码要 libopus JNI（~500 KB），留待独立 PR；flags bit2 = 0 时 Host 走 OpusCodec
 *  - UI Activity / CaptureService 的 MediaProjection 入口未提交——本类只暴露 hook；调用方未注入 token 时 API 29+ 直接报 "NEED_PROJECTION"，不会静默无数据
 *
 * 真机必做前置：UI 拿到 MediaProjection 后 [captureProjection](token)、再 [startCapture]
 * CI/JVM 单测：不走到 [record.startRecording()]，可在 onError 断言 NEED_PROJECTION 分支
 */
class PhoneAudioCapturer(private val applicationContext: android.content.Context) {

    companion object {
        private const val TAG = "PhoneAudioCapturer"
        const val FRAME_MS = 20
        const val SAMPLE_RATE = 48_000
        const val CHANNEL_CONFIG = AudioFormat.CHANNEL_IN_MONO
        const val AUDIO_FORMAT = AudioFormat.ENCODING_PCM_16BIT

        /** §4 flags bit2 = 1 表示 payload 为裸 PCM（绕过 Host 的 OpusCodec） */
        const val FLAG_PCM_RAW = 0x04
        const val FLAG_FIRST = 0x01
        const val FLAG_LAST = 0x02
        const val WIRE_VERSION: Byte = 1
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var captureJob: Job? = null
    private var record: AudioRecord? = null
    private var projection: MediaProjection? = null
    private var socket: DatagramSocket? = null

    @Volatile private var running = false
    @Volatile var remoteHost: String = "127.0.0.1"
    @Volatile var remotePort: Int = 51_000
    @Volatile private var lastRoundTripMs: Long = 0

    /** UI 调用方在拿到 MediaProjection 回调后注入；token 即失效（API 30+）才需重置。 */
    fun captureProjection(proj: MediaProjection) {
        this.projection = proj
    }

    /**
     * 启动音频采集并按 §4 封帧 UDP 推送。
     * 注：当前为占位 AudioPlaybackCapture 路径；调用方必须先 [captureProjection] 才在 API 29+ 上工作。
     * @param port 兼容占位：忽略；新值请直接赋值 [remotePort]
     */
    @SuppressLint("MissingPermission")
    fun startCapture(port: Int, onResult: (Boolean, String?) -> Unit) {
        if (running) { onResult(true, null); return }
        if (port > 0) remotePort = port

        val minBuf = AudioRecord.getMinBufferSize(SAMPLE_RATE, CHANNEL_CONFIG, AUDIO_FORMAT)
        if (minBuf <= 0) { onResult(false, "AudioRecord.getMinBufferSize=$minBuf 不支持"); return }

        // 单帧 48k * 20ms * 单声道 * 2 字节 = 1920 B
        val frameSizeBytes = (SAMPLE_RATE * FRAME_MS / 1000) * 1 * 2

        captureJob = scope.launch {
            try {
                val proj = projection
                val useCaptureApi = proj != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && proj == null) {
                    onResult(false, "NEED_PROJECTION: UI 需先 MediaProjection 授权再启动")
                    return@launch
                }

                record = if (useCaptureApi) {
                    AudioRecord.Builder()
                        .setAudioPlaybackCaptureConfig(buildConfig(proj!!))
                        .setAudioFormat(AudioFormat.Builder()
                            .setSampleRate(SAMPLE_RATE)
                            .setEncoding(AUDIO_FORMAT)
                            .setChannelMask(CHANNEL_CONFIG)
                            .build())
                        .setBufferSizeInBytes(maxOf(minBuf, frameSizeBytes * 4))
                        .build()
                } else {
                    @Suppress("DEPRECATION")
                    AudioRecord(
                        MediaRecorder.AudioSource.VOICE_CALL,
                        SAMPLE_RATE, CHANNEL_CONFIG, AUDIO_FORMAT,
                        maxOf(minBuf, frameSizeBytes * 4))
                }
                record?.startRecording() ?: run {
                    onResult(false, "AudioRecord.startRecording() 失败"); return@launch
                }

                socket = DatagramSocket()
                running = true
                onResult(true, null)

                val buffer = ByteArray(frameSizeBytes)
                var seq: Long = 0
                var pts: Long = 0
                val samplesPerFrame = (SAMPLE_RATE * FRAME_MS / 1000).toLong()

                while (isActive && running) {
                    val n = record!!.read(buffer, 0, buffer.size)
                    if (n <= 0) {
                        Log.w(TAG, "AudioRecord.read 异常 code=$n"); break
                    }
                    if (n != frameSizeBytes) continue    // 半帧丢弃

                    seq++
                    pts += samplesPerFrame

                    val payload: ByteArray = if (n == buffer.size) buffer else buffer.copyOf(n)
                    val frame = PacketFramer.assemblePcmFrame(
                        version = WIRE_VERSION,
                        flags = (FLAG_FIRST or FLAG_LAST or FLAG_PCM_RAW).toByte(),
                        seq = seq, pts = pts, pcm = payload)

                    try {
                        val s = socket ?: break
                        s.send(DatagramPacket(
                            frame, frame.size,
                            InetAddress.getByName(remoteHost), remotePort))
                        lastRoundTripMs = (frameSizeBytes * 1000L) / (SAMPLE_RATE * 2)
                    } catch (e: Exception) {
                        Log.w(TAG, "UDP send 失败: ${e.message}")
                    }
                }
            } catch (t: Throwable) {
                Log.e(TAG, "采集异常", t)
                onResult(false, t.message ?: "采集异常")
            } finally {
                stopInternal()
            }
        }
    }

    fun stopCapture() {
        running = false
        captureJob = null
        stopInternal()
    }

    private fun stopInternal() {
        try { record?.stop() } catch (_: Throwable) { }
        try { record?.release() } catch (_: Throwable) { }
        record = null
        try { socket?.close() } catch (_: Throwable) { }
        socket = null
    }

    fun getLastRoundTripMs(): Long = lastRoundTripMs

    @RequiresApi(Build.VERSION_CODES.Q)
    private fun buildConfig(proj: MediaProjection): AudioPlaybackCaptureConfiguration =
        AudioPlaybackCaptureConfiguration.Builder(proj)
            // 默认采集：媒体 + 游戏用途；电话/铃声忽略（不抢响铃/通话）
            .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
            .addMatchingUsage(AudioAttributes.USAGE_GAME)
            .addMatchingUsage(AudioAttributes.USAGE_NOTIFICATION_RINGTONE)
            // 不采集自己的应用 UID（看 Logs: Process.myUid()）
            .excludeUid(android.os.Process.myUid())
            .build()
}
