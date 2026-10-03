# AG2 Router - Per-User Windows Uninstaller
# Safely removes AG2 Router binaries, shortcuts, and owned startup registrations.
#
# ARCHITECTURAL INVARIANTS:
# 1. Permanent Preservation Guarantee:
#    The uninstaller NEVER touches or deletes %LOCALAPPDATA%\AG2-Router (which houses persistent
#    user accounts, encrypted session vault, routing journal, and configuration), NOR the Windows
#    Credential Manager target 'gemini:antigravity'. User identity is durable across installs/uninstalls.
# 2. Synthetic Packaging Isolation (R01):
#    All host operations are mediated by Invoke-AG2RouterEnvironment. Injected test environments
#    fail closed if an operation is missing or if production fallback is attempted.
# 3. Ownership Disambiguation & Fixed-Key Protection (R08):
#    "absent != foreign". If the uninstall registration or startup Run value exists but cannot be proven
#    to point to our binary path, uninstallation skips or aborts rather than deleting third-party data.
# 4. Stopped-State Fail-Closed Verification (R06):
#    Assert-AG2RouterStopped requires positive STOPPED proof from both named mutex and process table.
#    UNKNOWN != STOPPED: Any query failure immediately aborts uninstallation.
param(
    [switch]$Force,
    [hashtable]$RegistryOverride = $null,
    [hashtable]$InstallationEnvironment = $null
)

$ErrorActionPreference = "Stop"

# Dispatcher for environment operations.
# Routes all file, registry, process, and shortcut actions through the injected test environment
# when running packaging tests, failing closed with BoundaryErrors if an operation is missing.
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
$ExpectedInstallDir = Join-Path $LocalAppData "Programs\AG2Router"
$ExePath = Join-Path $ExpectedInstallDir "AG2Router.exe"
$ProgramsFolder = (Invoke-AG2RouterEnvironment 'KnownFolder' @{ Name = 'Programs' })
$ShortcutPath = Join-Path $ProgramsFolder "AG2 Router.lnk"
$DesktopFolder = (Invoke-AG2RouterEnvironment 'KnownFolder' @{ Name = 'DesktopDirectory' })
$DesktopShortcutPath = Join-Path $DesktopFolder "AG2 Router.lnk"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$RunValueName = "AG2Router"
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

