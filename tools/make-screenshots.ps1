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
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr h, out int processId);

    public delegate bool EnumProc(IntPtr h, IntPtr param);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc callback, IntPtr param);

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

# Every top-level window the app has open, not just its main one: the editor is a window
# of its own, and it is the one worth photographing.
function Get-ShotwinWindows {
    # Script scope, and a list rather than an array: the callback runs in its own scope,
    # so a local variable is invisible to it and appending to it silently builds nothing.
    $script:found = New-Object System.Collections.ArrayList

    $callback = [Shot+EnumProc] {
        param($handle, $param)

        if (-not [Shot]::IsWindowVisible($handle)) { return $true }

        $owner = 0
        [Shot]::GetWindowThreadProcessId($handle, [ref]$owner) | Out-Null

        $process = Get-Process -Id $owner -ErrorAction SilentlyContinue
        if (-not $process -or $process.ProcessName -ne "Shotwin") { return $true }

        $text = New-Object System.Text.StringBuilder 512
        [Shot]::GetWindowTextW($handle, $text, $text.Capacity) | Out-Null

        $title = $text.ToString()
        if (-not $title) { return $true }

        # The home window is the one with an About page in its navigation rail. Counting
        # radio buttons is not enough — the editor's tools are radio buttons too, and the
        # editor was being saved over the home window's picture. Nothing else in this app
        # has an About.
        $isHome = $false
        try {
            $element = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
            $condition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, "About")

            $about = $element.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants, $condition)

            $isHome = $null -ne $about
        } catch { }

        [void]$script:found.Add([pscustomobject]@{
            Handle = $handle; Title = $title; IsHome = $isHome })

        return $true
    }

    [Shot]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    return $script:found
}

$windows = Get-ShotwinWindows

if (-not $windows) {
    # Closing Shotwin leaves it in the tray with no window; launching it again brings the
    # window back, which is exactly what is wanted here.
    $exe = Join-Path $env:LOCALAPPDATA "Programs\Shotwin\Shotwin.exe"
    if (-not (Test-Path $exe)) { throw "Shotwin is not installed at $exe" }

    Start-Process $exe
    Start-Sleep -Seconds 3

    $windows = Get-ShotwinWindows
}

if (-not $windows) { throw "Shotwin has no window to photograph" }

# The main window carries the left rail and is 880 wide by design; an editor is sized to
# the picture it holds. Telling them apart by handle does not work — once an editor is in
# front, it is the process's "main" window — and neither does the title, because both are
# called "Shotwin". What differs is that only one of them is the home window, and the home
# window is the one that was there first.
foreach ($found in $windows) {
    # Told apart by handle, not by title: an editor is titled "Shotwin" too, so matching
    # on the text quietly saved the editor over the main window's picture.
    $name = if ($found.IsHome) { "window" } else { "editor" }

    [Shot]::ShowWindow($found.Handle, [Shot]::Restore) | Out-Null
    [Shot]::SetForegroundWindow($found.Handle) | Out-Null
    Start-Sleep -Milliseconds 700

    Save-Window $found.Handle $name
}
