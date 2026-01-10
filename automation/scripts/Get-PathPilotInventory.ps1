[CmdletBinding(DefaultParameterSetName = 'Inventory')]
param(
    [Parameter(Mandatory = $false, ParameterSetName = 'Inventory')]
    [Parameter(Mandatory = $false, ParameterSetName = 'Switch')]
    [string] $ConfigPath,

    [Parameter(Mandatory = $false, ParameterSetName = 'Inventory')]
    [ValidateSet('json', 'markdown')]
    [string] $Export = 'json',

    [Parameter(Mandatory = $false, ParameterSetName = 'Switch')]
    [string[]] $Switch,

    [Parameter(Mandatory = $false, ParameterSetName = 'Switch')]
    [string] $SwitchRuntimeId,

    [Parameter(Mandatory = $false, ParameterSetName = 'Switch')]
    [string] $SwitchInstallPath,

    [Parameter(Mandatory = $false, ParameterSetName = 'Inventory')]
    [Parameter(Mandatory = $false, ParameterSetName = 'Switch')]
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { (Get-Location).Path }
$modulePath = Join-Path -Path $scriptRoot -ChildPath '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

function Resolve-ConfigPath {
    param(
        [string] $ConfigPathValue,
        [string] $ScriptRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($ConfigPathValue)) {
        $expanded = Resolve-InventoryPath -Value $ConfigPathValue
        if (-not (Test-Path -LiteralPath $expanded)) {
            throw "Configuration file '$ConfigPathValue' was not found."
        }

        return $expanded
    }

    $defaultPath = Join-Path -Path $ScriptRoot -ChildPath '..\..\data\catalog\runtime-inventory.json'
    $defaultPath = [System.IO.Path]::GetFullPath($defaultPath)
    if (-not (Test-Path -LiteralPath $defaultPath)) {
        throw "Default runtime inventory configuration not found at '$defaultPath'."
    }

    return $defaultPath
}

function Get-RuntimeConfigEntries {
    param([string] $RootPath)

    if ([string]::IsNullOrWhiteSpace($RootPath)) {
        return @()
    }

    $visited = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    return @(Read-RuntimeConfigDocument -TargetPath $RootPath -Visited $visited)
}

function Read-RuntimeConfigDocument {
    param(
        [string] $TargetPath,
        [System.Collections.Generic.HashSet[string]] $Visited
    )

    if ([string]::IsNullOrWhiteSpace($TargetPath)) {
        return @()
    }

    $item = Get-Item -LiteralPath $TargetPath -ErrorAction Stop

    if ($item.PSIsContainer) {
        $results = New-Object 'System.Collections.Generic.List[psobject]'
        $files = Get-ChildItem -Path $item.FullName -Filter '*.json' -File -ErrorAction SilentlyContinue | Sort-Object FullName
        foreach ($file in $files) {
            foreach ($runtime in Read-RuntimeConfigDocument -TargetPath $file.FullName -Visited $Visited) {
                $results.Add($runtime) | Out-Null
            }
        }

        return $results.ToArray()
    }

    $fullPath = $item.FullName
    if ($Visited.Contains($fullPath)) {
        return @()
    }

    $Visited.Add($fullPath) | Out-Null

    $configText = Get-Content -LiteralPath $fullPath -Raw -ErrorAction Stop
    $document = ConvertFrom-Json -InputObject $configText -ErrorAction Stop

    $entries = New-Object 'System.Collections.Generic.List[psobject]'
    if ($document.PSObject.Properties['runtimes']) {
        foreach ($runtime in @($document.runtimes)) {
            if ($runtime) {
                $entries.Add($runtime) | Out-Null
            }
        }
    }

    if ($document.PSObject.Properties['includes']) {
        foreach ($include in @($document.includes)) {
            if ([string]::IsNullOrWhiteSpace($include)) { continue }
            $includePath = Join-Path -Path $item.DirectoryName -ChildPath $include
            foreach ($runtime in Read-RuntimeConfigDocument -TargetPath $includePath -Visited $Visited) {
                $entries.Add($runtime) | Out-Null
            }
        }
    }

    return $entries.ToArray()
}

function Resolve-InventoryPath {
    param(
        [string] $Value,
        [switch] $AllowWildcards
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $trimmed = $Value.Trim()
    $unquoted = $AllowWildcards ? $trimmed : $trimmed.Trim('"')
    if ([string]::IsNullOrWhiteSpace($unquoted)) {
        return $null
    }

    $expanded = [System.Environment]::ExpandEnvironmentVariables($unquoted)
    if ($expanded.StartsWith('~')) {
        $home = $env:USERPROFILE
        if (-not [string]::IsNullOrWhiteSpace($home)) {
            $expanded = $home + $expanded.Substring(1)
        }
    }

    if ($AllowWildcards) {
        return $expanded
    }

    try {
        return [System.IO.Path]::GetFullPath($expanded)
    }
    catch {
        return $expanded
    }
}

function Normalize-InventoryPath {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $resolved = Resolve-InventoryPath -Value $Value
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return $null
    }

    try {
        $normalized = [System.IO.Path]::GetFullPath($resolved)
    }
    catch {
        $normalized = $resolved
    }

    $normalized = $normalized -replace '[\\/]+$', ''
    return $normalized.ToUpperInvariant()
}

function Get-DiscoveryPropertyValues {
    param(
        [psobject] $Discovery,
        [string] $PropertyName
    )

    if (-not $Discovery -or [string]::IsNullOrWhiteSpace($PropertyName)) {
        return @()
    }

    $property = $Discovery.PSObject.Properties[$PropertyName]
    if (-not $property) {
        return @()
    }

    $value = $property.Value
    if ($null -eq $value) {
        return @()
    }

    if ($value -is [string]) {
        return @($value)
    }

    if ($value -is [System.Collections.IEnumerable]) {
        return @($value)
    }

    return @($value)
}

function Get-PathPilotDataDirectory {
    $programData = $env:ProgramData
    if ([string]::IsNullOrWhiteSpace($programData)) {
        $programData = 'C:\\ProgramData'
    }

    $target = Join-Path -Path $programData -ChildPath 'TidyWindow\PathPilot'
    if (-not (Test-Path -LiteralPath $target)) {
        [void](New-Item -Path $target -ItemType Directory -Force)
    }

    return $target
}

function Backup-MachinePath {
    param([string] $CurrentPathValue)

    $dataDirectory = Get-PathPilotDataDirectory
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupPath = Join-Path -Path $dataDirectory -ChildPath "backup-$timestamp.reg"
    $registryKey = 'HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment'

    try {
        & reg.exe export $registryKey $backupPath /y *> $null
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $backupPath)) {
            return $backupPath
        }
    }
    catch {
        # fall through to manual backup
    }

    try {
        $escaped = ($CurrentPathValue ?? '').Replace('\\', '\\\\').Replace('"', '\\"')
        $contentLines = @(
            'Windows Registry Editor Version 5.00',
            '',
            "[$registryKey]",
            "`"Path`"=`"$escaped`""
        )
        $content = $contentLines -join [Environment]::NewLine
        $content | Out-File -FilePath $backupPath -Encoding Unicode -Force
        return $backupPath
    }
    catch {
        return $null
    }
}

