# TrayStats 打包脚本：生成便携版 zip 和 Setup.exe 安装包
# 用法：pwsh -File package.ps1 [-Version 1.0.0]
# 产物（dist\ 目录）：
#   TrayStats-<版本>-portable.zip   便携版，解压双击即用
#   TrayStats-<版本>-Setup.exe      安装包，双击安装到当前用户目录（SetupBuilder.cs 编译生成）
param([string]$Version = "1.0.0")

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$build = Join-Path $root 'build'
$dist = Join-Path $root 'dist'
$installer = Join-Path $root 'installer'

# 系统自带 .NET Framework 编译器
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw '未找到系统自带编译器 csc.exe' }

if (-not (Test-Path (Join-Path $build 'TrayStats.exe'))) {
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw '编译失败，打包中止' }
}
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

# ---------- 1) 便携版 zip ----------
$portable = Join-Path $dist "TrayStats-$Version-portable.zip"
Compress-Archive -Force `
    -Path (Join-Path $build 'TrayStats.exe'), (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') `
    -DestinationPath $portable
Write-Host "便携版已生成: $portable"

# ---------- 2) Setup.exe（安装器生成器 → 自包含安装包）----------
$builder = Join-Path $build 'SetupBuilder.exe'
& $csc /nologo /target:winexe /codepage:65001 /out:"$builder" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $installer 'SetupBuilder.cs')
if ($LASTEXITCODE -ne 0) { throw '安装器编译失败' }
Start-Sleep -Milliseconds 500   # 等待杀毒软件释放刚编译的文件

$setup = Join-Path $dist "TrayStats-$Version-Setup.exe"
& $builder /make "$setup" (Join-Path $build 'TrayStats.exe') $Version
if (-not (Test-Path $setup)) { throw 'Setup.exe 生成失败' }

Write-Host ""
Write-Host "全部产物（$dist）：" -ForegroundColor Green
Get-ChildItem $dist | ForEach-Object { Write-Host ("  " + $_.Name + "  (" + [math]::Round($_.Length/1KB) + " KB)") }
