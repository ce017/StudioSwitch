# Builds StudioSwitch.exe with the C# compiler that ships with Windows (.NET Framework 4.x) — no SDK needed.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw = Split-Path $csc
$refs = @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll') | ForEach-Object { "/r:$(Join-Path $fw $_)" }
$icon = Join-Path $obj 'StudioSwitch.ico'

# Icon: two offset "windows" with a swap arrow, rendered to a 256px PNG inside an .ico container.
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 256, 256
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)
function RoundRect($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $r, $r, 180, 90); $p.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
    $p.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90); $p.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
    $p.CloseFigure(); return $p
}
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 58, 64, 76))), (RoundRect 20 20 150 120 36))
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 162, 255))), (RoundRect 86 106 150 120 36))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 18
$pen.StartCap = 'Round'; $pen.EndCap = 'ArrowAnchor'
$g.DrawLine($pen, 120, 166, 200, 166)
$g.Dispose()

# Small sizes as classic 32-bit DIB frames (the taskbar and title bar need these); 256 as PNG.
function DibFrame($master, $size) {
    $small = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = 'HighQualityBicubic'; $sg.PixelOffsetMode = 'HighQuality'
    $sg.DrawImage($master, 0, 0, $size, $size)
    $sg.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]($size * $size * 4 + $maskRow * $size)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {            # bottom-up BGRA
        for ($x = 0; $x -lt $size; $x++) { $c = $small.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) }
    }
    $w.Write((New-Object byte[] ($maskRow * $size)))  # AND mask unused with alpha
    $small.Dispose()
    return , $ms.ToArray()
}
function WriteIco($path, $frames) {
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$f.Data.Length); $bw.Write([uint32]$offset)
        $offset += $f.Data.Length
    }
    foreach ($f in $frames) { $bw.Write($f.Data) }
    $bw.Close()
}
$pngStream = New-Object System.IO.MemoryStream
$bmp.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$small = @(16, 20, 24, 32, 40, 48, 64) | ForEach-Object { @{ Size = $_; Data = (DibFrame $bmp $_) } }
WriteIco $icon (@($small) + @(@{ Size = 256; Data = $pngStream.ToArray() }))
$formIcon = Join-Path $obj 'StudioSwitchForm.ico'   # DIB-only copy: System.Drawing.Icon can't read PNG frames
WriteIco $formIcon @($small)

$shim = Join-Path $obj 'StudioSwitchShim.exe'
& $csc /nologo /optimize+ /target:exe /platform:anycpu "/out:$shim" $refs "/win32icon:$icon" `
    (Join-Path $src 'AssemblyInfo.cs') (Join-Path $src 'Common.cs') (Join-Path $src 'Shim.cs')
if ($LASTEXITCODE) { throw "shim build failed" }

$app = Join-Path $root 'StudioSwitch.exe'
& $csc /nologo /optimize+ /target:winexe /platform:anycpu "/out:$app" $refs `
    '/r:System.Windows.Forms.dll' '/r:System.Drawing.dll' "/win32icon:$icon" "/resource:$shim,StudioSwitchShim.exe" "/resource:$formIcon,StudioSwitchForm.ico" `
    (Join-Path $src 'AssemblyInfo.cs') (Join-Path $src 'Common.cs') (Join-Path $src 'Hooks.cs') (Join-Path $src 'App.cs')
if ($LASTEXITCODE) { throw "app build failed" }

Get-Item $shim, $app | Select-Object Name, Length
