@echo off
chcp 65001 >nul
title SongLink M2.5 - 简化版真机测试执行器

echo.
echo ========================================
echo   SongLink M2.5 真机测试执行器
echo ========================================
echo.

REM === Step 1: Host 状态检查 ===
echo [步骤 1] 检查 Host 服务器...
powershell.exe -Command "& { Get-Process -Name 'dotnet' -ErrorAction SilentlyContinue | Measure-Object }" | findstr /i "count.*1\|Count.*[2-9]" >nul 2>&1
if %errorlevel% equ 0 (
    echo [✓] Host 已运行
) else (
    echo [!] 正在启动 Host...
    start "" dotnet run --project src/SongLink.Host.Core/SongLink.Host.Core.csproj -- adapter=fake --pin="123456"
    timeout /t 3 /nobreak >nul
    echo [等待 Host 完全启动...]
)
echo.

REM === Step 2: ADB 设备检查 ===
echo [步骤 2] 检查 Android 设备...
adb devices | findstr "device" >nul 2>&1
if %errorlevel% equ 0 (
    echo [✓] 设备已连接并授权
) else (
    echo [✗] 设备未连接或未被授权
    echo      请先在手机上点击允许 USB 调试，然后按任意键重试...
    pause
    goto CHECK_ADB
)
echo.

:CHECK_ADB
adb devices | findstr "device" >nul 2>&1
if %errorlevel% neq 0 (
    echo [错误] 设备仍未连接
    pause
    exit /b 1
)
echo.

REM === Step 3: APK 安装检查 ===
echo [步骤 3] APK 安装检查...
set /p has_apk="是否有 APK 文件可以安装？(Y/N): "
if /i "%has_apk%"=="Y" goto INSTALL_APK
if /i "%has_apk%"=="N" goto NO_APK

:INSTALL_APK
set /p apk_path="APK 文件路径： "
adb install -r "%apk_path%"
if %errorlevel% equ 0 (
    echo [✓] APK 安装成功
) else (
    echo [⚠] 安装失败，请检查 APK 文件是否正确
)
goto STEP4

:NO_APK
echo.
echo [提示] 请先通过以下方式之一安装 APK:
echo   1. 参考 scripts/apk-install-guide.md 中的指南
echo   2. 使用 Android Studio 编译 mobile/android-remote
echo   3. 从团队成员获取预编译的 APK
echo.
pause
goto END
STEP4

REM === Step 4: 测试流程说明 ===
echo.
echo ========================================
echo   开始 P0/P1功能测试
echo ========================================
echo.
echo 请按以下顺序在手机上操作：
echo.
echo 🔴 P0(必须全部通过):
echo   [ ] 1. AudioPlaybackCapture 音频采集
echo      - 打开 StreamPage → 开始串流 → 播放音乐
echo      - 记录 CPU/Memory/延迟数据
   
echo   [ ] 2. Bluetooth RFCOMM 双向配对
echo      - Host 切换蓝牙模式 → 手机端扫描 → 配对
   
echo   [ ] 3. QR Code 扫码配对
echo      - 浏览器访问 localhost:51999/qr-pairing  
echo      - 手机扫码 → 自动连接
   
echo   [ ] 4. 实时频谱动画效果
echo      - Spectrum 页面保持 5 分钟观察
echo      - 记录帧率/掉帧次数
   
echo.
echo 🟡 P1(建议通过):
echo   [ ] 5. 深色模式自动切换
echo      - 切换系统主题 → 观察配色变化
   
echo   [ ] 6. 中/英文本地化
echo      - 切换系统语言 → 对比文本翻译
   
echo   [ ] 7. Excel 导出可读性
echo      - 录制 50 条历史 → 导出 → Excel 打开检查
   
echo   [ ] 8. Web Admin 频谱流畅度
echo      - 浏览器访问 localhost:51999
echo      - 打开 Spectrum 页观察
echo.
echo ========================================
echo   测试结果记录表格
echo ========================================
echo.
echo 请在 docs/真机测试验证报告.md 中填写实测数据！
echo.
echo 每完成一项测试，记录如下信息：
echo | 测试项 | 预期结果 | 实测结果 | 状态 |
echo |--------|---------|----------|------|
echo | P0-1   | +5~15%  | __%     | ☐    |
echo.
echo ========================================
echo   完成后输入"FINISHED"生成总结报告
echo ========================================
echo.

REM === Step 5: 等待用户完成 ===
:WAIT_COMPLETE
set input=
set /p input=是否已完成所有测试？(是/否):
if /i not "%input%"=="是" goto WAIT_COMPLETE

echo.
echo ========================================
echo   正在生成测试总结报告...
echo ========================================
echo.

REM 这里将来扩展为自动生成 HTML 报告逻辑
echo ✓ 测试流程已完成！
echo.
echo 请检查以下内容:
echo   1. docs/真机测试验证报告.md 中的实测数据
echo   2. 计算 P0/P1通过率
echo   3. 根据测试结果做发布决策
echo.
echo 如果 P0=100% 且 P1≥80%，则可以发布！
echo.
pause

exit /b 0

END
echo 测试流程结束
exit /b 0
