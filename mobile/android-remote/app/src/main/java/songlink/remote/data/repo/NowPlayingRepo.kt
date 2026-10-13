package songlink.remote.data.repo

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import songlink.remote.data.model.Art
import songlink.remote.data.model.Frame
import songlink.remote.data.model.Lyric
import songlink.remote.data.model.NowPlaying
import songlink.remote.data.model.PlayStatus
import songlink.remote.data.model.SearchResults
import songlink.remote.data.model.SrcMeta
import songlink.remote.data.model.State
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 播放状态仓库：把 Host 广播的帧归并成 UI 可直接消费的 StateFlow / SharedFlow。
 *
 * 对应帧（契约 §3.4 / §3.5 / §3.2b / §3.7 / §3.8）：
 * - `state`     → [nowPlaying]
 * - `srcMeta`   → [nowPlaying]（PHONE_STREAM 模式手机侧上报）
 * - `lyric`     → [lyric]
 * - `art`       → [artRequests]（图片本身经 HTTP 拉取，这里只给引用）
 * - `searchResults` → [searchResults]
 */
@Singleton
class NowPlayingRepo @Inject constructor() {

    private val _nowPlaying = MutableStateFlow(NowPlaying())
    val nowPlaying: StateFlow<NowPlaying> = _nowPlaying.asStateFlow()

    private val _lyric = MutableStateFlow<Lyric?>(null)
    val lyric: StateFlow<Lyric?> = _lyric.asStateFlow()

    /** 搜索结果带 replay(1)：发起 search 后订阅者仍能拿到最近一次结果。 */
    private val _searchResults = MutableSharedFlow<SearchResults>(replay = 1)
    val searchResults: SharedFlow<SearchResults> = _searchResults.asSharedFlow()

    private val _artRequests = MutableSharedFlow<Art>(replay = 1)
    val artRequests: SharedFlow<Art> = _artRequests.asSharedFlow()

    /** 收到一帧；与播放状态无关的帧直接忽略。 */
    fun onFrame(frame: Frame) {
        when (frame) {
            is State -> _nowPlaying.value = frame.now
            // PHONE_STREAM 下 pos/dur 由手机侧 srcMeta 驱动（契约 §3.4 末条）
            is SrcMeta -> _nowPlaying.value = NowPlaying(
                title = frame.title,
                artist = frame.artist,
                album = _nowPlaying.value.album,
                pos = frame.pos,
                dur = frame.dur,
                status = frame.status,
                artRef = frame.artRef,
                sourceMode = _nowPlaying.value.sourceMode,
            )
            is Lyric -> _lyric.value = frame
            is Art -> _artRequests.tryEmit(frame)
            is SearchResults -> _searchResults.tryEmit(frame)
            else -> Unit
        }
    }

    /** 断线或换源时清空，保证 UI 不会残留上一首歌的歌词。 */
    fun reset() {
        _nowPlaying.value = NowPlaying()
        _lyric.value = null
    }

    /** 当前是否在播放（给 UI 的便捷判断）。 */
    fun isPlaying(): Boolean = _nowPlaying.value.status == PlayStatus.PLAYING
}