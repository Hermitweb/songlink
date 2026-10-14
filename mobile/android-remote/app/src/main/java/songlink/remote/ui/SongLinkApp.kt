package songlink.remote.ui

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
import androidx.compose.material.icons.filled.GraphicEq
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Help
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import songlink.remote.ui.history.HistoryPage
import songlink.remote.ui.stream.StreamPage

object Routes {
    const val HOME = "home"
    const val STREAM = "stream"
    const val HISTORY = "history"
    const val SCAN = "scan"
}

@Composable
fun SongLinkApp() {
    val navController = rememberNavController()
    NavHost(navController = navController, startDestination = Routes.HOME) {
        composable(Routes.HOME) {
            HomePage(
                onStream = { navController.navigate(Routes.STREAM) },
                onHistory = { navController.navigate(Routes.HISTORY) },
                onScan = { navController.navigate(Routes.SCAN) }
            )
        }
        composable(Routes.STREAM) { StreamPage(onBack = { navController.popBackStack() }) }
        composable(Routes.HISTORY) { HistoryPage(onBack = { navController.popBackStack() }) }
        composable(Routes.SCAN) {
            QrCodeScanPage(
                navController = navController,
                onScanSuccess = { _, _ -> navController.popBackStack() }
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
    onStream: () -> Unit,
    onHistory: () -> Unit,
    onScan: () -> Unit
) {
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
            // 标题区
            Text(
                "请选择要使用的功能",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
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
                    .clickable { /* TODO 帮助 */ }
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
