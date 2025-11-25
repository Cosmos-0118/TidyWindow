function Resolve-TidyPath {
    [CmdletBinding()]
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    if ($expanded.StartsWith('~')) {
        $home = $env:USERPROFILE
        if (-not [string]::IsNullOrWhiteSpace($home)) {
            $expanded = $home + $expanded.Substring(1)
        }
    }

    try {
        return [System.IO.Path]::GetFullPath($expanded)
    }
    catch {
        return $expanded
    }
}

function ConvertTo-TidyNameKey {
    [CmdletBinding()]
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $clean = $Value.ToLowerInvariant()
    $clean = [System.Text.RegularExpressions.Regex]::Replace($clean, '[^a-z0-9]', '')
    if ([string]::IsNullOrWhiteSpace($clean)) {
        return $null
    }

    return $clean
}

function Get-TidyProgramDataDirectory {
    [CmdletBinding()]
    param()

    $programData = $env:ProgramData
    if ([string]::IsNullOrWhiteSpace($programData)) {
        $programData = 'C:\ProgramData'
    }

    $root = Join-Path -Path $programData -ChildPath 'TidyWindow'
    if (-not (Test-Path -LiteralPath $root)) {
        [void](New-Item -Path $root -ItemType Directory -Force)
    }

    return $root
}

function New-TidyFeatureRunDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $FeatureName,
        [Parameter(Mandatory = $true)]
        [string] $AppIdentifier
    )

    $root = Get-TidyProgramDataDirectory
    $featureRoot = Join-Path -Path $root -ChildPath $FeatureName
    if (-not (Test-Path -LiteralPath $featureRoot)) {
        [void](New-Item -Path $featureRoot -ItemType Directory -Force)
    }

    $safeId = [System.Text.RegularExpressions.Regex]::Replace($AppIdentifier, '[^A-Za-z0-9_-]', '_')
    if ([string]::IsNullOrWhiteSpace($safeId)) {
        $safeId = 'app'
    }

    $target = Join-Path -Path $featureRoot -ChildPath $safeId
    if (-not (Test-Path -LiteralPath $target)) {
        [void](New-Item -Path $target -ItemType Directory -Force)
    }

    return $target
}

function Write-TidyStructuredEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Type,
        [object] $Payload
    )

    $envelope = [ordered]@{
        type      = $Type
        timestamp = [DateTimeOffset]::UtcNow.ToString('o')
    }

    if ($Payload) {
        if ($Payload -is [System.Collections.IDictionary]) {
            foreach ($key in $Payload.Keys) {
                $envelope[$key] = $Payload[$key]
            }
        }
        elseif ($Payload -is [pscustomobject]) {
            foreach ($prop in $Payload.PSObject.Properties) {
                if (-not [string]::IsNullOrWhiteSpace($prop.Name)) {
                    $envelope[$prop.Name] = $prop.Value
                }
            }
        }
        else {
            $envelope['payload'] = $Payload
        }
    }

    $json = $envelope | ConvertTo-Json -Depth 6 -Compress
    Write-Output $json
}

function Write-TidyRunLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [hashtable] $Payload
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
        [void](New-Item -Path $directory -ItemType Directory -Force)
    }

    $Payload | ConvertTo-Json -Depth 6 | Out-File -FilePath $Path -Encoding utf8 -Force
}

function Invoke-TidyCommandLine {
    [CmdletBinding()]
    param([string] $CommandLine)

    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return [pscustomobject]@{ exitCode = 0; output = ''; errors = ''; durationMs = 0 }
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'cmd.exe'
    $psi.Arguments = "/d /s /c ""$CommandLine"""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    $start = [DateTimeOffset]::UtcNow
    $null = $process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $duration = ([DateTimeOffset]::UtcNow - $start).TotalMilliseconds

    return [pscustomobject]@{
        exitCode  = $process.ExitCode
        output    = $stdout
        errors    = $stderr
        durationMs = [math]::Round($duration, 0)
    }
}

function Get-TidyProcessSnapshot {
    [CmdletBinding()]
    param()

    $list = New-Object 'System.Collections.Generic.List[psobject]'
    try {
        Get-Process | ForEach-Object {
            $path = $null
            try { $path = $_.Path } catch { $path = $null }
            $list.Add([pscustomobject]@{ id = $_.Id; name = $_.ProcessName; path = $path }) | Out-Null
        }
    }
    catch {
        # Ignore snapshot failures.
    }

    return $list
}

function Get-TidyServiceSnapshot {
    [CmdletBinding()]
    param()

    $list = New-Object 'System.Collections.Generic.List[psobject]'
    try {
        Get-CimInstance -ClassName Win32_Service | ForEach-Object {
            $list.Add([pscustomobject]@{
                name        = $_.Name
                displayName = $_.DisplayName
                path        = $_.PathName
                state       = $_.State
            }) | Out-Null
        }
    }
    catch {
        # Ignore snapshot failures.
    }

    return $list
}

