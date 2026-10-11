# TrayStats 编译脚本
# 使用 Windows 系统自带的 .NET Framework 4.8 编译器（csc.exe），无需安装任何 SDK。
# 用法：pwsh -File build.ps1        （编译到 build\TrayStats.exe）
#       pwsh -File package.ps1      （编译 + 打包便携版 zip 和 Setup.exe）

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# 优先 64 位编译器，其次 32 位
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "未找到系统自带编译器 csc.exe（.NET Framework 4.x）。Windows 10/11 默认自带，请检查系统完整性。"
}

$outDir = Join-Path $here 'build'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

Write-Host "编译器: $csc"
& $csc /nologo /codepage:65001 /target:winexe /out:"$outDir\TrayStats.exe" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $here 'src\TrayStats.cs')

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "编译成功: $outDir\TrayStats.exe" -ForegroundColor Green
    Write-Host "运行 'TrayStats.exe test' 可做无界面自检；pwsh -File package.ps1 可打包安装包。"
} else {
    Write-Host "编译失败（退出码 $LASTEXITCODE）" -ForegroundColor Red
    exit 1
}
