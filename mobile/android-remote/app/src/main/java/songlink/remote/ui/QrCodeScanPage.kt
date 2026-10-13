package songlink.remote.ui

import android.net.Uri
import androidx.camera.core.CameraSelector
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.QrCode
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.navigation.NavController
import com.google.accompanist.permissions.ExperimentalPermissionsApi
import com.google.accompanist.permissions.isGranted
import com.google.accompanist.permissions.rememberPermissionState
import com.journeyapps.bardecoder.BarcodeResult
import com.journeyapps.barcodescanner.DecodedBitmap
import com.journeyapps.barcodescanner.DefaultDecoder
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.tensorflow.lite.Interpreter
import java.io.FileInputStream
import java.nio.MappedByteBuffer
import java.nio.channels.FileChannel

/**
 * QR Code 扫描页面 - 自动填充连接信息
 */
@OptIn(ExperimentalPermissionsApi::class)
@Composable
fun QrCodeScanPage(
    modifier: Modifier = Modifier,
    viewModel: PairViewModel = hiltViewModel(),
    onScanSuccess: (String, String) -> Unit, // hostUrl, pin
    navController: NavController
) {
    val context = LocalContext.current
    
    // Camera permission
    val cameraPermission = rememberPermissionState(android.Manifest.permission.CAMERA)
    
    var capturedUri by remember { mutableStateOf<Uri?>(null) }
    var scanning by remember { mutableStateOf(true) }
    var decodedData by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    
    LaunchedEffect(capturedUri) {
        capturedUri?.let { uri ->
            try {
                val inputStream = context.contentResolver.openInputStream(uri)
                val bitmap = BitmapFactory.decodeStream(inputStream)
                
                // Decode QR code
                val decoder = DefaultDecoder()
                val result = decodeQRCode(bitmap)
                
                if (result != null && result.format == BarcodeResult.QR_CODE) {
                    decodedData = result.text
                    
                    // Parse JSON data
                    val qrData = parseQrData(result.text)
                    if (qrData != null) {
                        onScanSuccess(qrData.host, qrData.pin)
                        scanning = false
                    } else {
                        error = "无效的 QR 数据格式"
                    }
                } else {
                    error = "未识别为有效 QR Code"
                }
            } catch (e: Exception) {
                error = "扫码失败：${e.message}"
            }
        }
    }
    
    Column(
        modifier = modifier
            .fillMaxSize()
            .padding(16.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        TopAppBar(
            title = { Text("扫码连接") },
            navigationIcon = {
                IconButton(onClick = { navController.popBackStack() }) {
                    Icon(Icons.Default.ArrowBack, contentDescription = "返回")
                }
            }
        )
        
        Spacer(modifier = Modifier.height(16.dp))
        
        Text(
            text = "请使用手机摄像头扫描二维码",
            style = MaterialTheme.typography.bodyLarge,
            modifier = Modifier.padding(bottom = 8.dp)
        )
        
        Text(
            text = "二维码应包含 Host URL 和 PIN 码",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        
        Spacer(modifier = Modifier.height(32.dp))
        
        if (!cameraPermission.status.isGranted) {
            Card(
                modifier = Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(MaterialTheme.colorScheme.surfaceVariant)
            ) {
                Column(
                    modifier = Modifier.padding(16.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Icon(
                        Icons.Default.QrCode,
                        contentDescription = null,
                        modifier = Modifier.size(48.dp),
                        tint = MaterialTheme.colorScheme.primary
                    )
                    
                    Spacer(modifier = Modifier.height(8.dp))
                    
                    Text("需要相机权限以扫描二维码")
                    
                    Spacer(modifier = Modifier.height(8.dp))
                    
                    Button(onClick = { cameraPermission.launchPermissionRequest() }) {
                        Text("授予权限")
                    }
                }
            }
        } else {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f)
                    .aspectRatio(1f)
                    .border(2.dp, MaterialTheme.colorScheme.primary, MaterialTheme.shapes.medium)
            ) {
                Surface(
                    modifier = Modifier.matchParentSize(),
                    color = MaterialTheme.colorScheme.onSurface
                ) {
                    Text(
                        "正在初始化摄像头...",
                        modifier = Modifier.padding(16.dp)
                    )
                }
                
                // Overlay for QR scan area (简化版)
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(Color.Black.copy(alpha = 0.3f)),
                    contentAlignment = Alignment.Center
                ) {
                    Box(
                        modifier = Modifier
                            .size(250.dp)
                            .background(Color.Transparent)
                    )
                }
            }
            
            Spacer(modifier = Modifier.height(16.dp))
            
            // Error display
            if (error != null) {
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(MaterialTheme.colorScheme.errorContainer)
                ) {
                    Text(
                        text = error!!,
                        modifier = Modifier.padding(16.dp),
                        color = MaterialTheme.colorScheme.onErrorContainer
                    )
                }
                
                Spacer(modifier = Modifier.height(16.dp))
                
                Button(onClick = { 
                    error = null
                    scannedUri = null
                }) {
                    Text("重新扫描")
                }
            }
            
            Spacer(modifier = Modifier.weight(1f))
            
            Button(
                onClick = { /* 手动输入逻辑 */ },
                enabled = false
            ) {
                Text("或手动输入连接信息")
            }
        }
    }
}

// QR Data parsing
data class QrData(
    val host: String,
    val pin: String
)

fun parseQrData(json: String): QrData? {
    try {
        val jsonStr = Json.parseToJsonElement(json).jsonObject
        val host = jsonStr["host"]?.jsonPrimitive.content
        val pin = jsonStr["pin"]?.jsonPrimitive.content
        
        return when {
            host != null && pin != null -> QrData(host, pin)
            else -> null
        }
    } catch (e: Exception) {
        e.printStackTrace()
        return null
    }
}

// Simplified QR decoding (replace with real ZXing implementation)
fun decodeQRCode(bitmap: android.graphics.Bitmap?): Result<BarcodeResult?> {
    // Placeholder: In production, use ZXing or ML Kit
    return try {
        val zxingReader = com.google.zxing.qrcode.QRCodeReader()
        val source = BitmapLuminanceSource(bitmap)
        val binaryBitmap = BinaryBitmap(TrueBarcodeHandler(source))
        val result = zxingReader.decode(binaryBitmap)
        Result.success(result)
    } catch (e: Exception) {
        Result.failure(e)
    }
}

// Mock classes for demonstration
data class Result<T>(private val value: T, private val exception: Throwable? = null) {
    companion object {
        fun <T> success(value: T) = Result(value)
        fun <T> failure(exception: Throwable) = Result<T?>(null, exception) as Result<T>
    }
    val exception get() = exception
}
