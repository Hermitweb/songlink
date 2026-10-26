package songlink.remote.data.transport

import android.content.Context
import android.media.projection.MediaProjection
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import songlink.remote.data.audio.PhoneAudioCapturer
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 音频传输管理器（架构 §3.9 audio:start）：封装 [PhoneAudioCapturer] + RTT 统计上报。
 *
 * 真机启动顺序：
 *   1. UI Activity 拿到 MediaProjection 后调用 [bindMediaProjection]
 *   2. 调用 [startAudioCapture](host, port) 触发 PCM 采集 + UDP 推送
 *   3. UI 通过 [getLatency] 拿 RTT 估算（每帧 ~20 ms 估算 + 网络 RTT）
 *
 * 当前 commit 范围：
 *   - UDP 推送路径 + §4 封帧已 work（[PhoneAudioCapturer.assemblePcmFrame]）
 *   - Opus 编码（2026-10-26 起）：[OpusJni] 有 native 库时走 Opus（flags bit2=0），
 *     没有 NDK 的机器自动回退裸 PCM 直通（flags bit2=1，Host 已支持两条路径）
 *   - 真机 end-to-end 验证需要：MediaProjection 用户授权 + PC Host 启动 AudioStreamSink
 *     （C# 端兼容 PCM 直通需要单独 PR 扩 [AudioStreamWire]）
 *
 * CI/JVM 单测：可调 [startAudioCapture]，未 bindMediaProjection 时 [PhoneAudioCapturer]
 * 会回 false + "NEED_PROJECTION"——单测断言该错误路径即可。
 */
@Singleton
class AudioTransportManager @Inject constructor(
    @ApplicationContext context: Context
) {

    private val capturer = PhoneAudioCapturer(context)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    @Volatile
    private var running = false

    @Volatile
    private var remoteHost: String = "127.0.0.1"

    @Volatile
    private var remotePort: Int = 51_000

    val isRunning: Boolean get() = running

    /** UI 授权 MediaProjection 后调用。token 失效前有效（API 30+ 重新申请）。 */
    fun bindMediaProjection(projection: MediaProjection) {
        capturer.captureProjection(projection)
    }

    /**
     * 启动音频采集并按 §4 封帧 UDP 推送。
     * @param host 目标 PC IP
     * @param port PC UDP 监听端口（与 C# AudioStreamSink.LocalPort 协商一致）
     */
    fun startAudioCapture(host: String, port: Int, callback: (Boolean, String?) -> Unit) {
        remoteHost = host
        remotePort = port
        // 真正发包的是 capturer（它用自己的 remoteHost/remotePort 字段组 datagram 目标）。
        // 只写本类字段的话，帧会发往 capturer 默认值 127.0.0.1 —— 真机实测 PC 侧 0 收包。
        capturer.remoteHost = host
        capturer.remotePort = port
        scope.launch {
            try {
                capturer.startCapture(port) { ok, err ->
                    running = ok
                    callback(ok, err)
                }
            } catch (e: Exception) {
                running = false
                callback(false, e.message ?: "启动采集失败")
            }
        }
    }

    /** 停止音频采集（不取消 MediaProjection；UI 生命周期管理）。 */
    fun stopAudioCapture() {
        running = false
        capturer.stopCapture()
    }

    /** RTT 估算（毫秒）。PCM 直通模式下为本地封帧时延占位 + 网络单向延迟。
     *  真实 RTT 需要 Host 回 echo 心跳；当前未实现回环，仅按帧时长算。 */
    fun getLatency(): Int = capturer.getLastRoundTripMs().toInt()
}
