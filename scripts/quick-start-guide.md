# 🚀 SongLink M2.5 - 真机测试快速启动指南

## ✅ **当前环境状态**

- ✅ Host 服务器：已启动 (监听 51999 端口)
- ✅ ADB 设备：dd359e08 (已授权 USB 调试)
- ⏸️ Android APK：需要手动编译或获取

---

## 📱 **Step 1: 安装 APK（任选其一）**

### **方式 A: 使用预编译的 APK（推荐）**

如果您有他人提供或之前编译过的 APK：

```bash
# 直接安装
adb install path/to/SongLink.apk
```

### **方式 B: 通过 Android Studio 编译**

1. 打开 Android Studio
2. File → Open → 选择 `mobile/android-remote`
3. 等待 Gradle Sync 完成
4. Build → Make Project (Ctrl+F9)
5. APK 位置：`app/build/outputs/apk/debug/app-debug.apk`
6. 拖拽到手机或用 adb install

### **方式 C: 使用 Web Debug APK**

如果上述方式都不可行，可以通过网页版模拟：

**在浏览器中打开：**
```
http://localhost:51999/docs  # Swagger API 文档
http://localhost:51999  # 管理后台主页
```

然后通过 Python Simulator 连接验证功能：

```python
cd E:\workspace\点歌台面板
python outputs/songlink_remote_sim.py ws://localhost:51999 123456
```

---

## 🔍 **Step 2: 执行 P0 功能测试**

### **P0-1: AudioPlaybackCapture 音频采集**

**手机端操作：**
1. 打开 SongLink App
2. Pair Page → 输入 Host URL (`ws://localhost:51999`) + PIN="123456"
3. 连接成功后进入 StreamPage
4. 点击"开始串流"按钮
5. 播放任意音乐 → 观察 CPU/Memory 指标

**PC 端监控：**
```bash
# Windows 资源管理器任务管理查看 dotnet 进程
# 或使用 Performance Monitor 工具
```

**预期结果：**
- ☑ CPU 使用率上升 5~15%
- ☑ Memory 增长 ~20MB
- ☑ 延迟 ≤200ms

**实测记录：**
| 指标 | 预期 | 实测 | 状态 |
|------|------|------|------|
| CPU | +5~15% | __% | ☐ Pass ☐ Fail |
| Memory | +20MB | __ MB | ☐ Pass ☐ Fail |
| 延迟 | ≤200ms | __ ms | ☐ Pass ☐ Fail |

---

### **P0-2: Bluetooth RFCOMM 双向配对**

**手机端操作：**
1. App → 选择"蓝牙连接"
2. 扫描附近设备
3. 显示 PIN 码 "123456"
4. PC 确认匹配

**PC 端配置（如需要）：**
```csharp
// 修改 HostConfig 启用蓝牙模式
--adapter=bluetooth-rfcomm --pin="123456"
```

**预期结果：**
- ☑ 扫描耗时 ≤5s
- ☑ 配对耗时 ≤10s
- ☑ 数据传输延迟 ≤50ms

**实测记录：**
| 指标 | 预期 | 实测 | 状态 |
|------|------|------|------|
| 扫描 | ≤5s | __ s | ☐ Pass ☐ Fail |
| 配对 | ≤10s | __ s | ☐ Pass ☐ Fail |
| 传输 | ≤50ms | __ ms | ☐ Pass ☐ Fail |

---

### **P0-3: QR Code 扫码配对**

**手机端操作：**
1. App 打开 Camera
2. 对准 Host 显示的 QR 码（http://localhost:51999/qr-pairing）
3. 自动识别 URL/PIN 并连接

**预期结果：**
- ☑ 识别准确率 100%
- ☑ 识别耗时 ≤2s

**实测记录：**
| 指标 | 预期 | 实测 | 状态 |
|------|------|------|------|
| 准确率 | 100% | __% | ☐ Pass ☐ Fail |
| 耗时 | ≤2s | __ s | ☐ Pass ☐ Fail |

---

### **P0-4: 实时频谱动画效果**

**手机端操作：**
1. Spectrum 页面保持 5 分钟
2. 观察动画流畅度
3. 记录掉帧次数和卡顿现象

**预期结果：**
- ☑ Canvas 渲染 ≥60 FPS
- ☑ 掉帧 ≤3 次/5min
- ☑ 触控延迟 ≤100ms

**实测记录：**
| 指标 | 预期 | 实测 | 状态 |
|------|------|------|------|
| 帧率 | ≥60 FPS | __ FPS | ☐ Pass ☐ Fail |
| 掉帧 | ≤3 次 | __ 次 | ☐ Pass ☐ Fail |
| 延迟 | ≤100ms | __ ms | ☐ Pass ☐ Fail |

