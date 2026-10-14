# SongLink 点歌台 — PowerShell 安装脚本
# 零依赖便携安装:复制文件 + 桌面/开始菜单快捷方式 + 可选开机自启
#
# 用法:
#   powershell -ExecutionPolicy Bypass -File scripts\install.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -InstallDir "D:\Apps\SongLink"

param(
    [string]$InstallDir = "$env:LOCALAPPDATA\SongLink",
    [string]$SourceDir = "$PSScriptRoot\..\dist\SongLink-win-x64",
    [switch]$DesktopShortcut,
    [switch]$StartMenuShortcut,
    [switch]$StartWithWindows,
    [switch]$Force,
    [switch]$NoStart
)

# 默认值
if (-not $PSBoundParameters.ContainsKey('DesktopShortcut')) { $DesktopShortcut = $true }
if (-not $PSBoundParameters.ContainsKey('StartMenuShortcut')) { $StartMenuShortcut = $true }

$ErrorActionPreference = 'Stop'

Write-Host '[install] 启动' -ForegroundColor Cyan

# 检查源
$exe = Join-Path $SourceDir 'SongLink.exe'
if (-not (Test-Path $exe)) {
    Write-Host '[install] 错误: 找不到 SongLink.exe,请先跑 scripts/publish-desktop.ps1' -ForegroundColor Red
    exit 1
}

# 已装提示
$marker = Join-Path $InstallDir '.installed'
if ((Test-Path $marker) -and (-not $Force)) {
    $oldVer = Get-Content $marker -ErrorAction SilentlyContinue
    Write-Host "[install] 检测到已安装: $oldVer" -ForegroundColor Yellow
    $ans = Read-Host '[install] 覆盖升级? [Y/n]'
    if ($ans -eq 'n') { exit 0 }
    if ($ans -eq 'N') { exit 0 }
}

# 创建目录
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# 复制文件
Write-Host "[install] 复制到 $InstallDir" -ForegroundColor Cyan
$entries = Get-ChildItem -Path $SourceDir -Force
foreach ($e in $entries) {
    if ($e.Name -match '^\.\.?$') { continue }
    $dest = Join-Path $InstallDir $e.Name
    if ($e.PSIsContainer) {
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        Copy-Item -Path $e.FullName -Destination $dest -Recurse -Force
    } else {
        Copy-Item -Path $e.FullName -Destination $dest -Force
    }
}
$cnt = (Get-ChildItem $InstallDir -Recurse -File | Measure-Object).Count
Write-Host "[install] 已复制 $cnt 个文件" -ForegroundColor Green

# 写版本
$ver = '1.0.0'
try {
    $vi = (Get-Item $exe).VersionInfo
    if ($vi.FileVersion) { $ver = $vi.FileVersion }
} catch {}
Set-Content -Path $marker -Value $ver -Encoding UTF8 -NoNewline

# 复制 uninstall
Copy-Item -Path "$PSScriptRoot\uninstall.ps1" -Destination (Join-Path $InstallDir 'uninstall.ps1') -Force

# 快捷方式
$shell = New-Object -ComObject WScript.Shell
$dq = [char]34

if ($DesktopShortcut) {
    $desk = [Environment]::GetFolderPath('Desktop')
    $lnk = Join-Path $desk 'SongLink 点歌台.lnk'
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = Split-Path $exe -Parent
    $s.IconLocation = "$exe,0"
    $s.Description = 'SongLink 点歌台'
    $s.Save()
    Write-Host "[install] 桌面: $lnk" -ForegroundColor Green

    $uninstLnk = Join-Path $desk '卸载 SongLink.lnk'
    $uninstScript = Join-Path $InstallDir 'uninstall.ps1'
    $s = $shell.CreateShortcut($uninstLnk)
    $s.TargetPath = 'powershell.exe'
    $s.IconLocation = 'shell32.dll,27'
    $s.Arguments = "-NoProfile -ExecutionPolicy Bypass -File $dq$uninstScript$dq"
    $s.Description = '卸载 SongLink 点歌台'
    $s.Save()
}

if ($StartMenuShortcut) {
    $smDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\SongLink'
    if (-not (Test-Path $smDir)) { New-Item -ItemType Directory -Path $smDir -Force | Out-Null }
    $lnk = Join-Path $smDir 'SongLink 点歌台.lnk'
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = Split-Path $exe -Parent
    $s.IconLocation = "$exe,0"
    $s.Description = 'SongLink 点歌台'
    $s.Save()

    $uninstLnk = Join-Path $smDir '卸载 SongLink.lnk'
    $uninstScript = Join-Path $InstallDir 'uninstall.ps1'
    $s = $shell.CreateShortcut($uninstLnk)
    $s.TargetPath = 'powershell.exe'
    $s.IconLocation = 'shell32.dll,27'
    $s.Arguments = "-NoProfile -ExecutionPolicy Bypass -File $dq$uninstScript$dq"
    $s.Description = '卸载 SongLink 点歌台'
    $s.Save()
    Write-Host "[install] 开始菜单: $smDir" -ForegroundColor Green
}

# 开机自启
if ($StartWithWindows) {
    $reg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $val = "$dq$exe$dq"
    Set-ItemProperty -Path $reg -Name 'SongLink' -Value $val -ErrorAction Stop
    Write-Host '[install] 开机自启已设' -ForegroundColor Green
}

Write-Host ''
Write-Host '================================================================' -ForegroundColor Green
Write-Host '  SongLink 点歌台安装完成!' -ForegroundColor Green
Write-Host '================================================================' -ForegroundColor Green
Write-Host ''
Write-Host "  安装位置: $InstallDir"
Write-Host "  卸载: 桌面'卸载 SongLink.lnk' 或控制面板"
Write-Host ''

if (-not $NoStart) {
    $ans = Read-Host '现在启动 SongLink 吗? [Y/n]'
    if ($ans -ne 'n') {
        if ($ans -ne 'N') {
            Write-Host '[install] 启动中...' -ForegroundColor Cyan
            Start-Process -FilePath $exe
        }
    }
}