# Determines channel ownership for the specified target directory during uninstallation.
# Invariants (R08):
# - "absent != foreign": If the legacy registry key exists but cannot be confirmed to belong to this
#   installation, it is flagged as ForeignConflict and uninstallation aborts to prevent deleting foreign registrations.
# - Channel isolation: Refuses to uninstall an Inno Setup installation via the PowerShell uninstaller (InnoOwned).
#   The user must invoke the Inno uninstaller (unins000.exe) instead.
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
Write-Host "AG2 Router - Per-User Uninstallation" -ForegroundColor Cyan
Write-Host "Target Directory: $ExpectedInstallDir" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# 1. Path Safety Preflight Verification
$NormalizedExpected = [System.IO.Path]::GetFullPath($ExpectedInstallDir).TrimEnd('\')
if (-not $NormalizedExpected.StartsWith([System.IO.Path]::GetFullPath($LocalAppData), [System.StringComparison]::OrdinalIgnoreCase) -or `
    -not $NormalizedExpected.EndsWith("Programs\AG2Router", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Security Preflight Failure: Target directory '$ExpectedInstallDir' is not the authorized per-user installation path (%LOCALAPPDATA%\Programs\AG2Router). Aborting uninstallation."
}

# 1b. Installation Ownership Verification
$ownership = Get-AG2RouterInstallationOwnership -TargetDir $ExpectedInstallDir -RegistryOverride $RegistryOverride

if ($ownership.Status -eq "ForeignConflict") {
    throw "Installation Channel Conflict: An existing uninstall registration at the AG2 Router PowerShell registry key could not be proven to belong to this installation. Refusing to remove or modify it."
}

if ($ownership.Status -eq "InnoOwned") {
    $uninstMsg = if ($ownership.InnoUninstaller) { " Run uninstaller: `"$($ownership.InnoUninstaller)`"" } else { " Use Windows Settings > Installed Apps to uninstall." }
    throw "Installation Channel Conflict: Target directory '$ExpectedInstallDir' is owned by an Inno Setup installation (AppId: {D37E7404-585A-4B6A-B7F9-5360980DF628}). The PowerShell uninstaller cannot remove an Inno Setup installation.$uninstMsg"
}

if ($ownership.Status -eq "Ambiguous") {
    throw "Installation Channel Conflict: Ambiguous installation ownership detected for '$ExpectedInstallDir': $($ownership.Detail) Refusing to remove files or registry entries. Resolve the conflicting installation state before uninstalling."
}

if ($ownership.Status -eq "None") {
    Write-Host "No AG2 Router installation detected at '$ExpectedInstallDir'." -ForegroundColor Yellow
    Write-Host "Uninstallation completed (no action required)." -ForegroundColor Green
    return
}

# 2. Graceful Process Shutdown via Named Mutex and Process Check
if (-not (Get-Command 'Get-AG2RouterProcessState' -ErrorAction SilentlyContinue -CommandType Function)) {
    function Get-AG2RouterProcessState { return Invoke-AG2RouterEnvironment 'ProcessState' }
}
if (-not (Get-Command 'Get-AG2RouterMutexState' -ErrorAction SilentlyContinue -CommandType Function)) {
    function Get-AG2RouterMutexState { return Invoke-AG2RouterEnvironment 'MutexState' }
}

function Invoke-AG2RouterExitRequest {
    param([string]$InstalledExe)
    if (-not (Test-AG2RouterFile $InstalledExe)) {
        throw "AG2 Router is running but its shutdown executable is missing. Uninstallation aborted."
    }
    try {
        $exitProcess = Invoke-AG2RouterEnvironment 'StartExitProcess' @{ Path = $InstalledExe }
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

# Verifies that AG2 Router is completely stopped before uninstallation proceeds.
# Invariants (R06):
# - UNKNOWN != STOPPED: Both mutex and process state must affirmatively resolve to 'STOPPED'.
#   Any UNKNOWN state throws an error and aborts uninstallation.
# - Graceful shutdown (--exit): If a running process is detected, sends an exit request and awaits
#   termination for up to 5 seconds. If termination cannot be proven, uninstallation aborts to
#   prevent deleting files currently held open.
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
if (Invoke-AG2RouterEnvironment 'RegistryExists' @{ Path = $RunKey }) {
    $runVal = (Invoke-AG2RouterEnvironment 'ReadRegistry' @{ Path = $RunKey; Name = $RunValueName }).$RunValueName
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
                Invoke-AG2RouterEnvironment 'RemoveRegistryValue' @{ Path = $RunKey; Name = $RunValueName }
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
if (Test-AG2RouterFile $ShortcutPath) {
    Write-Host "Checking Start Menu shortcut ownership..."
    try {
        $scTarget = Invoke-AG2RouterEnvironment 'ShortcutTarget' @{ Path = $ShortcutPath }

        if ($scTarget -and [System.IO.Path]::GetFullPath($scTarget).Equals([System.IO.Path]::GetFullPath($ExePath), [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-AG2RouterFile -Force $ShortcutPath -ErrorAction SilentlyContinue
            Write-Host "  [OK] Owned Start Menu shortcut removed: $ShortcutPath" -ForegroundColor Green
        } else {
            Write-Host "  [SKIPPED] Start Menu shortcut points to '$scTarget', not '$ExePath'. Leaving untouched." -ForegroundColor Yellow
        }
    } catch {
        Write-Warning "Failed to inspect Start Menu shortcut: $_"
    }
}

# B. Desktop Shortcut
if (Test-AG2RouterFile $DesktopShortcutPath) {
    Write-Host "Checking Desktop shortcut ownership..."
    try {
        $scTarget = Invoke-AG2RouterEnvironment 'ShortcutTarget' @{ Path = $DesktopShortcutPath }

        if ($scTarget -and [System.IO.Path]::GetFullPath($scTarget).Equals([System.IO.Path]::GetFullPath($ExePath), [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-AG2RouterFile -Force $DesktopShortcutPath -ErrorAction SilentlyContinue
            Write-Host "  [OK] Owned Desktop shortcut removed: $DesktopShortcutPath" -ForegroundColor Green
        } else {
            Write-Host "  [SKIPPED] Desktop shortcut points to '$scTarget', not '$ExePath'. Leaving untouched." -ForegroundColor Yellow
        }
    } catch {
        Write-Warning "Failed to inspect Desktop shortcut: $_"
    }
}

# 5. Remove Uninstall Registry Key
if (Invoke-AG2RouterEnvironment 'RegistryExists' @{ Path = $UninstallKey }) {
    Write-Host "Checking Add/Remove Programs registry ownership..."
    $instLoc = (Invoke-AG2RouterEnvironment 'ReadRegistry' @{ Path = $UninstallKey; Name = 'InstallLocation' }).InstallLocation
    if ($instLoc -and [System.IO.Path]::GetFullPath($instLoc).Equals($NormalizedExpected, [System.StringComparison]::OrdinalIgnoreCase)) {
        Invoke-AG2RouterEnvironment 'RemoveRegistry' @{ Path = $UninstallKey }
        Write-Host "  [OK] Add/Remove Programs registry key removed." -ForegroundColor Green
    } else {
        Write-Host "  [SKIPPED] Uninstall key InstallLocation ('$instLoc') does not match expected '$NormalizedExpected'." -ForegroundColor Yellow
    }
}

# 6. Remove Program Binaries Directory
if (Test-AG2RouterFile $ExpectedInstallDir) {
    Write-Host "Removing program binaries from $ExpectedInstallDir..."
    # Explicitly ensure we never delete a parent directory
    if ($NormalizedExpected.Equals([System.IO.Path]::GetFullPath($LocalAppData), [System.StringComparison]::OrdinalIgnoreCase) -or `
        $NormalizedExpected.Equals([System.IO.Path]::GetFullPath((Join-Path $LocalAppData "Programs")), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Catastrophic Safety Failure: Resolved path matches a parent directory root! Aborting removal."
    }

    Remove-AG2RouterFile -Path $ExpectedInstallDir -Recurse -Force
    Write-Host "  [OK] Program directory removed." -ForegroundColor Green
}

# 7. Preservation Confirmation
$UserDataDir = Join-Path $LocalAppData "AG2-Router"
Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "Uninstallation completed." -ForegroundColor Green
if (Test-AG2RouterFile $UserDataDir) {
    Write-Host "Preserved user data and accounts directory:" -ForegroundColor Green
    Write-Host "  $UserDataDir" -ForegroundColor Yellow
}
Write-Host "Preserved Windows Credential Manager target 'gemini:antigravity'." -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Cyan
