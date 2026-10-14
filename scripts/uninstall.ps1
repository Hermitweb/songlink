# SongLink 点歌台 — 卸载脚本
# 复制到 $InstallDir 目录后从那里运行

$ErrorActionPreference = 'Stop'

# 这个脚本通常被放在 $InstallDir 里,直接从 $ScriptDir 拿
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# 如果在源码 scripts/ 跑(脚本目录下没有 SongLink.exe),问用户安装路径
if (-not (Test-Path (Join-Path $ScriptDir 'SongLink.exe'))) {
    $InstallDir = Read-Host '请输入 SongLink 安装路径(如 C:\Users\XXX\AppData\Local\SongLink)'
    if ([string]::IsNullOrWhiteSpace($InstallDir)) {
        Write-Host '[uninstall] 未指定路径,已取消' -ForegroundColor Yellow
        exit 1
    }
} else {
    $InstallDir = $ScriptDir
}

Write-Host "[uninstall] 准备卸载 SongLink 点歌台 (安装路径: $InstallDir)" -ForegroundColor Cyan

# 关闭运行中的实例
Get-Process -Name 'SongLink' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "[uninstall] 关闭进程 PID $($_.Id)"
    $_ | Stop-Process -Force
    Start-Sleep -Seconds 1
}

# 桌面快捷方式
$desktop = [Environment]::GetFolderPath('Desktop')
Remove-Item "$desktop\SongLink 点歌台.lnk" -ErrorAction SilentlyContinue
Remove-Item "$desktop\卸载 SongLink.lnk" -ErrorAction SilentlyContinue

# 开始菜单
$smStart = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\SongLink"
Remove-Item "$smStart\SongLink 点歌台.lnk" -ErrorAction SilentlyContinue
Remove-Item "$smStart\卸载 SongLink.lnk" -ErrorAction SilentlyContinue
Remove-Item "$smStart" -Recurse -ErrorAction SilentlyContinue

# 开机自启
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $run -Name 'SongLink' -ErrorAction SilentlyContinue

# 安装目录
if (Test-Path $InstallDir) {
    Get-ChildItem -Path $InstallDir -Recurse -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "[uninstall] 已删除 $InstallDir" -ForegroundColor Green
}

# 日志
$logDir = "$env:LOCALAPPDATA\SongLink"
if (Test-Path $logDir) { Remove-Item $logDir -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "[uninstall] 完成" -ForegroundColor Green
Write-Host "[uninstall] 提示:WebView2 / .NET 8 runtime 是系统级,不影响其他应用" -ForegroundColor Yellow
