# AG2 Router - Per-User Windows Uninstaller
# Safely removes AG2 Router binaries, shortcuts, and owned startup registrations.
# PERMANENT PRESERVATION GUARANTEE: Never touches %LOCALAPPDATA%\AG2-Router (data, vault, sessions)
# or Windows Credential Manager target 'gemini:antigravity'.
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$ExpectedInstallDir = Join-Path $env:LOCALAPPDATA "Programs\AG2Router"
$ExePath = Join-Path $ExpectedInstallDir "AG2Router.exe"
$ProgramsFolder = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Programs)
$ShortcutPath = Join-Path $ProgramsFolder "AG2 Router.lnk"
$DesktopFolder = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::DesktopDirectory)
$DesktopShortcutPath = Join-Path $DesktopFolder "AG2 Router.lnk"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$RunValueName = "AG2Router"
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AG2Router"

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "AG2 Router - Per-User Uninstallation" -ForegroundColor Cyan
Write-Host "Target Directory: $ExpectedInstallDir" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# 1. Path Safety Preflight Verification
$NormalizedExpected = [System.IO.Path]::GetFullPath($ExpectedInstallDir).TrimEnd('\')
if (-not $NormalizedExpected.StartsWith([System.IO.Path]::GetFullPath($env:LOCALAPPDATA), [System.StringComparison]::OrdinalIgnoreCase) -or `
    -not $NormalizedExpected.EndsWith("Programs\AG2Router", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Security Preflight Failure: Target directory '$ExpectedInstallDir' is not the authorized per-user installation path (%LOCALAPPDATA%\Programs\AG2Router). Aborting uninstallation."
}

# 2. Graceful Process Shutdown via Named Mutex and Process Check
function Get-AG2RouterProcessState {
    try {
        # A successful full enumeration with no matching process proves STOPPED.
        # Get-Process -Name would throw for an ordinary absent process, making
        # that case indistinguishable from a failed query.
        $ag2Processes = @(Get-Process -ErrorAction Stop | Where-Object { $_.ProcessName -eq 'AG2Router' })
        if ($ag2Processes.Count -gt 0) { return 'RUNNING' }
        return 'STOPPED'
    } catch {
        return 'UNKNOWN'
    }
}

function Get-AG2RouterMutexState {
    try {
        $sessionMutex = $null
        $mutexHeld = [System.Threading.Mutex]::TryOpenExisting("Local\AG2Router_Session_Mutex", [ref]$sessionMutex)
        if ($mutexHeld -and $sessionMutex) { $sessionMutex.Dispose() }
        if ($mutexHeld) { return 'RUNNING' }
        return 'STOPPED'
    } catch {
        return 'UNKNOWN'
    }
}

function Invoke-AG2RouterExitRequest {
    param([string]$InstalledExe)
    if (-not (Test-Path $InstalledExe)) {
        throw "AG2 Router is running but its shutdown executable is missing. Uninstallation aborted."
    }
    try {
        $exitProcess = Start-Process -FilePath $InstalledExe -ArgumentList "--exit" -PassThru -WindowStyle Hidden
        if (-not $exitProcess.WaitForExit(5000)) {
            throw "Shutdown request (--exit) did not finish within five seconds. Uninstallation aborted."
        }
        if ($exitProcess.ExitCode -ne 0) {
            throw "Shutdown request (--exit) returned exit code $($exitProcess.ExitCode). Uninstallation aborted."
        }
    } catch {
        throw "Failed to complete --exit shutdown request. Uninstallation aborted: $_"
    }
}

function Assert-AG2RouterStopped {
    param([string]$InstalledExe)
    $mutexState = Get-AG2RouterMutexState
    if ($mutexState -eq 'UNKNOWN') {
        throw 'AG2 Router mutex state could not be verified. Uninstallation aborted.'
    }
    $processState = Get-AG2RouterProcessState
    if ($processState -eq 'UNKNOWN') {
        throw 'AG2 Router process state could not be verified. Uninstallation aborted.'
    }

    if ($mutexState -eq 'RUNNING' -or $processState -eq 'RUNNING') {
        Write-Host "Detected running AG2 Router instance. Requesting graceful shutdown (--exit)..."
        Invoke-AG2RouterExitRequest -InstalledExe $InstalledExe

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt 5000) {
            $mutexState = Get-AG2RouterMutexState
            if ($mutexState -eq 'UNKNOWN') {
                throw 'AG2 Router mutex state became unknown after --exit. Uninstallation aborted.'
            }
            $processState = Get-AG2RouterProcessState
            if ($processState -eq 'UNKNOWN') {
                throw 'AG2 Router process state became unknown after --exit. Uninstallation aborted.'
            }
            if ($mutexState -eq 'STOPPED' -and $processState -eq 'STOPPED') {
                Write-Host "  [OK] Process closed gracefully." -ForegroundColor Green
                return
            }
            Start-Sleep -Milliseconds 250
        }
        throw 'AG2 Router could not be proven stopped within five seconds. Uninstallation aborted.'
    }
}

Assert-AG2RouterStopped -InstalledExe $ExePath

# 3. Startup Registry Ownership Check & Safe Removal
if (Test-Path $RunKey) {
    $runVal = (Get-ItemProperty -Path $RunKey -Name $RunValueName -ErrorAction SilentlyContinue).$RunValueName
    if ($runVal) {
        Write-Host "Evaluating startup registration ownership for '$RunValueName'..."
        # Extract executable path from registry string
        # Handles canonical quoted paths: "C:\path\AG2Router.exe" --tray
        # Handles unquoted paths: C:\path\AG2Router.exe --tray
        $trimmedRunVal = $runVal.Trim()
        $extractedExe = $null
        if ($trimmedRunVal -match '^"(?<exe>[^"]+)"(?:\s+.*)?$') {
            $extractedExe = $Matches['exe'].Trim()
        } elseif ($trimmedRunVal -match '^(?<exe>.*?\.exe)(?:\s+.*)?$') {
            $extractedExe = $Matches['exe'].Trim()
        } else {
            $extractedExe = $trimmedRunVal
        }

        try {
            $normalizedRunPath = [System.IO.Path]::GetFullPath($extractedExe)
            $normalizedExePath = [System.IO.Path]::GetFullPath($ExePath)

            if ($normalizedRunPath.Equals($normalizedExePath, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-ItemProperty -Path $RunKey -Name $RunValueName -Force -ErrorAction SilentlyContinue
                Write-Host "  [OK] Owned startup Run key removed: $runVal" -ForegroundColor Green
            } else {
                Write-Host "  [SKIPPED] Startup Run value '$RunValueName' points to a different target: '$runVal'. Leaving untouched (ownership mismatch)." -ForegroundColor Yellow
            }
        } catch {
            Write-Host "  [SKIPPED] Could not normalize startup path '$extractedExe'. Leaving untouched." -ForegroundColor Yellow
        }
    }
}

# 4. Shortcut Ownership Check & Safe Removal
# A. Start Menu Shortcut
if (Test-Path $ShortcutPath) {
    Write-Host "Checking Start Menu shortcut ownership..."
    try {
        $wsh = New-Object -ComObject WScript.Shell
        $sc = $wsh.CreateShortcut($ShortcutPath)
        $scTarget = $sc.TargetPath
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($sc) | Out-Null
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($wsh) | Out-Null

        if ($scTarget -and [System.IO.Path]::GetFullPath($scTarget).Equals([System.IO.Path]::GetFullPath($ExePath), [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -Force $ShortcutPath -ErrorAction SilentlyContinue
            Write-Host "  [OK] Owned Start Menu shortcut removed: $ShortcutPath" -ForegroundColor Green
        } else {
            Write-Host "  [SKIPPED] Start Menu shortcut points to '$scTarget', not '$ExePath'. Leaving untouched." -ForegroundColor Yellow
        }
    } catch {
        Write-Warning "Failed to inspect Start Menu shortcut: $_"
    }
}

# B. Desktop Shortcut
if (Test-Path $DesktopShortcutPath) {
    Write-Host "Checking Desktop shortcut ownership..."
    try {
        $wsh = New-Object -ComObject WScript.Shell
        $sc = $wsh.CreateShortcut($DesktopShortcutPath)
        $scTarget = $sc.TargetPath
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($sc) | Out-Null
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($wsh) | Out-Null

        if ($scTarget -and [System.IO.Path]::GetFullPath($scTarget).Equals([System.IO.Path]::GetFullPath($ExePath), [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -Force $DesktopShortcutPath -ErrorAction SilentlyContinue
            Write-Host "  [OK] Owned Desktop shortcut removed: $DesktopShortcutPath" -ForegroundColor Green
        } else {
            Write-Host "  [SKIPPED] Desktop shortcut points to '$scTarget', not '$ExePath'. Leaving untouched." -ForegroundColor Yellow
        }
    } catch {
        Write-Warning "Failed to inspect Desktop shortcut: $_"
    }
}

# 5. Remove Uninstall Registry Key
if (Test-Path $UninstallKey) {
    Write-Host "Checking Add/Remove Programs registry ownership..."
    $instLoc = (Get-ItemProperty -Path $UninstallKey -Name "InstallLocation" -ErrorAction SilentlyContinue).InstallLocation
    if ($instLoc -and [System.IO.Path]::GetFullPath($instLoc).Equals($NormalizedExpected, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  [OK] Add/Remove Programs registry key removed." -ForegroundColor Green
    } else {
        Write-Host "  [SKIPPED] Uninstall key InstallLocation ('$instLoc') does not match expected '$NormalizedExpected'." -ForegroundColor Yellow
    }
}

# 6. Remove Program Binaries Directory
if (Test-Path $ExpectedInstallDir) {
    Write-Host "Removing program binaries from $ExpectedInstallDir..."
    # Explicitly ensure we never delete a parent directory
    if ($NormalizedExpected.Equals([System.IO.Path]::GetFullPath($env:LOCALAPPDATA), [System.StringComparison]::OrdinalIgnoreCase) -or `
        $NormalizedExpected.Equals([System.IO.Path]::GetFullPath(Join-Path $env:LOCALAPPDATA "Programs"), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Catastrophic Safety Failure: Resolved path matches a parent directory root! Aborting removal."
    }

    Remove-Item -Path $ExpectedInstallDir -Recurse -Force
    Write-Host "  [OK] Program directory removed." -ForegroundColor Green
}

# 7. Preservation Confirmation
$UserDataDir = Join-Path $env:LOCALAPPDATA "AG2-Router"
Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "Uninstallation completed." -ForegroundColor Green
if (Test-Path $UserDataDir) {
    Write-Host "Preserved user data and accounts directory:" -ForegroundColor Green
    Write-Host "  $UserDataDir" -ForegroundColor Yellow
}
Write-Host "Preserved Windows Credential Manager target 'gemini:antigravity'." -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Cyan
