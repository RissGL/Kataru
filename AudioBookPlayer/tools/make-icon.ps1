<#
.SYNOPSIS
    生成程序图标 Assets\app.ico（多尺寸）与界面用的 Assets\app-header.png。

.DESCRIPTION
    -Style Word（默认）：近黑圆角方底 + 米白「語」字 + 嵌在「口」里的均衡器（金→琥珀渐变）。
        均衡器的位置由连通域分析自动定位（只认被笔画围起来的封闭空白），换字体也不会跑偏。
        16/20/24px 用加粗黑体、去掉均衡器，保证小尺寸还能认出是个字。
    -Style Mark：早期设计，暗红渐变底 + 米白播放三角 + 字幕条。

    16~64px 写经典 DIB 帧，128/256 写 PNG 帧；另外输出一张深/浅背景的预览条。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\make-icon.ps1
    powershell -ExecutionPolicy Bypass -File .\tools\make-icon.ps1 -Style Mark
#>
param(
    [ValidateSet('Word', 'Mark')]
    [string]$Style = 'Word',
    [string]$IconPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets\app.ico'),
    [string]$HeaderPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets\app-header.png'),
    [int]$HeaderSize = 256,
    [int]$PngFromSize = 128
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# ---- 配色（与 Themes\Dark.xaml 一致：黑 / 米白 / 暗红 / 少量金）----
$tileTop = [System.Drawing.Color]::FromArgb(255, 28, 22, 22)
$tileBottom = [System.Drawing.Color]::FromArgb(255, 12, 10, 10)
$tileEdge = [System.Drawing.Color]::FromArgb(64, 126, 98, 78)
$glowColor = [System.Drawing.Color]::FromArgb(42, 168, 78, 46)
$creamTop = [System.Drawing.Color]::FromArgb(255, 250, 244, 233)
$creamBottom = [System.Drawing.Color]::FromArgb(255, 222, 206, 184)
$barLeft = [System.Drawing.Color]::FromArgb(255, 247, 186, 46)
$barRight = [System.Drawing.Color]::FromArgb(255, 226, 88, 48)
$redLight = [System.Drawing.Color]::FromArgb(255, 166, 46, 38)
$redDeep = [System.Drawing.Color]::FromArgb(255, 58, 15, 14)

$serifCandidates = @('Yu Mincho', 'YuMincho', 'MS PMincho', 'MS Mincho', 'SimSun', 'Songti SC', 'Noto Serif CJK JP')
$sansCandidates = @('Yu Gothic UI', 'Meiryo', 'Microsoft YaHei UI', 'SimHei', 'MS Gothic')

function New-RoundedRect {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)

    $maxRadius = [Math]::Min($W, $H) / 2.0
    if ($R -gt $maxRadius) { $R = $maxRadius }

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2.0
    if ($d -le 0.01) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF($X, $Y, $W, $H)))
    }
    else {
        $path.AddArc($X, $Y, $d, $d, 180, 90)
        $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
        $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
        $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    }
    $path.CloseFigure()
    return $path
}

function New-PointF {
    param([single]$X, [single]$Y)
    return [System.Drawing.PointF]::new($X, $Y)
}

function Get-InstalledFamily {
    param([string[]]$Names)
    foreach ($name in $Names) {
        try {
            return New-Object System.Drawing.FontFamily($name)
        }
        catch {
            continue
        }
    }
    return (New-Object System.Drawing.FontFamily([System.Drawing.FontFamily]::GenericSerif.Name))
}

function Start-Canvas {
    param([int]$Size)
    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    return @{ Bitmap = $bitmap; Graphics = $graphics }
}

<#
    用 GraphicsPath.AddString 拿到精确墨迹框，再等比缩放居中到目标方框，换字体也不会跑偏。
