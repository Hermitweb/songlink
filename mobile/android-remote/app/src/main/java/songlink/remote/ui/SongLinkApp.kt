package songlink.remote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import songlink.remote.ui.history.HistoryPage
import songlink.remote.ui.stream.StreamPage

/**
 * 极简导航骨架（AI 生成占位：原工程没有任何入口页面，也没有 NavHost）。
 * 只负责把已有的 StreamPage / HistoryPage / QrCodeScanPage 接起来，不含业务逻辑。
 */
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
        composable(Routes.STREAM) {
            StreamPage(onBack = { navController.popBackStack() })
        }
        composable(Routes.HISTORY) {
            HistoryPage(onBack = { navController.popBackStack() })
        }
        composable(Routes.SCAN) {
            QrCodeScanPage(
                navController = navController,
                onScanSuccess = { _, _ ->
                    navController.popBackStack()
                }
            )
        }
    }
}

@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
private fun HomePage(
    onStream: () -> Unit,
    onHistory: () -> Unit,
    onScan: () -> Unit
) {
    Scaffold(
        topBar = { TopAppBar(title = { Text("SongLink 遥控端") }) }
    ) { padding ->
        Column(
            modifier = Modifier.fillMaxSize().padding(padding).padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Text("请选择功能", style = MaterialTheme.typography.titleMedium)
            Button(onClick = onStream) { Text("音频串流") }
            Button(onClick = onScan) { Text("扫码连接") }
            Button(onClick = onHistory) { Text("播放历史") }
        }
    }
}