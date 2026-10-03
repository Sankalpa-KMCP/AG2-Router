# Synthetic Packaging Test Isolation Environment (R01)
#
# PURPOSE & ARCHITECTURAL INVARIANTS:
# 1. Complete Host Isolation:
#    Enforces zero-host-mutation guarantees during release packaging and uninstaller tests.
#    All file operations, registry accesses, shortcut manipulations, and process lifecycle calls
#    are intercepted and constrained to an isolated fixture directory ($Root).
# 2. Path & Reparse Point Containment:
#    Assert-SyntheticPath validates that every accessed path is strictly rooted inside $Root.
#    To prevent directory junction / symlink escapes, every ancestor along the path is checked
#    for the ReparsePoint attribute. If any junction is encountered, it fails closed.
# 3. Static AST Whitelisting (R01):
#    Before the script under test (install.ps1 or uninstall.ps1) is run, Get-SyntheticExecutableScript
#    parses its AST using [Management.Automation.Language.Parser].
#    It replaces Invoke-AG2RouterProductionEnvironment with a fail-closed stub, and asserts that
#    every command, type expression, static method, and instance method exists in strict whitelists.
#    Any unmocked host command or unexpected dynamic call causes immediate test failure.
param([string]$Root)

$ErrorActionPreference = 'Stop'
$global:AG2Synthetic = @{
    Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    Registry = @{}
    Shortcuts = @{}
    Operations = [Collections.Generic.List[string]]::new()
    FilePaths = [Collections.Generic.List[string]]::new()
    BoundaryErrors = [Collections.Generic.List[string]]::new()
    ProcessState = 'STOPPED'
    MutexState = 'STOPPED'
    ProcessStates = [Collections.Generic.Queue[string]]::new()
    MutexStates = [Collections.Generic.Queue[string]]::new()
    ShutdownCompletes = $true
    ShutdownExitCode = 0
    ExitRequests = 0
    FileVersion = $null
}

function Deny-SyntheticOperation([string]$Message) {
    [void]$global:AG2Synthetic.BoundaryErrors.Add($Message)
    throw $Message
}

function Assert-SyntheticPath([string]$Path) {
    if (-not $Path -or $Path -notmatch '^[A-Za-z]:[\\/]' -or
        [Management.Automation.WildcardPattern]::ContainsWildcardCharacters($Path)) {
        Deny-SyntheticOperation "Invalid synthetic filesystem path: '$Path'."
    }
    $full = [IO.Path]::GetFullPath($Path)
    $rootPath = $global:AG2Synthetic.Root
    if (-not $full.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -and
        -not $full.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Deny-SyntheticOperation "Path outside synthetic root: '$Path'."
    }
    # Check ancestors before opening a descendant; never follow a junction out of the fixture.
    $current = $rootPath
    $relative = $full.Substring($rootPath.Length).TrimStart('\')
    foreach ($part in @('') + @($relative.Split('\'))) {
        if ($part) { $current = [IO.Path]::Combine($current, $part) }
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Deny-SyntheticOperation "Reparse point in synthetic path: '$current'."
            }
        }
    }
    [void]$global:AG2Synthetic.FilePaths.Add($full)
}

function Assert-SyntheticTree([string]$Path) {
    Assert-SyntheticPath $Path
    if ([IO.Directory]::Exists($Path)) {
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($Path)) {
            Assert-SyntheticTree $entry
        }
    }
}

function Record-SyntheticOperation([string]$Name, [hashtable]$Parameters) {
    [void]$global:AG2Synthetic.Operations.Add($Name + ':' + $Parameters.Path)
}

