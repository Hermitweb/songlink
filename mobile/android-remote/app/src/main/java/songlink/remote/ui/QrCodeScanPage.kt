package songlink.remote.ui

import android.graphics.BitmapFactory
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.QrCode
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.navigation.NavController
import com.google.accompanist.permissions.ExperimentalPermissionsApi
import com.google.accompanist.permissions.isGranted
import com.google.accompanist.permissions.rememberPermissionState
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.LuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * QR Code 扫描页面 - 自动填充连接信息
 *
 * 注意（AI 生成占位）：摄像头预览尚未接入 CameraX，
 * 当前页面只负责权限申请 + 展示 UI；真正解码走 [decodeQRCode] 的 ZXing 路径。
 */
@OptIn(ExperimentalPermissionsApi::class, ExperimentalMaterial3Api::class)
@Composable
fun QrCodeScanPage(
    modifier: Modifier = Modifier,
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
                val bitmap = withContext(Dispatchers.IO) {
                    context.contentResolver.openInputStream(uri).use { BitmapFactory.decodeStream(it) }
                }

                // Decode QR code
                val result = decodeQRCode(bitmap)

                if (result != null && result.barcodeFormat == BarcodeFormat.QR_CODE) {
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
                    capturedUri = null
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

/**
 * 解析 Web 面板 QR modal 生成的二维码内容。
 * 格式:JSON {"wsUrl":"ws://host:port/","pin":"6digits"} — 由 Host /qr 端点编码。
 * 旧版 "host+pin" 字段也兼容(早期 Host 用过)。
 */
fun parseQrData(json: String): QrData? {
    try {
        val obj = Json.parseToJsonElement(json).jsonObject
        // 新版:wsUrl + pin
        val wsUrl = obj["wsUrl"]?.jsonPrimitive?.content
        val pin = obj["pin"]?.jsonPrimitive?.content
        if (!wsUrl.isNullOrEmpty() && !pin.isNullOrEmpty()) {
            // wsUrl 形如 "ws://192.168.15.205:51999/" → 保留给连接逻辑直接用
            return QrData(wsUrl, pin)
        }
        // 旧版兼容
        val host = obj["host"]?.jsonPrimitive?.content
        if (!host.isNullOrEmpty() && !pin.isNullOrEmpty()) {
            return QrData(host, pin)
        }
        return null
    } catch (e: Exception) {
        return null
    }
}

/**
 * 用 ZXing core 解码 Bitmap 中的二维码。
 * 返回 null 表示解码失败（AI 生成：替换原文件中不存在的 BitmapLuminanceSource/BinaryBitmap 调用）。
 */
fun decodeQRCode(bitmap: android.graphics.Bitmap?): com.google.zxing.Result? {
    if (bitmap == null) return null
    return try {
        val source = BitmapLuminanceSource(bitmap)
        QRCodeReader().decode(BinaryBitmap(HybridBinarizer(source)))
    } catch (e: Exception) {
        e.printStackTrace()
        null
    }
}

/** 把 Bitmap 转成 ZXing 的灰度源 */
private class BitmapLuminanceSource(bitmap: android.graphics.Bitmap) :
    LuminanceSource(bitmap.width, bitmap.height) {

    private val luminances: ByteArray = run {
        val w = bitmap.width
        val h = bitmap.height
        val pixels = IntArray(w * h)
        bitmap.getPixels(pixels, 0, w, 0, 0, w, h)
        ByteArray(w * h) { i ->
            val p = pixels[i]
            val r = (p shr 16) and 0xFF
            val g = (p shr 8) and 0xFF
            val b = p and 0xFF
            // Rec.601 luma
            ((r * 33 + g * 34 + b * 33) / 100).toByte()
        }
    }

    override fun getRow(y: Int, row: ByteArray?): ByteArray {
        val out = row ?: ByteArray(width)
        val offset = y * width
        System.arraycopy(luminances, offset, out, 0, width)
        return out
    }

    override fun getMatrix(): ByteArray = luminances
}