---

## 🟡 **Step 3: 执行 P1 功能测试（建议验证）**

### **P1-5: 深色模式自动切换**

**操作步骤：**
1. 系统浅色主题 → App → 观察配色
2. 系统深色主题 → 重启 App → 再次观察

**实测记录：**
| 场景 | 预期 | 实测 | 状态 |
|------|------|------|------|
| Light Mode | 浅色调 | ☐ Yes ☐ No | ☐ Pass ☐ Fail |
| Dark Mode | 深色调 | ☐ Yes ☐ No | ☐ Pass ☐ Fail |
| Status Bar | 跟随主题 | ☐ Yes ☐ No | ☐ Pass ☐ Fail |

---

### **P1-6: 中/英文本地化完整性**

**操作步骤：**
1. 系统中文 → 截图所有文本
2. 系统英文 → 对比差异
3. 检查翻译完整性

**实测记录：**
- 主页面：__ /6 完整 ☐
- 控制页：__ /6 完整 ☐
- 设置页：__ /6 完整 ☐

---

### **P1-7: Excel 导出文件可读性**

**操作步骤：**
1. 录制至少 50 条播放历史
2. 点击"导出 Excel"
3. 用 Excel/WPS 打开检查

**实测记录：**
| 属性 | 预期 | 实测 | 状态 |
|------|------|------|------|
| 文件大小 | >1KB | __ KB | ☐ Pass ☐ Fail |
| 兼容性 | 完美兼容 | ☐ Yes ☐ No | ☐ Pass ☐ Fail |
| 列数 | ≥5 列 | __ 列 | ☐ Pass ☐ Fail |
| 行数 | =50 | __ 行 | ☐ Pass ☐ Fail |

---

### **P1-8: Web Admin 频谱可视化流畅度**

**操作步骤：**
1. 浏览器访问 localhost:51999
2. 打开 Spectrum 页面
3. Performance Monitor 分析

**实测记录：**
| 指标 | 预期 | 实测 | 状态 |
|------|------|------|------|
| 帧率 | ≥60 FPS | __ FPS | ☐ Pass ☐ Fail |
| CPU 占用 | ≤10% | __% | ☐ Pass ☐ Fail |
| GPU 加速 | Hardware | ☐ Yes ☐ No | ☐ Pass ☐ Fail |

---

## 📊 **最终测试报告模板**

完成以上所有测试后，请复制以下格式填写完整报告：

```markdown
# SongLink M2.5 真机测试总结报告

## 测试概览
- 测试日期：YYYY-MM-DD HH:MM
- 设备型号：_________________ (Android API ____)
- PC 环境：Windows ___ / .NET 8.0
- Host PIN: 123456

## P0 测试结果（必须全部通过）
| 测试项 | 预期 | 实测 | 状态 |
|--------|------|------|------|
| AudioPlaybackCapture | +5~15% CPU | __% | ☐ Pass ☐ Fail |
| Bluetooth Pairing | ≤10s | __s | ☐ Pass ☐ Fail |
| QR Code Scanning | 100% | __% | ☐ Pass ☐ Fail |
| Realtime Spectrum | ≥60 FPS | __FPS | ☐ Pass ☐ Fail |

**P0 通过率**: __/4 = ____%

## P1 测试结果
| 测试项 | 预期 | 实测 | 状态 |
|--------|------|------|------|
| Dark Mode | 正确切换 | ☑☒ | ☐ Pass ☐ Fail |
| I18n | 完整翻译 | ☑☒ | ☐ Pass ☐ Fail |
| Excel Export | 可读美观 | ☑☒ | ☐ Pass ☐ Fail |
| Web Admin Spectrum | ≥60 FPS | __FPS | ☐ Pass ☐ Fail |

**P1 通过率**: __/4 = ____%

## 发现的问题
### Blocker (必须修复)
1. _________________________

### Major (建议修复)
1. _________________________

## 最终结论
- ☑ 通过发布 (P0=100%, P1≥80%)
- ☐ 需进一步测试 (部分 P0 未通过)
- ☐ 不通过 (关键功能缺失)

## 备注
_______________________________________________
```

---

## 💡 **常见问题速查**

| 问题 | 解决方案 |
|------|---------|
| Host 启动失败 | 检查防火墙放行 TCP 51999 |
| ADB 无法识别设备 | 重新授权 USB 调试，重新插拔 |
| APK 安装失败 | 检查是否有同名包冲突 |
| Connection refused | 确认 Host 正常运行，防火墙未拦截 |
| QR Code 识别率低 | 确保光照充足，屏幕亮度调高 |

---

**祝测试顺利！有任何问题随时联系。** 🎵✨

*版本：v1.0 - 2026-10-12*
