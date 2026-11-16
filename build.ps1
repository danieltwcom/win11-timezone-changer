# Builds TimeZoneChanger.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
# Usage:  powershell -ExecutionPolicy Bypass -File .\build.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$ico  = Join-Path $src 'app.ico'
$out  = Join-Path $root 'TimeZoneChanger.exe'
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

# ---------- Generate the icon (globe + clock) if it doesn't exist ----------
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing

    function New-IconBitmap([int]$s) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::Transparent)
        $m = [single]($s * 0.04)
        $d = [single]($s - 2 * $m)

        # Globe
        $rect = New-Object System.Drawing.RectangleF($m, $m, $d, $d)
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
            [System.Drawing.Color]::FromArgb(255, 64, 156, 255), [System.Drawing.Color]::FromArgb(255, 0, 84, 180), 45)
        $g.FillEllipse($brush, $rect)
        $penW = [single][Math]::Max(1, $s / 24)
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(170, 255, 255, 255), $penW)
        $g.DrawEllipse($pen, $m + $d * 0.28, $m, $d * 0.44, $d)              # meridian
        $g.DrawLine($pen, $m + $d / 2, $m, $m + $d / 2, $m + $d)              # central meridian
        $g.DrawLine($pen, $m, $m + $d / 2, $m + $d, $m + $d / 2)              # equator
        $g.DrawLine($pen, $m + $d * 0.1, $m + $d * 0.27, $m + $d * 0.9, $m + $d * 0.27)
        $g.DrawLine($pen, $m + $d * 0.1, $m + $d * 0.73, $m + $d * 0.9, $m + $d * 0.73)

        # Clock badge (bottom-right)
        $cd = [single]($s * 0.56)
        $cx = [single]($s - $cd - $m * 0.2); $cy = $cx
        $g.FillEllipse([System.Drawing.Brushes]::White, $cx, $cy, $cd, $cd)
        $ring = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0, 60, 140), [single][Math]::Max(1, $s / 18))
        $g.DrawEllipse($ring, $cx + $ring.Width / 2, $cy + $ring.Width / 2, $cd - $ring.Width, $cd - $ring.Width)
        $hand = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0, 60, 140), [single][Math]::Max(1, $s / 14))
        $hand.StartCap = 'Round'; $hand.EndCap = 'Round'
        $ccx = $cx + $cd / 2; $ccy = $cy + $cd / 2
        $g.DrawLine($hand, $ccx, $ccy, $ccx, $ccy - $cd * 0.32)               # minute hand
        $g.DrawLine($hand, $ccx, $ccy, $ccx + $cd * 0.22, $ccy)               # hour hand
        $g.Dispose()
        return $bmp
    }

    # Write a multi-size .ico with 32-bit DIB entries
    $sizes = 16, 20, 24, 32, 48, 64, 256
    $images = @()
    foreach ($s in $sizes) {
        $bmp = New-IconBitmap $s
        $data = $bmp.LockBits((New-Object System.Drawing.Rectangle(0, 0, $s, $s)), 'ReadOnly', 'Format32bppArgb')
        $stride = $data.Stride
        $raw = New-Object byte[] ($stride * $s)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
        $bmp.UnlockBits($data); $bmp.Dispose()

        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2))
        $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
        $bw.Write([int]($s * $s * 4 + $maskRow * $s))
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $s - 1; $y -ge 0; $y--) { $bw.Write($raw, $y * $stride, $s * 4) }   # bottom-up BGRA
        $bw.Write((New-Object byte[] ($maskRow * $s)))                                 # empty AND mask
        $bw.Flush()
        $images += , @($s, $ms.ToArray())
    }

    $fs = [System.IO.File]::Create($ico)
    $w = New-Object System.IO.BinaryWriter($fs)
    $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $s = $img[0]; $bytes = $img[1]
        $dim = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$bytes.Length); $w.Write([int]$offset)
        $offset += $bytes.Length
    }
    foreach ($img in $images) { $w.Write([byte[]]$img[1]) }
    $w.Close()
    Write-Host "Generated icon: $ico"
}

# ---------- Compile ----------
# If the exe is currently running it is locked, but Windows allows renaming it - move it aside.
if (Test-Path $out) {
    $old = Join-Path $root 'TimeZoneChanger.old.exe'
    Remove-Item $old -ErrorAction SilentlyContinue
    try { [IO.File]::Delete($out) } catch { Rename-Item $out 'TimeZoneChanger.old.exe'; Write-Host 'Running copy moved to TimeZoneChanger.old.exe - restart the app to use the new build.' }
}
& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    "/out:$out" "/win32icon:$ico" "/win32manifest:$(Join-Path $src 'app.manifest')" "/resource:$ico,TimeZoneChanger.app.ico" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    (Join-Path $src 'TimeZoneChanger.cs')

if ($LASTEXITCODE -ne 0) { throw "Build failed." }
Write-Host "Built: $out"

