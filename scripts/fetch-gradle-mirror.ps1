#!/usr/bin/env pwsh
<#
.SYNOPSIS
    在 Gradle 官方分发地址不可达时，从国内镜像把 Gradle 发行包塞进 wrapper 缓存。

.DESCRIPTION
    本机网络下 services.gradle.org 会 302 到 github.com 的 CDN，而 github.com 不通，
    所以 gradlew 会卡在 "Downloading gradle-8.6-bin.zip" 然后 60 秒超时
    （networkTimeout=60000 就是为此放宽过的）。

    Google Maven(dl.google.com) 和 Maven Central 本身是通的，所以真正卡住的只有
    Gradle 发行包这一个文件。本脚本把它下载到 wrapper 认的缓存目录里，
    gradlew 下次就会直接解压使用，不再联网。

    之后跑 gradlew 时如果还缺依赖（AGP / Kotlin / AndroidX），走官方源就行，
    不需要额外配置镜像。

.PARAMETER ProjectPath
    含 gradle/wrapper/gradle-wrapper.properties 的 Android 工程目录。
    默认取 mobile/android-remote。

.EXAMPLE
    pwsh -File scripts/fetch-gradle-mirror.ps1
    pwsh -File scripts/fetch-gradle-mirror.ps1 -ProjectPath mobile/android-remote
#>
[CmdletBinding()]
param(
    [string]$ProjectPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'mobile\android-remote'),
    [string]$Mirror = 'https://mirrors.cloud.tencent.com/gradle'
)

$ErrorActionPreference = 'Stop'

$propsFile = Join-Path $ProjectPath 'gradle\wrapper\gradle-wrapper.properties'
if (-not (Test-Path $propsFile)) {
    throw "找不到 $propsFile"
}

$distributionUrl = (Select-String -Path $propsFile -Pattern '^distributionUrl=(.+)$').Matches[0].Groups[1].Value.Trim()
if (-not $distributionUrl) { throw "distributionUrl 解析失败" }
Write-Host "[gradle-mirror] distributionUrl = $distributionUrl"

# 从 URL 反推发行包名，例如 gradle-8.6-bin.zip
$fileName = Split-Path -Leaf ($distributionUrl -replace '\\', '/')
if ($fileName -notmatch '^(gradle-[\d.]+-(bin|all)\.zip)$') {
    throw "无法从 URL 推断发行包名: $fileName"
}
$version = ($fileName -replace '^gradle-', '' -replace '-(bin|all)\.zip$', '')

# wrapper 把发行包放在 ~/.gradle/wrapper/dists/<name>/<hash>/ 下，<hash> 由 URL 决定。
# 我们直接复用已存在的目录（哪怕是上次下载失败留下的 .part），这样不用复刻 hash 算法。
$distsRoot = Join-Path $env:USERPROFILE ".gradle\wrapper\dists"
$distDir = Join-Path $distsRoot ($fileName -replace '\.zip$', '')
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
}
$hashDir = Get-ChildItem $distDir -Directory | Select-Object -First 1
if (-not $hashDir) {
    $hashDir = New-Item -ItemType Directory -Force -Path (Join-Path $distDir 'local')
}

$zipPath = Join-Path $hashDir.FullName $fileName
Write-Host "[gradle-mirror] 目标: $zipPath"

$mirrorUrl = "$Mirror/$fileName"
Write-Host "[gradle-mirror] 从镜像下载: $mirrorUrl"
$sw = [Diagnostics.Stopwatch]::StartNew()
Invoke-WebRequest -Uri $mirrorUrl -OutFile $zipPath -TimeoutSec 900 -UseBasicParsing
$sw.Stop()
Write-Host ("[gradle-mirror] 下载完成 {0:N1} MB，用时 {1:N1}s" -f ((Get-Item $zipPath).Length / 1MB), $sw.Elapsed.TotalSeconds)

# 校验 zip 能正常打开（避免下到一半的坏包）
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
$count = $zip.Entries.Count
$zip.Dispose()
Write-Host "[gradle-mirror] zip 校验通过，$count 个条目"

Write-Host "[gradle-mirror] 现在可以跑: cd $ProjectPath; .\gradlew.bat :app:assembleDebug" -ForegroundColor Green
