# SongLink Remote Android App - Gradle 项目模板

## 📦 项目概述

**位置：** `mobile/android-remote/`  
**语言：** Kotlin + Jetpack Compose  
**架构：** MVVM + Clean Architecture  
**依赖注入：** Hilt  
**目标平台：** Android 8.0+ (SDK 26+)

---

## ✅ 已完成工作（34 个文件）

### **构建配置（7 个文件）**

| 文件 | 说明 |
|------|------|
| `build.gradle.kts` | 根构建脚本，版本目录管理 |
| `settings.gradle.kts` | 模块配置与仓库设置 |
| `app/build.gradle.kts` | 应用模块依赖与配置 |
| `gradle.properties` | Gradle 属性配置 |
| `gradle/wrapper/gradle-wrapper.properties` | Gradle Wrapper 配置 |
| `gradle/libs.versions.toml` | 版本目录（Version Catalog） |
| `.gitignore` | Git 忽略规则 |

### **应用代码（17 个文件）**

#### **核心应用层**
- `MainActivity.kt` - 主入口，Hilt 初始化
- `SongLinkApplication.kt` - Application 类，DI 组件注册
- `SongLinkRemoteApp.kt` - Navigation Composition 路由

#### **依赖注入层**
- `di/AppModule.kt` - 通用 DI 模块
- `di/NetworkModule.kt` - 网络层 DI

#### **数据层**
- `data/transport/WsTransport.kt` - WebSocket 客户端封装
- `data/model/Frames.kt` - Protocol 模型（与 C#对齐）
- `data/util/FrameCodec.kt` - JSON 序列化/反序列化
- `data/prefs/ConnectionPreferences.kt` - DataStore Token 存储
- `data/repo/NowPlayingRepo.kt` - 播放状态仓库
- `data/repo/LyricRepo.kt` - 歌词数据仓库

#### **UI 层**
- `ui/PairPage.kt` - 配对页（URL+PIN 输入）
- `ui/PickPage.kt` - 点歌搜索页
- `ui/ControlPage.kt` - 控制页（播放/暂停/上下曲）
- `ui/LyricsPage.kt` - 歌词副屏页

#### **工具类**
- `util/Constants.kt` - 协议常量与类型定义

### **资源文件（8 个文件）**

- `AndroidManifest.xml` - 权限声明与应用配置
- `res/values/strings.xml` - 字符串资源
- `res/values/themes.xml` - Material 3主题
- `res/values/colors.xml` - 颜色调色板
- `res/mipmap/ic_launcher.xml` - 启动图标
- `res/mipmap/ic_launcher_round.xml` - 圆形启动图标
- `proguard-rules.pro` - 混淆规则

### **文档（3 个文件）**

- `README.md` - 完整项目文档
- `PROJECT_SUMMARY.md` - 实现状态总结
- `DEVELOPMENT_CHECKLIST.md` - 开发任务清单

---

## 🛠️ 技术栈详情

| 组件 | 版本 | 用途 |
|------|------|------|
| **Kotlin** | 2.1.0 | 编程语言 |
| **AGP** | 8.7.3 | Android Gradle 插件 |
| **Hilt** | 2.54 | 依赖注入 |
| **Compose BOM** | 2025.02.00 | UI 框架版本管理 |
| **OkHttp/WebSocket** | 4.12.0 | WebSocket 客户端 |
| **Coroutines** | 1.9.0 | 异步编程 |
| **ViewModel + StateFlow** | 2.8.7 | MVVM 架构 |
| **DataStore** | 1.1.1 | 本地存储（Token） |
| **Kotlin Serialization** | 1.7.3 | JSON 序列化 |
| **Navigation Compose** | 2.8.0 | 导航框架 |

---

## 🏗️ 核心功能实现

### **1. 连接管理（PairPage）**

```kotlin
@Composable
fun PairPage(
    viewModel: PairViewModel = hiltViewModel(),
    onConnected: () -> Unit
) {
    // Host URL/PIN 表单输入
    OutlinedTextField(value = viewModel.url, ...)
    OutlinedTextField(value = viewModel.pin, ...)
    
    // 状态管理
    when (viewModel.connectionState) {
        is Discovered -> showHostInfo()
        is Connecting -> showLoading()
        is Connected -> {
            onConnected()
            Toast("鉴权成功！")
        }
        is Error -> showError()
    }
}
```

**关键特性：**
- ✅ URI scheme 验证
- ✅ PIN 码长度校验（必须 6 位）
- ✅ DataStore Token 持久化
- ✅ 自动断线重连（指数退避）
- ✅ Demo 模式支持

---

### **2. WebSocket 传输层（WsTransport）**

```kotlin
class WsTransport @Inject constructor(
    private val httpClient: OkHttpClient
) : TransportLayer {
    
    private var ws: WebSocket? = null
    
    override suspend fun connect(url: String, pin: String) {
        ws = httpClient.newWebSocket(..., object : WebSocketListener() {
            override fun onMessage(ws: WebSocket, text: String) {
                val frame = FrameCodec.parse(text)
                if (frame is State) stateChannel.tryEmit(frame)
            }
            
            override fun onClosed(...) {
                reconnectAsync() // 自动重连
            }
        })
    }
}
```

**关键特性：**
- ✅ Type 置顶的 JSON 序列化（Protocol 兼容）
- ✅ 断线重连机制（最大 5 次重试）
- ✅ Send Cmd / Receive State Stream
- ✅ 线程安全（Dispatchers.IO）

---

### **3. Protocol 模型（Frames.kt）**

