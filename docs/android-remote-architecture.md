# SongLink Remote - Android 遥控端 App 架构设计

## 📋 项目概述

**位置：** `mobile/app`  
**语言：** Kotlin + Jetpack Compose  
**目标平台：** Android 8.0+ (SDK 26+)  
**最低支持：** Android 7.0 (SDK 24)

---

## 🏗️ 核心架构

### **1. 分层结构**

```
android-remote/
├── app/                          # Android Application
│   ├── ui/                       # UI 层（Composable）
│   │   ├── main/                 # 主流程页面
│   │   ├── pair/                 # 配对连接页
│   │   ├── control/              # 控制页（播放/暂停/上下曲）
│   │   ├── pick/                 # 点歌搜索页
│   │   ├── lyrics/               # 歌词副屏页
│   │   └── settings/             # 设置页
│   ├── data/                     # 数据层
│   │   ├── transport/            # 传输层（WebSocket/蓝牙）
│   │   ├── repo/                 # 仓库层（NowPlaying/SearchResults/Lyrics）
│   │   └── model/                # 协议模型（与 Protocol.cs 对齐）
│   ├── di/                       # 依赖注入（Hilt）
│   ├── util/                     # 工具类
│   └── MainActivity.kt
├── shared-contract/              # 共享协议定义
│   └── songlink-protocol/        # 从 C# 生成的 Kotlin DSL
└── build.gradle.kts
```

---

### **2. 数据流（MVVM + MVI）**

```kotlin
// ViewModel 状态流
sealed class RemoteUiState {
    object Idle : RemoteUiState()
    data class Connected(val pin: String, val hostInfo: HostInfo) : RemoteUiState()
    data class Error(val message: String) : RemoteUiState()
}

// ViewModel
class RemoteViewModel @Inject constructor(
    private val transportRepo: TransportRepo,
    private val nowPlayingRepo: NowPlayingRepo,
    private val lyricRepo: LyricRepo
) : ViewModel() {
    
    private val _uiState = MutableStateFlow<RemoteUiState>(Idle)
    val uiState: StateFlow<RemoteUiState> = _uiState.asStateFlow()
    
    fun connect(hostUrl: String, pin: String) = viewModelScope.launch {
        // 1. 建立 WebSocket 连接
        transportRepo.connect(hostUrl, pin)
        
        // 2. 接收鉴权结果
        val authResult = transportRepo.receiveAuthResult()
        
        // 3. 订阅状态广播
        launch {
            transportRepo.stateChannel.collect { state ->
                nowPlayingRepo.update(state.now)
            }
        }
        
        // 4. 更新 UI 状态
        _uiState.value = Connected(pin, authResult.hostInfo)
    }
}
```

---

### **3. 传输层实现**

#### **A. WebSocket 客户端（主通道）**

```kotlin
class WsTransport(
    private val wsClient: WebSocketClient,
    private val frameCodec: FrameCodec
) : TransportLayer {
    
    private var ws: WebSocket? = null
    
    override suspend fun connect(url: String, pin: String) {
        ws = wsClient.connect(url) { 
            // 发送 Hello 帧（type 置顶）
            send(FrameCodec.toFrame(Hello(
                role = "remote",
                device = "Pixel-7-Pro",
                caps = listOf("cmd", "state", "lyric", "art"),
                pin = pin
            )))
            
            // 接收鉴权结果
            val authResult = receiveAuthResult()
            if (!authResult.ok) throw AuthenticationException()
            
            token = authResult.token
        }
    }
    
    // 发送命令
    override suspend fun sendCmd(action: String, args: JsonObject?) {
        val cmd = Cmd(
            id = generateId(),
            action = action,
            args = args
        )
        ws?.send(FrameCodec.serialize(cmd))
    }
    
    // 接收通道
    override val stateChannel = flow {
        while (true) {
            val raw = ws?.receive() ?: break
            val frame = FrameCodec.parse(raw)
            if (frame is State) emit(frame)
        }
    }
}
```