$global:AG2InstallationEnvironment = @{
    BoundaryErrors = $global:AG2Synthetic.BoundaryErrors
    LocalAppData = {
        param($p)
        Record-SyntheticOperation 'LocalAppData' $p
        return [IO.Path]::Combine($global:AG2Synthetic.Root, 'LocalApp')
    }
    KnownFolder = {
        param($p)
        Record-SyntheticOperation ('KnownFolder/' + $p.Name) $p
        switch ($p.Name) {
            'Programs' { return [IO.Path]::Combine($global:AG2Synthetic.Root, 'StartMenu') }
            'DesktopDirectory' { return [IO.Path]::Combine($global:AG2Synthetic.Root, 'Desktop') }
            default { Deny-SyntheticOperation "Unmapped synthetic known folder: '$($p.Name)'." }
        }
    }
    RegistryExists = {
        param($p)
        Record-SyntheticOperation 'RegistryExists' $p
        return $global:AG2Synthetic.Registry.ContainsKey($p.Path)
    }
    ReadRegistry = {
        param($p)
        Record-SyntheticOperation 'ReadRegistry' $p
        if ($global:AG2Synthetic.Registry.ContainsKey($p.Path)) {
            return [pscustomobject]$global:AG2Synthetic.Registry[$p.Path]
        }
        return $null
    }
    CreateRegistry = {
        param($p)
        Record-SyntheticOperation 'CreateRegistry' $p
        if (-not $global:AG2Synthetic.Registry.ContainsKey($p.Path)) {
            $global:AG2Synthetic.Registry[$p.Path] = @{}
        }
    }
    SetRegistry = {
        param($p)
        Record-SyntheticOperation 'SetRegistry' $p
        if (-not $global:AG2Synthetic.Registry.ContainsKey($p.Path)) {
            Deny-SyntheticOperation 'Synthetic registry write requires an existing key.'
        }
        $global:AG2Synthetic.Registry[$p.Path][$p.Name] = $p.Value
    }
    RemoveRegistry = {
        param($p)
        Record-SyntheticOperation 'RemoveRegistry' $p
        $global:AG2Synthetic.Registry.Remove($p.Path)
    }
    RemoveRegistryValue = {
        param($p)
        Record-SyntheticOperation 'RemoveRegistryValue' $p
        if ($global:AG2Synthetic.Registry.ContainsKey($p.Path)) {
            $global:AG2Synthetic.Registry[$p.Path].Remove($p.Name)
        }
    }
    CreateShortcut = {
        param($p)
        Record-SyntheticOperation 'CreateShortcut' $p
        Assert-SyntheticPath $p.Path
        Assert-SyntheticPath $p.Target
        Assert-SyntheticPath $p.WorkingDirectory
        [IO.File]::WriteAllText($p.Path, 'synthetic shortcut')
        $global:AG2Synthetic.Shortcuts[$p.Path] = $p.Target
    }
    ShortcutTarget = {
        param($p)
        Record-SyntheticOperation 'ShortcutTarget' $p
        Assert-SyntheticPath $p.Path
        return $global:AG2Synthetic.Shortcuts[$p.Path]
    }
    ProcessState = {
        param($p)
        Record-SyntheticOperation 'ProcessState' $p
        if ($global:AG2Synthetic.ProcessStates.Count) { return $global:AG2Synthetic.ProcessStates.Dequeue() }
        return $global:AG2Synthetic.ProcessState
    }
    MutexState = {
        param($p)
        Record-SyntheticOperation 'MutexState' $p
        if ($global:AG2Synthetic.MutexStates.Count) { return $global:AG2Synthetic.MutexStates.Dequeue() }
        return $global:AG2Synthetic.MutexState
    }
    StartExitProcess = {
        param($p)
        Record-SyntheticOperation 'StartExitProcess' $p
        Assert-SyntheticPath $p.Path
        $global:AG2Synthetic.ExitRequests++
        if ($global:AG2Synthetic.ShutdownCompletes -and $global:AG2Synthetic.ShutdownExitCode -eq 0) {
            $global:AG2Synthetic.ProcessState = 'STOPPED'
            $global:AG2Synthetic.MutexState = 'STOPPED'
        }
        $result = [pscustomobject]@{ ExitCode = $global:AG2Synthetic.ShutdownExitCode }
        $result | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($milliseconds) return $global:AG2Synthetic.ShutdownCompletes }
        return $result
    }
    FileVersion = {
        param($p)
        Record-SyntheticOperation 'FileVersion' $p
        Assert-SyntheticPath $p.Path
        return $global:AG2Synthetic.FileVersion
    }
    FileSystem = {
        param($p)
        Record-SyntheticOperation ('FileSystem/' + $p.Command) $p
        $commands = @{
            'Test-Path' = 'Microsoft.PowerShell.Management\Test-Path'
            'Get-ChildItem' = 'Microsoft.PowerShell.Management\Get-ChildItem'
            'New-Item' = 'Microsoft.PowerShell.Management\New-Item'
            'Copy-Item' = 'Microsoft.PowerShell.Management\Copy-Item'
            'Move-Item' = 'Microsoft.PowerShell.Management\Move-Item'
            'Remove-Item' = 'Microsoft.PowerShell.Management\Remove-Item'
        }
        if (-not $commands.ContainsKey($p.Command)) { Deny-SyntheticOperation 'Unmapped synthetic filesystem operation.' }
        $arguments = @{}
        foreach ($key in $p.Arguments.Keys) { $arguments[$key] = $p.Arguments[$key] }
        foreach ($name in @('Path', 'LiteralPath', 'Destination')) {
            if ($arguments.ContainsKey($name)) {
                Assert-SyntheticTree $arguments[$name]
                $arguments[$name] = [IO.Path]::GetFullPath($arguments[$name])
            }
        }
        if ($arguments.ItemType -and $arguments.ItemType -ne 'Directory') {
            Deny-SyntheticOperation 'Synthetic New-Item only supports ordinary directories.'
        }
        $result = & $commands[$p.Command] @arguments
        if ($p.Command -eq 'Remove-Item') { $global:AG2Synthetic.Shortcuts.Remove($arguments.Path) }
        return $result
    }
}

