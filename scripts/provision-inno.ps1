# AG2 Router - pinned Inno Setup provisioning and verification
[CmdletBinding()]
param(
    [string]$PinnedVersion = "6.4.0",
    [string]$InstallDirectory = "C:\Program Files (x86)\Inno Setup 6",
    [string]$ChocolateyCommand = "choco"
)

$ErrorActionPreference = "Stop"
$IsccPath = Join-Path $InstallDirectory "ISCC.exe"

function Get-InstalledInnoVersion {
    $packageOutput = @(& $ChocolateyCommand list --exact innosetup --limit-output)
    if ($LASTEXITCODE -ne 0) {
        throw "Chocolatey failed to query the installed Inno Setup package (exit code $LASTEXITCODE)."
    }

    $packageLines = @($packageOutput | Where-Object { $_ -match '^(?i:innosetup)\|' })
    if ($packageLines.Count -eq 0) {
        return $null
    }
    if ($packageLines.Count -ne 1 -or $packageLines[0] -notmatch '^(?i:innosetup)\|(?<Version>[^|]+)$') {
        throw "Chocolatey returned ambiguous Inno Setup package evidence: $($packageLines -join ', ')"
    }

    return $Matches.Version
}

# Inno Setup 6.4.0 does not support the newer --version option. A no-output
# smoke compilation proves that this exact executable is runnable and reports
# its compiler engine version without creating an installer.
function Test-InnoCompiler {
    if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
        return [pscustomobject]@{ Verified = $false; Details = "compiler path is missing" }
    }
    if ((Get-Item -LiteralPath $IsccPath).Length -eq 0) {
        return [pscustomobject]@{ Verified = $false; Details = "compiler file is empty" }
    }

    $probeDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("AG2Router_InnoProbe_" + [System.Guid]::NewGuid().ToString("N"))
    $probeScript = Join-Path $probeDirectory "compiler-probe.iss"
    try {
        New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
        [System.IO.File]::WriteAllLines($probeScript, @(
            "[Setup]",
            "AppName=AG2 Router Compiler Probe",
            "AppVersion=1.0.0",
            "DefaultDirName={tmp}\AG2RouterCompilerProbe",
            "PrivilegesRequired=lowest",
            "Uninstallable=no"
        ))

        $compilerOutput = @(& $IsccPath /O- $probeScript 2>&1 | ForEach-Object { $_.ToString() })
        $compilerExitCode = $LASTEXITCODE
        $expectedEvidence = "Compiler engine version: Inno Setup $PinnedVersion"
        if ($compilerExitCode -ne 0) {
            return [pscustomobject]@{ Verified = $false; Details = "exit code $compilerExitCode; output: $($compilerOutput -join ' | ')" }
        }
        if ($compilerOutput -notcontains $expectedEvidence) {
            return [pscustomobject]@{ Verified = $false; Details = "missing '$expectedEvidence'; output: $($compilerOutput -join ' | ')" }
        }

        return [pscustomobject]@{ Verified = $true; Details = $expectedEvidence }
    } catch {
        return [pscustomobject]@{ Verified = $false; Details = $_.Exception.Message }
    } finally {
        if (Test-Path -LiteralPath $probeDirectory) {
            Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$installedVersion = Get-InstalledInnoVersion
$initialCompilerProbe = Test-InnoCompiler
$needsProvisioning = ($installedVersion -ne $PinnedVersion) -or -not $initialCompilerProbe.Verified

if ($needsProvisioning) {
    $state = if ($installedVersion) { "package version $installedVersion" } else { "no installed package" }
    Write-Host "Provisioning pinned Inno Setup $PinnedVersion (found $state; compiler evidence: $($initialCompilerProbe.Details))."

    & $ChocolateyCommand upgrade innosetup "--version=$PinnedVersion" --allow-downgrade --force --install-if-not-installed -y --no-progress --limit-output
    $chocolateyExitCode = $LASTEXITCODE
    if ($chocolateyExitCode -notin @(0, 1641, 3010)) {
        throw "Chocolatey failed to provision Inno Setup $PinnedVersion (exit code $chocolateyExitCode)."
    }
} else {
    Write-Host "Reusing Chocolatey Inno Setup package $PinnedVersion at $IsccPath."
}

$verifiedPackageVersion = Get-InstalledInnoVersion
if ($verifiedPackageVersion -ne $PinnedVersion) {
    throw "Inno Setup package version mismatch after provisioning. Expected $PinnedVersion, found '$verifiedPackageVersion'."
}
if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf) -or (Get-Item -LiteralPath $IsccPath).Length -eq 0) {
    throw "Pinned Inno Setup compiler was not found at the expected path: $IsccPath"
}

$finalCompilerProbe = if ($needsProvisioning) { Test-InnoCompiler } else { $initialCompilerProbe }
if (-not $finalCompilerProbe.Verified) {
    throw "Pinned ISCC compiler verification failed after provisioning: $($finalCompilerProbe.Details)"
}

Write-Host "Verified Chocolatey package innosetup|$verifiedPackageVersion and compiler engine at $IsccPath."
if ($env:GITHUB_OUTPUT) {
    "iscc_path=$IsccPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}