#>
function Get-GlyphPath {
    param(
        [string]$Text,
        [System.Drawing.FontFamily]$Family,
        [int]$GlyphStyle,
        [single]$TargetSize,
        [single]$TargetRatio
    )

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddString($Text, $Family, $GlyphStyle, 200.0, (New-PointF 0 0), [System.Drawing.StringFormat]::GenericTypographic)

    $bounds = $path.GetBounds()
    $scale = ($TargetSize * $TargetRatio) / [Math]::Max($bounds.Width, $bounds.Height)

    # GDI+ 的 Matrix 是"前插"语义：先调用的在最外层，所以书写顺序要反过来。
    # 想要 p' = (p - 墨迹左上角) * scale + 居中偏移，就先写居中、再缩放、最后平移负的墨迹原点。
    $offsetX = ($TargetSize - $bounds.Width * $scale) / 2
    $offsetY = ($TargetSize - $bounds.Height * $scale) / 2

    $matrix = New-Object System.Drawing.Drawing2D.Matrix
    $matrix.Translate($offsetX, $offsetY)
    $matrix.Scale($scale, $scale)
    $matrix.Translate(-$bounds.X, -$bounds.Y)
    $path.Transform($matrix)
    $matrix.Dispose()


    return $path
}

<#
    找「吾」下面那个「口」的内腔：
    对字形蒙版做连通域分析，只保留"不接触画布边缘"的空白块（被笔画围起来的封闭区域），
    再在右下半区挑面积最大的一块。返回归一化坐标，各尺寸按比例缩放。
#>
function Get-NormalizedPocket {
    param(
        [int]$Size,
        [System.Drawing.FontFamily]$Family,
        [int]$GlyphStyle,
        [single]$Ratio
    )

    $glyphPath = Get-GlyphPath -Text '語' -Family $Family -GlyphStyle $GlyphStyle -TargetSize $Size -TargetRatio $Ratio

    $mask = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($mask)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $graphics.FillPath($brush, $glyphPath)
    $brush.Dispose()
    $graphics.Dispose()
    $glyphPath.Dispose()

    $total = $Size * $Size
    $empty = [bool[]]::new($total)
    for ($y = 0; $y -lt $Size; $y++) {
        for ($x = 0; $x -lt $Size; $x++) {
            $empty[$y * $Size + $x] = ($mask.GetPixel($x, $y).A -lt 32)
        }
    }
    $mask.Dispose()

    $visited = [bool[]]::new($total)
    $stack = New-Object System.Collections.Generic.Stack[int]
    $best = $null

    for ($seed = 0; $seed -lt $total; $seed++) {
        if ($visited[$seed] -or -not $empty[$seed]) { continue }

        $stack.Clear()
        $stack.Push($seed)
        $visited[$seed] = $true

        $area = 0
        $minX = $Size; $maxX = -1; $minY = $Size; $maxY = -1
        $touchesBorder = $false

        while ($stack.Count -gt 0) {
            $index = $stack.Pop()
            $px = $index % $Size
            $py = [int](($index - $px) / $Size)

            $area++
            if ($px -lt $minX) { $minX = $px }
            if ($px -gt $maxX) { $maxX = $px }
            if ($py -lt $minY) { $minY = $py }
            if ($py -gt $maxY) { $maxY = $py }
            if ($px -eq 0 -or $py -eq 0 -or $px -eq ($Size - 1) -or $py -eq ($Size - 1)) { $touchesBorder = $true }

            if ($px -gt 0) { $n = $index - 1; if (-not $visited[$n] -and $empty[$n]) { $visited[$n] = $true; $stack.Push($n) } }
            if ($px -lt ($Size - 1)) { $n = $index + 1; if (-not $visited[$n] -and $empty[$n]) { $visited[$n] = $true; $stack.Push($n) } }
            if ($py -gt 0) { $n = $index - $Size; if (-not $visited[$n] -and $empty[$n]) { $visited[$n] = $true; $stack.Push($n) } }
            if ($py -lt ($Size - 1)) { $n = $index + $Size; if (-not $visited[$n] -and $empty[$n]) { $visited[$n] = $true; $stack.Push($n) } }
        }

        if ($touchesBorder) { continue }                                        # 背景 / 外部空白
        if ($area -lt ($total * 0.002)) { continue }                            # 太碎
        if ((($minX + $maxX) / 2.0) -lt ($Size * 0.45)) { continue }            # 只要右半边
        if ((($minY + $maxY) / 2.0) -lt ($Size * 0.45)) { continue }            # 只要下半边

        if ($null -eq $best -or $area -gt $best.Area) {
            $best = [pscustomobject]@{ Area = $area; X0 = $minX; X1 = $maxX; Y0 = $minY; Y1 = $maxY }
        }
    }

    if ($null -eq $best) { return $null }

    return [pscustomobject]@{
        X0 = [single]($best.X0 / $Size)
        X1 = [single](($best.X1 + 1) / $Size)
        Y0 = [single]($best.Y0 / $Size)
        Y1 = [single](($best.Y1 + 1) / $Size)
    }
}

