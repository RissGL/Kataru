<#
.SYNOPSIS
    生成示例书库的封面图（tools\make-sample-covers.ps1）。

.DESCRIPTION
    只用 System.Drawing 画：深色渐变底 + 一点暗红 + 金色细线 + 书名。纯示例素材，
    真实使用时程序会优先读取书文件夹里的 cover.jpg / folder.png 等封面文件。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\make-sample-covers.ps1
#>
param(
    [string]$SamplesRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'samples')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$covers = @(
    [pscustomobject]@{
        Folder   = '化物語 上'
        Title    = '化物語'
        Volume   = '上'
        Author   = '西尾維新'
        AccentR  = 158; AccentG = 43; AccentB = 37
    },
    [pscustomobject]@{
        Folder   = '偽物語 上'
        Title    = '偽物語'
        Volume   = '上'
        Author   = '西尾維新'
        AccentR  = 120; AccentG = 32; AccentB = 52
    }
)

$width = 600
$height = 800

foreach ($cover in $covers) {
    $target = Join-Path (Join-Path $SamplesRoot $cover.Folder) 'cover.png'
    $directory = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # 底：近黑 → 暗红
    $rect = New-Object System.Drawing.RectangleF(0, 0, $width, $height)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 22, 18, 19),
        [System.Drawing.Color]::FromArgb(255, $cover.AccentR, $cover.AccentG, $cover.AccentB),
        60.0)
    $g.FillRectangle($bg, $rect)

    # 顶部压暗，让文字更清晰
    $shade = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.RectangleF(0, 0, $width, $height * 0.7)),
        [System.Drawing.Color]::FromArgb(150, 12, 10, 10),
        [System.Drawing.Color]::FromArgb(0, 12, 10, 10),
        90.0)
    $g.FillRectangle($shade, 0, 0, $width, $height * 0.7)

    # 金色细线
    $gold = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 201, 162, 39))
    $g.FillRectangle($gold, 60, 232, 96, 3)

    $titleFont = New-Object System.Drawing.Font('Yu Mincho, MS Mincho, SimSun', 72, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $volumeFont = New-Object System.Drawing.Font('Yu Mincho, MS Mincho, SimSun', 40, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $authorFont = New-Object System.Drawing.Font('Yu Gothic UI, Microsoft YaHei UI', 22, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $smallFont = New-Object System.Drawing.Font('Yu Gothic UI, Microsoft YaHei UI', 16, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245, 241, 235, 225))
    $dim = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(200, 205, 195, 182))

    $g.DrawString($cover.Title, $titleFont, $white, 58, 120)
    $g.DrawString($cover.Volume, $volumeFont, $white, 62, 264)
    $g.DrawString($cover.Author, $authorFont, $dim, 62, 330)

    $g.DrawString('AudioBookPlayer 示例素材', $smallFont, $dim, 60, ($height - 70))
    $g.DrawString('SUB                 PLAY', $smallFont, $gold, 60, ($height - 110))

    $g.Dispose()
    $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $titleFont.Dispose(); $volumeFont.Dispose(); $authorFont.Dispose(); $smallFont.Dispose()
    $white.Dispose(); $dim.Dispose(); $gold.Dispose(); $bg.Dispose(); $shade.Dispose()

    Write-Host "已生成封面：$target"
}
