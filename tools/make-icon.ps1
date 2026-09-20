# Иконка Islet: скруглённая капсула с точкой-курсором слева.
# Рисуется примитивами System.Drawing и собирается в многоразмерный .ico (внутри PNG).
param([string]$Out = "islet.ico")

Add-Type -AssemblyName System.Drawing

function New-IsletBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$size

    # --- подложка: тёмный скруглённый квадрат, как плитка приложения ---
    $tileRadius = $s * 0.22
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $tileRadius * 2
    $tile.AddArc(0, 0, $d, $d, 180, 90)
    $tile.AddArc($s - $d, 0, $d, $d, 270, 90)
    $tile.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
    $tile.AddArc(0, $s - $d, $d, $d, 90, 90)
    $tile.CloseFigure()

    $tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point 0, 0),
        (New-Object System.Drawing.Point ([int]$size), ([int]$size)),
        [System.Drawing.Color]::FromArgb(255, 14, 26, 34),
        [System.Drawing.Color]::FromArgb(255, 6, 11, 15))
    $g.FillPath($tileBrush, $tile)

    # --- капсула у верхнего края: ровно то, чем островок и является ---
    $pillW = $s * 0.64
    $pillH = $s * 0.215
    $pillX = ($s - $pillW) / 2
    $pillY = $s * 0.205
    $pillR = $pillH

    $pill = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pill.AddArc($pillX, $pillY, $pillR, $pillH, 90, 180)
    $pill.AddArc($pillX + $pillW - $pillR, $pillY, $pillR, $pillH, 270, 180)
    $pill.CloseFigure()

    # Сплошная мятная заливка: силуэт выживает и в 16 px.
    $pillBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point ([int]$pillX), ([int]$pillY)),
        (New-Object System.Drawing.Point ([int]($pillX + $pillW)), ([int]($pillY + $pillH))),
        [System.Drawing.Color]::FromArgb(255, 94, 240, 219),
        [System.Drawing.Color]::FromArgb(255, 45, 200, 178))
    $g.FillPath($pillBrush, $pill)

    # --- точка-курсор внутри капсулы, цветом подложки ---
    if ($size -ge 24) {
        $dotD = $pillH * 0.44
        $dotX = $pillX + $pillH * 0.30
        $dotY = $pillY + ($pillH - $dotD) / 2
        $dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 6, 11, 15))
        $g.FillEllipse($dotBrush, $dotX, $dotY, $dotD, $dotD)
    }

    # --- строка поиска: намёк на то, что в капсулу печатают ---
    if ($size -ge 32) {
        $lineH = [Math]::Max(1.0, $pillH * 0.13)
        $lineX = $pillX + $pillH * 0.95
        $lineW = $pillW - ($pillH * 1.5)
        $lineY = $pillY + ($pillH - $lineH) / 2
        $lineBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 6, 11, 15))
        $g.FillRectangle($lineBrush, $lineX, $lineY, $lineW, $lineH)
    }

    $g.Dispose()
    return $bmp
}

# --- сборка .ico: заголовок + записи + PNG каждого размера ---
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = @()
foreach ($size in $sizes) {
    $bmp = New-IsletBitmap $size
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
    $ms.Dispose()
}

$icoStream = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($icoStream)
$w.Write([uint16]0)              # reserved
$w.Write([uint16]1)              # type: icon
$w.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $w.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
    $w.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
    $w.Write([byte]0)             # палитра не используется
    $w.Write([byte]0)             # reserved
    $w.Write([uint16]1)           # цветовые плоскости
    $w.Write([uint16]32)          # бит на пиксель
    $w.Write([uint32]$pngs[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Out, $icoStream.ToArray())
$w.Dispose()
$icoStream.Dispose()

"{0} — {1} размеров, {2} КБ" -f $Out, $sizes.Count, [math]::Round((Get-Item $Out).Length / 1KB, 1)
