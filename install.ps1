<#
.SYNOPSIS
    Publishes Shotwin and installs it for the current user.

.DESCRIPTION
    Builds a self-contained single-file exe (no .NET runtime needed on the target),
    copies it to %LOCALAPPDATA%\Programs\Shotwin, and creates a Start menu shortcut so
    it can be launched and pinned like any other app.

    Per-user on purpose: no admin rights, no UAC prompt, and uninstalling is deleting
    a folder and a shortcut.

.PARAMETER StartWithWindows
    Also add a Startup shortcut so Shotwin is in the tray after every login.

.PARAMETER Uninstall
    Remove the installed copy and the shortcuts.
#>
[CmdletBinding()]
param(
    [switch]$StartWithWindows,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$AppName    = 'Shotwin'
$Root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project    = Join-Path $Root 'src\Shotwin\Shotwin.csproj'
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$ExePath    = Join-Path $InstallDir "$AppName.exe"
$StartMenu  = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
$StartupLnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup\$AppName.lnk"

function Stop-Shotwin {
    Get-Process $AppName -ErrorAction SilentlyContinue | ForEach-Object {
        $_ | Stop-Process -Force
        Write-Host "Stopped running $AppName (PID $($_.Id))"
    }
    Start-Sleep -Milliseconds 600
}

function New-Shortcut([string]$Path, [string]$Target, [string]$Description) {
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($Path)
    $lnk.TargetPath       = $Target
    $lnk.WorkingDirectory = Split-Path -Parent $Target
    $lnk.IconLocation     = "$Target,0"
    $lnk.Description      = $Description
    $lnk.Save()
    [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
}

if ($Uninstall) {
    Stop-Shotwin
    foreach ($p in @($StartMenu, $StartupLnk)) {
        if (Test-Path $p) { Remove-Item $p -Force; Write-Host "Removed $p" }
    }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force; Write-Host "Removed $InstallDir" }
    Write-Host "$AppName uninstalled." -ForegroundColor Green
    return
}

# --- Publish ---------------------------------------------------------------------
$dotnet = 'dotnet'
if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { throw 'dotnet not found. Install the .NET 10 SDK.' }
}

$publishDir = Join-Path $Root 'artifacts\publish'
Write-Host 'Publishing self-contained build...' -ForegroundColor Cyan

& $dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $publishDir `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

# --- Install ---------------------------------------------------------------------
Stop-Shotwin

if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
Copy-Item (Join-Path $publishDir '*') $InstallDir -Recurse -Force

if (-not (Test-Path $ExePath)) { throw "Published exe not found at $ExePath" }

New-Shortcut -Path $StartMenu -Target $ExePath -Description 'Screenshot tool for pixel work'
Write-Host "Start menu shortcut: $StartMenu"

# Run-at-login lives in the HKCU Run key so the Settings window can read it back and
# toggle it. A Startup-folder shortcut would be a second, invisible source of truth.
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($StartWithWindows) {
    # --background, or the home window would pop up at every sign-in.
    Set-ItemProperty -Path $RunKey -Name $AppName -Value "`"$ExePath`" --background"
    Write-Host 'Start at login:     enabled'
}
else {
    if ((Get-ItemProperty -Path $RunKey -Name $AppName -ErrorAction SilentlyContinue)) {
        Remove-ItemProperty -Path $RunKey -Name $AppName
    }
    Write-Host 'Start at login:     off (pass -StartWithWindows, or use Settings)'
}

# Clean up the Startup-folder shortcut older installs created.
if (Test-Path $StartupLnk) { Remove-Item $StartupLnk -Force }

$size = '{0:N0} MB' -f ((Get-Item $ExePath).Length / 1MB)
Write-Host ''
Write-Host "$AppName installed to $InstallDir ($size)" -ForegroundColor Green
Write-Host 'Search the Start menu for Shotwin, or press Ctrl+Shift+2 once it is running.'

Start-Process $ExePath
