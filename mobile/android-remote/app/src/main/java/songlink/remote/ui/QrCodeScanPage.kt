package songlink.remote.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.QrCode
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.navigation.NavController
import com.google.accompanist.permissions.ExperimentalPermissionsApi
import com.google.accompanist.permissions.isGranted
import com.google.accompanist.permissions.rememberPermissionState
import com.google.zxing.BinaryBitmap
import com.google.zxing.LuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanIntentResult
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * QR Code 扫描页面 - 自动填充连接信息
 *
 * 摄像头预览由 zxing-android-embedded 自带的 CaptureActivity 提供（库内已含 CameraX），
 * 本页面只负责：相机权限 -> 拉起扫描 Activity -> 用 [parseQrData] 解析出 host / pin。
 */
@OptIn(ExperimentalPermissionsApi::class, ExperimentalMaterial3Api::class)
@Composable
fun QrCodeScanPage(
    modifier: Modifier = Modifier,
    onScanSuccess: (String, String) -> Unit, // hostUrl, pin
    onManualInput: () -> Unit, // 手动输入连接信息（跳配对页）
    navController: NavController
) {
    // Camera permission
    val cameraPermission = rememberPermissionState(android.Manifest.permission.CAMERA)

    var error by remember { mutableStateOf<String?>(null) }
    // 只自动拉起一次；用户取消后可以手动点"重新扫描"
    var autoLaunched by remember { mutableStateOf(false) }

    // 扫描配置：只要二维码，竖屏取景框，关闭提示音
    val scanOptions = remember {
        ScanOptions().apply {
            setDesiredBarcodeFormats(ScanOptions.QR_CODE)
            setPrompt("请将二维码对准取景框")
            setBeepEnabled(false)
            setOrientationLocked(true)
            setCameraId(0) // 0 = 后置摄像头
        }
    }

    val scanLauncher = rememberLauncherForActivityResult(ScanContract()) { result: ScanIntentResult ->
        val contents = result.contents
        if (contents.isNullOrEmpty()) {
            // 用户按了返回键取消，不算错误
            return@rememberLauncherForActivityResult
        }
        val qrData = parseQrData(contents)
        if (qrData != null) {
            error = null
            onScanSuccess(qrData.host, qrData.pin)
        } else {
            error = "二维码内容不是有效的连接信息"
        }
    }

    // 权限授予后自动开始扫描
    LaunchedEffect(cameraPermission.status.isGranted) {
        if (cameraPermission.status.isGranted && !autoLaunched) {
            autoLaunched = true
            scanLauncher.launch(scanOptions)
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
            // 已授权：真实预览由 CaptureActivity 提供，这里只做取景框示意与状态
            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .aspectRatio(1f),
                colors = CardDefaults.cardColors(MaterialTheme.colorScheme.surfaceVariant)
            ) {
                Column(
                    modifier = Modifier.fillMaxSize(),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center
                ) {
                    Icon(
                        Icons.Default.QrCode,
                        contentDescription = null,
                        modifier = Modifier.size(120.dp),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Spacer(modifier = Modifier.height(16.dp))
                    Text(
                        "点击下方按钮打开取景器",
                        style = MaterialTheme.typography.bodyMedium
                    )
                }
            }

            Spacer(modifier = Modifier.height(16.dp))

            Button(onClick = { scanLauncher.launch(scanOptions) }) {
                Text("开始扫码")
            }

            // Error display
            if (error != null) {
                Spacer(modifier = Modifier.height(16.dp))

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
                    scanLauncher.launch(scanOptions)
                }) {
                    Text("重新扫描")
                }
            }

            Spacer(modifier = Modifier.weight(1f))

            Button(
                onClick = onManualInput
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
 * 返回 null 表示解码失败。
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
