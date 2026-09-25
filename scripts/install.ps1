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

# 1. Graceful Shutdown of Existing Running Instance via Named Mutex and Process Check
$sessionMutex = $null
$mutexHeld = [System.Threading.Mutex]::TryOpenExisting("Local\AG2Router_Session_Mutex", [ref]$sessionMutex)
if ($mutexHeld -and $sessionMutex) {
    $sessionMutex.Dispose()
}
$runningProcesses = Get-Process -Name "AG2Router" -ErrorAction SilentlyContinue

if ($mutexHeld -or $runningProcesses) {
    Write-Host "Detected running AG2 Router instance. Requesting graceful shutdown (--exit)..."
    if (-not (Test-Path $ExePath)) {
        throw "AG2 Router is running but its shutdown executable is missing at $ExePath. Installation aborted."
    }

    try {
        $exitProcess = Start-Process -FilePath $ExePath -ArgumentList "--exit" -PassThru -WindowStyle Hidden
        if (-not $exitProcess.WaitForExit(5000)) {
            throw "Shutdown request (--exit) did not finish within five seconds. Installation aborted."
        }
        if ($exitProcess.ExitCode -ne 0) {
            throw "Shutdown request (--exit) returned exit code $($exitProcess.ExitCode). Installation aborted."
        }
    } catch {
        throw "Failed to complete --exit shutdown request. Installation aborted: $_"
    }

    # Wait up to 5 seconds for mutex release and process termination
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $stillRunning = $true
    while ($sw.ElapsedMilliseconds -lt 5000) {
        $testMutex = $null
        $mHeld = [System.Threading.Mutex]::TryOpenExisting("Local\AG2Router_Session_Mutex", [ref]$testMutex)
        if ($mHeld -and $testMutex) { $testMutex.Dispose() }
        $remaining = Get-Process -Name "AG2Router" -ErrorAction SilentlyContinue
        if (-not $mHeld -and -not $remaining) {
            $stillRunning = $false
            break
        }
        Start-Sleep -Milliseconds 250
    }

    if ($stillRunning) {
        throw "AG2 Router is currently running and could not be gracefully closed within 5 seconds. Please exit AG2 Router from the system tray before proceeding."
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

# 3. Staging Directory & Atomic Directory Swap (V021-020)
Write-Host "Staging binaries for atomic installation to $InstallDir..."
$ParentDir = Split-Path -Path $InstallDir -Parent
if (-not (Test-Path $ParentDir)) {
    New-Item -ItemType Directory -Force -Path $ParentDir | Out-Null
}

$StagingDir = "$InstallDir.staging.$PID"
$BackupDir = "$InstallDir.bak.$PID"

if (Test-Path $StagingDir) {
    Remove-Item -Path $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null

# Copy files from SourceDir to StagingDir (excluding install scripts and staging/backup directories)
Get-ChildItem -Path $SourceDir | ForEach-Object {
    if ($_.Name -ne "install.ps1" -and $_.Name -ne "dist" -and -not $_.Name.StartsWith("AG2Router.staging.") -and -not $_.Name.StartsWith("AG2Router.bak.")) {
        Copy-Item -Path $_.FullName -Destination $StagingDir -Recurse -Force
    }
}

# Ensure uninstall script is copied into StagingDir
$sourceUninstall = Join-Path $SourceDir "uninstall.ps1"
if (Test-Path $sourceUninstall) {
    Copy-Item -Path $sourceUninstall -Destination (Join-Path $StagingDir "uninstall.ps1") -Force
}

# Validate essential executable exists in staging before swapping
$stagedExePath = Join-Path $StagingDir "AG2Router.exe"
if (-not (Test-Path $stagedExePath)) {
    Remove-Item -Path $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
    throw "Installation failed: AG2Router.exe not found in staged payload at $stagedExePath"
}

# Perform atomic rename swap with backup and rollback
$hasExistingInstall = Test-Path $InstallDir
if ($hasExistingInstall) {
    if (Test-Path $BackupDir) {
        Remove-Item -Path $BackupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Move-Item -Path $InstallDir -Destination $BackupDir -Force
}

try {
    Move-Item -Path $StagingDir -Destination $InstallDir -Force
} catch {
    # Rollback to previous installation if swap failed
    if ($hasExistingInstall -and (Test-Path $BackupDir) -and -not (Test-Path $InstallDir)) {
        Move-Item -Path $BackupDir -Destination $InstallDir -Force -ErrorAction SilentlyContinue
    }
    throw "Installation failed during atomic directory swap: $_"
} finally {
    if (Test-Path $InstallDir) {
        if (Test-Path $BackupDir) {
            Remove-Item -Path $BackupDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path $StagingDir) {
        Remove-Item -Path $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Validate essential executable exists in final installation
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

    # Derive DisplayVersion dynamically from the installed executable, falling back to release version
    $DisplayVersion = $null
    if (Test-Path $ExePath) {
        try {
            $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
            if ($vi.FileVersion) {
                $DisplayVersion = $vi.FileVersion
            }
        } catch {
            Write-Warning "Could not read file version from $ExePath: $_"
        }
    }
    if (-not $DisplayVersion) {
        $DisplayVersion = "0.4.0"
    }

    Set-ItemProperty -Path $UninstallKey -Name "DisplayName" -Value "AG2 Router"
    Set-ItemProperty -Path $UninstallKey -Name "DisplayVersion" -Value $DisplayVersion
    Set-ItemProperty -Path $UninstallKey -Name "Publisher" -Value "AG2"
    Set-ItemProperty -Path $UninstallKey -Name "InstallLocation" -Value $InstallDir
    Set-ItemProperty -Path $UninstallKey -Name "DisplayIcon" -Value "$ExePath,0"
    Set-ItemProperty -Path $UninstallKey -Name "UninstallString" -Value "powershell.exe -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
    Set-ItemProperty -Path $UninstallKey -Name "NoModify" -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallKey -Name "NoRepair" -Value 1 -Type DWord
    Write-Host "  [OK] Registered in Add/Remove Programs (HKCU) as v$DisplayVersion." -ForegroundColor Green
} catch {
    Write-Warning "Failed to register Add/Remove programs entry: $_"
}

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "Installation completed successfully!" -ForegroundColor Green
Write-Host "To launch AG2 Router: & `"$ExePath`"" -ForegroundColor Cyan
Write-Host "To launch to tray:    & `"$ExePath`" --tray" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan
