# third_party/opus — Xiph Opus 1.4 C 源码（官方发行版）

来源：<https://github.com/xiph/opus> tag `v1.4`（2018 官方 release，OPUS 标准
RFC7845 的参考实现）。BSD-3-Clause，见 `COPYING`。

## 为什么进仓

Android 遥控端要 Opus 编码手机音频（架构 §3.7：PCM 经 Opus 走 UDP 串流，
48kHz 立体声约 128–256 kbps，Wi-Fi 无压力；裸 PCM 同一链路要 ~1.5 Mbps）。
仓库不引入预编译 .so（平台/版本漂移风险 + 4 ABI 体积），改为**源码进仓、
NDK 现编**：

- `mobile/android-remote/app/src/main/cpp/CMakeLists.txt` 直接
  `add_subdirectory` 本目录的官方 CMakeLists 出静态库 `opus`，
  再编一个 20 行左右的 JNI 薄壳（`opus_jni.c`）打包成
  `libsonglink_opus.so`。
- Kotlin 侧 `OpusJni` 加载失败/初始化失败时**自动回退裸 PCM 直通**
  （flags bit2=1，Host 已支持），缺 NDK 的机器 APK 照样能出、链路照样通。

## 复现构建

```
mobile/android-remote/gradlew :app:externalNativeBuildDebug   # 需本地 NDK + CMake
```
