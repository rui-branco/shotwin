# Shoots the README's screenshots from the running app, so they are always of the build
# in the repo rather than of whatever was on screen the day someone cropped a picture.
#
# Captures the window's own pixels with PrintWindow rather than copying that patch of the
# screen. Copying the screen picks up whatever is behind the window: DWM rounds the
# corners, so the desktop shows through all four of them, and anything overlapping the
# edges lands in the picture too.
#
# The bitmap is then cropped to DWM's extended frame bounds, because a window rect on
# Windows 10 and 11 includes an invisible resize border that would otherwise leave a dead
# margin around the shot.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$docs = Join-Path $root "docs"
if (-not (Test-Path $docs)) { New-Item -ItemType Directory -Path $docs | Out-Null }

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class Shot
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public const int ExtendedFrameBounds = 9;
    public const int Restore = 9;

    // Renders the whole window, including the parts drawn by the composition engine.
    // Without it a modern window comes back blank.
    public const uint RenderFullContent = 2;
}
"@

function Save-Window($handle, $name) {
    $frame = New-Object Shot+RECT
    $size = [System.Runtime.InteropServices.Marshal]::SizeOf($frame)

    if ([Shot]::DwmGetWindowAttribute($handle, [Shot]::ExtendedFrameBounds, [ref]$frame, $size) -ne 0) {
        throw "Could not measure the window"
    }

    $window = New-Object Shot+RECT
    if (-not [Shot]::GetWindowRect($handle, [ref]$window)) { throw "Could not find the window" }

    $wholeWidth  = $window.Right - $window.Left
    $wholeHeight = $window.Bottom - $window.Top

    $whole = New-Object System.Drawing.Bitmap($wholeWidth, $wholeHeight)
    $graphics = [System.Drawing.Graphics]::FromImage($whole)
    $hdc = $graphics.GetHdc()

    $ok = [Shot]::PrintWindow($handle, $hdc, [Shot]::RenderFullContent)

    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()

    if (-not $ok) { $whole.Dispose(); throw "The window would not draw itself" }

    # Trim the invisible resize border off each side.
    $crop = New-Object System.Drawing.Rectangle(
        ($frame.Left - $window.Left),
        ($frame.Top - $window.Top),
        ($frame.Right - $frame.Left),
        ($frame.Bottom - $frame.Top))

    $shot = $whole.Clone($crop, $whole.PixelFormat)

    $out = Join-Path $docs "$name.png"
    $shot.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)

    $shot.Dispose()
    $whole.Dispose()

    Write-Host "Wrote $out ($($crop.Width)x$($crop.Height))"
}

$shotwin = Get-Process Shotwin -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Select-Object -First 1

if (-not $shotwin) {
    # Closing Shotwin leaves it in the tray with no window; launching it again brings the
    # window back, which is exactly what is wanted here.
    $exe = Join-Path $env:LOCALAPPDATA "Programs\Shotwin\Shotwin.exe"
    if (-not (Test-Path $exe)) { throw "Shotwin is not installed at $exe" }

    Start-Process $exe
    Start-Sleep -Seconds 3

    $shotwin = Get-Process Shotwin -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1
}

if (-not $shotwin) { throw "Shotwin has no window to photograph" }

$handle = $shotwin.MainWindowHandle

[Shot]::ShowWindow($handle, [Shot]::Restore) | Out-Null
[Shot]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 700

Save-Window $handle "window"