function Get-SyntheticExecutableScript([string]$Path) {
    Assert-SyntheticPath $Path
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { Deny-SyntheticOperation ('Installer parse error: ' + $errors[0].Message) }
    $production = @($ast.FindAll({ param($n)
        $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-AG2RouterProductionEnvironment'
    }, $true))
    if ($production.Count -ne 1) { Deny-SyntheticOperation 'Expected exactly one production installation adapter.' }

    # Only the unreachable production adapter is replaced. Its native operations are never loaded
    # into the child runspace, even if a future change accidentally selects production fallback.
    $text = [IO.File]::ReadAllText($Path)
    $text = $text.Remove($production[0].Extent.StartOffset, $production[0].Extent.EndOffset - $production[0].Extent.StartOffset).Insert(
        $production[0].Extent.StartOffset,
        "function Invoke-AG2RouterProductionEnvironment { Deny-SyntheticOperation 'Production adapter reached in synthetic execution.' }")
    $ast = [Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
    $allowedCommands = @(
        'Invoke-AG2RouterEnvironment', 'Invoke-AG2RouterProductionEnvironment', 'Deny-SyntheticOperation',
        'Test-AG2RouterFile', 'Get-AG2RouterFiles', 'New-AG2RouterDirectory', 'Copy-AG2RouterFile',
        'Move-AG2RouterFile', 'Remove-AG2RouterFile', 'Get-AG2RouterRegistryValue',
        'Test-AG2RouterInnoRegistrationMatch', 'Test-AG2RouterPowerShellRegistrationMatch',
        'Get-AG2RouterInstallationOwnership', 'Get-AG2RouterProcessState', 'Get-AG2RouterMutexState',
        'Invoke-AG2RouterExitRequest', 'Assert-AG2RouterStopped', 'Get-Command', 'Join-Path',
        'Split-Path', 'Write-Host', 'Write-Warning', 'Out-Null', 'ForEach-Object', 'Start-Sleep')
    $allowedTypes = @('string', 'hashtable', 'switch', 'void', 'scriptblock', 'PSCustomObject', 'System.IO.Path',
        'System.StringComparison', 'System.Diagnostics.Stopwatch')
    $allowedMethods = @('ContainsKey', 'Add', 'ToString', 'Trim', 'TrimEnd', 'ToLowerInvariant',
        'Equals', 'StartsWith', 'EndsWith', 'Contains', 'WaitForExit')
    foreach ($node in $ast.FindAll({ param($n) $true }, $true)) {
        if ($node -is [Management.Automation.Language.CommandAst]) {
            $name = $node.GetCommandName()
            if (-not $name) {
                $parent = $node.Parent
                while ($parent -and $parent -isnot [Management.Automation.Language.FunctionDefinitionAst]) { $parent = $parent.Parent }
                if (-not $parent -or $parent.Name -ne 'Invoke-AG2RouterEnvironment' -or
                    $node.CommandElements[0].Extent.Text -ne '$InstallationEnvironment[$Operation]') {
                    Deny-SyntheticOperation 'Unapproved dynamic invocation in installation script.'
                }
            } elseif ($name -notin $allowedCommands) {
                Deny-SyntheticOperation "Unmocked host command in installation script: '$name'."
            }
        }
        if ($node -is [Management.Automation.Language.TypeExpressionAst] -or $node -is [Management.Automation.Language.TypeConstraintAst]) {
            if ($node.TypeName.FullName -notin $allowedTypes) {
                Deny-SyntheticOperation "Unapproved type in installation script: '$($node.TypeName.FullName)'."
            }
        }
        if ($node -is [Management.Automation.Language.InvokeMemberExpressionAst]) {
            if ($node.Static) {
                $call = $node.Expression.Extent.Text + '::' + $node.Member.Extent.Text
                if ($call -notin @('[System.IO.Path]::GetFullPath', '[System.IO.Path]::GetDirectoryName', '[System.Diagnostics.Stopwatch]::StartNew')) {
                    Deny-SyntheticOperation "Unmocked static call in installation script: '$call'."
                }
            } elseif ($node.Member.Extent.Text -notin $allowedMethods) {
                Deny-SyntheticOperation "Unmocked method in installation script: '$($node.Member.Extent.Text)'."
            }
        }
        if ($node -is [Management.Automation.Language.VariableExpressionAst] -and $node.VariablePath.IsDriveQualified -and
            $node.VariablePath.DriveName -notin @('script', 'local', 'private')) {
            Deny-SyntheticOperation "Unapproved variable namespace in installation script: '$($node.VariablePath)'."
        }
        if ($node -is [Management.Automation.Language.FileRedirectionAst]) {
            Deny-SyntheticOperation 'Unmocked file redirection in installation script.'
        }
    }
    $destination = [IO.Path]::Combine($global:AG2Synthetic.Root, 'Scripts', 'synthetic-' + [IO.Path]::GetFileName($Path))
    Assert-SyntheticPath $destination
    [IO.File]::WriteAllText($destination, $text)
    return $destination
}

function Invoke-SyntheticInstallation([string]$Kind, [hashtable]$ExtraParameters = @{}) {
    $scriptPath = Get-SyntheticExecutableScript ([IO.Path]::Combine($global:AG2Synthetic.Root, 'Scripts', $Kind + '.ps1'))
    $parameters = @{ InstallationEnvironment = $global:AG2InstallationEnvironment }
    if ($Kind -eq 'install') { $parameters.SourceDir = [IO.Path]::Combine($global:AG2Synthetic.Root, 'SourcePayload') }
    foreach ($key in $ExtraParameters.Keys) {
        if ($key -eq 'InstallationEnvironment') { Deny-SyntheticOperation 'Cannot replace the synthetic environment.' }
        $parameters[$key] = $ExtraParameters[$key]
    }
    & $scriptPath @parameters
    if ($global:AG2Synthetic.BoundaryErrors.Count) { throw 'Synthetic boundary violation was caught by the installer.' }
}
