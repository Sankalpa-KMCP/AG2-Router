# AG2 Router - Release Packaging Script
# Produces self-contained win-x64 release package and checksum manifest
param(
    [string]$Configuration = "Release",
    [string]$Version = "",
    [switch]$RequireInstaller,
    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

# Derive canonical version from dotnet/Directory.Build.props if not explicitly specified
if (-not $Version) {
    $PropsPath = Join-Path $RepoRoot "dotnet\Directory.Build.props"
    if (Test-Path $PropsPath) {
        [xml]$propsXml = Get-Content $PropsPath
        $Version = $propsXml.Project.PropertyGroup.Version
    }
    if (-not $Version) {
        $Version = "0.3.1"
    }
}
$PublishDir = Join-Path $RepoRoot "publish\win-x64"
$DistDir = Join-Path $RepoRoot "dist"
$ProjectFile = Join-Path $RepoRoot "dotnet\src\AG2Router.App\AG2Router.App.csproj"
$ZipFileName = "AG2Router-v$Version-win-x64.zip"
$ZipFilePath = Join-Path $DistDir $ZipFileName
$InstallerExe = "AG2Router-Setup-v$Version-win-x64.exe"
$InstallerPath = Join-Path $DistDir $InstallerExe
$SumsFile = Join-Path $DistDir "SHA256SUMS.txt"

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "AG2 Router Release Packaging: Version $Version ($Configuration)" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# Frontend output is generated source for the published wwwroot payload. Always
# regenerate it before publishing, including standalone packaging invocations.
Write-Host "Building dashboard assets for the release payload..."
Push-Location $RepoRoot
try {
    & npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "npm run build failed with exit code $LASTEXITCODE"
    }
} finally {
    Pop-Location
}

# 1. Clean previous publish and staging outputs
if (Test-Path $PublishDir) {
    Write-Host "[1/6] Cleaning prior publish directory: $PublishDir"
    Remove-Item -Recurse -Force $PublishDir
}
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
}
foreach ($priorOutput in @($ZipFilePath, $InstallerPath, $SumsFile)) {
    if (Test-Path -LiteralPath $priorOutput) {
        Remove-Item -LiteralPath $priorOutput -Force
    }
}