function ConvertTo-TidyRegistryPath {
    [CmdletBinding()]
    param([string] $KeyPath)

    if ([string]::IsNullOrWhiteSpace($KeyPath)) { return $null }

    switch -Regex ($KeyPath) {
        '^(HKEY_LOCAL_MACHINE|HKLM)\\(.+)$' { return "Registry::HKEY_LOCAL_MACHINE\$($matches[2])" }
        '^(HKEY_CURRENT_USER|HKCU)\\(.+)$'  { return "Registry::HKEY_CURRENT_USER\$($matches[2])" }
        '^(HKEY_CLASSES_ROOT|HKCR)\\(.+)$'  { return "Registry::HKEY_CLASSES_ROOT\$($matches[2])" }
        '^(HKEY_USERS|HKU)\\(.+)$'          { return "Registry::HKEY_USERS\$($matches[2])" }
        '^(HKEY_CURRENT_CONFIG|HKCC)\\(.+)$'{ return "Registry::HKEY_CURRENT_CONFIG\$($matches[2])" }
        Default { return $KeyPath }
    }
}

function Measure-TidyDirectoryBytes {
    [CmdletBinding()]
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return 0 }

    try {
        $items = Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue
        return ($items | Measure-Object -Property Length -Sum).Sum
    }
    catch {
        return 0
    }
}

function New-TidyArtifactId {
    [CmdletBinding()]
    param()

    return [Guid]::NewGuid().ToString('n')
}

function New-OblivionArtifactId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Type,
        [Parameter(Mandatory = $true)][string] $Path,
        [string] $Reason
    )

    $typeKey = if ($Type) { $Type.Trim().ToLowerInvariant() } else { [string]::Empty }
    $normalizedPath = $null
    try {
        $normalizedPath = Resolve-OblivionFullPath -Path $Path
    }
    catch {
        $normalizedPath = $null
    }

    if ([string]::IsNullOrWhiteSpace($normalizedPath)) {
        $normalizedPath = if ($Path) { $Path } else { [string]::Empty }
    }

    $reasonKey = if ([string]::IsNullOrWhiteSpace($Reason)) { [string]::Empty } else { $Reason.Trim().ToLowerInvariant() }
    $input = "$typeKey::$normalizedPath::$reasonKey"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($input)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    $hex = -join ($hashBytes | ForEach-Object { $_.ToString('x2') })
    if ($hex.Length -gt 32) {
        return $hex.Substring(0, 32)
    }

    return $hex
}

function New-TidyFileArtifact {
    [CmdletBinding()]
    param(
        [string] $Path,
        [string] $Reason,
        [bool] $DefaultSelected = $true,
        [string] $Confidence = 'anchor',
        [string] $SourceAnchor,
        [hashtable] $Metadata
    )

    $resolved = Resolve-TidyPath -Path $Path
    if ([string]::IsNullOrWhiteSpace($resolved) -or -not (Test-Path -LiteralPath $resolved)) {
        return $null
    }

    $item = Get-Item -LiteralPath $resolved -ErrorAction SilentlyContinue
    if (-not $item) { return $null }

    $isDirectory = $item.PSIsContainer
    $size = if ($isDirectory) { Measure-TidyDirectoryBytes -Path $resolved } else { $item.Length }

    $programFiles = $env:ProgramFiles
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $requiresElevation = $false
    if ($programFiles -and $resolved.StartsWith($programFiles, [System.StringComparison]::OrdinalIgnoreCase)) {
        $requiresElevation = $true
    }
    elseif ($programFilesX86 -and $resolved.StartsWith($programFilesX86, [System.StringComparison]::OrdinalIgnoreCase)) {
        $requiresElevation = $true
    }

    $metadataPayload = [ordered]@{ reason = $Reason }
    if ($Confidence) { $metadataPayload['confidence'] = $Confidence }
    if ($SourceAnchor) { $metadataPayload['sourceAnchor'] = $SourceAnchor }
    if ($Metadata) {
        foreach ($key in $Metadata.Keys) {
            $metadataPayload[$key] = $Metadata[$key]
        }
    }

    $artifactId = New-OblivionArtifactId -Type ($isDirectory ? 'Directory' : 'File') -Path $resolved -Reason $Reason
    if ([string]::IsNullOrWhiteSpace($artifactId)) {
        $artifactId = New-TidyArtifactId
    }

    return [pscustomobject]@{
        id                = $artifactId
        type              = $isDirectory ? 'Directory' : 'File'
        group             = 'Files'
        path              = $resolved
        displayName       = Split-Path -Path $resolved -Leaf
        sizeBytes         = $size
        defaultSelected   = $DefaultSelected
        requiresElevation = $requiresElevation
        metadata          = $metadataPayload
    }
}

