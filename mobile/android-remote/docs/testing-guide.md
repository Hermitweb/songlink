# SongLink Remote Android - 单元测试与集成测试指南

## 📋 测试覆盖范围

### **1. 单元测试（Unit Tests）**

| 模块 | 测试文件 | 覆盖率目标 | 状态 |
|------|---------|----------|------|
| **Frames.kt 模型** | FramesTest.kt | 80%+ | ✅ 已实现 |
| **PairViewModel** | PairViewModelTest.kt | 70%+ | ✅ 已实现 |
| **PickViewModel** | PickViewModelTest.kt | pending | ⏸️ 待实现 |
| **LyricsViewModel** | LyricsViewModelTest.kt | pending | ⏸️ 待实现 |
| **WsTransport** | WsTransportTest.kt | 60%+ | ⏸️ 待实现 |
| **FrameCodec** | FrameCodecTest.kt | 90%+ | ⏸️ 待实现 |

### **2. 集成测试（Integration Tests）**

| 测试场景 | 测试文件 | 验证内容 | 状态 |
|---------|---------|---------|------|
| **WebSocket 握手** | WsTransportIntegrationTest.kt | Hello/Auth sequence | ✅ 已实现 |
| **断线重连** | ReconnectIntegrationTest.kt | Exponential backoff | ⏸️ 待实现 |
| **CommandSendFlow** | CommandFlowTest.kt | Cmd → Ack flow | ⏸️ 待实现 |
| **EndToEnd_Host** | HostConnectionTest.kt | Full protocol loop | ⏸️ 待实现 |

---

## 🔧 **运行测试命令**

### **Android Studio GUI**

```bash
# 所有测试
./gradlew test

# 单个测试类
./gradlew test --tests "songlink.remote.data.model.FramesTest"

# 特定测试方法
./gradlew test --tests "songlink.remote.data.model.FramesTest.Hello serializes with type first"

# 带报告生成
./gradlew testDebugUnitTest jacocoTestReport
# Report: app/build/reports/tests/testDebugUnitTest/index.html
```

### **CI/CD Pipeline (.github/workflows/android-tests.yml)**

```yaml
name: Android Unit Tests

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    
    steps:
    - uses: actions/checkout@v4
    
    - name: Set up JDK 17
      uses: actions/setup-java@v4
      with:
        java-version: '17'
        distribution: 'temurin'
    
    - name: Grant execute permission
      run: chmod +x gradlew
    
    - name: Run unit tests
      run: ./gradlew test
    
    - name: Upload test report
      uses: actions/upload-artifact@v4
      if: always()
      with:
        name: test-report
        path: app/build/reports/tests/test
```

---

## 🎯 **测试最佳实践**

### **1. ViewModel 测试规范**

```kotlin
@HiltAndroidTest
@RunWith(AndroidJUnit4::class)
class PairViewModelTest {
    
    @get:Rule
    val hiltRule = HiltAndroidRule(this)
    
    private lateinit var viewModel: PairViewModel
    
    @Before
    fun setup() {
        hiltRule.inject()
        
        // Mock dependencies
        viewModel = PairViewModel(
            savedStateHandle = SavedStateHandle(),
            wsTransport = mockitoMock(),
            nowPlayingRepo = mockitoMock()
        )
    }
    
    @Test
    fun `test specific business logic`() = runTest {
        // Arrange
        viewModel.serverUrl = "ws://localhost:51999"
        viewModel.pin = "123456"
        
        // Act
        viewModel.connect()
        
        // Assert
        verify(wsTransport).connect("ws://localhost:51999", "123456")
    }
}
```

### **2. 异步流程测试（Coroutines Test）**

```kotlin
@Test
fun `connection flow_changes_correctly()`() = runTest {
    val states = mutableListOf<ConnectionState>()
    viewModel.connectionState.collect { state -> states.add(state) }
    
    viewModel.connect()
    
    // Verify state transitions
    assertEquals(
        listOf(Discovered, Connecting, Connected),
        states.take(3)
    )
}
```

### **3. 模拟真实 Host 通信**