# 2. Publish self-contained win-x64 application
Write-Host "[2/6] Publishing self-contained win-x64 application..."
& dotnet publish $ProjectFile `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# 3. Strip / remove .pdb files from the release publish payload
Write-Host "[3/6] Stripping debug symbols (*.pdb) from release payload..."
Get-ChildItem -Path $PublishDir -Filter "*.pdb" -Recurse | ForEach-Object {
    Remove-Item -Force $_.FullName
}

# Copy standalone install/uninstall helper scripts into publish folder
$InstallScript = Join-Path $RepoRoot "scripts\install.ps1"
$UninstallScript = Join-Path $RepoRoot "scripts\uninstall.ps1"
if (Test-Path $InstallScript) {
    Copy-Item -Path $InstallScript -Destination (Join-Path $PublishDir "install.ps1") -Force
}
if (Test-Path $UninstallScript) {
    Copy-Item -Path $UninstallScript -Destination (Join-Path $PublishDir "uninstall.ps1") -Force
}

# 4. Verify publish directory completeness
Write-Host "[4/6] Validating staged publish payload..."
$RequiredFiles = @(
    "AG2Router.exe",
    "AG2Router.dll",
    "AG2Router.Core.dll",
    "AG2Router.AG2.dll",
    "AG2Router.Windows.dll",
    "WebView2Loader.dll",
    "Microsoft.Web.WebView2.Core.dll",
    "wwwroot\index.html",
    "wwwroot\styles.css",
    "wwwroot\app.js"
)

foreach ($relPath in $RequiredFiles) {
    $fullPath = Join-Path $PublishDir $relPath
    if (-not (Test-Path $fullPath)) {
        throw "Missing required release asset: $relPath at $fullPath"
    }
}
Write-Host "  [PASS] All essential binaries and wwwroot UI assets verified." -ForegroundColor Green

# 5. Package ZIP archive
Write-Host "[5/6] Creating release archive: $ZipFileName..."
# Use Compress-Archive with deterministic sorting
$FilesToZip = Get-ChildItem -Path $PublishDir -Recurse | Sort-Object FullName
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipFilePath -CompressionLevel Optimal

$ZipHash = (Get-FileHash -Path $ZipFilePath -Algorithm SHA256).Hash
Write-Host "  [OK] SHA256 ($ZipFileName): $ZipHash" -ForegroundColor Yellow

$SumsContent = "$ZipHash  $ZipFileName`r`n"

# 6. Check for Inno Setup compiler (ISCC)
Write-Host "[6/6] Checking for Inno Setup Compiler (ISCC)..."
$IssScript = Join-Path $RepoRoot "installer\AG2Router.iss"
$ResolvedIsccPath = $null

if ($IsccPath) {
    if (Test-Path -LiteralPath $IsccPath -PathType Leaf) {
        $ResolvedIsccPath = (Resolve-Path -LiteralPath $IsccPath).Path
    } elseif ($RequireInstaller) {
        throw "Installer compilation is required, but the specified ISCC path does not exist: $IsccPath"
    } else {
        Write-Warning "Specified ISCC path does not exist: $IsccPath"
    }
} else {
    $IsccCmd = Get-Command iscc -CommandType Application -ErrorAction SilentlyContinue
    if ($IsccCmd) {
        $ResolvedIsccPath = $IsccCmd.Source
    }
}

if ($ResolvedIsccPath -and (Test-Path -LiteralPath $IssScript -PathType Leaf)) {
    Write-Host "  ISCC found at: $ResolvedIsccPath. Compiling installer..."
    & $ResolvedIsccPath /DAppVersion=$Version /DSourceDir="$PublishDir" /DOutputDir="$DistDir" $IssScript
    $isccExitCode = $LASTEXITCODE
    if ($isccExitCode -eq 0) {
        if (Test-Path -LiteralPath $InstallerPath -PathType Leaf) {
            $InstallerHash = (Get-FileHash -Path $InstallerPath -Algorithm SHA256).Hash
            $SumsContent += "$InstallerHash  $InstallerExe`r`n"
            Write-Host "  [OK] Compiled installer: $InstallerExe (SHA256: $InstallerHash)" -ForegroundColor Green
        } elseif ($RequireInstaller) {
            throw "ISCC reported success, but the required installer was not produced: $InstallerPath"
        } else {
            Write-Warning "ISCC reported success, but no installer was produced at: $InstallerPath"
        }
    } else {
        if ($RequireInstaller) {
            throw "Required ISCC compilation failed with exit code $isccExitCode"
        }
        Write-Warning "ISCC compilation returned exit code $isccExitCode"
    }
} else {
    $reason = if (-not $ResolvedIsccPath) { "ISCC is unavailable" } else { "Inno Setup script is missing: $IssScript" }
    if ($RequireInstaller) {
        throw "Installer compilation is required, but $reason."
    }
    Write-Host "  $reason. Release package is ZIP + PowerShell install/uninstall scripts." -ForegroundColor Yellow
}

[System.IO.File]::WriteAllText($SumsFile, $SumsContent, [System.Text.Encoding]::UTF8)
Write-Host "Generated checksum manifest: $SumsFile" -ForegroundColor Green

# 7. Post-Packaging Artifact Introspection & Validation
Write-Host "Validating produced ZIP archive integrity..."
$ExtractTestDir = Join-Path ([System.IO.Path]::GetTempPath()) ("AG2Router_Verify_" + [System.Guid]::NewGuid().ToString("N"))
try {
    Expand-Archive -Path $ZipFilePath -DestinationPath $ExtractTestDir -Force

    # Assert required files in extracted ZIP
    foreach ($relPath in $RequiredFiles) {
        $checkPath = Join-Path $ExtractTestDir $relPath
        if (-not (Test-Path $checkPath)) {
            throw "Artifact introspection error: $relPath missing from extracted ZIP payload!"
        }
    }

    # Assert NO .pdb files
    $pdbMatches = Get-ChildItem -Path $ExtractTestDir -Filter "*.pdb" -Recurse
    if ($pdbMatches.Count -gt 0) {
        throw "Artifact introspection error: Release payload contains debug symbol files (*.pdb)!"
    }

    # Assert NO test assemblies or fixtures
    $forbiddenNames = @("*Tests.dll", "*xunit*", "accounts-v1.json", "*.log")
    foreach ($pat in $forbiddenNames) {
        $leaks = Get-ChildItem -Path $ExtractTestDir -Filter $pat -Recurse
        if ($leaks.Count -gt 0) {
            throw "Artifact introspection error: Forbidden pattern '$pat' found in release payload: $($leaks[0].FullName)"
        }
    }

    # Secret and local path scanning on non-binary assets (text files, js, html, css)
    $textFiles = Get-ChildItem -Path $ExtractTestDir -Include "*.json", "*.html", "*.css", "*.js", "*.txt", "*.ps1" -Recurse
    foreach ($tf in $textFiles) {
        $content = [System.IO.File]::ReadAllText($tf.FullName)
        if ($content -match "11111111-2222-3333-4444-555555555555") {
            throw "Artifact introspection error: Test token leak detected in $($tf.FullName)"
        }
        if ($content -match "x-codeium-csrf-token") {
            throw "Artifact introspection error: CSRF token header leak in $($tf.FullName)"
        }
    }

    Write-Host "  [PASS] Artifact introspection passed all safety, layout, and secret hygiene checks." -ForegroundColor Green
} finally {
    if (Test-Path $ExtractTestDir) {
        Remove-Item -Recurse -Force $ExtractTestDir -ErrorAction SilentlyContinue
    }
}

$RequiredOutputs = @($ZipFilePath, $SumsFile)
if ($RequireInstaller) {
    $RequiredOutputs += $InstallerPath
}
foreach ($requiredOutput in $RequiredOutputs) {
    if (-not (Test-Path -LiteralPath $requiredOutput -PathType Leaf) -or (Get-Item -LiteralPath $requiredOutput).Length -eq 0) {
        throw "Required packaging output is missing: $requiredOutput"
    }
}

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "Release packaging completed successfully." -ForegroundColor Green
Write-Host "Distribution Directory: $DistDir" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan
