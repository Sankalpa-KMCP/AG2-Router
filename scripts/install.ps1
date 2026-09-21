# AG2 Router - Per-User Windows Installer
# Installs AG2 Router into %LOCALAPPDATA%\Programs\AG2Router without admin privileges.
param(
    [string]$SourceDir = $PSScriptRoot,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\AG2Router"
$ExePath = Join-Path $InstallDir "AG2Router.exe"
$ProgramsFolder = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Programs)
$ShortcutPath = Join-Path $ProgramsFolder "AG2 Router.lnk"
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AG2Router"

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "AG2 Router - Per-User Installation" -ForegroundColor Cyan
Write-Host "Target Directory: $InstallDir" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# 1. Graceful Shutdown of Existing Running Instance
$runningProcesses = Get-Process -Name "AG2Router" -ErrorAction SilentlyContinue
if ($runningProcesses) {
    Write-Host "Detected running AG2 Router instance. Requesting graceful shutdown (--exit)..."
    if (Test-Path $ExePath) {
        try {
            $exitProcess = Start-Process -FilePath $ExePath -ArgumentList "--exit" -Wait -PassThru -WindowStyle Hidden
            if ($exitProcess.ExitCode -ne 0) {
                Write-Warning "Shutdown request (--exit) returned exit code $($exitProcess.ExitCode)"
            }
        } catch {
            Write-Warning "Failed to invoke --exit signal: $_"
        }
    }

    # Wait up to 5 seconds for process termination
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 5000) {
        $remaining = Get-Process -Name "AG2Router" -ErrorAction SilentlyContinue
        if (-not $remaining) { break }
        Start-Sleep -Milliseconds 250
    }

    $stillRunning = Get-Process -Name "AG2Router" -ErrorAction SilentlyContinue
    if ($stillRunning) {
        throw "AG2 Router is currently running and could not be gracefully closed. Please exit AG2 Router from the system tray before proceeding."
    }
    Write-Host "  [OK] Previous instance closed gracefully." -ForegroundColor Green
}

# 2. Prerequisite Check: Microsoft Edge WebView2 Evergreen Runtime
$webview2Installed = $false
$wv2Paths = @(
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    "HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
)

foreach ($regPath in $wv2Paths) {
    if (Test-Path $regPath) {
        $pv = (Get-ItemProperty -Path $regPath -Name "pv" -ErrorAction SilentlyContinue).pv
        if ($pv -and $pv -ne "0.0.0.0") {
            $webview2Installed = $true
            Write-Host "  [OK] Microsoft Edge WebView2 Evergreen Runtime detected: version $pv" -ForegroundColor Green
            break
        }
    }
}

if (-not $webview2Installed) {
    Write-Warning "Microsoft Edge WebView2 Evergreen Runtime was not detected."
    Write-Warning "The system tray and background router will operate, but the interactive UI requires WebView2."
    Write-Warning "Download from: https://developer.microsoft.com/en-us/microsoft-edge/webview2/"
}

# 3. Create Target Directory & Copy Files
Write-Host "Installing binaries to $InstallDir..."
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
}

# Copy files from SourceDir to InstallDir (excluding install.ps1 to avoid self-locking if run from there)
Get-ChildItem -Path $SourceDir | ForEach-Object {
    if ($_.Name -ne "install.ps1" -and $_.Name -ne "dist") {
        Copy-Item -Path $_.FullName -Destination $InstallDir -Recurse -Force
    }
}

# Ensure uninstall script is copied into InstallDir
$sourceUninstall = Join-Path $SourceDir "uninstall.ps1"
if (Test-Path $sourceUninstall) {
    Copy-Item -Path $sourceUninstall -Destination (Join-Path $InstallDir "uninstall.ps1") -Force
}

# Validate essential executable exists
if (-not (Test-Path $ExePath)) {
    throw "Installation failed: AG2Router.exe not found at $ExePath"
}

# 4. Create Start Menu Shortcut
Write-Host "Creating Start Menu shortcut..."
try {
    $wsh = New-Object -ComObject WScript.Shell
    $shortcut = $wsh.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $ExePath
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description = "AG2 Router - Antigravity Quota Router"
    $shortcut.IconLocation = "$ExePath,0"
    $shortcut.Save()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) | Out-Null
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($wsh) | Out-Null
    Write-Host "  [OK] Shortcut created: $ShortcutPath" -ForegroundColor Green
} catch {
    Write-Warning "Failed to create Start Menu shortcut: $_"
}

# 5. Register in Windows Add/Remove Programs (HKCU per-user)
Write-Host "Registering uninstaller in Windows registry..."
try {
    if (-not (Test-Path $UninstallKey)) {
        New-Item -Path $UninstallKey -Force | Out-Null
    }
    Set-ItemProperty -Path $UninstallKey -Name "DisplayName" -Value "AG2 Router"
    Set-ItemProperty -Path $UninstallKey -Name "DisplayVersion" -Value "0.2.0"
    Set-ItemProperty -Path $UninstallKey -Name "Publisher" -Value "AG2"
    Set-ItemProperty -Path $UninstallKey -Name "InstallLocation" -Value $InstallDir
    Set-ItemProperty -Path $UninstallKey -Name "DisplayIcon" -Value "$ExePath,0"
    Set-ItemProperty -Path $UninstallKey -Name "UninstallString" -Value "powershell.exe -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
    Set-ItemProperty -Path $UninstallKey -Name "NoModify" -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallKey -Name "NoRepair" -Value 1 -Type DWord
    Write-Host "  [OK] Registered in Add/Remove Programs (HKCU)." -ForegroundColor Green
} catch {
    Write-Warning "Failed to register Add/Remove programs entry: $_"
}

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "Installation completed successfully!" -ForegroundColor Green
Write-Host "To launch AG2 Router: & `"$ExePath`"" -ForegroundColor Cyan
Write-Host "To launch to tray:    & `"$ExePath`" --tray" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan
