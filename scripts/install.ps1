# AG2 Router - Per-User Windows Installer
# Installs AG2 Router into %LOCALAPPDATA%\Programs\AG2Router without admin privileges.
#
# ARCHITECTURAL INVARIANTS:
# 1. Synthetic Packaging Isolation (R01):
#    To allow automated testing without host side-effects (e.g. polluting host registry, Start Menu,
#    or killing host processes), all host interactions are routed through Invoke-AG2RouterEnvironment.
#    When $InstallationEnvironment is supplied, operations execute against an in-memory/isolated fixture.
#    Fail-closed policy: An injected environment MUST implement every requested operation; fallback to
#    production is strictly forbidden to prevent accidental host escapes.
# 2. Channel Ownership & Fixed-Key Protection (R08):
#    Differentiates between Inno Setup installations, PowerShell-managed installations, and foreign keys.
#    If the legacy registry key exists but cannot be proven to belong to this installation, it fails closed
#    with ForeignConflict to preserve foreign registry data value-for-value.
# 3. Process & Mutex Verification (R06):
#    Assert-AG2RouterStopped requires dual positive STOPPED proof (both process list and named mutex).
#    UNKNOWN != STOPPED: Any query failure or ambiguity aborts the install to prevent file-lock corruptions.
# 4. Atomic Staging & Rollback:
#    Binaries are staged into a PID-tagged directory, validated, and swapped into place using a backup
#    directory to guarantee atomic install/upgrade with clean rollback upon failure.
param(
    [string]$SourceDir = $PSScriptRoot,
    [switch]$Force,
    [hashtable]$RegistryOverride = $null,
    [hashtable]$InstallationEnvironment = $null
)

$ErrorActionPreference = "Stop"

# Dispatcher for environment operations.
# If $InstallationEnvironment is provided (synthetic testing mode), routes through the test mock.
# Fails closed immediately if an operation is unimplemented or if BoundaryErrors is missing.
function Invoke-AG2RouterEnvironment {
    param([string]$Operation, [hashtable]$Parameters = @{})
    if ($null -ne $InstallationEnvironment) {
        if (-not $InstallationEnvironment.ContainsKey('BoundaryErrors')) {
            throw 'An injected installation environment must supply BoundaryErrors.'
        }
        if (-not $InstallationEnvironment.ContainsKey($Operation) -or
            $InstallationEnvironment[$Operation] -isnot [scriptblock]) {
            $message = "Installation environment does not implement '$Operation'."
            [void]$InstallationEnvironment.BoundaryErrors.Add($message)
            throw $message
        }
        return & $InstallationEnvironment[$Operation] $Parameters
    }
    return Invoke-AG2RouterProductionEnvironment -Operation $Operation -Parameters $Parameters
}

function Invoke-AG2RouterProductionEnvironment {
    param([string]$Operation, [hashtable]$Parameters)
    if ($null -ne $InstallationEnvironment) {
        $message = 'Production installation resources are forbidden with an injected environment.'
        [void]$InstallationEnvironment.BoundaryErrors.Add($message)
        throw $message
    }
    switch ($Operation) {
        'LocalAppData' { return $env:LOCALAPPDATA }
        'KnownFolder' { return [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]$Parameters.Name) }
        'RegistryExists' { return Test-Path -LiteralPath $Parameters.Path }
        'ReadRegistry' {
            if (Test-Path -LiteralPath $Parameters.Path) {
                if ($Parameters.Name) {
                    return Get-ItemProperty -LiteralPath $Parameters.Path -Name $Parameters.Name -ErrorAction SilentlyContinue
                }
                return Get-ItemProperty -LiteralPath $Parameters.Path -ErrorAction SilentlyContinue
            }
            return $null
        }
        'CreateRegistry' { New-Item -Path $Parameters.Path -Force | Out-Null; return }
        'SetRegistry' {
            $values = @{ Path = $Parameters.Path; Name = $Parameters.Name; Value = $Parameters.Value }
            if ($Parameters.Type) { $values.Type = $Parameters.Type }
            Set-ItemProperty @values
            return
        }
        'RemoveRegistry' { Remove-Item -LiteralPath $Parameters.Path -Recurse -Force -ErrorAction SilentlyContinue; return }
        'RemoveRegistryValue' { Remove-ItemProperty -LiteralPath $Parameters.Path -Name $Parameters.Name -Force -ErrorAction SilentlyContinue; return }
        'CreateShortcut' {
            $wsh = New-Object -ComObject WScript.Shell
            $shortcut = $wsh.CreateShortcut($Parameters.Path)
            $shortcut.TargetPath = $Parameters.Target
            $shortcut.WorkingDirectory = $Parameters.WorkingDirectory
            $shortcut.Description = 'AG2 Router - Antigravity Quota Router'
            $shortcut.IconLocation = "$($Parameters.Target),0"
            $shortcut.Save()
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) | Out-Null
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($wsh) | Out-Null
            return
        }
        'ShortcutTarget' {
            $wsh = New-Object -ComObject WScript.Shell
            $shortcut = $wsh.CreateShortcut($Parameters.Path)
            $target = $shortcut.TargetPath
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) | Out-Null
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($wsh) | Out-Null
            return $target
        }
        'ProcessState' {
            try {
                $ag2Processes = @(Get-Process -ErrorAction Stop | Where-Object { $_.ProcessName -eq 'AG2Router' })
                if ($ag2Processes.Count -gt 0) { return 'RUNNING' }
                return 'STOPPED'
            } catch { return 'UNKNOWN' }
        }
        'MutexState' {
            try {
                $sessionMutex = $null
                $mutexHeld = [System.Threading.Mutex]::TryOpenExisting('Local\AG2Router_Session_Mutex', [ref]$sessionMutex)
                if ($mutexHeld -and $sessionMutex) { $sessionMutex.Dispose() }
                if ($mutexHeld) { return 'RUNNING' }
                return 'STOPPED'
            } catch { return 'UNKNOWN' }
        }
        'StartExitProcess' { return Start-Process -FilePath $Parameters.Path -ArgumentList '--exit' -PassThru -WindowStyle Hidden }
        'FileVersion' { return [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Parameters.Path).FileVersion }
        'FileSystem' {
            $commands = @{
                'Test-Path' = 'Microsoft.PowerShell.Management\Test-Path'
                'Get-ChildItem' = 'Microsoft.PowerShell.Management\Get-ChildItem'
                'New-Item' = 'Microsoft.PowerShell.Management\New-Item'
                'Copy-Item' = 'Microsoft.PowerShell.Management\Copy-Item'
                'Move-Item' = 'Microsoft.PowerShell.Management\Move-Item'
                'Remove-Item' = 'Microsoft.PowerShell.Management\Remove-Item'
            }
            if (-not $commands.ContainsKey($Parameters.Command)) { throw 'Unsupported installation filesystem operation.' }
            $arguments = $Parameters.Arguments
            return & $commands[$Parameters.Command] @arguments
        }
        default { throw "Unsupported installation environment operation '$Operation'." }
    }
}

function Test-AG2RouterFile {
    [CmdletBinding()] param([string]$Path, [string]$LiteralPath)
    Invoke-AG2RouterEnvironment 'FileSystem' @{ Command = 'Test-Path'; Arguments = $PSBoundParameters }
}
function Get-AG2RouterFiles {
    [CmdletBinding()] param([string]$Path, [string]$LiteralPath, [switch]$Force)
    Invoke-AG2RouterEnvironment 'FileSystem' @{ Command = 'Get-ChildItem'; Arguments = $PSBoundParameters }
}
function New-AG2RouterDirectory {
    [CmdletBinding()] param([string]$Path, [string]$ItemType, [switch]$Force)
    Invoke-AG2RouterEnvironment 'FileSystem' @{ Command = 'New-Item'; Arguments = $PSBoundParameters }
}
function Copy-AG2RouterFile {
    [CmdletBinding()] param([string]$Path, [string]$Destination, [switch]$Recurse, [switch]$Force)
    Invoke-AG2RouterEnvironment 'FileSystem' @{ Command = 'Copy-Item'; Arguments = $PSBoundParameters }
}
function Move-AG2RouterFile {
    [CmdletBinding()] param([string]$Path, [string]$Destination, [switch]$Force)
    Invoke-AG2RouterEnvironment 'FileSystem' @{ Command = 'Move-Item'; Arguments = $PSBoundParameters }
}
function Remove-AG2RouterFile {
    [CmdletBinding()] param([string]$Path, [switch]$Recurse, [switch]$Force)
    Invoke-AG2RouterEnvironment 'FileSystem' @{ Command = 'Remove-Item'; Arguments = $PSBoundParameters }
}