```kotlin
@Test
fun `endToHostProtocolLoop_works()`() = runTest {
    // Create mock server
    val mockServer = MockWebSocketServer(port = randomPort())
    mockServer.start(mockListener)
    
    // Connect real client
    val transport = WsTransport(okHttpClient, frameCodec)
    transport.connect("ws://localhost:$port", "123456")
    
    // Verify message roundtrip
    assertTrue(receivedState.await(3, TimeUnit.SECONDS))
}
```

---

## 📊 **测试覆盖率检查**

### **Gradle Jacoco 配置**

```gradle
// app/build.gradle.kts
plugins {
    id("jacoco")
}

tasks.register<JacocoReport>("jacocoTestReport") {
    dependsOn("testDebugUnitTest")
    
    reports {
        xml.required.set(true)
        html.required.set(true)
    }
    
    sourceDirectories.setFrom(files("src/main/java"))
    classDirectories.setFrom(files("build/intermediates/javac/debug/compileDebugJavaWithJavac/classes"))
    executionData.setFrom(fileTree("build").include("**/outputs/**/*"))
}
```

### **覆盖率阈值**

| 模块 | 最低覆盖率 | 目标覆盖率 |
|------|----------|----------|
| **ViewModels** | 60% | 80% |
| **Repositories** | 70% | 90% |
| **Models** | 80% | 100% |
| **UI Components** | 50% | 70% |

---

## 🐛 **常见问题调试**

### **Q1: Hilt 注入失败？**

```bash
# 清理缓存
./gradlew clean
rm -rf .gradle
./gradlew build

# 检查 module 依赖是否正确
dependencies {
    implementation "com.google.dagger:hilt-android:2.54"
    kapt "com.google.dagger:hilt-android-compiler:2.54"
}
```

### **Q2: Coroutines Test Timeout?**

```kotlin
@Test(timeout = 5000)
fun `slowOperation_timeout_handling()`() = runTest {
    // Use dispatchers to control time
    advanceTimeBy(100)
}
```

### **Q3: WebSocket Listener Leak?**

```kotlin
// Always cancel in after block
lifecycleScope.launch {
    try {
        transport.connect(url, pin)
    } finally {
        transport.cancel()
    }
}
```

---

## 🚀 **下一步扩展计划**

### **立即实现（本周）**
- [ ] PickViewModel 测试用例（搜索逻辑）
- [ ] LyricsViewModel 测试用例（时间轴计算）
- [ ] Reconnect Integration Test（指数退避验证）
- [ ] UI 组件测试（Compose Testing）

### **短期优化（下周）**
- [ ] End-to-End Host 真机联调测试
- [ ] Performance 基准测试（冷启动速度）
- [ ] Memory leak detector（LeakCanary 集成）
- [ ] Network conditions simulation（弱网测试）

### **长期规划（本月）**
- [ ] CI/CD自动化测试流水线
- [ ] Firebase Test Lab集成（真机农场）
- [ ] Automated UI Tests（Espresso）
- [ ] A/B Test 框架集成

---

## 📝 **测试 checklist（发布前必须完成）**

```
[ ] 所有单元测试通过 (./gradlew test)
[ ] 集成测试通过 (./gradlew connectedCheck)
[ ] 覆盖率 ≥ 70% (./gradlew jacocoTestReport)
[ ] Lint 检查无错误 (./gradlew lint)
[ ] Release 构建成功 (./gradlew assembleRelease)
[ ] ProGuard 混淆正确 (no reflection errors)
[ ] 真机测试通过（至少 2 款设备）
[ ] 网络切换测试（WiFi→4G→无网）
[ ] 内存泄漏检测通过（LeakCanary）
[ ] Crash-free rate ≥ 99.9%
```

---

## 📞 **支持资源**

- [Kotlin Coroutines Testing Docs](https://kotlinlang.org/docs/coroutines-test.html)
- [Hilt Testing Guide](https://developer.android.com/training/dependency-injection/hilt-testing)
- [Android Compose Testing](https://developer.android.com/jetpack/compose/testing)
- [Firebase Test Lab](https://firebase.google.com/docs/test-lab)

---

**测试是质量保证的第一道防线！持续完善测试体系，提升代码健壮性。** 🎯
