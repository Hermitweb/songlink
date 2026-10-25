package songlink.remote.ui.stream

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import songlink.remote.data.audio.MetadataReporter
import songlink.remote.data.audio.MetadataStatus
import songlink.remote.data.repo.SessionManager
import songlink.remote.data.transport.AudioTransportManager
import javax.inject.Inject
import dagger.hilt.android.lifecycle.HiltViewModel

/**
 * 音频串流 ViewModel（STATE MANAGEMENT）
 */
@HiltViewModel
class StreamViewModel @Inject constructor(
    private val audioTransportManager: AudioTransportManager,
    private val session: SessionManager,
    private val metadataReporter: MetadataReporter
) : ViewModel() {

    // 当前连接状态
    var isConnected by mutableStateOf(false)
        private set

    var streamUrl by mutableStateOf("")
        private set

    /** 没连上 Host 就没有串流目标，UI 据此禁用"开始串流"。 */
    val targetHost: String? get() = session.hostAddress

    /**
     * 元数据上报状态。电脑面板要显示"手机上这首歌"的歌词和封面，
     * 全靠这条 srcMeta 链路；读不到时要如实告诉用户原因，别让面板一直空着。
     */
    val metadataStatus: StateFlow<MetadataStatus> = metadataReporter.status

    private var streamingJob: Job? = null

    /**
     * 开始音频串流到 Host 的指定端口。
     *
     * @param port PC Host UDP 监听端口（默认 51000）
     * @param callback (success, error) 回调
     */
    fun startStreaming(port: Int, callback: (Boolean, String?) -> Unit) {
        viewModelScope.launch {
            try {
                // 目标是**那台跑着点歌台的电脑**，即用户配对时填的地址。
                // 以前这里取的是手机自己的局域网 IP，音频等于发回本机，永远送不到电脑。
                val hostIp = targetHost ?: run {
                    callback(false, "还没连上点歌台，请先在首页完成配对")
                    return@launch
                }

                streamUrl = "udp://$hostIp:$port"
                isConnected = true

                // 开始把"手机上正在放哪首歌"上报给电脑 —— 电脑面板靠这条 srcMeta
                // 才能显示对应的歌词和封面。不开的话音频过去了，电脑上却还是空白的。
                metadataReporter.start()

                // 启动音频采集器（Android 端实现）
                audioTransportManager.startAudioCapture(hostIp, port) { success, err ->
                    if (success) {
                        callback(true, null)
                    } else {
                        callback(false, err ?: "启动失败")
                    }
                }

                // 持续 Ping 检测连接健康度
                streamingJob = launch {
                    while (isConnected) {
                        delay(1000) // 每秒检查
                        val latency = audioTransportManager.getLatency()
                        if (latency > 1000) { // >1s 延迟视为断开
                            isConnected = false
                            callback(false, "连接超时")
                        }
                    }
                }

            } catch (e: Exception) {
                callback(false, e.message ?: "未知错误")
            }
        }
    }
    
    /**
     * 停止音频串流
     */
    fun stopStreaming() {
        isConnected = false
        streamingJob?.cancel()
        audioTransportManager.stopAudioCapture()
        metadataReporter.stop()
        streamUrl = ""
    }
}
