package dev.songlink.remote.audio

import android.app.Notification
import android.app.Service
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.IBinder
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * M0 Spike ② · 安卓音频采集串流（对应架构 §3.7 / §14 / 协议契约 §4）
 *
 * 链路：AudioPlaybackCapture(抓本机其他 App 的媒体音) → MediaCodec(OPUS 编码)
 *       → 按契约 §4 帧头打包 → UDP 发往 PC audio-sink。
 *
 * 关键点：
 *  - Android 10(API29)+ 才能用 AudioPlaybackCapture；需先经 MediaProjection 授权。
 *  - 受 DRM / ALLOW_CAPTURE_BY_NONE 保护的内容抓不到（架构 R10），此时应降级为“路径B：内置播放器”。
 *  - 蓝牙不参与本链路：手机→PC 走局域网 UDP；PC→蓝牙音箱出声在 PC 侧完成。
 *
 * 用法（伪）：startService(Intent(this, AudioCaptureService::class.java)
 *              .putExtra(EXTRA_RESULT_DATA, mediaProjectionResultData))
 */
class AudioCaptureService : Service() {

    companion object {
        const val EXTRA_RESULT_DATA = "result_data"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val SAMPLE_RATE = 48000
        const val CHANNEL_COUNT = 2
        const val FRAME_MS = 20
        const val BITRATE = 160_000
        const val HEADER_LEN = 16
        const val FLAG_FIRST = 0x01
        const val FLAG_LAST = 0x02
    }

    @Volatile private var running = false
    private var thread: Thread? = null

    override fun onBind(i: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val resultData = intent?.getParcelableExtra<Intent>(EXTRA_RESULT_DATA)
        val host = intent?.getStringExtra(EXTRA_HOST) ?: return START_NOT_SUPPORTED
        val port = intent.getIntExtra(EXTRA_PORT, 51000)
        startForeground(1, buildNotification())

        val mpm = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        val projection = mpm.getMediaProjection(Activity.RESULT_OK, resultData!!)
        running = true
        thread = Thread { captureLoop(projection, host, port) }.also { it.start() }
        return START_STICKY
    }

    private fun captureLoop(projection: MediaProjection, host: String, port: Int) {
        // 1) 抓取“其他应用”的播放音频（排除自己）
        val config = android.media.AudioPlaybackCaptureConfiguration(projection)
            .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
            .addMatchingUsage(AudioAttributes.USAGE_GAME)
            .build()

        val fmt = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setSampleRate(SAMPLE_RATE)
            .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
            .build()

        val minBuf = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT)
        val recorder = AudioRecord.Builder()
            .setAudioFormat(fmt)
            .setBufferSizeInBytes(minBuf * 2)
            .setAudioPlaybackCaptureConfig(config)
            .build()

        // 2) MediaCodec OPUS 编码器
        val opusFormat = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, SAMPLE_RATE, CHANNEL_COUNT).apply {
            setInteger(MediaFormat.KEY_BIT_RATE, BITRATE)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 8192)
            setInteger(MediaFormat.KEY_AAC_PROFILE, 0) // 忽略，占位
        }
        val codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS)
        codec.configure(opusFormat, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        codec.start()

        val socket = DatagramSocket()
        val addr = InetAddress.getByName(host)
        val samplesPerFrame = SAMPLE_RATE * FRAME_MS / 1000      // 960/声道
        val frameBytes = samplesPerFrame * CHANNEL_COUNT * 2     // int16
        val pcm = ByteArray(frameBytes)
        var seq = 0L
        var pts = 0L

        recorder.startRecording()
        try {
            while (running) {
                var off = 0
                while (off < frameBytes && running) {
                    val n = recorder.read(pcm, off, frameBytes - off)
                    if (n <= 0) break
                    off += n
                }
                if (off < frameBytes) continue

                val opus = encodeFrame(codec, pcm, frameBytes) ?: continue

                // 3) 按契约 §4 打包：ver|flags|seq(4)|pts(8)|len(2)|payload
                val dgram = ByteArray(HEADER_LEN + opus.size)
                dgram[0] = 1
                dgram[1] = (FLAG_FIRST or FLAG_LAST).toByte()
                writeBE32(dgram, 2, seq); writeBE64(dgram, 6, pts); writeBE16(dgram, 14, opus.size)
                System.arraycopy(opus, 0, dgram, HEADER_LEN, opus.size)

                socket.send(DatagramPacket(dgram, dgram.size, addr, port))
                seq++; pts += samplesPerFrame
            }
        } finally {
            running = false
            recorder.stop(); recorder.release()
            codec.stop(); codec.release()
            projection.stop()
            socket.close()
        }
    }

    private fun encodeFrame(codec: MediaCodec, pcm: ByteArray, len: Int): ByteArray? {
        val inIdx = codec.dequeueInputBuffer(10_000)
        if (inIdx < 0) return null
        codec.get.getInputBuffer(inIdx)!!.apply { clear(); put(pcm, 0, len) }
        codec.queueInputBuffer(inIdx, 0, len, System.nanoTime() / 1000, 0)

        val info = MediaCodec.BufferInfo()
        val outIdx = codec.dequeueOutputBuffer(info, 10_000)
        if (outIdx < 0) return null
        val out = ByteArray(info.size)
        codec.getOutputBuffer(outIdx)!!.apply { position(info.offset); get(out) }
        codec.releaseOutputBuffer(outIdx, false)
        return out
    }

    private fun writeBE16(b: ByteArray, o: Int, v: Int) { b[o] = (v ushr 8).toByte(); b[o+1] = v.toByte() }
    private fun writeBE32(b: ByteArray, o: Int, v: Long) { for (i in 0..3) b[o+i] = (v ushr (8*(3-i))).toByte() }
    private fun writeBE64(b: ByteArray, o: Int, v: Long) { for (i in 0..7) b[o+i] = (v ushr (8*(7-i))).toByte() }

    private fun buildNotification(): Notification =
        Notification.Builder(this, "audio")
            .setContentTitle("SongLink 音频串流中")
            .setSmallIcon(android.R.drawable.stat_sys_headset)
            .build()

    override fun onDestroy() { running = false; thread?.join(500); super.onDestroy() }
}
