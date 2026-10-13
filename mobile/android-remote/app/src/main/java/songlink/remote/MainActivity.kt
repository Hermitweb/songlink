package songlink.remote

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import dagger.hilt.android.AndroidEntryPoint
import songlink.remote.ui.SongLinkApp
import songlink.remote.ui.theme.SongLinkTheme

/**
 * 应用唯一入口（AI 生成：原工程 AndroidManifest 声明了 .MainActivity 但文件缺失）
 */
@AndroidEntryPoint
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            SongLinkTheme {
                SongLinkApp()
            }
        }
    }
}