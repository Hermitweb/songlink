# SongLink Remote — native 构建：Opus 编码器（NDK 现编，源码 third_party/opus）
#
# 出两个东西：
#   1. opus —— Xiph 官方 C 源码编出的静态库（add_subdirectory 官方 CMakeLists）
#   2. songlink_opus —— 20 行 JNI 薄壳 + opus 静态库链接成 libsonglink_opus.so
#
# Kotlin 侧 OpusJni 加载该 .so 并编码；loadLibrary 失败时自动回退裸 PCM 直通
# （flags bit2=1，Host 已支持），所以没有 NDK 的机器 APK 照样能出、链路照样通。

cmake_minimum_required(VERSION 3.22)

# 官方 CMakeLists 的默认项会尝试装 pkg-config / 编 demo（都关掉）
set(OPUS_BUILD_SHARED_LIBRARY OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_PROGRAMS OFF CACHE BOOL "" FORCE)
set(OPUS_CUSTOM_MODES OFF CACHE BOOL "" FORCE)
set(OPUS_INSTALL_PKG_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
set(OPUS_INSTALL_CMAKE_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
# ARM/Android 无 SSE 等 intrinsics 依赖问题，float 实现即可
set(OPUS_FIXED_POINT OFF CACHE BOOL "" FORCE)

add_subdirectory(${CMAKE_CURRENT_LIST_DIR}/../../../../../../third_party/opus ${CMAKE_BINARY_DIR}/opus)

add_library(songlink_opus SHARED opus_jni.c)
target_link_libraries(songlink_opus opus)