function Set-MachinePathValue {
    param([string] $NewValue)

    if ($null -eq $NewValue) {
        throw 'Machine PATH value cannot be null.'
    }

    $envKey = 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment'
    Set-ItemProperty -Path $envKey -Name 'Path' -Value $NewValue -ErrorAction Stop | Out-Null
    [System.Environment]::SetEnvironmentVariable('Path', $NewValue, 'Machine')
}

function Get-MachinePathEntries {
    param([string] $PathValue)

    $entries = New-Object 'System.Collections.Generic.List[psobject]'
    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $entries
    }

    $parts = $PathValue -split ';'
    $index = 0
    foreach ($entry in $parts) {
        if ($null -eq $entry) {
            $index++
            continue
        }

        $value = $entry.Trim()
        if ([string]::IsNullOrWhiteSpace($value)) {
            $index++
            continue
        }

        $resolved = Resolve-InventoryPath -Value $value
        $entries.Add([pscustomobject]@{
                index    = $index
                value    = $value
                resolved = $resolved
            }) | Out-Null
        $index++
    }

    return $entries
}

function Build-RuntimeSnapshots {
    param(
        [psobject[]] $RuntimeConfig,
        [psobject[]] $MachinePathEntries,
        [System.Collections.Generic.List[string]] $Warnings
    )

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    foreach ($runtime in @($RuntimeConfig)) {
        $snapshot = Get-RuntimeInventory -Runtime $runtime -MachinePathEntries $MachinePathEntries -Warnings $Warnings
        if ($snapshot) {
            $results.Add($snapshot) | Out-Null
        }
    }

    return $results
}

function Add-RuntimeCandidate {
    param(
        [System.Collections.Generic.Dictionary[string, psobject]] $Lookup,
        [string] $ExecutablePath,
        [string] $Source,
        [string] $Note
    )

    if (-not $Lookup -or [string]::IsNullOrWhiteSpace($ExecutablePath)) {
        return
    }

    $resolved = Resolve-InventoryPath -Value $ExecutablePath
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return
    }

    if ($resolved.IndexOf('%') -ge 0) {
        return
    }

    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        return
    }

    if (-not $Lookup.ContainsKey($resolved)) {
        $Lookup[$resolved] = [pscustomobject]@{
            path    = $resolved
            sources = New-Object 'System.Collections.Generic.List[string]'
            notes   = New-Object 'System.Collections.Generic.List[string]'
        }
    }

    $entry = $Lookup[$resolved]

    if (-not [string]::IsNullOrWhiteSpace($Source)) {
        $exists = $false
        foreach ($existing in @($entry.sources)) {
            if ([string]::Equals($existing, $Source, [System.StringComparison]::OrdinalIgnoreCase)) {
                $exists = $true
                break
            }
        }

        if (-not $exists) {
            [void]$entry.sources.Add($Source)
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Note)) {
        $noteExists = $false
        foreach ($existingNote in @($entry.notes)) {
            if ([string]::Equals($existingNote, $Note, [System.StringComparison]::OrdinalIgnoreCase)) {
                $noteExists = $true
                break
            }
        }

        if (-not $noteExists) {
            [void]$entry.notes.Add($Note)
        }
    }
}

function Get-RuntimeVersion {
    param(
        [string] $ExecutablePath,
        [string[]] $VersionArguments,
        [string] $RuntimeId,
        [System.Collections.Generic.List[string]] $Warnings
    )

    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
        return $null
    }

    $arguments = @()
    foreach ($arg in @($VersionArguments)) {
        if (-not [string]::IsNullOrWhiteSpace($arg)) {
            $arguments += $arg.Trim()
        }
    }

    if ($arguments.Count -eq 0) {
        $arguments = @('--version')
    }

    try {
        $output = & $ExecutablePath @arguments 2>&1
        if ($output) {
            $version = Select-TidyBestVersion -Values $output
            if (-not [string]::IsNullOrWhiteSpace($version)) {
                return $version.Trim()
            }
        }
    }
    catch {
        if ($Warnings) {
            $message = $_.Exception.Message
            $Warnings.Add("[$RuntimeId] Failed to query version from '$ExecutablePath': $message") | Out-Null
        }
    }

    try {
        $item = Get-Item -LiteralPath $ExecutablePath -ErrorAction Stop
        if ($item.VersionInfo) {
            $version = Select-TidyBestVersion -Values @($item.VersionInfo.ProductVersion, $item.VersionInfo.FileVersion)
            if (-not [string]::IsNullOrWhiteSpace($version)) {
                return $version.Trim()
            }
        }
    }
    catch {
        # Ignore file version failures.
    }

    return $null
}

