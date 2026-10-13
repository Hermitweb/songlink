package songlink.remote.ui.viewmodel

import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModelStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestCoroutineScheduler
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import songlink.remote.Fixtures
import songlink.remote.data.model.AuthResult
import songlink.remote.data.repo.NowPlayingRepo
import songlink.remote.data.transport.WsTransport
import songlink.remote.util.Constants

/**
 * PairViewModel 单元测试（**JVM 版**，不依赖设备）。
 *
 * 原 androidTest 版本需要真机 + HiltAndroidRule，本轮改成纯构造 + 假依赖，
 * 这样 `gradlew test` 就能覆盖配对流程的校验与状态流转。
 *
 * 注意：StateFlow 对外只读，测「输入非法 URL」这类用例要通过
 * [PairViewModel.onServerUrlChange] 写值，而不是给只读属性赋值。
 */
@OptIn(ExperimentalCoroutinesApi::class)
class PairViewModelTest {

    private lateinit var savedState: SavedStateHandle
    private lateinit var fakeTransport: FakeWsTransport
    private lateinit var repo: NowPlayingRepo
    private val viewModelStore = ViewModelStore()

    /**
     * 关键：runTest 会自带一个 scheduler；如果 setMain 用的是另一个独立的 dispatcher，
     * viewModelScope.launch 会跑在那个 dispatcher 上，runTest 看不到，导致断言失败。
     * 共用一个 scheduler 之后，runTest 的 advanceUntilIdle 就能驱动 viewModelScope 协程。
     */
    private val testScheduler = TestCoroutineScheduler()
    private val testDispatcher = UnconfinedTestDispatcher(testScheduler)

    @Before
    fun setUp() {
        Dispatchers.setMain(testDispatcher)
        savedState = SavedStateHandle()
        fakeTransport = FakeWsTransport()
        repo = NowPlayingRepo()
    }

    @After
    fun tearDown() {
        viewModelStore.clear() // 触发 onCleared，停掉 viewModelScope 里的收集协程
        Dispatchers.resetMain()
    }

    // ------------------------------------------------------------------ 初始态

    @Test
    fun `initial state is empty and Discovered`() = runTest(testDispatcher) {
        val vm = createViewModel()

        assertEquals("", vm.serverUrl.value)
        assertEquals("", vm.pin.value)
        assertEquals(PairViewModel.ConnectionState.Discovered, vm.connectionState.value)
        assertTrue("空地址应视为未填好", vm.hasInvalidUrl)
        assertTrue("空 PIN 应视为未填好", vm.hasPinError)
    }

    @Test
    fun `saved values are restored from SavedStateHandle`() = runTest(testDispatcher) {
        savedState[Constants.KEY_SERVER_URL] = "ws://192.168.1.100:51999"
        savedState[Constants.KEY_PIN] = "123456"

        val vm = createViewModel()

        assertEquals("ws://192.168.1.100:51999", vm.serverUrl.value)
        assertEquals("123456", vm.pin.value)
        assertFalse(vm.hasInvalidUrl)
        assertFalse(vm.hasPinError)
    }

    @Test
    fun `editing inputs writes them back to SavedStateHandle`() = runTest(testDispatcher) {
        val vm = createViewModel()

        vm.onServerUrlChange("wss://songlink.local")
        vm.onPinChange("246810")

        assertEquals("wss://songlink.local", savedState.get<String>(Constants.KEY_SERVER_URL))
        assertEquals("246810", savedState.get<String>(Constants.KEY_PIN))
        assertEquals("wss://songlink.local", vm.serverUrl.value)
    }

    // -------------------------------------------------------------- 输入校验

    @Test
    fun `only ws and wss urls are accepted`() = runTest(testDispatcher) {
        val vm = createViewModel()

        listOf("ws://localhost:51999", "wss://192.168.1.100:51999", "ws://songlink.local")
            .forEach { url ->
                vm.onServerUrlChange(url)
                assertFalse("$url 应合法", vm.hasInvalidUrl)
            }

        listOf("not-a-url", "http://localhost:51999", "https://songlink.local", "", "localhost:51999")
            .forEach { url ->
                vm.onServerUrlChange(url)
                assertTrue("$url 应被判非法", vm.hasInvalidUrl)
            }
    }

    @Test
    fun `pin must be exactly six digits`() = runTest(testDispatcher) {
        val vm = createViewModel()

        vm.onPinChange("12345")
        assertTrue("位数不足", vm.hasPinError)
        vm.onPinChange("123456")
        assertFalse("6 位纯数字应合法", vm.hasPinError)
        vm.onPinChange("1234567")
        assertTrue("位数超出", vm.hasPinError)
        vm.onPinChange("abcDEF")
        assertTrue("非数字", vm.hasPinError)
        vm.onPinChange("12345a")
        assertTrue("含非数字", vm.hasPinError)
    }

    // ------------------------------------------------------------ 状态流转

