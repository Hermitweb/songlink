package songlink.remote.ui.capture

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import songlink.remote.data.audio.ProjectionService

/**
 * MediaProjection 授权 Activity（架构 §3.9 audio:start / 真机解锁项）
 *
 * 流:
 *   1. 用户在 UI 点击 "授权" -> 调 [MediaProjectionManager.createScreenCaptureIntent()]
 *      弹出系统授权弹窗 (Vivo 上是 Funtouch OS 系统弹窗)。
 *   2. 拿到 resultCode + data 后立刻启动 [ProjectionService] 前台服务,把 result 投过去。
 *   3. Service 调 [PhoneAudioCapturer] 真实 AudioRecord + AudioPlaybackCapture 路径。
 *
 * 入口:
 *   - 由 [StreamPage] / "开始串流" 按钮启动,传入 EXTRA_HOST / EXTRA_PORT
 *   - 完成后 self-finish(),回 Flow 给上层回包
 */
class CaptureProjectionActivity : ComponentActivity() {

    companion object {
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val RESULT_OK = Activity.RESULT_OK
        const val RESULT_DENIED = 2

        fun newIntent(ctx: Context, host: String, port: Int): Intent =
            Intent(ctx, CaptureProjectionActivity::class.java).apply {
                putExtra(EXTRA_HOST, host)
                putExtra(EXTRA_PORT, port)
            }
    }

    private lateinit var projectionLauncher: ActivityResultLauncher<Intent>
    private var host: String = "127.0.0.1"
    private var port: Int = 51_000
    private var status: String by mutableStateOf("点击下方按钮启动系统授权弹窗")

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        host = intent.getStringExtra(EXTRA_HOST) ?: "127.0.0.1"
        port = intent.getIntExtra(EXTRA_PORT, 51_000)

        projectionLauncher = registerForActivityResult(
            ActivityResultContracts.StartActivityForResult()
        ) { result ->
            if (result.resultCode == Activity.RESULT_OK && result.data != null) {
                status = "授权成功 → 启动前台服务 ..."
                ProjectionService.start(
                    this,
                    result.resultCode ?: Activity.RESULT_OK,
                    result.data!!,
                    host, port
                )
                // 等前台服务拿到 token 即可关闭本 Activity
                result.data!!;  // 防止 lint
                setResult(RESULT_OK)
                finish()
            } else {
                status = "未授权 ($result.resultCode), 请重试"
            }
        }

        setContent {
            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    Column(
                        modifier = Modifier.fillMaxSize().padding(24.dp),
                        verticalArrangement = Arrangement.Center,
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        Text(
                            "授权系统媒体投射",
                            style = MaterialTheme.typography.headlineMedium
                        )
                        Spacer(modifier = Modifier.height(12.dp))
                        Text(
                            "SongLink 需要采集系统音频以串流到 PC Host。\n" +
                            "点击下方启动 Android 系统授权弹窗,授权后系统会:",
                            style = MaterialTheme.typography.bodyMedium
                        )
                        Spacer(modifier = Modifier.height(8.dp))
                        Text(
                            "• 展示前台服务通知 (SongLink Remote)\n" +
                            "• 该通知为 Android Q+ 媒体投射的强制要求\n" +
                            "• 目标地址: $host:$port",
                            style = MaterialTheme.typography.bodySmall
                        )
                        Spacer(modifier = Modifier.height(24.dp))
                        Text(
                            status,
                            style = MaterialTheme.typography.bodyLarge,
                            color = MaterialTheme.colorScheme.primary
                        )
                        Spacer(modifier = Modifier.height(24.dp))
                        Button(onClick = ::launchConsent) { Text("启动授权") }
                        Spacer(modifier = Modifier.height(12.dp))
                        Button(onClick = ::cancel) { Text("取消") }
                    }
                }
            }
        }
    }

    private fun launchConsent() {
        val mpm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        // MediaProjectionManager.canProject 是 API 35+, Vivo 是 API 29 不用这个前置判断
        status = "等待系统授权..."
        projectionLauncher.launch(mpm.createScreenCaptureIntent())
    }

    private fun cancel() {
        setResult(RESULT_CANCELED)
        finish()
    }
}
