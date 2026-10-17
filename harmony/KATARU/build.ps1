# KATARU HarmonyOS 命令行构建脚本
# 用法：.\build.ps1            编译 HAP（未签名）
#       .\build.ps1 -Clean     先清理再编译
#
# 注意：本文件必须保存为 UTF-8 **带 BOM**，否则 Windows PowerShell 5.1 会把中文按 GBK 读，直接语法报错。
param([switch]$Clean)

$ErrorActionPreference = 'Stop'

# DevEco Studio 安装位置（装在别处就改这里）
$DevEco = 'D:\HormanyOS\DevEco Studio'

if (-not (Test-Path $DevEco)) {
    throw "找不到 DevEco Studio: $DevEco"
}

# 编译阶段需要 node，打包阶段需要 java —— 都用 DevEco 自带的
$env:JAVA_HOME = Join-Path $DevEco 'jbr'
$env:NODE_HOME = Join-Path $DevEco 'tools\node'
$env:DEVECO_SDK_HOME = Join-Path $DevEco 'sdk'
$env:PATH = @(
    (Join-Path $DevEco 'jbr\bin'),
    $env:NODE_HOME,
    (Join-Path $DevEco 'tools\ohpm\bin'),
    (Join-Path $DevEco 'tools\hvigor\bin'),
    $env:PATH
) -join ';'

Set-Location $PSScriptRoot

if ($Clean) {
    Write-Host '=== Clean build outputs ===' -ForegroundColor Yellow
    Remove-Item -Recurse -Force 'entry\build', 'build', '.hvigor', 'oh_modules' -ErrorAction SilentlyContinue
}

Write-Host '=== ohpm install ===' -ForegroundColor Cyan
& (Join-Path $DevEco 'tools\ohpm\bin\ohpm.bat') install --all
if ($LASTEXITCODE -ne 0) { throw 'ohpm install failed' }

Write-Host '=== hvigor assembleHap ===' -ForegroundColor Cyan
& (Join-Path $DevEco 'tools\hvigor\bin\hvigorw.bat') assembleHap --mode module -p product=default --no-daemon
$buildExit = $LASTEXITCODE

$hap = Get-ChildItem 'entry\build' -Recurse -Filter '*.hap' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($buildExit -ne 0) {
    Write-Host ''
    Write-Host 'BUILD FAILED - see ArkTS errors above' -ForegroundColor Red
    exit 1
}

if ($hap) {
    Write-Host ''
    Write-Host ('BUILD OK: ' + $hap.FullName) -ForegroundColor Green
    Write-Host ('    size: ' + [math]::Round($hap.Length / 1KB, 1) + ' KB')
}