package songlink.remote.ui

import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.GraphicEq
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.LinkOff
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.Help
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import songlink.remote.data.repo.SessionManager
import songlink.remote.ui.history.HistoryPage
import songlink.remote.ui.pair.PairPage
import songlink.remote.ui.panel.PanelPage
import songlink.remote.ui.panel.rememberSessionManager
import songlink.remote.ui.stream.StreamPage

object Routes {
    const val HOME = "home"
    const val STREAM = "stream"
    const val HISTORY = "history"
    const val SCAN = "scan"

    /** 点歌台主界面（点歌 / 队列 / 歌词三合一）。 */
    const val PANEL = "panel"

    /** 配对页带查询参数的完整路由：pair?host={host}&pin={pin} */
    const val PAIR = "pair?host={host}&pin={pin}"

    /**
     * 拼配对页路由。host / pin 来自二维码，必须 [Uri.encode] —— ws://192.168.1.5:51999/
     * 里的 : 和 / 会破坏 query 解析。
     */
    fun pairRoute(host: String? = null, pin: String? = null): String {
        val h = host?.takeIf { it.isNotBlank() }?.let { Uri.encode(it) } ?: ""
        val p = pin?.takeIf { it.isNotBlank() }?.let { Uri.encode(it) } ?: ""
        return "pair?host=$h&pin=$p"
    }
}

@Composable
fun SongLinkApp() {
    val navController = rememberNavController()
    NavHost(navController = navController, startDestination = Routes.HOME) {
        composable(Routes.HOME) {
            HomePage(
                onPanel = { navController.navigate(Routes.PANEL) },
                onStream = { navController.navigate(Routes.STREAM) },
                onHistory = { navController.navigate(Routes.HISTORY) },
                onScan = { navController.navigate(Routes.SCAN) },
                onPair = { navController.navigate(Routes.pairRoute()) }
            )
        }
        composable(Routes.PANEL) {
            PanelPage(
                onGoPair = { navController.navigate(Routes.pairRoute()) },
                onOpenStream = { navController.navigate(Routes.STREAM) },
                onOpenHistory = { navController.navigate(Routes.HISTORY) }
            )
        }
        composable(Routes.STREAM) { StreamPage(onBack = { navController.popBackStack() }) }
        composable(Routes.HISTORY) { HistoryPage(onBack = { navController.popBackStack() }) }
        composable(Routes.SCAN) {
            QrCodeScanPage(
                navController = navController,
                // 扫到的 host / pin 不再丢弃，带进配对页自动预填并连接
                onScanSuccess = { host, pin ->
                    navController.navigate(Routes.pairRoute(host, pin)) {
                        popUpTo(Routes.SCAN) { inclusive = true }
                    }
                },
                onManualInput = {
                    navController.navigate(Routes.pairRoute()) {
                        popUpTo(Routes.SCAN) { inclusive = true }
                    }
                }
            )
        }
        composable(
            route = Routes.PAIR,
            arguments = listOf(
                navArgument("host") { type = NavType.StringType; defaultValue = "" },
                navArgument("pin") { type = NavType.StringType; defaultValue = "" }
            )
        ) { entry ->
            val args = entry.arguments
            PairPage(
                initialHost = args?.getString("host"),
                initialPin = args?.getString("pin"),
                onBack = { navController.popBackStack() },
                onEnterPanel = {
                    // 配对页留在栈里，返回键能回到这里改配置而不是直接退出 App
                    navController.navigate(Routes.PANEL)
                }
            )
        }
    }
}