function Get-ArchitectureHeuristic {
    param([string] $ExecutablePath)

    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
        return 'unknown'
    }

    if ($ExecutablePath -match '(?i)Program Files \(x86\)' -or $ExecutablePath -match '(?i)\\x86\\') {
        return 'x86'
    }

    if ($ExecutablePath -match '(?i)\\arm64\\') {
        return 'arm64'
    }

    if ($ExecutablePath -match '(?i)\\arm\\') {
        return 'arm'
    }

    return 'x64'
}

function Resolve-CommandPath {
    param([System.Management.Automation.CommandInfo] $Command)

    if (-not $Command) {
        return $null
    }

    if (-not [string]::IsNullOrWhiteSpace($Command.Source)) {
        return $Command.Source
    }

    if (-not [string]::IsNullOrWhiteSpace($Command.Definition)) {
        return $Command.Definition
    }

    if (-not [string]::IsNullOrWhiteSpace($Command.Path)) {
        return $Command.Path
    }

    return $Command.Name
}

function Get-SafePathEntries {
    param([string] $PathValue)

    $results = New-Object 'System.Collections.Generic.List[string]'
    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $results
    }

    foreach ($part in $PathValue -split ';') {
        if ($null -eq $part) { continue }
        $value = $part.Trim()
        if ([string]::IsNullOrWhiteSpace($value)) { continue }

        if ($value.StartsWith('\\')) { continue }

        $resolved = Resolve-InventoryPath -Value $value
        if ([string]::IsNullOrWhiteSpace($resolved)) { continue }

        try {
            $root = [System.IO.Path]::GetPathRoot($resolved)
            if ([string]::IsNullOrWhiteSpace($root)) { continue }

            $drive = New-Object -TypeName System.IO.DriveInfo -ArgumentList $root
            if ($drive.DriveType -eq [System.IO.DriveType]::Network -or -not $drive.IsReady) {
                continue
            }
        }
        catch {
            continue
        }

        $results.Add($value) | Out-Null
    }

    return $results
}

function Resolve-ActiveExecutable {
    param(
        [string] $ExecutableName,
        [string[]] $WhereHints,
        [System.Collections.Generic.List[string]] $Warnings
    )

    $candidates = New-Object 'System.Collections.Generic.List[string]'
    $sources = New-Object 'System.Collections.Generic.Dictionary[string, string]' ([System.StringComparer]::OrdinalIgnoreCase)

    $originalPath = $env:Path
    $originalPathEntries = @($originalPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $safePathEntries = Get-SafePathEntries -PathValue $originalPath
    $sanitizedPath = if ($safePathEntries.Count -gt 0) { ($safePathEntries | Select-Object -Unique) -join ';' } else { $null }
    if ($sanitizedPath) {
        $env:Path = $sanitizedPath
    }

    try {
        if (-not [string]::IsNullOrWhiteSpace($ExecutableName)) {
            $command = Get-Command -Name $ExecutableName -ErrorAction SilentlyContinue
            if ($command) {
                $cmdPath = Resolve-CommandPath -Command $command
                if (-not [string]::IsNullOrWhiteSpace($cmdPath)) {
                    $resolved = Resolve-InventoryPath -Value $cmdPath
                    if (-not [string]::IsNullOrWhiteSpace($resolved) -and (Test-Path -LiteralPath $resolved -PathType Leaf)) {
                        [void]$candidates.Add($resolved)
                        $sources[$resolved] = 'CommandLookup'
                    }
                }
            }
        }

        $whereExe = Get-Command -Name 'where.exe' -ErrorAction SilentlyContinue
        if ($whereExe) {
            $targets = @()
            foreach ($hint in @($WhereHints)) {
                if (-not [string]::IsNullOrWhiteSpace($hint)) {
                    $targets += $hint.Trim()
                }
            }

            if ($targets.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($ExecutableName)) {
                $targets = @($ExecutableName)
            }

            foreach ($target in $targets) {
                try {
                    $lines = & $whereExe.Source $target 2>$null
                }
                catch {
                    $lines = @()
                }

                foreach ($line in @($lines)) {
                    if ([string]::IsNullOrWhiteSpace($line)) {
                        continue
                    }

                    $clean = $line.Trim()
                    $resolved = Resolve-InventoryPath -Value $clean
                    if ([string]::IsNullOrWhiteSpace($resolved)) {
                        continue
                    }

                    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
                        continue
                    }

                    if (-not $sources.ContainsKey($resolved)) {
                        $sources[$resolved] = "where:$target"
                        [void]$candidates.Add($resolved)
                    }
                }
            }
        }
    }
    finally {
        $env:Path = $originalPath

        if ($Warnings -and $safePathEntries.Count -lt $originalPathEntries.Count) {
            $warningMessage = 'Skipped network or unavailable PATH entries while resolving active executable to avoid hangs.'
            if (-not $Warnings.Contains($warningMessage)) {
                $Warnings.Add($warningMessage) | Out-Null
            }
        }
    }

    $primary = if ($candidates.Count -gt 0) { $candidates[0] } else { $null }
    $primarySource = if ($primary -and $sources.ContainsKey($primary)) { $sources[$primary] } else { $null }

    return [pscustomobject]@{
        Path       = $primary
        Candidates = $candidates
        Source     = $primarySource
    }
}

