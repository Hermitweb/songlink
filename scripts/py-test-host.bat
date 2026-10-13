@echo off
chcp 65001 >nul
title Python Remote Simulator - Host 核心功能验证

echo.
echo ========================================
echo   SongLink M2.5 - 核心功能验证
echo   (使用 Python Simulator 绕过 APK)
echo ========================================
echo.

REM === Step 1: 检查 Host 运行状态 ===
netstat -ano | findstr ":51999" >nul 2>&1
if %errorlevel% neq 0 (
    echo [提示] Host 未启动，正在尝试启动...
    cd src\SongLink.Host.Core
    dotnet run --adapter=fake --pin="123456"
    if %errorlevel% neq 0 (
        echo [错误] Host 启动失败，请修复编译问题
        pause
        exit /b 1
    )
) else (
    echo [✓] Host 服务器已运行在端口 51999
)
echo.

REM === Step 2: 启动 Python Simulator ===
echo [准备] Python Remote Simulator...
echo.
cd ..\..
python outputs\songlink_remote_sim.py ws://localhost:51999 123456

if %errorlevel% neq 0 (
    echo [错误] Python Simulator 运行失败
    echo        请确保：\n          1. Python 已安装\n          2. websockets 包已安装 (pip install websockets)
    pause
    exit /b 1
)

exit /b 0