/**
 * 主页 — 平铺大卡片,3 个主要功能 + 1 个设置入口
 * 设计目标:大按钮、单屏可读、无 emoji(用 Material vector 图标 + 文字)
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomePage(
    onPanel: () -> Unit,
    onStream: () -> Unit,
    onHistory: () -> Unit,
    onScan: () -> Unit,
    onPair: () -> Unit
) {
    var showHelp by remember { mutableStateOf(false) }
    val session = rememberSessionManager()
    val conn by session.connState.collectAsState()
    val connected = conn is SessionManager.ConnState.Connected

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Box(
                            modifier = Modifier
                                .size(32.dp)
                                .clip(CircleShape)
                                .background(MaterialTheme.colorScheme.primaryContainer),
                            contentAlignment = Alignment.Center
                        ) {
                            Icon(
                                Icons.Default.GraphicEq,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.size(20.dp)
                            )
                        }
                        Spacer(Modifier.width(10.dp))
                        Text("点歌台", fontWeight = FontWeight.SemiBold)
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.surface,
                    titleContentColor = MaterialTheme.colorScheme.onSurface
                )
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(horizontal = 16.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            // 连接状态：先让用户知道能不能点歌，再让他选功能
            Card(
                modifier = Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(
                    if (connected) MaterialTheme.colorScheme.primaryContainer
                    else MaterialTheme.colorScheme.surfaceVariant
                )
            ) {
                Row(
                    modifier = Modifier.padding(14.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Icon(
                        if (connected) Icons.Default.CheckCircle else Icons.Default.LinkOff,
                        contentDescription = null,
                        tint = if (connected) MaterialTheme.colorScheme.primary
                        else MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Spacer(Modifier.width(10.dp))
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            if (connected) "已连接到点歌台" else "还未连接点歌台",
                            style = MaterialTheme.typography.titleSmall,
                            fontWeight = FontWeight.SemiBold
                        )
                        Text(
                            if (connected) "可以直接点歌了" else "连上之后才能搜索和点歌",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    if (!connected) {
                        TextButton(onClick = onPair) { Text("去连接") }
                    }
                }
            }

            // 点歌是本 App 的主功能，放在最上面并给足权重
            HomeTile(
                icon = Icons.Default.MusicNote,
                title = "进入点歌台",
                subtitle = "搜索点歌、看队列、看歌词",
                onClick = onPanel
            )

            // 主功能大卡(垂直堆叠,每个卡占整行)
            HomeTile(
                icon = Icons.Default.GraphicEq,
                title = "音频串流",
                subtitle = "把手机正在播放的音乐同步到电脑",
                onClick = onStream
            )
            HomeTile(
                icon = Icons.Default.QrCodeScanner,
                title = "扫码连接",
                subtitle = "用摄像头扫一下电脑屏幕上的二维码",
                onClick = onScan
            )
            // 不用摄像头（长辈 / 光线差）时走手输路径
            HomeTile(
                icon = Icons.Default.Link,
                title = "手动连接",
                subtitle = "自己输入电脑上的地址和 PIN 码",
                onClick = onPair
            )
            HomeTile(
                icon = Icons.Default.History,
                title = "播放历史",
                subtitle = "查看手机最近点过的歌 / 导出 Excel",
                onClick = onHistory
            )

            Spacer(Modifier.weight(1f))

            // 底部小帮助
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable { showHelp = true }
                    .padding(vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.Center
            ) {
                Icon(
                    Icons.Default.Help,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.size(16.dp)
                )
                Spacer(Modifier.width(6.dp))
                Text(
                    "需要帮助?",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }

        // 帮助说明弹窗
        if (showHelp) {
            AlertDialog(
                onDismissRequest = { showHelp = false },
                title = { Text("怎么用") },
                text = {
                    Column {
                        Text("1. 先在电脑上打开点歌台，点「二维码」会显示一张码和 6 位 PIN。")
                        Spacer(modifier = Modifier.height(6.dp))
                        Text("2. 手机点「扫码连接」，允许相机权限后对准电脑屏幕扫一下，会自动填好并连接。")
                        Spacer(modifier = Modifier.height(6.dp))
                        Text("3. 扫不了就用「手动连接」，把电脑上的 ws:// 地址和 PIN 码手动敲进去。")
                        Spacer(modifier = Modifier.height(6.dp))
                        Text("4. 「音频串流」可以把手机正在放的歌同步到电脑；「播放历史」能导出 Excel。")
                    }
                },
                confirmButton = {
                    TextButton(onClick = { showHelp = false }) {
                        Text("知道了")
                    }
                }
            )
        }
    }
}

/**
 * 主页单卡 — 左侧大图标,右侧标题+描述
 * 圆角 14,浅描边,点击整卡触发
 */
@Composable
fun HomeTile(
    icon: ImageVector,
    title: String,
    subtitle: String,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(14.dp),
        color = MaterialTheme.colorScheme.surface,
        tonalElevation = 0.dp,
        border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.outline)
    ) {
        Row(
            modifier = Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                modifier = Modifier
                    .size(52.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(MaterialTheme.colorScheme.primaryContainer),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    icon,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(28.dp)
                )
            }
            Spacer(Modifier.width(14.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.height(2.dp))
                Text(
                    subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}