$LocalAppData = Invoke-AG2RouterEnvironment 'LocalAppData'
$InstallDir = Join-Path $LocalAppData "Programs\AG2Router"
$ExePath = Join-Path $InstallDir "AG2Router.exe"
$ProgramsFolder = (Invoke-AG2RouterEnvironment 'KnownFolder' @{ Name = 'Programs' })
$ShortcutPath = Join-Path $ProgramsFolder "AG2 Router.lnk"
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AG2Router"

function Get-AG2RouterRegistryValue {
    param(
        [string]$Path,
        [hashtable]$RegistryOverride = $null
    )
    if ($null -ne $InstallationEnvironment -and $null -ne $RegistryOverride) {
        return $RegistryOverride[$Path]
    }
    if ($RegistryOverride -and $RegistryOverride.ContainsKey($Path)) {
        return $RegistryOverride[$Path]
    }
    return Invoke-AG2RouterEnvironment 'ReadRegistry' @{ Path = $Path }
}

function Test-AG2RouterInnoRegistrationMatch {
    param(
        $Properties,
        [string]$TargetDir
    )
    if (-not $Properties) { return $false }

    $displayName = $Properties.DisplayName
    if (-not $displayName -or ($displayName -notmatch '(?i)^AG2\s+Router')) {
        return $false
    }

    $publisher = $Properties.Publisher
    if ($publisher -and ($publisher.ToString().Trim() -ne 'AG2')) {
        return $false
    }

    $normTarget = [System.IO.Path]::GetFullPath($TargetDir).TrimEnd('\')

    if ($Properties.InstallLocation) {
        try {
            $normLoc = [System.IO.Path]::GetFullPath($Properties.InstallLocation).TrimEnd('\')
            if ($normLoc.Equals($normTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        } catch {}
    }

    $appPath = $Properties.'Inno Setup: App Path'
    if ($appPath) {
        try {
            $normApp = [System.IO.Path]::GetFullPath($appPath).TrimEnd('\')
            if ($normApp.Equals($normTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        } catch {}
    }

    if ($Properties.UninstallString) {
        try {
            $uninst = $Properties.UninstallString.ToString().Trim()
            $exePath = $null
            if ($uninst -match '^"(?<exe>[^"]+)"') {
                $exePath = $Matches['exe']
            } elseif ($uninst -match '^(?<exe>\S+\.exe)') {
                $exePath = $Matches['exe']
            }
            if ($exePath) {
                $exeDir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($exePath)).TrimEnd('\')
                if ($exeDir.Equals($normTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $true
                }
            }
        } catch {}
    }

    return $false
}

function Test-AG2RouterPowerShellRegistrationMatch {
    param(
        $Properties,
        [string]$TargetDir
    )
    if (-not $Properties) { return $false }

    $displayName = $Properties.DisplayName
    if (-not $displayName -or $displayName.ToString().Trim() -ne 'AG2 Router') {
        return $false
    }

    $publisher = $Properties.Publisher
    if (-not $publisher -or $publisher.ToString().Trim() -ne 'AG2') {
        return $false
    }

    $installLocation = $Properties.InstallLocation
    if (-not $installLocation) {
        return $false
    }
    try {
        $normInstall = [System.IO.Path]::GetFullPath($installLocation).TrimEnd('\')
        $normTarget = [System.IO.Path]::GetFullPath($TargetDir).TrimEnd('\')
        if (-not $normInstall.Equals($normTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    } catch {
        return $false
    }

    $uninstallString = $Properties.UninstallString
    if (-not $uninstallString) {
        return $false
    }
    $lowerUninst = $uninstallString.ToString().ToLowerInvariant()
    if (-not $lowerUninst.Contains("uninstall.ps1") -and -not $lowerUninst.Contains("ag2router")) {
        return $false
    }

    return $true
}

# Determines channel ownership for the specified target directory.
# Invariants (R08):
# - "absent != foreign": If the legacy registry key exists but fails to match AG2 Router's publisher,
#   display name, install location, and uninstall string, the key is classified as ForeignConflict.
#   The installer will abort rather than overwrite third-party software registry keys.
# - Channel isolation: Distinguishes between Inno Setup installations (identified by InnoAppId GUID
#   and unins000 files) and PowerShell installations (identified by the AG2Router key).
#   PowerShell installer refuses to touch or upgrade an Inno-owned directory to prevent mixed-installer corruption.
function Get-AG2RouterInstallationOwnership {
    param(
        [string]$TargetDir,
        [hashtable]$RegistryOverride = $null
    )

    $InnoAppId = "{D37E7404-585A-4B6A-B7F9-5360980DF628}"
    $InnoKeyName = "${InnoAppId}_is1"
    $LegacyKeyName = "AG2Router"

    $innoHives = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$InnoKeyName",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$InnoKeyName",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$InnoKeyName"
    )

    $hasInnoReg = $false
    $innoUninstallerCmd = $null
    foreach ($innoPath in $innoHives) {
        $props = Get-AG2RouterRegistryValue -Path $innoPath -RegistryOverride $RegistryOverride
        if ($props -and (Test-AG2RouterInnoRegistrationMatch -Properties $props -TargetDir $TargetDir)) {
            $hasInnoReg = $true
            if ($props.UninstallString) {
                $innoUninstallerCmd = $props.UninstallString.ToString().Trim()
            }
            break
        }
    }

    $legacyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$LegacyKeyName"
    $legacyKeyExists = $false
    if ($null -ne $RegistryOverride) {
        if ($RegistryOverride.ContainsKey($legacyPath)) {
            $legacyKeyExists = ($null -ne $RegistryOverride[$legacyPath])
        } else {
            $legacyKeyExists = Invoke-AG2RouterEnvironment 'RegistryExists' @{ Path = $legacyPath }
        }
    } else {
        $legacyKeyExists = Invoke-AG2RouterEnvironment 'RegistryExists' @{ Path = $legacyPath }
    }

    $legacyProps = Get-AG2RouterRegistryValue -Path $legacyPath -RegistryOverride $RegistryOverride
    $hasPsReg = $false
    $hasForeignPsReg = $false
    if ($legacyKeyExists) {
        if ($legacyProps -and (Test-AG2RouterPowerShellRegistrationMatch -Properties $legacyProps -TargetDir $TargetDir)) {
            $hasPsReg = $true
        } else {
            $hasForeignPsReg = $true
        }
    }

    $hasInnoFiles = $false
    $hasAnyFiles = $false
    if (Test-AG2RouterFile -LiteralPath $TargetDir) {
        try {
            $items = @(Get-AG2RouterFiles -LiteralPath $TargetDir -Force -ErrorAction SilentlyContinue)
            if ($items.Count -gt 0) {
                $hasAnyFiles = $true
                foreach ($item in $items) {
                    if ($item.Name -like "unins*.exe" -or $item.Name -like "unins*.dat") {
                        $hasInnoFiles = $true
                        if (-not $innoUninstallerCmd -and $item.Name -like "unins*.exe") {
                            $innoUninstallerCmd = $item.FullName
                        }
                        break
                    }
                }
            }
        } catch {}
    }

    $result = [PSCustomObject]@{
        Status = "None"
        InnoUninstaller = $innoUninstallerCmd
        Detail = ""
    }

    if ($hasForeignPsReg) {
        $result.Status = "ForeignConflict"
        $result.Detail = "An existing uninstall registration at the AG2 Router PowerShell registry key ($LegacyKeyName) could not be proven to belong to this installation."
        return $result
    }

    if ($hasInnoReg -and $hasPsReg) {
        $result.Status = "Ambiguous"
        $result.Detail = "Conflicting registrations: Both Inno Setup ($InnoKeyName) and PowerShell ($LegacyKeyName) uninstall entries exist for '$TargetDir'."
        return $result
    }

    if ($hasInnoReg) {
        $result.Status = "InnoOwned"
        $result.Detail = "Inno Setup installation detected (AppId: $InnoAppId)."
        return $result
    }

    if ($hasPsReg) {
        if ($hasInnoFiles) {
            $result.Status = "Ambiguous"
            $result.Detail = "Conflicting state: PowerShell uninstall registration exists, but Inno Setup uninstaller files ('unins*') are present in '$TargetDir'."
            return $result
        }
        $result.Status = "PowerShellOwned"
        $result.Detail = "PowerShell-managed installation detected."
        return $result
    }

    # Neither registration exists
    if ($hasInnoFiles) {
        $result.Status = "Ambiguous"
        $result.Detail = "Inno Setup uninstaller files were detected in '$TargetDir', but no matching registry registration was found."
        return $result
    }

    if ($hasAnyFiles) {
        $result.Status = "Ambiguous"
        $result.Detail = "Target directory '$TargetDir' contains files, but no recognized installation registration was found."
        return $result
    }

    $result.Status = "None"
    $result.Detail = "No installation detected."
    return $result
}

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "AG2 Router - Per-User Installation" -ForegroundColor Cyan
Write-Host "Target Directory: $InstallDir" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# 0. Installation Ownership Verification
$ownership = Get-AG2RouterInstallationOwnership -TargetDir $InstallDir -RegistryOverride $RegistryOverride

if ($ownership.Status -eq "ForeignConflict") {
    throw "Installation Channel Conflict: An existing uninstall registration at the AG2 Router PowerShell registry key could not be proven to belong to this installation. Refusing to overwrite it."
}

if ($ownership.Status -eq "InnoOwned") {
    $uninstMsg = if ($ownership.InnoUninstaller) { " To switch channels, uninstall AG2 Router first (`"$($ownership.InnoUninstaller)`" or Windows Settings > Installed Apps) before installing via PowerShell." } else { " To switch channels, uninstall AG2 Router first via Windows Settings > Installed Apps before installing via PowerShell." }
    throw "Installation Channel Conflict: An Inno Setup installation of AG2 Router was detected at '$InstallDir' (AppId: {D37E7404-585A-4B6A-B7F9-5360980DF628}). The PowerShell installer cannot overwrite an Inno Setup installation. To upgrade, use the Inno Setup installer ('AG2Router-Setup-*.exe').$uninstMsg"
}

if ($ownership.Status -eq "Ambiguous") {
    throw "Installation Channel Conflict: Ambiguous installation ownership detected for '$InstallDir': $($ownership.Detail) Refusing to replace application files. Resolve the conflicting installation state before proceeding."
}

# 1. Graceful Shutdown of Existing Running Instance via Named Mutex and Process Check
if (-not (Get-Command 'Get-AG2RouterProcessState' -ErrorAction SilentlyContinue -CommandType Function)) {
    function Get-AG2RouterProcessState { return Invoke-AG2RouterEnvironment 'ProcessState' }
}
if (-not (Get-Command 'Get-AG2RouterMutexState' -ErrorAction SilentlyContinue -CommandType Function)) {
    function Get-AG2RouterMutexState { return Invoke-AG2RouterEnvironment 'MutexState' }
}

function Invoke-AG2RouterExitRequest {
    param([string]$InstalledExe)
    if (-not (Test-AG2RouterFile $InstalledExe)) {
        throw "AG2 Router is running but its shutdown executable is missing at $InstalledExe. Installation aborted."
    }

    try {
        $exitProcess = Invoke-AG2RouterEnvironment 'StartExitProcess' @{ Path = $InstalledExe }
        if (-not $exitProcess.WaitForExit(5000)) {
            throw "Shutdown request (--exit) did not finish within five seconds. Installation aborted."
        }
        if ($exitProcess.ExitCode -ne 0) {
            throw "Shutdown request (--exit) returned exit code $($exitProcess.ExitCode). Installation aborted."
        }
    } catch {
        throw "Failed to complete --exit shutdown request. Installation aborted: $_"
    }
}

# Verifies that AG2 Router is completely stopped before proceeding with file mutations.
# Invariants (R06):
# - Positive STOPPED proof: Requires BOTH named mutex (Local\AG2Router_Session_Mutex) AND process table
#   enumeration to return 'STOPPED'.
# - UNKNOWN != STOPPED: If either check encounters access denial or diagnostic errors, it returns 'UNKNOWN'.
#   The installer retries up to 3 times for transient glitches, and fails closed if UNKNOWN persists.
# - Graceful shutdown (--exit): If a running instance is detected, the installer signals it via the IPC
#   named pipe (--exit flag) and waits up to 5 seconds for clean termination. If the process does not exit
#   cleanly, installation is aborted to prevent file lock corruption.
function Assert-AG2RouterStopped {
    param([string]$InstalledExe)

    $mutexState = $null
    $processState = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $mutexState = Get-AG2RouterMutexState
        $processState = Get-AG2RouterProcessState

        if ($mutexState -eq 'RUNNING' -or $processState -eq 'RUNNING') {
            break
        }
        if ($mutexState -eq 'STOPPED' -and $processState -eq 'STOPPED') {
            break
        }
        if ($attempt -lt 3) {
            Start-Sleep -Milliseconds 100
        }
    }

    if ($mutexState -eq 'STOPPED' -and $processState -eq 'STOPPED') {
        return
    }

    if ($mutexState -eq 'RUNNING' -or $processState -eq 'RUNNING') {
        Write-Host "Detected running AG2 Router instance. Requesting graceful shutdown (--exit)..."
        Invoke-AG2RouterExitRequest -InstalledExe $InstalledExe

        # Poll in a wait loop (up to 5 seconds, sleeping 250ms) checking both states.
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $mState = $mutexState
        $pState = $processState
        while ($sw.ElapsedMilliseconds -lt 5000) {
            $mState = Get-AG2RouterMutexState
            $pState = Get-AG2RouterProcessState
            if ($mState -eq 'STOPPED' -and $pState -eq 'STOPPED') {
                Write-Host "  [OK] Previous instance closed gracefully." -ForegroundColor Green
                return
            }
            Start-Sleep -Milliseconds 250
        }

        if ($pState -eq 'RUNNING' -or $mState -eq 'RUNNING') {
            throw "AG2 Router is currently running and could not be gracefully closed within 5 seconds. Please exit AG2 Router from the system tray before proceeding."
        }
        if ($pState -eq 'UNKNOWN' -or $mState -eq 'UNKNOWN') {
            throw "AG2 Router stopped state could not be verified after shutdown request (process: $pState, mutex: $mState). Refusing to replace application files while instance state is unknown. Installation aborted."
        }
        return
    }

    throw "AG2 Router stopped state could not be verified (process: $processState, mutex: $mutexState). Refusing to replace application files while instance state is unknown. Please ensure AG2 Router is stopped before proceeding."
}

Assert-AG2RouterStopped -InstalledExe $ExePath

# 2. Prerequisite Check: Microsoft Edge WebView2 Evergreen Runtime
$webview2Installed = $false
$wv2Paths = @(
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    "HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
)

foreach ($regPath in $wv2Paths) {
    if (Invoke-AG2RouterEnvironment 'RegistryExists' @{ Path = $regPath }) {
        $pv = (Invoke-AG2RouterEnvironment 'ReadRegistry' @{ Path = $regPath; Name = 'pv' }).pv
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
if (-not (Test-AG2RouterFile $ParentDir)) {
    New-AG2RouterDirectory -ItemType Directory -Force -Path $ParentDir | Out-Null
}

$StagingDir = "$InstallDir.staging.$PID"
$BackupDir = "$InstallDir.bak.$PID"

if (Test-AG2RouterFile $StagingDir) {
    Remove-AG2RouterFile -Path $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-AG2RouterDirectory -ItemType Directory -Force -Path $StagingDir | Out-Null

# Copy files from SourceDir to StagingDir (excluding install scripts and staging/backup directories)
Get-AG2RouterFiles -Path $SourceDir | ForEach-Object {
    if ($_.Name -ne "install.ps1" -and $_.Name -ne "dist" -and -not $_.Name.StartsWith("AG2Router.staging.") -and -not $_.Name.StartsWith("AG2Router.bak.")) {
        Copy-AG2RouterFile -Path $_.FullName -Destination $StagingDir -Recurse -Force
    }
}

# Ensure uninstall script is copied into StagingDir
$sourceUninstall = Join-Path $SourceDir "uninstall.ps1"
if (Test-AG2RouterFile $sourceUninstall) {
    Copy-AG2RouterFile -Path $sourceUninstall -Destination (Join-Path $StagingDir "uninstall.ps1") -Force
}

# Validate essential executable exists in staging before swapping
$stagedExePath = Join-Path $StagingDir "AG2Router.exe"
if (-not (Test-AG2RouterFile $stagedExePath)) {
    Remove-AG2RouterFile -Path $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
    throw "Installation failed: AG2Router.exe not found in staged payload at $stagedExePath"
}

# 3b. Atomic Directory Swap & Rollback
# Invariant: Never overwrite the live installation directory in place.
# 1. Back up existing $InstallDir to $BackupDir ($InstallDir.bak.$PID).
# 2. Rename $StagingDir to $InstallDir.
# 3. If rename fails, rollback $BackupDir back to $InstallDir so the user remains on working binaries.
# 4. In finally block, clean up transient staging and backup artifacts.
$hasExistingInstall = Test-AG2RouterFile $InstallDir
if ($hasExistingInstall) {
    if (Test-AG2RouterFile $BackupDir) {
        Remove-AG2RouterFile -Path $BackupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Move-AG2RouterFile -Path $InstallDir -Destination $BackupDir -Force
}

try {
    Move-AG2RouterFile -Path $StagingDir -Destination $InstallDir -Force
} catch {
    # Rollback to previous installation if swap failed
    if ($hasExistingInstall -and (Test-AG2RouterFile $BackupDir) -and -not (Test-AG2RouterFile $InstallDir)) {
        Move-AG2RouterFile -Path $BackupDir -Destination $InstallDir -Force -ErrorAction SilentlyContinue
    }
    throw "Installation failed during atomic directory swap: $_"
} finally {
    if (Test-AG2RouterFile $InstallDir) {
        if (Test-AG2RouterFile $BackupDir) {
            Remove-AG2RouterFile -Path $BackupDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-AG2RouterFile $StagingDir) {
        Remove-AG2RouterFile -Path $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Validate essential executable exists in final installation
if (-not (Test-AG2RouterFile $ExePath)) {
    throw "Installation failed: AG2Router.exe not found at $ExePath"
}

# 4. Create Start Menu Shortcut
Write-Host "Creating Start Menu shortcut..."
try {
    Invoke-AG2RouterEnvironment 'CreateShortcut' @{ Path = $ShortcutPath; Target = $ExePath; WorkingDirectory = $InstallDir }
    Write-Host "  [OK] Shortcut created: $ShortcutPath" -ForegroundColor Green
} catch {
    Write-Warning "Failed to create Start Menu shortcut: $_"
}

# 5. Register in Windows Add/Remove Programs (HKCU per-user)
Write-Host "Registering uninstaller in Windows registry..."
try {
    if (-not (Invoke-AG2RouterEnvironment 'RegistryExists' @{ Path = $UninstallKey })) {
        Invoke-AG2RouterEnvironment 'CreateRegistry' @{ Path = $UninstallKey }
    }

    # Derive DisplayVersion dynamically from the installed executable, falling back to release version
    $DisplayVersion = $null
    if (Test-AG2RouterFile $ExePath) {
        try {
            $DisplayVersion = Invoke-AG2RouterEnvironment 'FileVersion' @{ Path = $ExePath }
        } catch {
            Write-Warning "Could not read file version from ${ExePath}: $_"
        }
    }
    if (-not $DisplayVersion) {
        $DisplayVersion = "0.4.0"
    }

    Invoke-AG2RouterEnvironment 'SetRegistry' @{ Path = $UninstallKey; Name = 'DisplayName'; Value = "AG2 Router" }
    Invoke-AG2RouterEnvironment 'SetRegistry' @{ Path = $UninstallKey; Name = 'DisplayVersion'; Value = $DisplayVersion }
    Invoke-AG2RouterEnvironment 'SetRegistry' @{ Path = $UninstallKey; Name = 'Publisher'; Value = "AG2" }
    Invoke-AG2RouterEnvironment 'SetRegistry' @{ Path = $UninstallKey; Name = 'InstallLocation'; Value = $InstallDir }
    Invoke-AG2RouterEnvironment 'SetRegistry' @{ Path = $UninstallKey; Name = 'DisplayIcon'; Value = "$ExePath,0" }
    Invoke-AG2RouterEnvironment 'SetRegistry' @{ Path = $UninstallKey; Name = 'UninstallString'; Value = "powershell.exe -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`"" }
    Invoke-AG2RouterEnvironment 'SetRegistry' @{ Path = $UninstallKey; Name = 'NoModify'; Value = 1; Type = 'DWord' }
    Invoke-AG2RouterEnvironment 'SetRegistry' @{ Path = $UninstallKey; Name = 'NoRepair'; Value = 1; Type = 'DWord' }
    Write-Host "  [OK] Registered in Add/Remove Programs (HKCU) as v$DisplayVersion." -ForegroundColor Green
} catch {
    Write-Warning "Failed to register Add/Remove programs entry: $_"
}

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "Installation completed successfully!" -ForegroundColor Green
Write-Host "To launch AG2 Router: & `"$ExePath`"" -ForegroundColor Cyan
Write-Host "To launch to tray:    & `"$ExePath`" --tray" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan
