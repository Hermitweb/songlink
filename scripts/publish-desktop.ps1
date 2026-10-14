# SongLink 点歌台桌面 app 发布脚本
#
# 输出: dist/SongLink.exe — 单文件、自包含、win-x64
#       用户双击就能跑,不依赖任何已装组件(.NET / WebView2 evergreen 自动下载)
#
# 用法:
#   powershell -ExecutionPolicy Bypass -File scripts/publish-desktop.ps1
#
# 可选环境变量:
#   SONGLINK_RID     win-x64 (默认) | win-arm64
#   SONGLINK_CONFIG  Release (默认) | Debug

$ErrorActionPreference = 'Stop'

$root      = Split-Path -Parent $PSScriptRoot
$project   = Join-Path $root 'src\SongLink.Host.Desktop\SongLink.Host.Desktop.csproj'
$distDir   = Join-Path $root 'dist'
$rid       = if ($env:SONGLINK_RID)    { $env:SONGLINK_RID }    else { 'win-x64' }
$config    = if ($env:SONGLINK_CONFIG) { $env:SONGLINK_CONFIG } else { 'Release' }

if (Test-Path $distDir) {
    Write-Host "[publish-desktop] 清空 dist/" -ForegroundColor Yellow
    Get-ChildItem -Path $distDir -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

$outPath = Join-Path $distDir ("SongLink-{0}" -f $rid)
$pubArgs = @(
    'publish', $project
    '-c', $config
    '-r', $rid
    '-o', $outPath
    '--self-contained', 'true'
    '-p:PublishSingleFile=true'
    '-p:IncludeAllContentForSelfExtract=true'
    '-p:EnableCompressionInSingleFile=true'
    '-p:DebugType=embedded'
    '-p:Version=1.0.0'
    '-p:InformationalVersion=1.0.0'
    '-p:NoWarn=NU1603'
    '--nologo'
)

Write-Host "[publish-desktop] dotnet $($pubArgs -join ' ')" -ForegroundColor Cyan
& dotnet @pubArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish 失败 (exit $LASTEXITCODE)"
}

# csproj <AssemblyName>SongLink</AssemblyName> 让 PublishSingleFile 直接产出 SongLink.exe
$exe = Join-Path $outPath 'SongLink.exe'
if (-not (Test-Path $exe)) {
    Write-Error "publish 产物里没找到 SongLink.exe at $exe"
}
$size = (Get-Item $exe).Length
$sizeMb = [Math]::Round($size / 1MB, 1)
Write-Host "[publish-desktop] 成功: $exe ($sizeMb MB)" -ForegroundColor Green
Write-Host "[publish-desktop] 双击即可在任意 Windows $rid 机器上跑" -ForegroundColor Green