function New-TidyRegistryArtifact {
    [CmdletBinding()]
    param([string] $KeyPath)

    $providerPath = ConvertTo-TidyRegistryPath -KeyPath $KeyPath
    if (-not $providerPath) { return $null }

    $artifactId = New-OblivionArtifactId -Type 'Registry' -Path $providerPath -Reason 'UninstallKey'
    if ([string]::IsNullOrWhiteSpace($artifactId)) {
        $artifactId = New-TidyArtifactId
    }

    return [pscustomobject]@{
        id                = $artifactId
        type              = 'Registry'
        group             = 'Registry'
        path              = $providerPath
        displayName       = $KeyPath
        sizeBytes         = 0
        defaultSelected   = $true
        requiresElevation = $providerPath -like 'Registry::HKEY_LOCAL_MACHINE*'
        metadata          = @{ reason = 'UninstallKey' }
    }
}

function New-TidyServiceArtifact {
    [CmdletBinding()]
    param([string] $ServiceName)

    if ([string]::IsNullOrWhiteSpace($ServiceName)) { return $null }

    $artifactId = New-OblivionArtifactId -Type 'Service' -Path $ServiceName -Reason 'ServiceHint'
    if ([string]::IsNullOrWhiteSpace($artifactId)) {
        $artifactId = New-TidyArtifactId
    }

    return [pscustomobject]@{
        id                = $artifactId
        type              = 'Service'
        group             = 'Services'
        path              = $ServiceName
        displayName       = $ServiceName
        sizeBytes         = 0
        defaultSelected   = $true
        requiresElevation = $true
        metadata          = @{ reason = 'ServiceHint' }
    }
}

function Get-TidyCandidateDataFolders {
    [CmdletBinding()]
    param([psobject] $App)

    $results = @()
    $name = $App.name
    if ([string]::IsNullOrWhiteSpace($name)) { return $results }

    $safe = [System.Text.RegularExpressions.Regex]::Replace($name, '[^A-Za-z0-9]', '')
    if ([string]::IsNullOrWhiteSpace($safe)) { return $results }

    $roots = @()
    if ($env:ProgramData) { $roots += $env:ProgramData }
    else { $roots += 'C:\ProgramData' }
    if ($env:LOCALAPPDATA) { $roots += $env:LOCALAPPDATA }
    if ($env:APPDATA) { $roots += $env:APPDATA }

    foreach ($root in $roots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $candidate = Join-Path -Path $root -ChildPath $safe
        if (Test-Path -LiteralPath $candidate) {
            $results += $candidate
        }
    }

    return $results
}

function Get-TidyArtifacts {
    [CmdletBinding()]
    param([psobject] $App)

    $artifacts = New-Object 'System.Collections.Generic.List[psobject]'

    $primaryInstallRoot = $null
    if ($App.PSObject.Properties['installRoot']) {
        $primaryInstallRoot = $App.installRoot
        foreach ($root in @($primaryInstallRoot)) {
            $artifact = New-TidyFileArtifact -Path $root -Reason 'InstallRoot' -SourceAnchor $root
            if ($artifact) { $artifacts.Add($artifact) | Out-Null }
        }
    }

    if ($App.PSObject.Properties['artifactHints']) {
        foreach ($hint in @($App.artifactHints)) {
            if ([string]::IsNullOrWhiteSpace($hint)) { continue }
            if ($primaryInstallRoot -and [string]::Equals($hint, $primaryInstallRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            $artifact = New-TidyFileArtifact -Path $hint -Reason 'Hint' -SourceAnchor $hint
            if ($artifact) { $artifacts.Add($artifact) | Out-Null }
        }
    }

    foreach ($folder in Get-TidyCandidateDataFolders -App $App) {
        $artifact = New-TidyFileArtifact -Path $folder -Reason 'DataFolder' -DefaultSelected:$false -Confidence 'heuristic'
        if ($artifact) { $artifacts.Add($artifact) | Out-Null }
    }

    if (
        $App.PSObject.Properties['registry'] -and
        $App.registry -and
        $App.registry.PSObject -and
        $App.registry.PSObject.Properties['keyPath'] -and
        $App.registry.keyPath
    ) {
        $artifact = New-TidyRegistryArtifact -KeyPath $App.registry.keyPath
        if ($artifact) { $artifacts.Add($artifact) | Out-Null }
    }

    if ($App.PSObject.Properties['serviceHints']) {
        foreach ($svc in @($App.serviceHints)) {
            $artifact = New-TidyServiceArtifact -ServiceName $svc
            if ($artifact) { $artifacts.Add($artifact) | Out-Null }
        }
    }

    return $artifacts
}