    @Test
    fun `successful connect goes Discovered - Connecting - Connected`() = runTest(testDispatcher) {
        fakeTransport.onConnect = { _, _ ->
            Result.success(AuthResult(ref = "h-1", ok = true, token = "tok-1", hostKey = "hk-1"))
        }
        val vm = createViewModel()
        vm.onServerUrlChange("ws://127.0.0.1:51999")
        vm.onPinChange("123456")

        val states = mutableListOf<PairViewModel.ConnectionState>()
        // 用 unconfined 调度器，保证 collect 在 vm.connect() 之前就已订阅，
        // 否则 StateFlow 会把 Connecting 直接合并掉（runTest 默认是 StandardTestDispatcher）
        backgroundScope.launch(UnconfinedTestDispatcher(testScheduler)) {
            vm.connectionState.collect { states += it }
        }

        vm.connect()
        // viewModelScope 里的 launch 一次性跑完两处 setValue 没有让出执行权，
        // 需要主动把调度器推进到底，collect 才能取到所有中间状态
        advanceUntilIdle()

        assertTrue(states.any { it is PairViewModel.ConnectionState.Connecting })
        val connected = states.filterIsInstance<PairViewModel.ConnectionState.Connected>().last()
        assertEquals("tok-1", connected.token)
        assertEquals("hk-1", connected.hostKey)
        assertEquals("tok-1", vm.token)
        assertEquals("hk-1", vm.hostKey)
        assertEquals("ws://127.0.0.1:51999", fakeTransport.lastUrl)
        assertEquals("123456", fakeTransport.lastPin)
    }

    @Test
    fun `failed connect lands on Failed`() = runTest(testDispatcher) {
        fakeTransport.onConnect = { _, _ -> Result.failure(IllegalStateException("Connection failed")) }
        val vm = createViewModel()
        vm.onServerUrlChange("ws://127.0.0.1:51999")
        vm.onPinChange("123456")

        vm.connect()

        val failed = vm.connectionState.value as PairViewModel.ConnectionState.Failed
        assertEquals("Connection failed", failed.reason)
    }

    @Test
    fun `invalid input never reaches the transport`() = runTest(testDispatcher) {
        val vm = createViewModel()

        vm.onServerUrlChange("http://localhost:51999")
        vm.connect()
        assertEquals(
            PairViewModel.ConnectionState.Failed(PairViewModel.INVALID_URL_REASON),
            vm.connectionState.value
        )

        vm.onServerUrlChange("ws://localhost:51999")
        vm.onPinChange("12345")
        vm.connect()
        assertEquals(
            PairViewModel.ConnectionState.Failed(PairViewModel.INVALID_PIN_REASON),
            vm.connectionState.value
        )

        assertEquals(0, fakeTransport.connectCount)
    }

    @Test
    fun `disconnect returns to Discovered and drops the socket`() = runTest(testDispatcher) {
        val vm = createViewModel()
        vm.onServerUrlChange("ws://127.0.0.1:51999")
        vm.onPinChange("123456")
        vm.connect()
        assertEquals(PairViewModel.ConnectionState.Connected("tok-1", null), vm.connectionState.value)

        vm.disconnect()

        assertEquals(PairViewModel.ConnectionState.Discovered, vm.connectionState.value)
        assertEquals(1, fakeTransport.disconnectCount)
        assertNull(vm.token)
    }

    // ---------------------------------------------------------- 播放状态联动

    @Test
    fun `inbound frames are forwarded to the now playing repo`() = runTest(testDispatcher) {
        // createViewModel() 的 viewModelStore.put 副作用负责把 VM 注册到 store 上,
        // 本测试只关心 repo 是否收到 frame,因此返回值不绑定变量。
        createViewModel()

        fakeTransport.handleTextMessage(Fixtures.text("state"))
        fakeTransport.handleTextMessage(Fixtures.text("lyric"))

        assertEquals("晴天", repo.nowPlaying.value.title)
        assertEquals("周杰伦", repo.nowPlaying.value.artist)
        assertEquals(61.5, repo.nowPlaying.value.pos, 1e-9)
        assertEquals(269.0, repo.nowPlaying.value.dur, 1e-9)
        assertEquals("playing", repo.nowPlaying.value.status)
        assertEquals("track-77", repo.lyric.value?.trackId)
        assertEquals(2, repo.lyric.value?.lines?.size)
    }

    private fun createViewModel(): PairViewModel {
        val vm = PairViewModel(savedStateHandle = savedState, wsTransport = fakeTransport, nowPlayingRepo = repo)
        viewModelStore.put("pair", vm)
        return vm
    }

    /** 只桩掉 [WsTransport.connect]，其余行为（frame 派发、断连计数）沿用真实实现。 */
    private class FakeWsTransport : WsTransport() {
        var connectCount = 0
        var disconnectCount = 0
        var lastUrl: String? = null
        var lastPin: String? = null

        var onConnect: (String, String) -> Result<AuthResult> =
            { _, _ -> Result.success(AuthResult(ref = "h-1", ok = true, token = "tok-1")) }

        override suspend fun connect(url: String, pin: String): Result<AuthResult> {
            connectCount++
            lastUrl = url
            lastPin = pin
            // 让出一次执行权：在 viewModelScope.launch 里 setValue(Connecting) 之后真正挂起，
            // 这样订阅 connectionState 的 collector 就能在 setValue(Connected) 之前观察到 Connecting。
            // 没有这行，UnconfinedTestDispatcher 会把整段协程同步跑完，StateFlow 的中间态被覆盖掉。
            kotlinx.coroutines.yield()
            return onConnect(url, pin)
        }

        override fun disconnect() {
            disconnectCount++
            super.disconnect()
        }
    }
}