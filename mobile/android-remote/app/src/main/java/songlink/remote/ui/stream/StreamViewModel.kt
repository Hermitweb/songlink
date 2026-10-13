package songlink.remote.ui.stream

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import songlink.remote.data.transport.AudioTransportManager
import javax.inject.Inject
import dagger.hilt.android.lifecycle.HiltViewModel
import java.net.Inet4Address
import java.net.NetworkInterface

/**
 * 音频串流 ViewModel（STATE MANAGEMENT）
 */
@HiltViewModel
class StreamViewModel @Inject constructor(
    private val audioTransportManager: AudioTransportManager
) : ViewModel() {
    
    // 当前连接状态
    var isConnected by mutableStateOf(false)
        private set
    
    var streamUrl by mutableStateOf("")
        private set
    
    private var streamingJob: Job? = null
    
    /**
     * 开始音频串流到指定端口
     * @param port PC Host UDP 监听端口（默认 51000）
     * @param callback (success, error) 回调
     */
    fun startStreaming(port: Int, callback: (Boolean, String?) -> Unit) {
        viewModelScope.launch {
            try {
                val hostIp = getLocalIpAddress() ?: run {
                    callback(false, "无法获取本机 IP")
                    return@launch
                }
                
                streamUrl = "udp://$hostIp:$port"
                isConnected = true
                
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
        streamUrl = ""
    }
    
    /**
     * 获取本地 IP 地址（局域网可访问）
     *
     * 取第一个可用（非回环、非虚拟接口）的 IPv4 地址。
     */
    private suspend fun getLocalIpAddress(): String? {
        return withContext(Dispatchers.IO) {
            runCatching {
                val candidates = NetworkInterface.getNetworkInterfaces()
                    ?.asSequence()
                    ?.filter { it.isUp && !it.isLoopback && !it.isVirtual }
                    ?.flatMap { it.inetAddresses.asSequence() }
                    ?.filterIsInstance<Inet4Address>()
                    ?.filter { !it.isLoopbackAddress }
                    ?.toList()
                    .orEmpty()

                // 优先局域网地址（PC Host 常在同一局域网），否则退回任意 IPv4
                (candidates.firstOrNull { it.isSiteLocalAddress } ?: candidates.firstOrNull())
                    ?.hostAddress
            }.getOrNull()
        }
    }
}
