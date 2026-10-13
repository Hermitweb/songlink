package songlink.remote.data.transport

import android.content.Context
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import songlink.remote.data.audio.PhoneAudioCapturer
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 音频传输管理器（AI 生成占位实现：原工程只有调用方，缺实现）
 *
 * 当前用 [PhoneAudioCapturer]（MediaRecorder）做采集，尚未实现 UDP 推流与 RTT 统计，
 * 因此 [getLatency] 恒为 0。
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
    private var latencyMs = 0

    val isRunning: Boolean get() = running

    /**
     * 启动音频采集。
     * @param port PC Host UDP 监听端口
     * @param callback (success, error)
     */
    fun startAudioCapture(port: Int, callback: (Boolean, String?) -> Unit) {
        scope.launch {
            try {
                // PhoneAudioCapturer 自带一个未使用的 callback 形参，这里传空实现，
                // 结果统一由本类的 callback 对外回调
                capturer.startCapture(port) { _, _ -> }
                running = true
                latencyMs = 0
                callback(true, null)
            } catch (e: Exception) {
                running = false
                callback(false, e.message ?: "启动音频采集失败")
            }
        }
    }

    /** 停止音频采集 */
    fun stopAudioCapture() {
        running = false
        capturer.stopCapture()
    }

    /** 当前链路延迟（毫秒）。占位实现，尚未实现 RTT 探测，恒返回 0。 */
    fun getLatency(): Int = latencyMs
}