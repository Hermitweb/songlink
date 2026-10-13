@echo off
chcp 65001 >nul
title SongLink M2.5 - 完整真机测试执行脚本

echo.
echo ========================================
echo   SongLink M2.5 真机测试自动化流程
echo ========================================
echo.
echo [当前 PC IP]: 192.168.15.205
echo [Host 端口]: 51999
echo [PIN 码]: 123456
echo.

REM === Step 1: 验证 Host 状态 ===
echo [步骤 1] 检查 Host 服务器状态...
netstat -ano | findstr ":51999" >nul 2>&1
if %errorlevel% equ 0 (
    echo [✓] Host 服务器已启动
) else (
    echo [!] Host 未运行，正在尝试启动...
    cd "E:\workspace\点歌台面板"
    start "" dotnet run --project src/SongLink.Host.Core/SongLink.Host.Core.csproj ^
        --adapter=fake --pin="123456"
    timeout /t 5 /nobreak >nul
    echo [等待 Host 完全启动...]
)
echo.

REM === Step 2: 验证 ADB 设备 ===
echo [步骤 2] 检查 Android 设备连接...
adb devices | findstr "device" >nul 2>&1
if %errorlevel% equ 0 (
    echo [✓] Android 设备已连接并授权
    adb devices
) else (
    echo [✗] 设备未连接或未被授权
    echo      请确认：\n  1. USB 调试已开启\n  2. 手机端点击了"允许 USB 调试"\n  3. 数据线连接正常
    pause
    exit /b 1
)
echo.

REM === Step 3: APK 安装引导 ===
echo [步骤 3] 安装 APK 引导
echo.
echo 请选择 APK 安装方式：
echo   A. 自动尝试从本地编译目录安装
echo   B. 手动复制 APK 到手机后自行安装
echo   C. 跳过 APK 安装（使用 Python Simulator 替代）
set /p choice=输入选项 (A/B/C): 

if /i "%choice%"=="A" goto INSTALL_LOCAL
if /i "%choice%"=="B" goto MANUAL_INSTALL
if /i "%choice%"=="C" goto SKIP_INSTALL

REM === 自动安装本地 APK ===
:INSTALL_LOCAL
echo [自动尝试安装本地 APK...]
cd mobile/android-remote/app
if exist build/outputs/apk/debug/app-debug.apk (
    echo [正在安装...]
    adb install -r build/outputs/apk/debug/app-debug.apk
    if %errorlevel% equ 0 (
        echo [✓] APK 安装成功
    ) else (
        echo [⚠] 安装失败，将手动提示您操作
        goto MANUAL_INSTALL
    )
) else (
    echo [⚠] 未找到本地 APK，跳转到手动安装步骤
)
cd ../../scripts
goto STEP4

REM === 手动安装 APK ===
:MANUAL_INSTALL
echo.
echo ========================================
echo   请手动完成以下操作：
echo ========================================
echo.
echo 方式 1: 通过 Android Studio 编译
echo   1. 打开 Android Studio -> mobile/android-remote
echo   2. Build -> Make Project
echo   3. 定位到：app/build/outputs/apk/debug/app-debug.apk
echo   4. 拖拽到手机上或执行：adb install <apk-path>
echo.
echo 方式 2: 获取预编译 APK
echo   从其他地方获取 apk 文件后执行：
echo   adb install /path/to/app-debug.apk
echo.
echo 完成后请按任意键继续...
pause
goto STEP4

REM === 跳过 APK 安装 ===
:SKIP_INSTALL
echo [提示] 将使用 Python Simulator 进行虚拟测试
echo        如需真机测试，请先安装 APK
pause
goto STEP4

REM === Step 4: P0-P1 测试流程 ===
:STEP4
echo.
echo ========================================
echo   开始 P0/P1 功能验证测试
echo ========================================
echo.

type checklists.txt

echo.
echo ========================================
echo [重要说明]
echo ========================================
echo.
echo 请在手机上依次完成以下测试项：
echo.
echo 🔴 P0(必须全部通过):
echo   1. AudioPlaybackCapture 音频采集
echo   2. Bluetooth RFCOMM 双向配对  
echo   3. QR Code 扫码配对交互
echo   4. 实时频谱动画效果
echo.
echo 🟡 P1(建议通过):
echo   5. 深色模式自动切换
echo   6. 中/英文本地化完整性
echo   7. Excel 导出文件可读性
echo   8. Web Admin 频谱可视化流畅度
echo.
echo 每完成一项，请记录实测数据到：
echo   docs/真机测试验证报告.md
echo.
echo ========================================
echo 完成后请输入"FINISHED"以生成总结报告
echo ========================================
echo.

REM 等待用户输入
:WAIT_FOR_INPUT
set input=
set /p input=请输入"FINISHED"以完成测试：
if /i not "%input%"=="FINISHED" goto WAIT_FOR_INPUT

REM === 生成最终报告 ===
echo.
echo ========================================
echo   生成最终测试报告...
echo ========================================
echo.

REM 这里可以在将来扩展为自动分析测试数据和生成 HTML 报告
echo.
echo ✓ 测试流程已完成！
echo.
echo 请检查以下内容：
echo   1. docs/真机测试验证报告.md 中的实测数据
echo   2. 计算 P0/P1 通过率
echo   3. 根据测试结果做发布决策
echo.
echo 如果 P0=100% 且 P1≥80%，则可以发布！
echo.
pause

exit /b 0
