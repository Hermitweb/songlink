@echo off
chcp 65001 >nul
title SongLink Host - QQ 音乐适配器

echo ========================================
echo   SongLink M2 MVP - QQ 音乐真机校准版
echo ========================================
echo.
echo [准备] 正在构建项目...
dotnet build src/SongLink.Host.Core/SongLink.Host.Core.csproj -c Release --no-incremental

if %errorlevel% neq 0 (
    echo [错误] 编译失败！
    pause
    exit /b 1
)

echo.
echo [启动] 使用 QQMusic 适配器 + OCR 兜底...
echo.

dotnet run --project src/SongLink.Host.Core/SongLink.Host.Core.csproj ^
    --adapter=uia ^
    --descriptor=src\SongLink.Host.Adapters.UiaPick\descriptors\qqmusic.json ^
    --ocr-assembly=bin\x64\Release\net8.0-windows\SongLink.Host.Adapters.UiaPick.dll ^
    --ocr-type=SongLink.Host.Adapters.UiaPick.Ocr.StaticOcrEngine

pause
