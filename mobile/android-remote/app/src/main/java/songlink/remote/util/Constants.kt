package songlink.remote.util

/**
 * 跨层共用的键名 / 默认值常量。
 *
 * `KEY_*` 同时用作 [androidx.lifecycle.SavedStateHandle] 的键，
 * 因此命名要稳定（改动会导致已保存的配对信息读不回来）。
 */
object Constants {

    // ---- SavedStateHandle 键 ----

    /** Host 的 WebSocket 地址，形如 `ws://192.168.1.100:51999`。 */
    const val KEY_SERVER_URL = "songlink.server_url"

    /** 配对 PIN，6 位纯数字。 */
    const val KEY_PIN = "songlink.pin"

    /** 鉴权通过后 Host 下发的长期令牌，重连时用 `hello.token` 免再输 PIN（契约 §6）。 */
    const val KEY_TOKEN = "songlink.token"

    /** Host 出站广播帧的验签密钥（`authResult.hostKey`，签名模式下才有）。 */
    const val KEY_HOST_KEY = "songlink.host_key"

    // ---- 协议常量 ----

    /** 本端角色：遥控端。 */
    const val ROLE_REMOTE = "remote"

    /** 本端对外声明的能力（契约 §3.1 `caps`，schema 枚举 cmd/state/lyric/art/audio）。 */
    val DEFAULT_CAPABILITIES = listOf("cmd", "state", "lyric", "art")

    /** 契约 §3.1：配对 PIN 为 6 位。 */
    const val PIN_LENGTH = 6

    /** 只接受 ws:// / wss://（契约 §1 主通道为 WebSocket）。 */
    const val WS_SCHEME_PREFIX = "ws://"
    const val WSS_SCHEME_PREFIX = "wss://"
}