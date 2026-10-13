package songlink.remote.data.audio

import android.content.Context
import android.media.MediaRecorder
import android.os.Build
import java.io.IOException
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

/**
 * 手机音频采集器（基于 MediaRecorder）
 * 替代：MediaAudioTrack (Android 12+) 
 */
class PhoneAudioCapturer(private val context: Context) {
    
    private var mediaRecorder: MediaRecorder? = null
    private var isRecording = false
    
    /**
     * 开始音频录制并发送到目标 UDP 端口
     */
    suspend fun startCapture(port: Int, callback: (Boolean, String?) -> Unit) = 
        suspendCoroutine { continuation ->
            try {
                mediaRecorder = MediaRecorder().apply {
                    setAudioSource(MediaRecorder.AudioSource.VOICE_CALL) // 通话/系统音频
                    setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
                    setAudioEncoder(MediaRecorder.AudioEncoder.AAC)
                    setAudioSamplingRate(48000)
                    setAudioEncodingBitRate(128000)
                    
                    // 注意：实际需设置 UDP 输出流
                    // setOutputFile() 需要 FileDescriptor
                    
                    prepare()
                    start()
                }
                
                isRecording = true
                continuation.resume(true)
                
            } catch (e: IOException) {
                isRecording = false
                continuation.resumeWith(Result.failure(e))
            }
        }
    
    /**
     * 停止录制
     */
    fun stopCapture() {
        mediaRecorder?.apply {
            if (isRecording) {
                stop()
                release()
                isRecording = false
            }
        }
        mediaRecorder = null
    }
    
    /**
     * 检查运行时权限（简化版）
     */
    fun checkPermissions(): Boolean {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            true // TODO: 请求 CAPTURE_AUDIO_OUTPUT 权限
        } else {
            true
        }
    }
}