#### **B. 蓝牙 fallback（可选增强）**

```kotlin
class BluetoothTransport : TransportLayer {
    private lateinit var bluetoothSocket: BluetoothSocket
    
    override suspend fun connect(hostIp: String, pin: String) {
        // RFCOMM 连接
        val adapter = BluetoothAdapter.getDefaultAdapter()
        val device = adapter.getRemoteDevice(hostIp)
        bluetoothSocket = device.createRfcommSocketToServiceRecord(SERVICE_UUID)
        bluetoothSocket.connect()
        
        // 交换 PIN 码完成配对
        write(pin.toByteArray())
    }
}
```

---

### **4. 核心页面设计**

#### **页面 1: PairPage（配对连接）**

```kotlin
@Composable
fun PairPage(
    modifier: Modifier = Modifier,
    onConnected: () -> Unit
) {
    val viewModel: PairViewModel = hiltViewModel()
    
    Column(modifier = modifier.padding(24.dp)) {
        Text("SongLink Remote", fontSize = 28.sp, fontWeight = FontWeight.Bold)
        
        Spacer(modifyHeight(16.dp))
        
        OutlinedTextField(
            value = viewModel.url,
            onValueChange = { viewModel.url = it },
            label = { Text("Host URL") },
            placeholder = { Text("ws://localhost:51999") }
        )
        
        Spacer(modifyHeight(8.dp))
        
        OutlinedTextField(
            value = viewModel.pin,
            onValueChange = { viewModel.pin = it },
            label = { Text("PIN 码") },
            placeholder = { Text("输入 Host 显示的 6 位数字") },
            isError = viewModel.hasPinError
        )
        
        Spacer(modifyHeight(24.dp))
        
        Button(
            onClick = { viewModel.connect(onSuccess = { onConnected() }) },
            modifier = Modifier.fillMaxWidth()
        ) {
            Text("连接")
            if (viewModel.isConnecting) LoadingIndicator()
        }
        
        if (viewModel.connectionError != null) {
            Text(viewModel.connectionError!!, color = Color.Red, modifier = marginTop(8.dp))
        }
    }
}
```

#### **页面 2: PickPage（点歌搜索）**

```kotlin
@Composable
fun PickPage(
    modifier: Modifier = Modifier,
    viewModel: PickViewModel = hiltViewModel()
) {
    var query by remember { mutableStateOf("") }
    
    Scaffold(
        topBar = {
            TopAppBar(title = { Text("点歌台") })
        }
    ) { padding ->
        Column(modifier = Modifier
            .fillMaxSize()
            .padding(padding)
            .verticalScroll(rememberScrollState())) {
            
            // 搜索框
            SearchBox(query = query, onQueryChange = { query = it }) {
                viewModel.search(query)
            }
            
            // 搜索结果列表
            results.map { track ->
                SearchResultItem(
                    title = track.title,
                    artist = track.artist,
                    onPick = { viewModel.pick(track) }
                )
            }
        }
    }
}
```

#### **页面 3: LyricsPage（歌词副屏）**

```kotlin
@Composable
fun LyricsPage(
    modifier: Modifier = Modifier,
    viewModel: LyricViewModel = hiltViewModel()
) {
    LaunchedEffect(Unit) {
        // 全屏模式
        android.view.WindowInsetsControllerCompat(
            activity.window.decorView.findViewById(android.R.id.content),
            android.view.WindowManager.LayoutParams.FLAG_FULLSCREEN
        ).systemBarsBehavior = 
            WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        
        viewModel.subscribeToLyrics()
    }
    
    Surface(modifier = modifier.fillMaxSize()) {
        Column(verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
            
            // 当前歌曲信息
            SongInfoCard(viewModel.currentSong)
            
            Spacer(modifyHeight(24.dp))
            
            // 滚动歌词
            ScrollableLyrics(
                lines = viewModel.lyricLines,
                currentPos = viewModel.currentPosition,
                offsetMs = viewModel.lyricOffsetMs
            )
            
            // 校准控件
            CalibrateSlider(
                value = viewModel.lyricOffsetMs,
                onValueChanged = { viewModel.offsetMs = it }
            )
        }
    }
}
```