function New-WordBitmap {
    param(
        [int]$Size,
        $Pocket = $null
    )

    $canvas = Start-Canvas -Size $Size
    $bitmap = $canvas.Bitmap
    $g = $canvas.Graphics

    $scale = $Size / 256.0
    $radius = 58 * $scale
    $rect = New-Object System.Drawing.RectangleF(0, 0, $Size, $Size)
    $small = $Size -le 24

    # ---- 1. 近黑圆角底 ----
    $tilePath = New-RoundedRect -X 0 -Y 0 -W $Size -H $Size -R $radius
    $tile = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $tileTop, $tileBottom, [single]90)
    $g.FillPath($tile, $tilePath)
    $tile.Dispose()

    # ---- 2. 底部一层暖光（小尺寸只会糊成一团，不画）----
    if (-not $small) {
        $glowRect = New-Object System.Drawing.RectangleF(($Size * 0.16), ($Size * 0.60), ($Size * 0.68), ($Size * 0.42))
        $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $glowPath.AddEllipse($glowRect)
        $glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
        $glowBrush.CenterColor = $glowColor
        $glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 168, 78, 46))
        $g.FillPath($glowBrush, $glowPath)
        $glowBrush.Dispose()
        $glowPath.Dispose()
    }

    # ---- 3. 极淡暖色描边（避免在深色任务栏上糊掉）----
    $edgePen = New-Object System.Drawing.Pen($tileEdge, ([single](1.5 * $scale)))
    $g.DrawPath($edgePen, $tilePath)
    $edgePen.Dispose()
    $tilePath.Dispose()

    # ---- 4. 「語」 ----
    if ($small) {
        $family = Get-InstalledFamily $sansCandidates
        $glyphStyle = [int][System.Drawing.FontStyle]::Bold
        $ratio = 0.76
    }
    else {
        $family = Get-InstalledFamily $serifCandidates
        $glyphStyle = [int][System.Drawing.FontStyle]::Regular
        $ratio = 0.70
    }

    $glyphPath = Get-GlyphPath -Text '語' -Family $family -GlyphStyle $glyphStyle -TargetSize $Size -TargetRatio $ratio
    $glyphRect = New-Object System.Drawing.RectangleF(0, 0, $Size, $Size)
    $glyphBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($glyphRect, $creamTop, $creamBottom, [single]90)
    $g.FillPath($glyphBrush, $glyphPath)
    $glyphBrush.Dispose()
    $glyphPath.Dispose()

    # ---- 5. 「口」里的均衡器 ----
    if (-not $small -and $null -ne $Pocket) {
        $pocketX0 = $Pocket.X0 * $Size
        $pocketX1 = $Pocket.X1 * $Size
        $pocketY0 = $Pocket.Y0 * $Size
        $pocketY1 = $Pocket.Y1 * $Size
        $pocketWidth = $pocketX1 - $pocketX0
        $pocketHeight = $pocketY1 - $pocketY0

        # 以「口」的内腔为中心，但比它大一圈（参考稿里就是这么"溢出"方框的）
        $centerX = ($pocketX0 + $pocketX1) / 2
        $centerY = ($pocketY0 + $pocketY1) / 2
        $eqWidth = $pocketWidth * 1.50
        $maxBarHeight = $pocketHeight * 2.05

        $barCount = 5
        $slot = $eqWidth / (2 * $barCount + 1)
        $barWidth = $slot * 0.82
        $heights = @(0.30, 0.58, 1.0, 0.72, 0.42)
        $eqLeft = $centerX - ($eqWidth / 2)

        $barRect = New-Object System.Drawing.RectangleF($eqLeft, ($centerY - $maxBarHeight / 2), $eqWidth, $maxBarHeight)
        $barBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($barRect, $barLeft, $barRight, [single]0)

        # 辉光：几层半透明粗条模拟（System.Drawing 没有模糊）
        $glowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(30, 245, 160, 60))
        for ($i = 0; $i -lt $barCount; $i++) {
            $x = $eqLeft + $slot * (1 + $i * 2)
            $barHeight = $maxBarHeight * $heights[$i]
            $y = $centerY - ($barHeight / 2)
            $glow = New-RoundedRect -X ($x - $barWidth * 0.75) -Y ($y - $barWidth * 0.75) `
                -W ($barWidth * 2.5) -H ($barHeight + $barWidth * 1.5) -R ($barWidth * 1.25)
            $g.FillPath($glowBrush, $glow)
            $glow.Dispose()
        }

        for ($i = 0; $i -lt $barCount; $i++) {
            $x = $eqLeft + $slot * (1 + $i * 2)
            $barHeight = $maxBarHeight * $heights[$i]
            $y = $centerY - ($barHeight / 2)
            $bar = New-RoundedRect -X $x -Y $y -W $barWidth -H $barHeight -R ($barWidth / 2)
            $g.FillPath($barBrush, $bar)
            $bar.Dispose()
        }

        # 最左边的小圆点（音频图标的起始点）
        $dotSize = $barWidth * 0.95
        $dotBrush = New-Object System.Drawing.SolidBrush($barLeft)
        $g.FillEllipse($dotBrush, ($eqLeft + $slot - $dotSize / 2), ($centerY - $dotSize / 2), $dotSize, $dotSize)
        $dotBrush.Dispose()

        $barBrush.Dispose()
        $glowBrush.Dispose()
    }

    $g.Dispose()
    return $bitmap
}

function New-MarkBitmap {
    param([int]$Size)

    $canvas = Start-Canvas -Size $Size
    $bitmap = $canvas.Bitmap
    $g = $canvas.Graphics

    $scale = $Size / 256.0
    $radius = 58 * $scale
    $rect = New-Object System.Drawing.RectangleF(0, 0, $Size, $Size)

    $bgPath = New-RoundedRect -X 0 -Y 0 -W $Size -H $Size -R $radius
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $redLight, $redDeep, [single]60)
    $g.FillPath($bg, $bgPath)

    $glossHeight = [single]($Size * 0.62)
    $glossRect = New-Object System.Drawing.RectangleF(0, 0, $Size, $glossHeight)
    $glossPath = New-RoundedRect -X 0 -Y 0 -W $Size -H $glossHeight -R $radius
    $gloss = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $glossRect,
        [System.Drawing.Color]::FromArgb(40, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
        [single]90)
    $g.FillPath($gloss, $glossPath)

    $creamBrush = New-Object System.Drawing.SolidBrush($creamTop)
    $barBrush = New-Object System.Drawing.SolidBrush($barLeft)

    if ($Size -le 24) {
        $triangle = [System.Drawing.PointF[]]@(
            (New-PointF -X (0.31 * $Size) -Y (0.20 * $Size)),
            (New-PointF -X (0.31 * $Size) -Y (0.63 * $Size)),
            (New-PointF -X (0.74 * $Size) -Y (0.415 * $Size))
        )
        $g.FillPolygon($creamBrush, $triangle)

        $bar = New-RoundedRect -X (0.25 * $Size) -Y (0.72 * $Size) -W (0.50 * $Size) -H (0.14 * $Size) -R (0.07 * $Size)
        $g.FillPath($barBrush, $bar)
        $bar.Dispose()
    }
    else {
        $triangle = [System.Drawing.PointF[]]@(
            (New-PointF -X (0.405 * $Size) -Y (0.265 * $Size)),
            (New-PointF -X (0.405 * $Size) -Y (0.545 * $Size)),
            (New-PointF -X (0.635 * $Size) -Y (0.405 * $Size))
        )
        $g.FillPolygon($creamBrush, $triangle)

        $bar1 = New-RoundedRect -X (0.245 * $Size) -Y (0.625 * $Size) -W (0.51 * $Size) -H (0.085 * $Size) -R (0.0425 * $Size)
        $g.FillPath($creamBrush, $bar1)

        $bar2 = New-RoundedRect -X (0.245 * $Size) -Y (0.755 * $Size) -W (0.33 * $Size) -H (0.065 * $Size) -R (0.0325 * $Size)
        $g.FillPath($barBrush, $bar2)

        $bar1.Dispose()
        $bar2.Dispose()
    }

    $creamBrush.Dispose()
    $barBrush.Dispose()
    $bg.Dispose()
    $gloss.Dispose()
    $bgPath.Dispose()
    $glossPath.Dispose()
    $g.Dispose()

    return $bitmap
}

function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

<#
    ICO 的 DIB 帧 = BITMAPINFOHEADER(40B) + XOR 位图(32bpp, 自下而上) + AND 掩码(1bpp, 每行 4 字节对齐)。
    高度写 2 倍是 ICO 的历史约定。
#>
function ConvertTo-IconDibBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $width = $Bitmap.Width
    $height = $Bitmap.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
    $locked = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $locked.Stride
        $pixels = [byte[]]::new($stride * $height)
        [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    }
    finally {
        $Bitmap.UnlockBits($locked)
    }

    $stream = New-Object System.IO.MemoryStream
    try {
        $writer = New-Object System.IO.BinaryWriter($stream)
        $writer.Write([System.UInt32]40)
        $writer.Write([System.Int32]$width)
        $writer.Write([System.Int32]($height * 2))
        $writer.Write([System.UInt16]1)
        $writer.Write([System.UInt16]32)
        $writer.Write([System.UInt32]0)
        $writer.Write([System.UInt32]($width * $height * 4))
        $writer.Write([System.Int32]0)
        $writer.Write([System.Int32]0)
        $writer.Write([System.UInt32]0)
        $writer.Write([System.UInt32]0)

        for ($y = $height - 1; $y -ge 0; $y--) {
            $writer.Write($pixels, $y * $stride, $width * 4)
        }

        $maskStride = [int]([Math]::Ceiling($width / 32.0) * 4)
        $mask = [byte[]]::new($maskStride * $height)
        $writer.Write($mask, 0, $mask.Length)
        $writer.Flush()

        return , $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

# ==================== 主流程 ====================

# 「口」的内腔只跟字体和字号比例有关，算一次就够
$pocket = $null
if ($Style -eq 'Word') {
    $serif = Get-InstalledFamily $serifCandidates
    Write-Host ("标题字体：{0}" -f $serif.Name)
    $pocket = Get-NormalizedPocket -Size 256 -Family $serif -GlyphStyle ([int][System.Drawing.FontStyle]::Regular) -Ratio 0.70
    $serif.Dispose()

    if ($null -eq $pocket) {
        Write-Host "没有找到封闭内腔，跳过均衡器"
    }
    else {
        Write-Host ("「口」内腔（归一化）：x {0:0.000}~{1:0.000}, y {2:0.000}~{3:0.000}" -f $pocket.X0, $pocket.X1, $pocket.Y0, $pocket.Y1)
        $pocketWidth = $pocket.X1 - $pocket.X0
        $pocketHeight = $pocket.Y1 - $pocket.Y0
        if ($pocketWidth -lt 0.10 -or $pocketHeight -lt 0.06 -or ($pocketWidth / $pocketHeight) -gt 3.5) {
            Write-Host "内腔尺寸不合适，跳过均衡器"
            $pocket = $null
        }
    }
}

# ---- 逐尺寸渲染 ----
$frames = @()
foreach ($size in $sizes) {
    $bitmap = if ($Style -eq 'Word') { New-WordBitmap -Size $size -Pocket $pocket } else { New-MarkBitmap -Size $size }
    try {
        $isPng = $size -ge $PngFromSize
        $bytes = if ($isPng) { ConvertTo-PngBytes -Bitmap $bitmap } else { ConvertTo-IconDibBytes -Bitmap $bitmap }
        $frames += [pscustomobject]@{ Size = $size; Bytes = [System.Byte[]]$bytes; IsPng = $isPng }
    }
    finally {
        $bitmap.Dispose()
    }
}

# ---- 拼 ICO ----
$iconDirectory = Split-Path -Parent $IconPath
if (-not (Test-Path -LiteralPath $iconDirectory)) {
    New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null
}

$stream = [System.IO.File]::Create($IconPath)
try {
    $writer = New-Object System.IO.BinaryWriter($stream)
    $writer.Write([System.UInt16]0)
    $writer.Write([System.UInt16]1)
    $writer.Write([System.UInt16]$frames.Count)

    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([System.Byte]$dimension)
        $writer.Write([System.Byte]$dimension)
        $writer.Write([System.Byte]0)
        $writer.Write([System.Byte]0)
        $writer.Write([System.UInt16]1)
        $writer.Write([System.UInt16]32)
        $writer.Write([System.UInt32]$frame.Bytes.Length)
        $writer.Write([System.UInt32]$offset)
        $offset += $frame.Bytes.Length
    }

    foreach ($frame in $frames) {
        $writer.Write([System.Byte[]]$frame.Bytes)
    }

    $writer.Flush()
}
finally {
    $stream.Dispose()
}

# ---- 界面用 PNG ----
$header = if ($Style -eq 'Word') { New-WordBitmap -Size $HeaderSize -Pocket $pocket } else { New-MarkBitmap -Size $HeaderSize }
try {
    $header.Save($HeaderPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $header.Dispose()
}

# ---- 预览条（深色 + 浅色两种背景）----
$previewSizes = @(64, 48, 32, 24, 16)
$gap = 18
$stripWidth = ($previewSizes | Measure-Object -Sum).Sum + ($gap * ($previewSizes.Count + 1))
$stripHeight = (64 * 2) + ($gap * 3)
$strip = New-Object System.Drawing.Bitmap($stripWidth, $stripHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stripGraphics = [System.Drawing.Graphics]::FromImage($strip)
$stripGraphics.Clear([System.Drawing.Color]::FromArgb(255, 18, 16, 17))
$lightBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 243, 240, 235))
$stripGraphics.FillRectangle($lightBrush, 0, (64 + $gap * 2), $stripWidth, (64 + $gap))
$lightBrush.Dispose()
$stripGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
foreach ($pass in 0, 1) {
    $x = $gap
    $y = $gap + $pass * (64 + $gap)
    foreach ($size in $previewSizes) {
        $icon = New-Object System.Drawing.Icon($IconPath, $size, $size)
        $stripGraphics.DrawIcon($icon, (New-Object System.Drawing.Rectangle($x, ($y + [int]((64 - $size) / 2)), $size, $size)))
        $icon.Dispose()
        $x += $size + $gap
    }
}
$stripGraphics.Dispose()
$previewPath = Join-Path $iconDirectory 'icon-preview.png'
$strip.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
$strip.Dispose()

Write-Host ("样式 {0} → ICO {1}（{2} 个尺寸，共 {3} B）" -f $Style, $IconPath, $frames.Count, (Get-Item $IconPath).Length)
Write-Host ("已生成 {0}" -f $HeaderPath)
Write-Host ("已生成预览 {0}" -f $previewPath)
