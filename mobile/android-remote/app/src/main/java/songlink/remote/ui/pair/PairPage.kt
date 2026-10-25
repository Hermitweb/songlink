package songlink.remote.ui.pair

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import songlink.remote.R
import songlink.remote.ui.viewmodel.PairViewModel
import songlink.remote.util.Constants

/**
 * 配对页面 — 输入 Host 地址 + 6 位 PIN，完成握手。
 *
 * [initialHost] / [initialPin] 来自扫码页的导航参数：非空时自动预填并直接发起连接；
 * 手输路径则两者均为空，用户自己填。真正的校验 / 握手 / 凭据保存都在 [PairViewModel] 里。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PairPage(
    modifier: Modifier = Modifier,
    initialHost: String? = null,
    initialPin: String? = null,
    viewModel: PairViewModel = hiltViewModel(),
    onBack: () -> Unit,
    onEnterPanel: () -> Unit = {}
) {
    val serverUrl by viewModel.serverUrl.collectAsState()
    val pin by viewModel.pin.collectAsState()
    val connectionState by viewModel.connectionState.collectAsState()

    val isConnecting = connectionState is PairViewModel.ConnectionState.Connecting
    val isConnected = connectionState is PairViewModel.ConnectionState.Connected

    // 扫码带来的 host / pin：先写进 ViewModel（连上了 SavedStateHandle）再自动连接
    LaunchedEffect(initialHost, initialPin) {
        val host = initialHost?.takeIf { it.isNotBlank() }
        val scannedPin = initialPin?.takeIf { it.isNotBlank() }
        if (host != null) viewModel.onServerUrlChange(host)
        if (scannedPin != null) viewModel.onPinChange(scannedPin)
        if (host != null && scannedPin != null) viewModel.connect()
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.pair_page_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.Default.ArrowBack, contentDescription = "返回")
                    }
                }
            )
        }
    ) { paddingValues ->
        Column(
            modifier = modifier
                .fillMaxSize()
                .padding(paddingValues)
                .padding(horizontal = 16.dp, vertical = 12.dp)
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Text(
                text = stringResource(R.string.hint_check_host_running),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            // Host 地址
            OutlinedTextField(
                value = serverUrl,
                onValueChange = viewModel::onServerUrlChange,
                label = { Text("主机地址") },
                placeholder = { Text(stringResource(R.string.pair_page_host_url_hint)) },
                singleLine = true,
                enabled = !isConnecting && !isConnected,
                isError = serverUrl.isNotEmpty() && viewModel.hasInvalidUrl,
                supportingText = {
                    if (serverUrl.isNotEmpty() && viewModel.hasInvalidUrl) {
                        Text(PairViewModel.INVALID_URL_REASON)
                    }
                },
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                modifier = Modifier.fillMaxWidth()
            )

            // PIN（6 位纯数字，数字键盘）
            OutlinedTextField(
                value = pin,
                onValueChange = { input ->
                    // 只保留数字且最多 6 位，避免用户输入字母导致握手永远失败
                    viewModel.onPinChange(input.filter { it.isDigit() }.take(Constants.PIN_LENGTH))
                },
                label = { Text("PIN 码") },
                placeholder = { Text(stringResource(R.string.pair_page_pin_hint)) },
                singleLine = true,
                enabled = !isConnecting && !isConnected,
                isError = pin.isNotEmpty() && viewModel.hasPinError,
                supportingText = {
                    if (pin.isNotEmpty() && viewModel.hasPinError) {
                        Text(PairViewModel.INVALID_PIN_REASON)
                    }
                },
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.fillMaxWidth()
            )

            // 连接 / 断开
            when (connectionState) {
                is PairViewModel.ConnectionState.Connected -> {
                    // 连上了就把主任务摆到最前面：直接进点歌台，断开放在次要位置
                    Button(
                        onClick = onEnterPanel,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text("进入点歌台")
                    }
                    Spacer(Modifier.height(8.dp))
                    OutlinedButton(
                        onClick = { viewModel.disconnect() },
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text("断开连接")
                    }
                }

                else -> {
                    Button(
                        onClick = { viewModel.connect() },
                        enabled = !isConnecting,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(stringResource(R.string.pair_page_connect_button))
                    }
                }
            }

            // 连接状态
            ConnectionStateCard(
                state = connectionState,
                modifier = Modifier.fillMaxWidth()
            )
        }
    }
}

/**
 * 连接状态卡片：连接中转圈、成功给绿色、失败把 reason 显示出来。
 */
@Composable
private fun ConnectionStateCard(
    state: PairViewModel.ConnectionState,
    modifier: Modifier = Modifier
) {
    when (state) {
        is PairViewModel.ConnectionState.Discovered -> StatusCard(
            modifier = modifier,
            containerColor = MaterialTheme.colorScheme.surfaceVariant,
            contentColor = MaterialTheme.colorScheme.onSurfaceVariant,
            title = stringResource(R.string.pair_page_disconnected)
        )

        is PairViewModel.ConnectionState.Connecting -> Card(
            modifier = modifier,
            colors = CardDefaults.cardColors(MaterialTheme.colorScheme.surfaceVariant)
        ) {
            Row(
                modifier = Modifier.padding(16.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                CircularProgressIndicator(modifier = Modifier.size(20.dp), strokeWidth = 2.dp)
                Spacer(modifier = Modifier.width(12.dp))
                Text(
                    stringResource(R.string.status_connecting),
                    style = MaterialTheme.typography.bodyMedium
                )
            }
        }

        is PairViewModel.ConnectionState.Connected -> StatusCard(
            modifier = modifier,
            containerColor = MaterialTheme.colorScheme.primaryContainer,
            contentColor = MaterialTheme.colorScheme.onPrimaryContainer,
            title = stringResource(R.string.pair_page_connected),
            subtitle = stringResource(R.string.status_connected_success),
            showSuccessIcon = true
        )

        is PairViewModel.ConnectionState.Failed -> StatusCard(
            modifier = modifier,
            containerColor = MaterialTheme.colorScheme.errorContainer,
            contentColor = MaterialTheme.colorScheme.onErrorContainer,
            title = stringResource(R.string.status_connection_failed),
            subtitle = state.reason,
            showErrorIcon = true
        )
    }
}

@Composable
private fun StatusCard(
    modifier: Modifier,
    containerColor: androidx.compose.ui.graphics.Color,
    contentColor: androidx.compose.ui.graphics.Color,
    title: String,
    subtitle: String? = null,
    showSuccessIcon: Boolean = false,
    showErrorIcon: Boolean = false
) {
    Card(
        modifier = modifier,
        colors = CardDefaults.cardColors(containerColor = containerColor)
    ) {
        Row(
            modifier = Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            when {
                showSuccessIcon -> Icon(
                    Icons.Default.CheckCircle,
                    contentDescription = null,
                    tint = contentColor
                )

                showErrorIcon -> Icon(
                    Icons.Default.ErrorOutline,
                    contentDescription = null,
                    tint = contentColor
                )
            }

            if (showSuccessIcon || showErrorIcon) {
                Spacer(modifier = Modifier.width(12.dp))
            }

            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = title,
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                    color = contentColor
                )
                if (subtitle != null) {
                    Spacer(modifier = Modifier.height(2.dp))
                    Text(
                        text = subtitle,
                        style = MaterialTheme.typography.bodySmall,
                        color = contentColor
                    )
                }
            }
        }
    }
}