function Resolve-PathEntry {
    param(
        [psobject[]] $Entries,
        [string] $ActivePath
    )

    if (-not $Entries -or [string]::IsNullOrWhiteSpace($ActivePath)) {
        return $null
    }

    foreach ($entry in $Entries) {
        if (-not $entry) { continue }
        $resolved = if (-not [string]::IsNullOrWhiteSpace($entry.resolved)) { $entry.resolved } elseif (-not [string]::IsNullOrWhiteSpace($entry.value)) { Resolve-InventoryPath -Value $entry.value } else { $null }
        if ([string]::IsNullOrWhiteSpace($resolved)) { continue }

        if ($ActivePath.StartsWith($resolved, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $entry.value
        }
    }

    return $null
}

function Get-RegistryProbePaths {
    param(
        [psobject] $Probe,
        [string] $ExecutableName
    )

    $results = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if (-not $Probe -or [string]::IsNullOrWhiteSpace($Probe.key)) {
        return , @()
    }

    $keyPath = ConvertTo-RegistryProviderPath -Key $Probe.key
    if ([string]::IsNullOrWhiteSpace($keyPath)) {
        return , @()
    }

    $valueNames = @($Probe.valueNames)
    if ($valueNames.Count -eq 0) {
        $valueNames = @('InstallPath')
    }

    $valueKind = if ($Probe.valueKind) { $Probe.valueKind.ToString().ToLowerInvariant() } else { 'directory' }
    $append = if ($Probe.appendExecutable) { $Probe.appendExecutable.ToString() } else { $null }
    $includeSubkeys = $Probe.includeSubkeys -eq $true

    $keys = @()
    try {
        $baseKey = Get-Item -Path $keyPath -ErrorAction Stop
        if ($baseKey) {
            $keys += $baseKey
        }
    }
    catch {
        return , @()
    }

    if ($includeSubkeys) {
        try {
            $subKeys = Get-ChildItem -Path $keyPath -ErrorAction SilentlyContinue
            if ($subKeys) {
                $keys += $subKeys
            }
        }
        catch {
            # ignore
        }
    }

    foreach ($key in $keys) {
        foreach ($valueName in $valueNames) {
            if ([string]::IsNullOrWhiteSpace($valueName)) {
                continue
            }

            $candidate = $null
            try {
                $candidate = Get-ItemPropertyValue -Path $key.PSPath -Name $valueName -ErrorAction Stop
            }
            catch {
                continue
            }

            if ([string]::IsNullOrWhiteSpace($candidate)) {
                continue
            }

            $text = $candidate.ToString().Trim()
            if ([string]::IsNullOrWhiteSpace($text)) {
                continue
            }

            if ($valueKind -eq 'executable') {
                $resolved = Resolve-InventoryPath -Value $text
                if (-not [string]::IsNullOrWhiteSpace($resolved)) {
                    [void]$results.Add($resolved)
                }
            }
            else {
                $basePath = Resolve-InventoryPath -Value $text
                if ([string]::IsNullOrWhiteSpace($basePath)) {
                    continue
                }

                $target = if (-not [string]::IsNullOrWhiteSpace($append)) {
                    Join-Path -Path $basePath -ChildPath $append
                }
                elseif (-not [string]::IsNullOrWhiteSpace($ExecutableName)) {
                    Join-Path -Path $basePath -ChildPath $ExecutableName
                }
                else {
                    $basePath
                }

                $resolvedTarget = Resolve-InventoryPath -Value $target
                if (-not [string]::IsNullOrWhiteSpace($resolvedTarget)) {
                    [void]$results.Add($resolvedTarget)
                }
            }
        }
    }

    return , @($results.ToArray())
}

function ConvertTo-RegistryProviderPath {
    param([string] $Key)

    if ([string]::IsNullOrWhiteSpace($Key)) {
        return $null
    }

    $trimmed = $Key.Trim()

    switch -Regex ($trimmed) {
        '^(HKLM|HKEY_LOCAL_MACHINE)\\(.+)$' { return "Registry::HKEY_LOCAL_MACHINE\\$($Matches[2])" }
        '^(HKCU|HKEY_CURRENT_USER)\\(.+)$' { return "Registry::HKEY_CURRENT_USER\\$($Matches[2])" }
        '^(HKCR|HKEY_CLASSES_ROOT)\\(.+)$' { return "Registry::HKEY_CLASSES_ROOT\\$($Matches[2])" }
        '^(HKU|HKEY_USERS)\\(.+)$' { return "Registry::HKEY_USERS\\$($Matches[2])" }
        '^(HKCC|HKEY_CURRENT_CONFIG)\\(.+)$' { return "Registry::HKEY_CURRENT_CONFIG\\$($Matches[2])" }
        Default { return $trimmed }
    }
}

function ConvertTo-PathPilotMarkdown {
    param([psobject] $Payload)

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $generatedAt = if ($Payload.generatedAt) { $Payload.generatedAt } else { [DateTimeOffset]::UtcNow.ToString('o') }

    $lines.Add('# PathPilot Runtime Inventory') | Out-Null
    $lines.Add("Generated at: $generatedAt") | Out-Null
    $lines.Add('') | Out-Null

    foreach ($runtime in @($Payload.runtimes)) {
        if (-not $runtime) { continue }
        $lines.Add("## $($runtime.name ?? $runtime.id)") | Out-Null
        if (-not [string]::IsNullOrWhiteSpace($runtime.description)) {
            $lines.Add($runtime.description) | Out-Null
            $lines.Add('') | Out-Null
        }

        $exeName = if (-not [string]::IsNullOrWhiteSpace($runtime.executableName)) { $runtime.executableName } else { 'n/a' }
        $lines.Add(('* Executable: ``{0}``' -f $exeName)) | Out-Null
        if (-not [string]::IsNullOrWhiteSpace($runtime.desiredVersion)) {
            $lines.Add(('* Desired version: ``{0}``' -f $runtime.desiredVersion)) | Out-Null
        }

        if ($runtime.active -and $runtime.active.executablePath) {
            $activeLine = ('* Active: ``{0}``' -f $runtime.active.executablePath)
            if ($runtime.active.pathEntry) {
                $activeLine += (' (PATH entry: ``{0}`` )' -f $runtime.active.pathEntry)
            }
            $lines.Add($activeLine) | Out-Null
        }

        $lines.Add('') | Out-Null
        if (-not $runtime.installations -or $runtime.installations.Count -eq 0) {
            $lines.Add('_No installations detected._') | Out-Null
            $lines.Add('') | Out-Null
            continue
        }

        $lines.Add('| Version | Arch | Source | Active | Path |') | Out-Null
        $lines.Add('|---|---|---|---|---|') | Out-Null
        foreach ($install in $runtime.installations) {
            $version = if (-not [string]::IsNullOrWhiteSpace($install.version)) { $install.version } else { 'Unknown' }
            $arch = if (-not [string]::IsNullOrWhiteSpace($install.architecture)) { $install.architecture } else { 'n/a' }
            $source = if (-not [string]::IsNullOrWhiteSpace($install.source)) { $install.source } else { 'n/a' }
            $active = if ($install.isActive) { '✅' } else { '' }
            $path = $install.executablePath
            $lines.Add("| $version | $arch | $source | $active | `$path` |") | Out-Null
        }

        $lines.Add('') | Out-Null
    }

    $switchResultProperty = $Payload.PSObject.Properties['switchResult']
    if ($switchResultProperty -and $switchResultProperty.Value) {
        $result = $switchResultProperty.Value
        $lines.Add('## Switch Result') | Out-Null
        if ($result.message) {
            $lines.Add($result.message) | Out-Null
        }

        $lines.Add(('* Runtime: {0}' -f ($result.runtimeId ?? ''))) | Out-Null
        if ($result.targetDirectory) {
            $lines.Add(('* Target directory: ``{0}``' -f $result.targetDirectory)) | Out-Null
        }
        if ($result.targetExecutable) {
            $lines.Add(('* Executable: ``{0}``' -f $result.targetExecutable)) | Out-Null
        }
        if ($result.backupPath) {
            $lines.Add(('* Backup captured at: ``{0}``' -f $result.backupPath)) | Out-Null
        }
        if ($result.logPath) {
            $lines.Add(('* Operation log: ``{0}``' -f $result.logPath)) | Out-Null
        }
        $lines.Add(('* PATH updated: {0}' -f $result.pathUpdated)) | Out-Null
        $lines.Add(('* Timestamp: {0}' -f ($result.timestamp ?? [DateTimeOffset]::UtcNow.ToString('o')))) | Out-Null
        $lines.Add('') | Out-Null
    }

    if ($Payload.warnings -and $Payload.warnings.Count -gt 0) {
        $lines.Add('## Warnings') | Out-Null
        foreach ($warning in $Payload.warnings) {
            if (-not [string]::IsNullOrWhiteSpace($warning)) {
                $lines.Add("- $warning") | Out-Null
            }
        }
    }

    return $lines -join [Environment]::NewLine
}

function Resolve-SwitchRequest {
    param(
        [string[]] $SwitchArgs,
        [string] $RuntimeId,
        [string] $InstallPath
    )

    $runtime = $null
    $path = $null

    if ($SwitchArgs -and $SwitchArgs.Count -gt 0) {
        $runtime = $SwitchArgs[0]
        if ($SwitchArgs.Count -lt 2) {
            throw 'Switch requires both runtime identifier and installation path.'
        }

        $path = ($SwitchArgs | Select-Object -Skip 1) -join ' '
    }

    if (-not $runtime -and -not [string]::IsNullOrWhiteSpace($RuntimeId) -and -not [string]::IsNullOrWhiteSpace($InstallPath)) {
        $runtime = $RuntimeId
        $path = $InstallPath
    }

    if ([string]::IsNullOrWhiteSpace($runtime) -or [string]::IsNullOrWhiteSpace($path)) {
        throw 'Specify -Switch <runtimeId> <installPath> or provide both -SwitchRuntimeId and -SwitchInstallPath.'
    }

    return [pscustomobject]@{
        runtimeId   = $runtime.Trim()
        installPath = $path
    }
}

function Resolve-ExportTarget {
    param(
        [string] $PathValue,
        [ValidateSet('json', 'markdown')] [string] $Format
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $null
    }

    $resolved = Resolve-InventoryPath -Value $PathValue
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return $null
    }

    if (Test-Path -LiteralPath $resolved -PathType Container) {
        $extension = if ($Format -eq 'markdown') { '.md' } else { '.json' }
        $fileName = "pathpilot-export-$((Get-Date -Format 'yyyyMMdd-HHmmss'))$extension"
        $resolved = Join-Path -Path $resolved -ChildPath $fileName
    }
    else {
        $directory = Split-Path -Parent $resolved
        if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
            [void](New-Item -Path $directory -ItemType Directory -Force)
        }
    }

    return $resolved
}

