# 📱 SongLink Android APK 安装指南

## ⚠️ 当前状态
- Host 服务器已启动（监听 51999 端口）✅
- ADB 设备连接成功（dd359e08）✅
- APK 需要编译或获取 ❌

---

## 🔧 方案选择（推荐按顺序尝试）

### **方案 A: 使用 GitHub Actions 编译 APK（最快）**

#### Step 1: 访问 GitHub 仓库页面
```
https://github.com/your-repo/mobile/android-remote
```

#### Step 2: 触发自动编译
```yaml
# 在 workflows 目录创建 trigger-build.yml
name: Build APK Trigger
on:
  workflow_dispatch:
    inputs:
      version:
        description: 'Version'
        required: false
        default: '1.0.0-test'

jobs:
  build-apk:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Set up JDK
        uses: actions/setup-java@v3
        with:
          java-version: '17'
          distribution: 'temurin'
      
      - name: Build APK
        working-directory: ./mobile/android-remote
        run: |
          chmod +x gradlew
          ./gradlew assembleDebug
      
      - name: Upload APK
        uses: actions/upload-artifact@v4
        with:
          name: songlink-debug
          path: mobile/android-remote/app/build/outputs/apk/debug/*.apk
```

#### Step 3: 下载 APK
- 运行 Workflow 后点击"Build APK Trigger"
- Artifact 中下载 `songlink-debug.apk`
- 发送到手机安装

---

### **方案 B: 使用 Android Studio 快速编译（推荐）**

#### Step 1: 打开项目
```
Android Studio → Open Existing Project → mobile/android-remote
```

#### Step 2: Sync & Build
1. 等待 Gradle Sync 完成（约 2-5 分钟）
2. Build → Make Project (Ctrl+F9)
3. 等待编译完成

#### Step 3: 找到 APK
```
路径：mobile/android-remote/app/build/outputs/apk/debug/app-debug.apk
```

#### Step 4: 安装
```bash
adb install mobile/android-remote/app/build/outputs/apk/debug/app-debug.apk
```

---

### **方案 C: 直接拖拽到手机（最简单）**

如果已有 APK 文件：
1. USB 数据线连接手机
2. 将 APK 文件复制到手机相册或下载文件夹
3. 用手机文件管理器打开 APK
4. 点击安装

---

### **方案 D: 使用预编译 APK（如有）**

如果同事或团队成员有之前编译的 APK：
```bash
# 发送 APK 到您电脑
scp user@host:/path/to/SongLink.apk ./temp/SongLink.apk

# 安装
adb install temp/SongLink.apk
```

---

## 📲 安装后操作步骤

### **Step 1: 授予权限**
打开 App → 允许录音权限（AudioPlaybackCapture 必需）

### **Step 2: 连接 Host**
1. Pair Page → 输入：
   ```
   Host URL: ws://localhost:51999
   PIN: 123456
   ```
2. 点击"连接"按钮
3. 成功后进入主界面

### **Step 3: 开始测试**
按照 P0/P1测试清单逐项执行

---

## 💡 常见问题

| 问题 | 解决方案 |
|------|---------|
| ADB 授权失败 | 手机端重新勾选"允许 USB 调试" |
| APK 无法安装 | 检查"未知来源"开关已开启 |
| 连接超时 | 确认 Host 正在运行（任务管理器→dotnet 进程） |
| AudioPlaybackCapture 无声音 | 确认 APP 支持音频捕获（QQ 音乐/网易云可行） |

---

## ✅ 推荐流程

1. **最快方案**: 方案 A（GitHub Actions，无需本地环境）
2. **最可靠方案**: 方案 B（Android Studio 保证质量）
3. **临时方案**: 方案 C/D（如果有现成 APK）

---

*版本：v1.0 - 2026-10-12*  
*完成后继续执行 P0/P1测试*
