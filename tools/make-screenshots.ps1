# Shoots the README's screenshots from the running app, so they are always of the build
# in the repo rather than of whatever was on screen the day someone cropped a picture.
#
# Captures the window by its own frame rather than the whole screen: DWM's extended frame
# bounds, not GetWindowRect, which on Windows 10 and 11 includes an invisible resize
# border and leaves a dead margin around the shot.

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

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public const int ExtendedFrameBounds = 9;
    public const int Restore = 9;
}
"@

function Save-Window($handle, $name) {
    $bounds = New-Object Shot+RECT
    $size = [System.Runtime.InteropServices.Marshal]::SizeOf($bounds)

    if ([Shot]::DwmGetWindowAttribute($handle, [Shot]::ExtendedFrameBounds, [ref]$bounds, $size) -ne 0) {
        throw "Could not measure the window"
    }

    $width  = $bounds.Right - $bounds.Left
    $height = $bounds.Bottom - $bounds.Top

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)

    $out = Join-Path $docs "$name.png"
    $bitmap.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)

    $graphics.Dispose()
    $bitmap.Dispose()

    Write-Host "Wrote $out (${width}x${height})"
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