---

### **5. 关键组件**

#### **SearchBox（搜索框）**

```kotlin
@Composable
fun SearchBox(
    query: String,
    onQueryChange: (String) -> Unit,
    onSearch: () -> Unit
) {
    OutlinedTextField(
        value = query,
        onValueChange = onQueryChange,
        placeholder = { Text("搜索歌曲...") },
        leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
        modifier = Modifier.fillMaxWidth().height(56.dp),
        singleLine = true,
        textStyle = TextStyle(fontSize = 16.sp),
        colors = TextFieldDefaults.outlinedTextFieldColors(
            backgroundColor = Color.White.copy(alpha = 0.9f)
        )
    )
}
```

#### **ScrollableLyrics（滚动歌词）**

```kotlin
@Composable
fun ScrollableLyrics(
    lines: List<LyricLine>,
    currentPos: Double,
    offsetMs: Int
) {
    var selectedLineIndex by remember(lines, currentPos) { mutableIntStateOf(0) }
    
    LaunchedEffect(currentPos) {
        // 计算当前行索引（按时间轴匹配）
        val newPosIndex = lines.indexOfFirst { line -> line.t >= currentPos }
        if (newPosIndex != -1 && newPosIndex != selectedLineIndex) {
            selectedLineIndex = newPosIndex
            // 滚动到选中行
            scrollToIndex(selectedLineIndex)
        }
    }
    
    VerticalScrollbarAdapter(
        items = lines,
        selectedLineIndex = selectedLineIndex,
        onItemClick = { index -> jumpToTime(lines[index].t) }
    )
}
```

---

### **6. 依赖注入（Hilt）**

```kotlin
@Module
@InstallIn(SingletonComponent::class)
object TransportModule {
    
    @Singleton
    fun provideWsTransport(): WsTransport {
        val client = OkHttpClient.Builder()
            .websocketFactory(DefaultWebSocketFactory())
            .build()
        return WsTransport(client)
    }
    
    @Singleton
    fun provideNowPlayingRepo(): NowPlayingRepo {
        return NowPlayingRepo()
    }
}
```

---

## 🔧 开发规范

### **1. 命名规范**

- **Package:** `songlink.remote.*`（小写，下划线分隔）
- **Activity/Fragment:** PascalCase（如 `MainActivity`, `PairFragment`）
- **ViewModels:** PascalCase + `ViewModel`后缀
- **Resources:** snake_case（如 `res/values/strings.xml` → `pairing_title`）

### **2. UI 设计规范**

- 使用 Material You Design System
- 最小点击区域：48dp × 48dp
- 字体大小：标题 28sp, 正文 16sp, 辅助文本 14sp
- 间距：8dp 倍数（8/16/24/32dp）

### **3. 错误处理**

```kotlin
sealed class RemoteError : Throwable() {
    object NetworkTimeout : RemoteError()
    object AuthenticationFailed : RemoteError()
    object ProtocolVersionMismatch : RemoteError()
    data class Unknown(val code: Int) : RemoteError()
}
```

---

## 📦 构建配置

```gradle
dependencies {
    implementation "org.jetbrains.kotlinx:kotlinx-coroutines-android:1.7.3"
    implementation "androidx.lifecycle:lifecycle-viewmodel-compose:2.6.2"
    implementation "com.google.dagger:hilt-android:2.48"
    implementation "com.squareup.okhttp3:okhttp:4.12.0"
    implementation "io.github.pvzhe:websocket-client:1.5.0"
}
```

---

## 🚀 下一步

1. **Week 1:** 基础框架搭建 + PairPage 实现
2. **Week 2:** PickPage + ControlPage 实现
3. **Week 3:** LyricsPage + SettingsPage 实现
4. **Week 4:** 联调测试 + Bug 修复

---

**此文档将持续更新，根据实际开发进展补充细节。** 🎵
