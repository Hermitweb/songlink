@echo off
cd /d E:\workspace\点歌台面板

echo [1/2] 编译项目...
dotnet build src/SongLink.Host.Core/SongLink.Host.Core.csproj -c Release --no-incremental

echo [2/2] 启动 Host (QQ 音乐 + OCR)...
dotnet run --project src/SongLink.Host.Core/SongLink.Host.Core.csproj ^
  --adapter=uia ^
  --descriptor=src\SongLink.Host.Adapters.UiaPick\descriptors\qqmusic.json ^
  --ocr-assembly=bin\x64\Release\net8.0-windows\SongLink.Host.Adapters.UiaPick.dll ^
  --ocr-type=SongLink.Host.Adapters.UiaPick.Ocr.StaticOcrEngine

pause