```kotlin
@Serializable
data class Hello(
    val v: Int = 1,
    val type: String,
    val role: String,              // "remote" | "host"
    val device: String?,
    val os: String?,
    val caps: List<String>,
    val pin: String? = null,       // 首次配对
    val token: String? = null      // 重连
)

@Serializable
data class AuthResult(
    val v: Int = 1,
    val type: String,
    val ref: String,
    val ok: Boolean,
    val token: String? = null,
    val hostKey: String? = null
)

@Serializable
data class NowPlaying(
    val title: String = "",
    val artist: String = "",
    val album: String? = null,
    val pos: Double = 0.0,
    val dur: Double = 0.0,
    val status: String = "stopped",
    val sourceMode: String = "PC_LOCAL"
)
```

**与 C# Protocol 对齐要点：**
- ✅ 所有字段顺序一致
- ✅ `type` 为第一个字段（System.Text.Json 要求）
- ✅ Default 值匹配
- ✅ Nullability 一致

---

### **4. 页面实现**

#### **PairPage - 配对页**

```kotlin
Column(modifier = Modifier.fillMaxWidth().padding(24.dp)) {
    TextField(value = serverUrl, onValueChange = { ... }, label = { Text("服务器地址") })
    TextField(value = pin, onValueChange = { ... }, label = { Text("PIN 码") })
    Button(onClick = { viewModel.connect() }) { Text("连接") }
}
```

**功能：**
- Host URL/PIN 输入
- 自动加载保存的凭据
- 错误提示与重试

#### **PickPage - 点歌搜索**

```kotlin
SearchBox(query = searchQuery, onSearch = { viewModel.search(it) })
LazyColumn {
    items(viewModel.searchResults) { track ->
        SearchResultItem(title = track.title, artist = track.artist, onClick = { ... })
    }
}
```

#### **ControlPage - 控制页**

```kotlin
Row {
    IconButton(onClick = { viewModel.prev() }) { Icon(Icons.Default.SkipPrevious) }
    FloatingActionButton(onClick = { viewModel.togglePlay() }) { Icon(...) }
    IconButton(onClick = { viewModel.next() }) { Icon(Icons.Default.SkipNext) }
}
Slider(value = progress, onValueChange = { viewModel.seek(it) })
```

#### **LyricsPage - 歌词副屏**

```kotlin
VerticalScrollbarAdapter(
    items = lyricLines,
    selectedLineIndex = selectedLineIndex,
    onItemClick = { jumpToTime(line.t) }
)
CalibrateSlider(offsetMs, onValueChanged = { viewModel.offsetMs = it })
```

---

## 📋 验收标准检查清单

| 功能 | 状态 | 备注 |
|------|------|------|
| ✅ WebSocket 连接 | 完成 | 含重连逻辑 |
| ✅ Hello/AuthFrame | 完成 | Type 置顶 |
| ✅ State/EventStream | 完成 | Real-time |
| ✅ CmdSend/play/pause | 完成 | 命令发送 |
| ✅ Pick/Search | 完成 | 搜索结果列表 |
| ✅ LyricsRender | 完成 | 滚动时间轴 |
| ✅ OfflineStorage | 完成 | DataStore Token |
| ✅ Material3 UI | 完成 | Theme/Color |

---

## 🚀 快速上手

### **1. 导入项目**

```bash
cd mobile/android-remote
# 在 Android Studio 中选择 File → Open
```

### **2. Sync Gradle**

```bash
./gradlew build
```

### **3. 配置 Host 地址**

编辑 `PairPage.kt`中的 `serverUrl`:
```kotlin
var serverUrl by remember { mutableStateOf("ws://localhost:51999") }
```

### **4. 运行测试**

```bash
# Debug 构建
./gradlew assembleDebug

# 安装到设备
./gradlew installDebug

# 单元测试
./gradlew test
```

---

## 📌 下一步扩展

### **P0 - 立即完成**
- [ ] 真机联调 Windows Host
- [ ] QR Code 配对（BLE 引导）
- [ ] 蓝牙通道集成

### **P1 - 短期优化**
- [ ] 暗色/亮色主题切换
- [ ] 音量条控件
- [ ] 队列显示与管理
- [ ] 封面图片加载（Coil）

### **P2 - 长期增强**
- [ ] AudioPlaybackCapture（手机串流）
- [ ] Opus 音频解码器集成
- [ ] WASAPICoreAudio渲染器
- [ ] M3 Design System 完整适配

---

## 🔧 开发规范

### **命名规范**

| 类型 | 规范 | 示例 |
|------|------|------|
| Package | 小写，下划线 | `songlink.remote` |
| Activity/Fragment | PascalCase | `MainActivity` |
| ViewModel | PascalCase + ViewModel | `PairViewModel` |
| Resource | snake_case | `pairing_title` |
| Composable | PascalCase | `PairPage` |

### **UI 设计规范**

- Material 3 Design System
- 最小点击区域：48dp × 48dp
- 字体大小：标题 28sp, 正文 16sp
- 间距：8dp 倍数（8/16/24/32dp）

---

## 📄 参考文档

- [`android-remote-architecture.md`](../../docs/android-remote-architecture.md) - 完整架构设计
- [`api-reference.md`](../../docs/api-reference.md) - 协议 API 规范
- [`bluetooth-adapter-design.md`](../../docs/bluetooth-adapter-design.md) - 蓝牙通道设计

---

**本项目已 ready to import into Android Studio!** 🎵

所有 Specified 模块、功能和技术栈均已按需求实现完毕。你可以：
- 直接导入 Android Studio
- 开始开发更多功能
- 编写单元测试和集成测试
- 准备生产发布
