@echo off
chcp 65001 >nul
REM 延迟扩展：下方 if/else 代码块中要用 !answer! 读取 set /p 的输入
setlocal EnableDelayedExpansion
title SongLink M2.5 - 真机测试自动化脚本

echo.
echo ========================================
echo   SongLink M2.5 - 真机测试启动脚本
echo ========================================
echo.

REM === 检查前置条件 ===
echo [步骤 1] 检查前置环境...

REM 检查 .NET 运行时
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [错误] 未检测到 .NET 8.0，请先安装.NET 8.0 Desktop Runtime
    pause
    exit /b 1
)

REM 检查 ADB（Android Debug Bridge）
adb version >nul 2>&1
if %errorlevel% neq 0 (
    echo [警告] 未检测到 ADB，将跳过 Android 自动测试环节
    echo        请手动安装 Android SDK Platform Tools
) else (
    echo [✓] ADB 已安装，可以连接 Android 设备
)

REM 检查 Host 是否在运行
netstat -ano | findstr ":51999" >nul 2>&1
if %errorlevel% equ 0 (
    echo [✓] Host 已在运行（端口 51999）
) else (
    echo [!] Host 未启动，是否现在启动？ (Y/N):
    set /p answer=Y
    if /i not "!answer!"=="Y" exit /b 1
    
    cd "src\SongLink.Host.Core"
    dotnet run --adapter=fake --pin="123456"
    if %errorlevel% neq 0 (
        echo [错误] Host 启动失败
        exit /b 1
    )
    cd "..\.."
)

echo.
echo [✓] 所有前置条件检查通过!
echo.

REM === 开始测试流程 ===
echo 请选择测试模式:
echo   A: 完整自动化测试 (需要真机)
echo   B: 核心功能手动验证
set /p mode="输入选项 (A/B/AUTO/MANUAL): "

if /i not "%mode%"=="A" and not "%mode%"=="B" and not "%mode%"=="AUTO" and not "%mode%"=="MANUAL" (
    echo [错误] 无效的选项，请输入 A、B、AUTO 或 MANUAL
    pause
    exit /b 1
)

if /i "%mode%"=="A" goto RUN_AUTO_TESTS
if /i "%mode%"=="B" goto RUN_MANUAL_CHECK

REM === AUTO 模式 ===
:RUN_AUTO_TESTS
echo.
echo [AUTO MODE] 启动自动化测试...
echo.

REM 检查设备连接
adb devices | findstr "device" >nul 2>&1
if %errorlevel% neq 0 (
    echo [错误] 未检测到连接的 Android 设备
    echo 请确认:
    echo   1. 设备 USB 调试已开启
    echo   2. 授权此电脑
    pause
    exit /b 1
)

echo [✓] 检测到 Android 设备
adb devices | findstr "device"

REM 安装 APK（仓库内不含预编译 APK，需先自行构建）
cd mobile/android-remote/app
set "APK_PATH=build\outputs\apk\debug\app-debug.apk"
if not exist "%APK_PATH%" (
    echo [错误] 未找到 APK: mobile\android-remote\app\%APK_PATH%
    echo.
    echo 仓库中不包含预编译的 .apk 文件，请先构建:
    echo   cd mobile\android-remote
    echo   gradlew.bat :app:assembleDebug
    echo.
    echo 构建产物路径:
    echo   mobile\android-remote\app\build\outputs\apk\debug\app-debug.apk
    echo.
    echo 也可以改用 MANUAL 模式手工验证。
    cd "..\..\..\scripts"
    pause
    exit /b 1
)

echo [正在安装 SongLink Remote APK...]
adb install -r "%APK_PATH%"
if %errorlevel% neq 0 (
    echo [错误] APK 安装失败
    cd "..\..\..\scripts"
    exit /b 1
)
cd "..\..\..\scripts"

echo [✓] APK 安装成功
echo.
echo [提示] 请在手机上打开 SongLink App 并手动完成配对流程
echo        然后返回此窗口查看测试结果...
pause

REM 等待用户手动操作
echo.
echo [等待中] 请在手机上完成以下操作:
echo   1. 打开 SongLink App
echo   2. Pair Page -^> 输入 host URL 和 PIN
echo   3. 连接成功后进入 StreamPage -^> 点击"开始串流"
echo   4. 保持串流状态至少 30 秒
pause

goto SHOW_SUMMARY

REM === MANUAL 模式 ===
:RUN_MANUAL_CHECK
echo.
echo [MANUAL CHECK] 开始手动验证流程...
echo.

type checklists.txt
echo.
echo 请按以下步骤逐一验证:
echo   [P0-1] AudioPlaybackCapture 音频采集
echo   [P0-2] Bluetooth RFCOMM 配对
echo   [P0-3] QR Code 扫码配对
echo   [P0-4] 实时频谱可视化
echo.
echo 每完成一项，记录实测数据到 真机测试最终报告.md（唯一权威报告）
echo.
pause

REM 显示总结模板
:SHOW_SUMMARY
echo.
echo ========================================
echo   测试总结模板
echo ========================================
echo.
echo 请将你的测试结果填入 真机测试最终报告.md（唯一权威报告）
echo.
echo 示例记录格式:
echo   ^| 测试项 ^| 预期 ^| 实测 ^| 结论 ^|
echo   ^| CPU 提升 ^| Pass ^|
echo.
echo ========================================
echo   测试完成后请提交报告！
echo ========================================

pause

exit /b 0
