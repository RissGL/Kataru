<#
.SYNOPSIS
    生成 MVP 验收用的测试音频：每条字幕开始的瞬间会有一声短促提示音。

.DESCRIPTION
    读取 test.srt 的时间轴，生成等长的 16bit / 单声道 WAV。
    听到"嘀"声时画面上应该正好切换字幕，用来肉眼 / 耳朵校验同步。
    只使用 .NET 自带 API，不依赖任何第三方库。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\make-test-audio.ps1
#>
param(
    [string]$SrtPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'samples\测试样例\test.srt'),
    [string]$WavPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'samples\测试样例\test.wav'),
    [double]$DurationSeconds = 30.0,
    [int]$SampleRate = 22050,
    [double]$BeepSeconds = 0.13,
    [double]$BeepFrequency = 440.0,
    [double]$BeepAmplitude = 0.28
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $SrtPath)) {
    throw "找不到字幕文件：$SrtPath"
}

# ---- 1. 从 SRT 里取出每条字幕的开始时间 ----
$starts = New-Object System.Collections.Generic.List[double]
foreach ($line in (Get-Content -LiteralPath $SrtPath -Encoding UTF8)) {
    if ($line -match '^\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->') {
        $fraction = $matches[4]
        $ms = [int]$fraction
        if ($fraction.Length -eq 1) { $ms *= 100 }
        elseif ($fraction.Length -eq 2) { $ms *= 10 }
        $starts.Add((([int]$matches[1] * 3600) + ([int]$matches[2] * 60) + [int]$matches[3]) + ($ms / 1000.0))
    }
}

if ($starts.Count -eq 0) {
    throw "在 $SrtPath 里没有找到任何时间轴。"
}

Write-Host "从字幕中读到 $($starts.Count) 条时间轴。"

# ---- 2. 生成 PCM ----
$totalSamples = [int]($SampleRate * $DurationSeconds)
$pcm = [System.Int16[]]::new($totalSamples)               # 默认全是 0（静音）

$beepLength = [int]($SampleRate * $BeepSeconds)
$beep = [System.Int16[]]::new($beepLength)
$fadeLength = [int]($SampleRate * 0.015)           # 15ms 淡入淡出，避免爆音

for ($i = 0; $i -lt $beepLength; $i++) {
    $envelope = 1.0
    if ($i -lt $fadeLength) { $envelope = $i / [double]$fadeLength }
    elseif ($i -gt ($beepLength - $fadeLength)) { $envelope = ($beepLength - $i) / [double]$fadeLength }
    if ($envelope -lt 0) { $envelope = 0 }

    $value = [Math]::Sin(2.0 * [Math]::PI * $BeepFrequency * ($i / [double]$SampleRate)) * $BeepAmplitude * $envelope
    $beep[$i] = [System.Int16]([Math]::Round($value * 32767))
}

foreach ($start in $starts) {
    $offset = [int]($start * $SampleRate)
    if ($offset -lt 0) { $offset = 0 }
    $count = [Math]::Min($beepLength, $totalSamples - $offset)
    if ($count -gt 0) {
        [Array]::Copy($beep, 0, $pcm, $offset, $count)
    }
}

# ---- 3. 写 WAV（44 字节标准头 + PCM 数据） ----
$bytes = [byte[]]::new($totalSamples * 2)
[Buffer]::BlockCopy($pcm, 0, $bytes, 0, $bytes.Length)

$stream = [System.IO.File]::Create($WavPath)
try {
    $writer = New-Object System.IO.BinaryWriter($stream)
    $ascii = [System.Text.Encoding]::ASCII

    $writer.Write($ascii.GetBytes('RIFF'))
    $writer.Write([int](36 + $bytes.Length))
    $writer.Write($ascii.GetBytes('WAVE'))
    $writer.Write($ascii.GetBytes('fmt '))
    $writer.Write([int]16)                 # fmt chunk 大小
    $writer.Write([System.Int16]1)                # PCM
    $writer.Write([System.Int16]1)                # 单声道
    $writer.Write([int]$SampleRate)
    $writer.Write([int]($SampleRate * 2))  # 字节率
    $writer.Write([System.Int16]2)                # 块对齐
    $writer.Write([System.Int16]16)               # 位深
    $writer.Write($ascii.GetBytes('data'))
    $writer.Write([int]$bytes.Length)
    $writer.Write($bytes)
    $writer.Flush()
}
finally {
    $stream.Dispose()
}

Write-Host "已生成：$WavPath"
Write-Host ("时长 {0:0.###} 秒 / {1} Hz / 单声道 / 16 bit / {2:0.0} MB" -f $DurationSeconds, $SampleRate, ((Get-Item -LiteralPath $WavPath).Length / 1MB))
