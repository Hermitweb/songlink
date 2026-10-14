package songlink.remote.ui.theme

import android.app.Activity
import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import androidx.core.view.WindowCompat

// 亮色配色(默认):与 Web 面板对齐
private val LightColors = lightColorScheme(
    primary = BrandBlue,
    onPrimary = BrandBgCard,
    primaryContainer = BrandBlueSoft,
    onPrimaryContainer = BrandBlueDeep,
    secondary = BrandBlueGrey,
    onSecondary = BrandBgCard,
    secondaryContainer = BrandBgSoft,
    onSecondaryContainer = BrandText,
    tertiary = BrandSuccess,
    background = BrandBg,
    onBackground = BrandText,
    surface = BrandBgCard,
    onSurface = BrandText,
    surfaceVariant = BrandBgSoft,
    onSurfaceVariant = BrandTextMute,
    outline = BrandBorder,
    outlineVariant = BrandBorder,
    error = BrandDanger,
    onError = BrandBgCard,
    errorContainer = BrandDangerSoft,
    onErrorContainer = BrandDanger,
)

// 暗色配色(可选)
private val DarkColors = darkColorScheme(
    primary = BrandBlueDark,
    onPrimary = BrandTextDark,
    primaryContainer = BrandBlueSoftDark,
    onPrimaryContainer = BrandBlueDark,
    secondary = BrandTextMuteDark,
    onSecondary = BrandTextDark,
    secondaryContainer = BrandBgSoftDark,
    onSecondaryContainer = BrandTextDark,
    tertiary = BrandSuccessDark,
    background = BrandBgDark,
    onBackground = BrandTextDark,
    surface = BrandBgCardDark,
    onSurface = BrandTextDark,
    surfaceVariant = BrandBgSoftDark,
    onSurfaceVariant = BrandTextMuteDark,
    outline = BrandBorderDark,
    outlineVariant = BrandBorderDark,
    error = BrandDangerDark,
    onError = BrandTextDark,
    errorContainer = BrandDangerSoftDark,
    onErrorContainer = BrandDangerDark,
)

// 排版:稍大的标题/正文,配合大按钮
private val SongLinkTypography = Typography(
    titleLarge = TextStyle(fontSize = 22.sp, fontWeight = FontWeight.SemiBold),
    titleMedium = TextStyle(fontSize = 18.sp, fontWeight = FontWeight.SemiBold),
    titleSmall = TextStyle(fontSize = 15.sp, fontWeight = FontWeight.Medium),
    bodyLarge = TextStyle(fontSize = 16.sp),
    bodyMedium = TextStyle(fontSize = 14.sp),
    bodySmall = TextStyle(fontSize = 12.sp),
    labelLarge = TextStyle(fontSize = 14.sp, fontWeight = FontWeight.Medium),
    labelMedium = TextStyle(fontSize = 12.sp, fontWeight = FontWeight.Medium),
    labelSmall = TextStyle(fontSize = 11.sp, fontWeight = FontWeight.Medium),
)

@Composable
fun SongLinkTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit
) {
    val colorScheme = if (darkTheme) DarkColors else LightColors

    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = (view.context as Activity).window
            window.statusBarColor = colorScheme.surface.toArgb()
            WindowCompat.getInsetsController(window, view).isAppearanceLightStatusBars = !darkTheme
        }
    }

    MaterialTheme(
        colorScheme = colorScheme,
        typography = SongLinkTypography,
        content = content
    )
}