function Write-PathPilotExport {
    param(
        [string] $Content,
        [string] $TargetPath
    )

    if ([string]::IsNullOrWhiteSpace($Content) -or [string]::IsNullOrWhiteSpace($TargetPath)) {
        return $null
    }

    $directory = Split-Path -Parent $TargetPath
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
        [void](New-Item -Path $directory -ItemType Directory -Force)
    }

    $Content | Out-File -FilePath $TargetPath -Encoding utf8 -Force
    return $TargetPath
}

function Write-PathPilotSwitchLog {
    param(
        [psobject] $Request,
        [psobject] $Result,
        [System.Collections.Generic.List[string]] $Warnings
    )

    try {
        $directory = Get-PathPilotDataDirectory
        $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $filePath = Join-Path -Path $directory -ChildPath "switch-$timestamp.json"
        $logPayload = [pscustomobject]@{
            timestamp = [DateTimeOffset]::UtcNow.ToString('o')
            request   = $Request
            result    = $Result
            warnings  = $Warnings
        }

        $logPayload | ConvertTo-Json -Depth 6 | Out-File -FilePath $filePath -Encoding utf8 -Force
        return $filePath
    }
    catch {
        return $null
    }
}

function Invoke-PathPilotSwitch {
    param(
        [psobject] $Request,
        [System.Collections.Generic.List[psobject]] $Runtimes,
        [string] $MachinePathRaw,
        [System.Collections.Generic.List[psobject]] $MachinePathEntries,
        [System.Collections.Generic.List[string]] $Warnings
    )

    if (-not $Request -or [string]::IsNullOrWhiteSpace($Request.runtimeId)) {
        throw 'Switch runtime identifier is required.'
    }

    if ([string]::IsNullOrWhiteSpace($Request.installPath)) {
        throw 'Switch installation path is required.'
    }

    $runtime = $Runtimes | Where-Object { $_.id -and [string]::Equals($_.id, $Request.runtimeId, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
    if (-not $runtime) {
        throw "Runtime '$($Request.runtimeId)' was not found in the configuration."
    }

    $resolvedInstallPath = Resolve-InventoryPath -Value $Request.installPath
    if ([string]::IsNullOrWhiteSpace($resolvedInstallPath)) {
        throw 'Unable to resolve the requested installation path.'
    }

    if ((Test-Path -LiteralPath $resolvedInstallPath -PathType Container)) {
        $resolvedInstallPath = Join-Path -Path $resolvedInstallPath -ChildPath $runtime.executableName
    }

    if (-not (Test-Path -LiteralPath $resolvedInstallPath -PathType Leaf)) {
        $missingMessage = "The requested installation does not contain $($runtime.executableName). Path: $resolvedInstallPath"
        Write-Output $missingMessage
        throw $missingMessage
    }

    $targetDirectory = Split-Path -Parent $resolvedInstallPath
    if ([string]::IsNullOrWhiteSpace($targetDirectory)) {
        throw 'Unable to determine installation directory for the requested runtime. '
    }

    $normalizedTargetDirectory = Normalize-InventoryPath -Value $targetDirectory
    if ([string]::IsNullOrWhiteSpace($normalizedTargetDirectory)) {
        throw 'Unable to normalize the target installation directory.'
    }

    $runtimeDirectories = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($install in @($runtime.installations)) {
        if ($install.directory) {
            $normalized = Normalize-InventoryPath -Value $install.directory
            if ($normalized) {
                [void]$runtimeDirectories.Add($normalized)
            }
        }
    }

    $existingParts = if ($MachinePathRaw) { $MachinePathRaw -split ';' } else { @() }
    $filtered = New-Object 'System.Collections.Generic.List[string]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($part in $existingParts) {
        if ($null -eq $part) { continue }
        $value = $part.Trim()
        if ([string]::IsNullOrWhiteSpace($value)) { continue }

        $normalized = Normalize-InventoryPath -Value $value
        if ($normalized -and ($runtimeDirectories.Contains($normalized) -or $normalized -eq $normalizedTargetDirectory)) {
            continue
        }

        if ($seen.Add($value)) {
            $filtered.Add($value) | Out-Null
        }
    }

    $newParts = New-Object 'System.Collections.Generic.List[string]'
    $newParts.Add($targetDirectory) | Out-Null
    foreach ($value in $filtered) {
        $newParts.Add($value) | Out-Null
    }

    $newPathValue = ($newParts | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ';'
    $pathChanged = -not [string]::Equals($MachinePathRaw, $newPathValue, [System.StringComparison]::Ordinal)
    $backupPath = $null

    if ($pathChanged) {
        $backupPath = Backup-MachinePath -CurrentPathValue $MachinePathRaw
        if (-not $backupPath) {
            throw 'Failed to capture a PATH backup; switch aborted.'
        }

        Set-MachinePathValue -NewValue $newPathValue
    }

    $matchedInstallation = $runtime.installations | Where-Object { $_.executablePath -and [string]::Equals($_.executablePath, $resolvedInstallPath, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1

    $statusMessage = if ($pathChanged) {
        "Runtime '$($runtime.name ?? $runtime.id)' now resolves to '$targetDirectory'."
    }
    else {
        "Runtime '$($runtime.name ?? $runtime.id)' was already first on PATH."
    }

    return [pscustomobject]@{
        runtimeId        = $runtime.id
        targetExecutable = $resolvedInstallPath
        targetDirectory  = $targetDirectory
        installationId   = if ($matchedInstallation) { $matchedInstallation.id } else { $null }
        backupPath       = $backupPath
        previousPath     = $MachinePathRaw
        updatedPath      = if ($pathChanged) { $newPathValue } else { $MachinePathRaw }
        pathUpdated      = $pathChanged
        success          = $true
        message          = $statusMessage
        timestamp        = [DateTimeOffset]::UtcNow.ToString('o')
    }
}

function Get-RuntimeInventory {
    param(
        [psobject] $Runtime,
        [psobject[]] $MachinePathEntries,
        [System.Collections.Generic.List[string]] $Warnings
    )

    if (-not $Runtime) {
        return $null
    }

    foreach ($propertyName in @('id', 'displayName', 'executableName', 'description', 'desiredVersion', 'versionArguments', 'discovery')) {
        if (-not $Runtime.PSObject.Properties[$propertyName]) {
            Add-Member -InputObject $Runtime -NotePropertyName $propertyName -NotePropertyValue $null -Force | Out-Null
        }
    }

    $id = if (-not [string]::IsNullOrWhiteSpace($Runtime.id)) { $Runtime.id } else { [Guid]::NewGuid().ToString('N') }
    $displayName = if (-not [string]::IsNullOrWhiteSpace($Runtime.displayName)) { $Runtime.displayName } else { $id }
    $executableName = $Runtime.executableName

    if ([string]::IsNullOrWhiteSpace($executableName)) {
        $Warnings.Add("Runtime '$id' is missing 'executableName' in config.") | Out-Null
        return $null
    }

    $candidateLookup = New-Object 'System.Collections.Generic.Dictionary[string, psobject]' ([System.StringComparer]::OrdinalIgnoreCase)
    $discovery = $Runtime.discovery

    if ($discovery) {
        foreach ($pattern in (Get-DiscoveryPropertyValues -Discovery $discovery -PropertyName 'pathGlobs')) {
            if ([string]::IsNullOrWhiteSpace($pattern)) { continue }
            $expanded = Resolve-InventoryPath -Value $pattern -AllowWildcards
            if ([string]::IsNullOrWhiteSpace($expanded)) { continue }
            try {
                $files = Get-ChildItem -Path $expanded -File -ErrorAction SilentlyContinue
            }
            catch {
                $files = @()
            }

            foreach ($file in @($files)) {
                Add-RuntimeCandidate -Lookup $candidateLookup -ExecutablePath $file.FullName -Source 'PathGlob' -Note $pattern
            }
        }

        foreach ($pattern in (Get-DiscoveryPropertyValues -Discovery $discovery -PropertyName 'directoryGlobs')) {
            if ([string]::IsNullOrWhiteSpace($pattern)) { continue }
            $expanded = Resolve-InventoryPath -Value $pattern -AllowWildcards
            if ([string]::IsNullOrWhiteSpace($expanded)) { continue }
            try {
                $directories = Get-ChildItem -Path $expanded -Directory -ErrorAction SilentlyContinue
            }
            catch {
                $directories = @()
            }

            foreach ($directory in @($directories)) {
                $candidate = Join-Path -Path $directory.FullName -ChildPath $executableName
                Add-RuntimeCandidate -Lookup $candidateLookup -ExecutablePath $candidate -Source 'Directory' -Note $directory.FullName
            }
        }

        foreach ($probe in (Get-DiscoveryPropertyValues -Discovery $discovery -PropertyName 'registryProbes')) {
            foreach ($path in Get-RegistryProbePaths -Probe $probe -ExecutableName $executableName) {
                Add-RuntimeCandidate -Lookup $candidateLookup -ExecutablePath $path -Source 'Registry' -Note $probe.key
            }
        }

        foreach ($hint in (Get-DiscoveryPropertyValues -Discovery $discovery -PropertyName 'whereHints')) {
            if ([string]::IsNullOrWhiteSpace($hint)) { continue }
            $whereExe = Get-Command -Name 'where.exe' -ErrorAction SilentlyContinue
            if (-not $whereExe) { break }
            try {
                $lines = & $whereExe.Source $hint 2>$null
            }
            catch {
                $lines = @()
            }

            foreach ($line in @($lines)) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                Add-RuntimeCandidate -Lookup $candidateLookup -ExecutablePath $line -Source 'WhereHint' -Note $hint
            }
        }
    }

    $commandCandidate = Get-Command -Name $executableName -ErrorAction SilentlyContinue
    if ($commandCandidate) {
        $commandPath = Resolve-CommandPath -Command $commandCandidate
        if (-not [string]::IsNullOrWhiteSpace($commandPath)) {
            Add-RuntimeCandidate -Lookup $candidateLookup -ExecutablePath $commandPath -Source 'CommandLookup' -Note 'Get-Command'
        }
    }

    $installations = New-Object 'System.Collections.Generic.List[psobject]'
    $installLookup = New-Object 'System.Collections.Generic.Dictionary[string, psobject]' ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($entry in $candidateLookup.Values) {
        if (-not $entry) { continue }
        $version = Get-RuntimeVersion -ExecutablePath $entry.path -VersionArguments $Runtime.versionArguments -RuntimeId $id -Warnings $Warnings
        $architecture = Get-ArchitectureHeuristic -ExecutablePath $entry.path
        $notesArray = @()
        if ($entry.notes -and $entry.notes.Count -gt 0) {
            $notesArray = @($entry.notes)
        }

        $install = [pscustomobject]@{
            id             = [Guid]::NewGuid().ToString('N')
            directory      = Split-Path -Parent $entry.path
            executablePath = $entry.path
            version        = $version
            architecture   = $architecture
            source         = ($entry.sources -join ', ')
            notes          = $notesArray
            isActive       = $false
        }

        $installations.Add($install) | Out-Null
        $installLookup[$entry.path] = $install
    }

    $installations = @($installations | Sort-Object -Property directory, executablePath)

    $whereHints = @(Get-DiscoveryPropertyValues -Discovery $discovery -PropertyName 'whereHints')
    if ($whereHints.Count -eq 0) { $whereHints = $null }
    $activeInfo = Resolve-ActiveExecutable -ExecutableName $executableName -WhereHints $whereHints -Warnings $Warnings
    $matchedInstallation = $null
    if ($activeInfo.Path -and $installLookup.ContainsKey($activeInfo.Path)) {
        $matchedInstallation = $installLookup[$activeInfo.Path]
        $matchedInstallation.isActive = $true
    }

    $pathEntry = Resolve-PathEntry -Entries $MachinePathEntries -ActivePath $activeInfo.Path

    $status = [pscustomobject]@{
        isMissing        = $installations.Count -eq 0
        hasDuplicates    = $installations.Count -gt 1
        isDrifted        = $false
        hasUnknownActive = $false
    }

    if (-not [string]::IsNullOrWhiteSpace($Runtime.desiredVersion)) {
        $desired = $Runtime.desiredVersion.Trim()
        $status.isDrifted = $installations.Count -eq 0 -or -not ($installations | Where-Object { $_.version -and $_.version.StartsWith($desired, [System.StringComparison]::OrdinalIgnoreCase) })
    }

    if ($activeInfo.Path -and -not $matchedInstallation) {
        $status.hasUnknownActive = $true
    }

    $active = $null
    if ($activeInfo.Path) {
        $active = [pscustomobject]@{
            executablePath           = $activeInfo.Path
            pathEntry                = $pathEntry
            matchesKnownInstallation = [bool]$matchedInstallation
            installationId           = if ($matchedInstallation) { $matchedInstallation.id } else { $null }
            source                   = $activeInfo.Source
        }
    }

    $resolutionOrder = @($activeInfo.Candidates)

    return [pscustomobject]@{
        id              = $id
        name            = $displayName
        description     = $Runtime.description
        executableName  = $executableName
        desiredVersion  = $Runtime.desiredVersion
        installations   = $installations
        status          = $status
        active          = $active
        resolutionOrder = $resolutionOrder
    }
}

$resolvedConfigPath = Resolve-ConfigPath -ConfigPathValue $ConfigPath -ScriptRoot $scriptRoot
$runtimeConfig = @(Get-RuntimeConfigEntries -RootPath $resolvedConfigPath)
if ($runtimeConfig.Count -eq 0) {
    throw "Runtime inventory configuration did not define any runtimes."
}

$warnings = New-Object 'System.Collections.Generic.List[string]'
$machinePathRaw = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
$machinePathEntries = @(Get-MachinePathEntries -PathValue $machinePathRaw)
$runtimes = Build-RuntimeSnapshots -RuntimeConfig $runtimeConfig -MachinePathEntries $machinePathEntries -Warnings $warnings

$switchResult = $null
if ($PSCmdlet.ParameterSetName -eq 'Switch') {
    $switchRequest = Resolve-SwitchRequest -SwitchArgs $Switch -RuntimeId $SwitchRuntimeId -InstallPath $SwitchInstallPath
    $switchResult = Invoke-PathPilotSwitch -Request $switchRequest -Runtimes $runtimes -MachinePathRaw $machinePathRaw -MachinePathEntries $machinePathEntries -Warnings $warnings
    $logPath = Write-PathPilotSwitchLog -Request $switchRequest -Result $switchResult -Warnings $warnings
    if ($logPath) {
        Add-Member -InputObject $switchResult -NotePropertyName 'logPath' -NotePropertyValue $logPath -Force | Out-Null
    }

    $machinePathRaw = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
    $machinePathEntries = @(Get-MachinePathEntries -PathValue $machinePathRaw)
    $runtimes = Build-RuntimeSnapshots -RuntimeConfig $runtimeConfig -MachinePathEntries $machinePathEntries -Warnings $warnings
}

$payload = [pscustomobject]@{
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    runtimes    = $runtimes
    machinePath = [pscustomobject]@{
        raw     = $machinePathRaw
        entries = $machinePathEntries
    }
    warnings    = $warnings
}

if ($switchResult) {
    Add-Member -InputObject $payload -NotePropertyName 'switchResult' -NotePropertyValue $switchResult -Force | Out-Null
}

$resolvedExportPath = $null
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedExportPath = Resolve-ExportTarget -PathValue $OutputPath -Format $Export
    if (-not $resolvedExportPath) {
        throw 'Failed to resolve export destination path.'
    }

    Add-Member -InputObject $payload -NotePropertyName 'exportPath' -NotePropertyValue $resolvedExportPath -Force | Out-Null
}

if ($Export -eq 'markdown') {
    $markdown = ConvertTo-PathPilotMarkdown -Payload $payload
    if ($resolvedExportPath) {
        Write-PathPilotExport -Content $markdown -TargetPath $resolvedExportPath | Out-Null
    }

    $markdown
}
else {
    $jsonCompact = $payload | ConvertTo-Json -Depth 6 -Compress
    if ($resolvedExportPath) {
        $jsonPretty = $payload | ConvertTo-Json -Depth 6
        Write-PathPilotExport -Content $jsonPretty -TargetPath $resolvedExportPath | Out-Null
    }

    $jsonCompact
}

