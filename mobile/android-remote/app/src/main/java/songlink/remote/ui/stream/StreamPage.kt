package songlink.remote.ui.stream

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import songlink.remote.R
import songlink.remote.ui.capture.CaptureProjectionActivity

/**
 * 音频串流页面（PHONE_STREAM 模式入口）
 * 用于手机内置播放器音频抓取并传输到 PC Host
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun StreamPage(
    modifier: Modifier = Modifier,
    viewModel: StreamViewModel = hiltViewModel(),
    onBack: () -> Unit
) {
    val context = LocalContext.current
    var hasPermission by remember { mutableStateOf(false) }
    var isStreaming by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    
    // 申请权限（Android 12+ 需要 CAPTURE_AUDIO_OUTPUT）
    val permissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestMultiplePermissions()
    ) { permissions ->
        hasPermission = permissions.all { it.value }
    }

    // Android Q+ 真实 MediaProjection 授权: 系统弹出"截屏/录屏"权限窗
    val projectionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.StartActivityForResult()
    ) { _ ->
        // CaptureProjectionActivity 拿到权限后自己 finish() + 启前台服务,这里只更新 UI
        isStreaming = true
        errorMessage = "MediaProjection 已授权,前台服务运行中"
    }
    
    LaunchedEffect(Unit) {
        // 检查运行时权限
        // 注: CAPTURE_AUDIO_OUTPUT 实际不是 Android 公开权限;Android Q+ 用 MediaProjection 授权音频采集
        // 这里申请 RECORD_AUDIO + MODIFY_AUDIO_SETTINGS 即可,MediaProjection 单独走 CaptureProjectionActivity
        val toGrant = mutableListOf(
            Manifest.permission.RECORD_AUDIO,
            Manifest.permission.MODIFY_AUDIO_SETTINGS
        )
        // Android 13+ 加 POST_NOTIFICATIONS (前台服务通知需要)
        if (Build.VERSION.SDK_INT >= 33) {
            toGrant += android.Manifest.permission.POST_NOTIFICATIONS
        }

        val pending = toGrant.filter {
            androidx.core.content.ContextCompat.checkSelfPermission(
                context, it
            ) != android.content.pm.PackageManager.PERMISSION_GRANTED
        }
        if (pending.isNotEmpty()) {
            permissionLauncher.launch(pending.toTypedArray())
        } else {
            hasPermission = true
        }
    }
    
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("音频串流") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.Default.ArrowBack, contentDescription = "返回")
                    }
                }
            )
        }
    ) { padding ->
        Column(
            modifier = modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState()),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Spacer(modifier = Modifier.height(32.dp))
            
            Text(
                text = "将手机音频传输到电脑",
                style = MaterialTheme.typography.headlineMedium,
                color = MaterialTheme.colorScheme.primary
            )
            
            Spacer(modifier = Modifier.height(8.dp))
            
            Text(
                text = "播放手机上的音乐 → 声音经网络传 PC → PC 蓝牙音箱出",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            
            Spacer(modifier = Modifier.height(32.dp))
            
            // 当前状态卡片
            Card(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                colors = CardDefaults.cardColors(
                    containerColor = if (isStreaming) 
                        MaterialTheme.colorScheme.primaryContainer 
                    else 
                        MaterialTheme.colorScheme.surfaceVariant
                )
            ) {
                Column(
                    modifier = Modifier.padding(16.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Text(
                        text = if (isStreaming) "串流进行中" else "等待开始",
                        style = MaterialTheme.typography.titleLarge,
                        color = if (isStreaming)
                            MaterialTheme.colorScheme.onPrimaryContainer
                        else
                            MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    
                    Spacer(modifier = Modifier.height(8.dp))
                    
                    Text(
                        text = "延迟：~200ms | 带宽：~128kbps",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(24.dp))
            
            // 操作按钮
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.Center
            ) {
                Button(
                    onClick = {
                        if (!hasPermission) {
                            errorMessage = "需要先授予录音权限"
                            permissionLauncher.launch(arrayOf(
                                Manifest.permission.RECORD_AUDIO,
                                Manifest.permission.MODIFY_AUDIO_SETTINGS
                            ))
                        } else if (!isStreaming) {
                            // Android Q+ 真实 MediaProjection 授权: 跳到 CaptureProjectionActivity
                            val intent = CaptureProjectionActivity.newIntent(context, "192.168.15.205", 51000)
                            projectionLauncher.launch(intent)
                            viewModel.startStreaming(51000) { success, err ->
                                if (!success) errorMessage = "VM 启动提示: $err"
                            }
                        }
                    },
                    enabled = hasPermission
                ) {
                    Icon(Icons.Default.PlayArrow, contentDescription = null)
                    Spacer(modifier = Modifier.width(8.dp))
                    Text("开始串流")
                }
                
                Spacer(modifier = Modifier.width(16.dp))
                
                ElevatedButton(
                    onClick = {
                        viewModel.stopStreaming()
                        isStreaming = false
                        errorMessage = null
                    },
                    enabled = isStreaming,
                    colors = ButtonDefaults.elevatedButtonColors(
                        containerColor = MaterialTheme.colorScheme.errorContainer
                    )
                ) {
                    Icon(Icons.Default.Stop, contentDescription = null)
                    Spacer(modifier = Modifier.width(8.dp))
                    Text("停止串流")
                }
            }
            
            Spacer(modifier = Modifier.height(24.dp))
            
            // 错误提示
            errorMessage?.let { error ->
                Card(
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.errorContainer
                    )
                ) {
                    Text(
                        text = error,
                        modifier = Modifier.padding(16.dp),
                        color = MaterialTheme.colorScheme.onErrorContainer
                    )
                }
                
                Spacer(modifier = Modifier.height(16.dp))
            }
            
            // 使用说明
            Card(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp).padding(bottom = 16.dp),
                colors = CardDefaults.cardColors(MaterialTheme.colorScheme.surfaceVariant)
            ) {
                Column(
                    modifier = Modifier.padding(16.dp)
                ) {
                    Text(
                        text = "使用说明",
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.Bold
                    )
                    
                    Spacer(modifier = Modifier.height(8.dp))
                    
                    Text(
                        text = """
                        1. 点击“开始串流”授权后使用
                        2. 在手机任意音乐 App 播放音乐
                        3. 确保电脑端 SongLink Host 已启动
                        4. 声音将通过 PC 蓝牙音箱输出

                        注意:部分受 DRM 保护的内容可能无法抓取
""".trimIndent(),
                        style = MaterialTheme.typography.bodyMedium
                    )
                }
            }
            
            Spacer(modifier = Modifier.weight(1f))
        }
    }
}
