package songlink.remote.ui.viewmodel

import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import songlink.remote.data.model.AuthResult
import songlink.remote.data.repo.SessionManager
import songlink.remote.data.transport.WsTransport
import songlink.remote.util.Constants
import javax.inject.Inject

/**
 * 配对 ViewModel：输入 Host 地址 + 6 位 PIN，完成握手并把播放状态接进 [SessionManager]。
 *
 * 状态来源为 [SavedStateHandle]（进程重建后仍能恢复已填内容），写入时会同步回写，
 * 因此 UI 只管调 [onServerUrlChange] / [onPinChange]，不用自己保存。
 *
 * **与 [SessionManager] 的分工**：本类只管「配对表单」——地址/PIN 校验、发起握手、保存凭据；
 * 握手结果会同步给 [SessionManager.onConnectResult]，由后者统一维护全局会话状态
 * （连接态、队列、命令下发）。帧的收集也已全部收敛到 [SessionManager]，
 * 本类不再重复订阅 `wsTransport.frames`。
 *
 * [WsTransport] / [SessionManager] 是具体类而非接口，便于直接用 Hilt 单例；
 * 测试则传匿名子类或纯内存实例，不依赖设备。
 */
@HiltViewModel
class PairViewModel @Inject constructor(
    private val savedStateHandle: SavedStateHandle,
    private val wsTransport: WsTransport,
    private val session: SessionManager,
) : ViewModel() {

    /** 配对 / 连接状态机。 */
    sealed interface ConnectionState {
        /** 初始态：还没开始连接（可理解为「已发现可填的输入项」）。 */
        data object Discovered : ConnectionState

        /** 正在握手：已发 hello，等 authResult。 */
        data object Connecting : ConnectionState

        /** 鉴权通过。 */
        data class Connected(val token: String?, val hostKey: String?) : ConnectionState

        /** 握手失败，[reason] 用于 UI 提示。 */
        data class Failed(val reason: String) : ConnectionState
    }

    private val _serverUrl = MutableStateFlow(savedStateHandle.get<String>(Constants.KEY_SERVER_URL).orEmpty())
    val serverUrl: StateFlow<String> = _serverUrl.asStateFlow()

    private val _pin = MutableStateFlow(savedStateHandle.get<String>(Constants.KEY_PIN).orEmpty())
    val pin: StateFlow<String> = _pin.asStateFlow()

    private val _connectionState = MutableStateFlow<ConnectionState>(ConnectionState.Discovered)
    val connectionState: StateFlow<ConnectionState> = _connectionState.asStateFlow()

    /** 鉴权通过后下发的长期令牌，重连免 PIN（契约 §6）。 */
    var token: String? = savedStateHandle.get<String>(Constants.KEY_TOKEN)
        private set

    /** 签名模式下 Host 出站帧的验签密钥。 */
    var hostKey: String? = savedStateHandle.get<String>(Constants.KEY_HOST_KEY)
        private set

    /** 仅接受 ws:// / wss://（契约 §1 主通道为 WebSocket）；空值视为未填，算非法。 */
    val hasInvalidUrl: Boolean
        get() = !isValidServerUrl(_serverUrl.value)

    /** PIN 必须恰好 6 位纯数字（契约 §6）。 */
    val hasPinError: Boolean
        get() = !isValidPin(_pin.value)

    init {
        // 帧收集已移交 SessionManager（App 级单例），此处不再重复订阅，
        // 否则离开配对页后会残留一个仍在写 NowPlayingRepo 的僵尸收集协程。
    }

    fun onServerUrlChange(value: String) {
        _serverUrl.value = value
        savedStateHandle[Constants.KEY_SERVER_URL] = value
    }

    fun onPinChange(value: String) {
        _pin.value = value
        savedStateHandle[Constants.KEY_PIN] = value
    }

    /** 校验通过则发起握手；不通过直接进入 [ConnectionState.Failed]。 */
    fun connect() {
        if (hasInvalidUrl) {
            _connectionState.value = ConnectionState.Failed(INVALID_URL_REASON)
            session.onConnectResult(Result.failure(IllegalArgumentException(INVALID_URL_REASON)))
            return
        }
        if (hasPinError) {
            _connectionState.value = ConnectionState.Failed(INVALID_PIN_REASON)
            session.onConnectResult(Result.failure(IllegalArgumentException(INVALID_PIN_REASON)))
            return
        }
        viewModelScope.launch {
            _connectionState.value = ConnectionState.Connecting
            session.onConnecting()
            val result = wsTransport.connect(_serverUrl.value, _pin.value)
            // 结果同步给会话管理器，UI 其他页面（点歌台）据此显示连接态
            session.onConnectResult(result)
            _connectionState.value = result.fold(
                onSuccess = { auth: AuthResult ->
                    saveCredentials(auth)
                    // PIN 留作兜底：Host 重启后 token 会作废，那时还能自动重连
                    session.rememberPin(_pin.value)
                    ConnectionState.Connected(auth.token, auth.hostKey)
                },
                onFailure = { error ->
                    ConnectionState.Failed(error.message ?: CONNECT_FAILED_REASON)
                },
            )
        }
    }

    /** 主动断开，回到 [ConnectionState.Discovered]。 */
    fun disconnect() {
        session.reset()
        token = null
        hostKey = null
        savedStateHandle[Constants.KEY_TOKEN] = null
        savedStateHandle[Constants.KEY_HOST_KEY] = null
        _connectionState.value = ConnectionState.Discovered
    }

    private fun saveCredentials(auth: AuthResult) {
        auth.token?.let {
            token = it
            savedStateHandle[Constants.KEY_TOKEN] = it
        }
        auth.hostKey?.let {
            hostKey = it
            savedStateHandle[Constants.KEY_HOST_KEY] = it
        }
    }

    companion object {
        const val INVALID_URL_REASON = "地址必须是 ws:// 或 wss:// 开头"
        const val INVALID_PIN_REASON = "PIN 必须是 6 位数字"
        const val CONNECT_FAILED_REASON = "连接失败"

        /** 仅 ws:// / wss:// 视为合法地址。 */
        fun isValidServerUrl(url: String): Boolean {
            val trimmed = url.trim()
            return trimmed.startsWith(Constants.WS_SCHEME_PREFIX) ||
                trimmed.startsWith(Constants.WSS_SCHEME_PREFIX)
        }

        /** 恰好 [Constants.PIN_LENGTH] 位纯数字。 */
        fun isValidPin(pin: String): Boolean =
            pin.length == Constants.PIN_LENGTH && pin.all { it.isDigit() }
    }
}