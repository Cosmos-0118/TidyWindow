param(
    [Parameter(Mandatory = $true)]
    [string] $Manager,
    [Parameter(Mandatory = $true)]
    [string] $PackageId,
    [string] $DisplayName,
    [switch] $RequiresAdmin,
    [switch] $Elevated,
    [string] $ResultPath,
    [string] $TargetVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$callerScriptPath = $PSCmdlet.MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerScriptPath)) {
    $callerScriptPath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerScriptPath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -Path $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$script:TidyOutputLines = [System.Collections.Generic.List[string]]::new()
$script:TidyErrorLines = [System.Collections.Generic.List[string]]::new()
$script:OperationSucceeded = $true
$script:ResultPayload = $null
$script:UsingResultFile = -not [string]::IsNullOrWhiteSpace($ResultPath)

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

$targetVersionValue = if ([string]::IsNullOrWhiteSpace($TargetVersion)) {
    $null
} else {
    $TargetVersion.Trim()
}

function Write-TidyOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    if ([string]::IsNullOrWhiteSpace($Message)) {
        return
    }

    [void]$script:TidyOutputLines.Add($Message)
    Write-Output $Message
}

function Write-TidyError {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    if ([string]::IsNullOrWhiteSpace($Message)) {
        return
    }

    $script:OperationSucceeded = $false
    [void]$script:TidyErrorLines.Add($Message)
    Write-Output "[ERROR] $Message"
}

function Save-TidyResult {
    if (-not $script:UsingResultFile) {
        return
    }

    $payload = [pscustomobject]@{
        Success = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
        Output  = $script:TidyOutputLines
        Errors  = $script:TidyErrorLines
        Result  = $script:ResultPayload
    }

    $json = $payload | ConvertTo-Json -Depth 6
    Set-Content -Path $ResultPath -Value $json -Encoding UTF8
}

function Test-TidyAdmin {
    return [bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-TidyPowerShellExecutable {
    if ($PSVersionTable.PSEdition -eq 'Core') {
        $pwsh = Get-Command -Name 'pwsh' -ErrorAction SilentlyContinue
        if ($pwsh) {
            return $pwsh.Source
        }
    }

    $legacy = Get-Command -Name 'powershell.exe' -ErrorAction SilentlyContinue
    if ($legacy) {
        return $legacy.Source
    }

    throw 'Unable to locate a PowerShell executable to request elevation.'
}

function Request-TidyElevation {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ScriptPath,
        [Parameter(Mandatory = $true)]
        [string] $Manager,
        [Parameter(Mandatory = $true)]
        [string] $PackageId,
        [Parameter(Mandatory = $true)]
        [string] $DisplayName,
        [Parameter(Mandatory = $true)]
        [bool] $IncludeRequiresAdmin,
        [string] $TargetVersion
    )

    $resultTemp = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "tidywindow-update-" + ([System.Guid]::NewGuid().ToString('N')) + '.json')
    $shellPath = Get-TidyPowerShellExecutable

    function ConvertTo-TidyArgument {
        param(
            [Parameter(Mandatory = $true)]
            [string] $Value
        )

        $escaped = $Value -replace '"', '""'
        return "`"$escaped`""
    }

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (ConvertTo-TidyArgument -Value $ScriptPath),
        '-Manager', (ConvertTo-TidyArgument -Value $Manager),
        '-PackageId', (ConvertTo-TidyArgument -Value $PackageId),
        '-DisplayName', (ConvertTo-TidyArgument -Value $DisplayName),
        '-Elevated',
        '-ResultPath', (ConvertTo-TidyArgument -Value $resultTemp)
    )

    if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
        $arguments += @('-TargetVersion', (ConvertTo-TidyArgument -Value $TargetVersion))
    }

    if ($IncludeRequiresAdmin) {
        $arguments += '-RequiresAdmin'
    }

    Write-TidyLog -Level Information -Message "Requesting administrator approval to update '$DisplayName'."
    Write-TidyOutput -Message 'Requesting administrator approval. Windows may prompt for permission.'

    try {
        Start-Process -FilePath $shellPath -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait | Out-Null
    }
    catch {
        throw 'Administrator approval was denied or cancelled.'
    }

    if (-not (Test-Path -Path $resultTemp)) {
        throw 'Administrator approval was denied before the operation could start.'
    }

    try {
        $json = Get-Content -Path $resultTemp -Raw -ErrorAction Stop
        return (ConvertFrom-Json -InputObject $json -ErrorAction Stop)
    }
    finally {
        Remove-Item -Path $resultTemp -ErrorAction SilentlyContinue
    }
}

function Normalize-VersionString {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $trimmed = $Value.Trim()
    if ($trimmed -match '([0-9]+(?:[\._][0-9A-Za-z]+)*)') {
        $candidate = $matches[1].Replace('_', '.')
        return $candidate
    }

    return $trimmed
}

function Get-Status {
    param(
        [string] $Installed,
        [string] $Latest
    )

    $normalizedInstalled = Normalize-VersionString -Value $Installed
    $normalizedLatest = Normalize-VersionString -Value $Latest

    if ([string]::IsNullOrWhiteSpace($normalizedInstalled)) {
        return 'NotInstalled'
    }

    if ([string]::IsNullOrWhiteSpace($normalizedLatest) -or $normalizedLatest.Trim().ToLowerInvariant() -eq 'unknown') {
        return 'Unknown'
    }

    if ($normalizedInstalled -eq $normalizedLatest) {
        return 'UpToDate'
    }

    $installedVersion = $null
    $latestVersion = $null
    if ([version]::TryParse($normalizedInstalled, [ref]$installedVersion) -and [version]::TryParse($normalizedLatest, [ref]$latestVersion)) {
        if ($installedVersion -lt $latestVersion) {
            return 'UpdateAvailable'
        }

        return 'UpToDate'
    }

    return 'UpdateAvailable'
}

function Resolve-ManagerExecutable {
    param([string] $Key)

    switch ($Key) {
        'winget' {
            $cmd = Get-Command -Name 'winget' -ErrorAction SilentlyContinue
            if (-not $cmd) { throw 'winget CLI was not found on this machine.' }
            if ($cmd.Source) { return $cmd.Source }
            return 'winget'
        }
        'choco' {
            $cmd = Get-Command -Name 'choco' -ErrorAction SilentlyContinue
            if (-not $cmd) { throw 'Chocolatey (choco) CLI was not found on this machine.' }
            if ($cmd.Source) { return $cmd.Source }
            return 'choco'
        }
        'chocolatey' {
            return Resolve-ManagerExecutable -Key 'choco'
        }
        'scoop' {
            $cmd = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue
            if (-not $cmd) { throw 'Scoop CLI was not found on this machine.' }
            if ($cmd.Source) { return $cmd.Source }
            return 'scoop'
        }
        default {
            throw "Unsupported package manager '$Key'."
        }
    }
}

function Get-WingetInstalledVersion {
    param([string] $PackageId)

    $command = Get-Command -Name 'winget' -ErrorAction SilentlyContinue
    if (-not $command) { return $null }

    $exe = if ($command.Source) { $command.Source } else { 'winget' }
    try {
        $jsonOutput = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' '--output' 'json' 2>$null
        if ($LASTEXITCODE -eq 0 -and $jsonOutput) {
            $payload = ConvertFrom-Json -InputObject ($jsonOutput -join [Environment]::NewLine) -ErrorAction Stop
            $candidates = @()

            if ($payload) {
                if ($payload.InstalledPackages) { $candidates += @($payload.InstalledPackages) }
                if ($payload.Items) { $candidates += @($payload.Items) }
                if ($payload.Packages) { $candidates += @($payload.Packages) }
                if ($payload.Sources) {
                    foreach ($source in @($payload.Sources)) {
                        if ($source.Packages) { $candidates += @($source.Packages) }
                        if ($source.InstalledPackages) { $candidates += @($source.InstalledPackages) }
                    }
                }

                if ($payload -is [System.Collections.IDictionary] -and $candidates.Count -eq 0) {
                    foreach ($value in $payload.Values) {
                        if ($value -is [System.Collections.IEnumerable] -and $value -isnot [string]) {
                            $candidates += @($value)
                        }
                    }
                }

                foreach ($candidate in $candidates) {
                    if (-not $candidate) { continue }

                    $version = $null

                    if ($candidate.Version) { $version = $candidate.Version }
                    elseif ($candidate.version) { $version = $candidate.version }
                    elseif ($candidate.InstalledVersion) { $version = $candidate.InstalledVersion }
                    elseif ($candidate.installedVersion) { $version = $candidate.installedVersion }

                    if (-not $version) { continue }

                    if ($candidate.PackageIdentifier -and [string]::Equals($candidate.PackageIdentifier, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                        return $version.ToString().Trim()
                    }
                    if ($candidate.Id -and [string]::Equals($candidate.Id, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                        return $version.ToString().Trim()
                    }
                    if (-not $candidate.PackageIdentifier -and -not $candidate.Id) {
                        return $version.ToString().Trim()
                    }
                }
            }
        }
    }
    catch { }

    try {
        $fallback = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null
        foreach ($line in @($fallback)) {
            if ($line -match '\s+' + [Regex]::Escape($PackageId) + '\s+([^\s]+)') {
                return $matches[1].Trim()
            }
        }
    }
    catch { }

    return $null
}

function Get-WingetAvailableVersion {
    param([string] $PackageId)

    $command = Get-Command -Name 'winget' -ErrorAction SilentlyContinue
    if (-not $command) { return $null }

    $exe = if ($command.Source) { $command.Source } else { 'winget' }
    try {
        $jsonOutput = & $exe 'show' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' '--output' 'json' 2>$null
        if ($LASTEXITCODE -eq 0 -and $jsonOutput) {
            $data = ConvertFrom-Json -InputObject ($jsonOutput -join [Environment]::NewLine) -ErrorAction Stop
            if ($data -and $data.Versions -and $data.Versions.Count -gt 0) {
                $latest = $data.Versions | Select-Object -First 1
                if ($latest.Version) { return $latest.Version.Trim() }
            }
            elseif ($data -and $data.Version) {
                return $data.Version.Trim()
            }
        }
    }
    catch { }

    try {
        $fallback = & $exe 'show' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null
        foreach ($line in @($fallback)) {
            if ($line -match '^\s*Version\s*:\s*(.+)$') {
                return $matches[1].Trim()
            }
        }
    }
    catch { }

    return $null
}

function Get-ChocoInstalledVersion {
    param([string] $PackageId)

    $command = Get-Command -Name 'choco' -ErrorAction SilentlyContinue
    if (-not $command) { return $null }

    $exe = if ($command.Source) { $command.Source } else { 'choco' }
    try {
        $output = & $exe 'list' $PackageId '--local-only' '--exact' '--limit-output' 2>$null
        foreach ($line in @($output)) {
            if ($line -match '^\s*' + [Regex]::Escape($PackageId) + '\|(.+)$') {
                return $matches[1].Trim()
            }
        }
    }
    catch { }

    return $null
}

function Get-ChocoAvailableVersion {
    param([string] $PackageId)

    $command = Get-Command -Name 'choco' -ErrorAction SilentlyContinue
    if (-not $command) { return $null }

    $exe = if ($command.Source) { $command.Source } else { 'choco' }
    try {
        $output = & $exe 'search' $PackageId '--exact' '--limit-output' 2>$null
        foreach ($line in @($output)) {
            if ($line -match '^\s*' + [Regex]::Escape($PackageId) + '\|(.+)$') {
                return $matches[1].Trim()
            }
        }
    }
    catch { }

    return $null
}

function Get-ScoopManifestPaths {
    param([string] $PackageId)

    $root = $env:SCOOP
    if ([string]::IsNullOrWhiteSpace($root)) {
        $root = Join-Path -Path ([Environment]::GetFolderPath('UserProfile')) -ChildPath 'scoop'
    }

    if ([string]::IsNullOrWhiteSpace($PackageId) -or [string]::IsNullOrWhiteSpace($root)) {
        return [pscustomobject]@{
            Root            = $root
            BucketPath      = $null
            WorkspacePath   = $null
            WorkspaceExists = $false
        }
    }

    $bucketPath = $null
    $bucketRoot = if ($root) { Join-Path -Path $root -ChildPath 'buckets' } else { $null }
    if ($bucketRoot -and (Test-Path -Path $bucketRoot)) {
        foreach ($bucket in Get-ChildItem -Path $bucketRoot -Directory -ErrorAction SilentlyContinue) {
            foreach ($extension in @('.json', '.yml', '.yaml')) {
                $candidate = Join-Path -Path $bucket.FullName -ChildPath (Join-Path -Path 'bucket' -ChildPath "$PackageId$extension")
                if (Test-Path -Path $candidate) {
                    $bucketPath = $candidate
                    break
                }
            }

            if ($bucketPath) { break }
        }
    }

    $workspaceDir = if ($root) { Join-Path -Path $root -ChildPath 'workspace' } else { $null }
    $workspacePath = $null
    $workspaceExists = $false
    if ($workspaceDir) {
        $workspacePath = Join-Path -Path $workspaceDir -ChildPath "$PackageId.json"
        if (Test-Path -Path $workspacePath) {
            $workspaceExists = $true
        }
    }

    return [pscustomobject]@{
        Root            = $root
        BucketPath      = $bucketPath
        WorkspacePath   = $workspacePath
        WorkspaceExists = $workspaceExists
    }
}

function Get-ScoopInstalledVersion {
    param([string] $PackageId)

    $command = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue
    if (-not $command) { return $null }

    $exe = if ($command.Source) { $command.Source } else { 'scoop' }

    # Prefer scoop export because scoop list --json is not available in older builds.
    try {
        $output = & $exe 'export' 2>$null
        if ($LASTEXITCODE -eq 0 -and $output) {
            $payload = ConvertFrom-Json -InputObject ($output -join [Environment]::NewLine) -ErrorAction Stop
            $apps = @()

            if ($payload) {
                if ($payload.apps) { $apps += @($payload.apps) }
                if ($payload.Apps) { $apps += @($payload.Apps) }
                if ($payload -is [System.Collections.IEnumerable] -and $payload -isnot [string]) {
                    $apps += @($payload)
                }
            }

            foreach ($entry in $apps) {
                if (-not $entry) { continue }

                $name = $entry.Name
                if (-not $name) { $name = $entry.name }
                if (-not $name) { $name = $entry.Id }
                if (-not $name) { $name = $entry.id }

                if ($name -and [string]::Equals($name, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                    if ($entry.Version) { return ($entry.Version).ToString().Trim() }
                    if ($entry.version) { return ($entry.version).ToString().Trim() }
                }
            }
        }
    }
    catch { }

    # Fall back to scoop list --json if export is unavailable.
    try {
        $output = & $exe 'list' '--json' 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $output) { throw 'list-json-unavailable' }

        $appsPayload = ConvertFrom-Json -InputObject ($output -join [Environment]::NewLine) -ErrorAction Stop
        $apps = @()

        if ($appsPayload -is [System.Collections.IEnumerable] -and $appsPayload -isnot [string]) {
            $apps = @($appsPayload)
        }
        elseif ($appsPayload -is [System.Collections.IDictionary]) {
            if ($appsPayload.ContainsKey('apps')) {
                $apps = @($appsPayload['apps'])
            }
            elseif ($appsPayload.ContainsKey('Apps')) {
                $apps = @($appsPayload['Apps'])
            }
            elseif ($appsPayload.ContainsKey('installed')) {
                $apps = @($appsPayload['installed'])
            }

            if ($apps.Count -eq 0) {
                foreach ($key in $appsPayload.Keys) {
                    $entry = $appsPayload[$key]
                    if ($entry -is [System.Collections.IEnumerable] -and $entry -isnot [string]) {
                        $apps += @($entry)
                    }
                    elseif ($null -ne $entry) {
                        $apps += ,$entry
                    }
                }
            }
        }

        foreach ($entry in $apps) {
            $name = $entry.Name
            if (-not $name) { $name = $entry.name }
            if (-not $name) { $name = $entry.id }

            if ($name -and [string]::Equals($name, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                if ($entry.Version) { return ($entry.Version).ToString().Trim() }
                if ($entry.version) { return ($entry.version).ToString().Trim() }
                if ($entry.installed) { return ($entry.installed).ToString().Trim() }
            }
        }
    }
    catch { }

    try {
        $fallback = & $exe 'info' $PackageId 2>$null
        foreach ($line in @($fallback)) {
            if ($line -match '^\s*Installed Version\s*:\s*(.+)$') { return $matches[1].Trim() }
            if ($line -match '^\s*Version\s*:\s*(.+)$') { return $matches[1].Trim() }
        }
    }
    catch { }

    return $null
}

function Get-ScoopAvailableVersion {
    param([string] $PackageId)

    $command = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue
    if (-not $command) { return $null }

    $exe = if ($command.Source) { $command.Source } else { 'scoop' }
    try {
        $paths = Get-ScoopManifestPaths -PackageId $PackageId
        if ($paths.BucketPath -and (Test-Path -Path $paths.BucketPath)) {
            $content = Get-Content -Path $paths.BucketPath -Raw -ErrorAction Stop
            if ($paths.BucketPath.EndsWith('.json', [System.StringComparison]::OrdinalIgnoreCase)) {
                $manifest = $content | ConvertFrom-Json -ErrorAction Stop
                if ($manifest.version) { return ($manifest.version).ToString().Trim() }
                if ($manifest.Version) { return ($manifest.Version).ToString().Trim() }
            }
            else {
                foreach ($line in $content -split "`n") {
                    if ($line -match '^\s*version\s*:\s*(.+)$') {
                        return $matches[1].Trim()
                    }
                }
            }
        }

        if ($paths.WorkspaceExists -and $paths.WorkspacePath -and (Test-Path -Path $paths.WorkspacePath)) {
            $content = Get-Content -Path $paths.WorkspacePath -Raw -ErrorAction Stop
            $manifest = $content | ConvertFrom-Json -ErrorAction Stop
            if ($manifest.version) { return ($manifest.version).ToString().Trim() }
            if ($manifest.Version) { return ($manifest.Version).ToString().Trim() }
        }
    }
    catch {
        # Ignore manifest probing failures and continue with CLI-based approaches.
    }

    try {
        $output = & $exe 'info' $PackageId '--json' 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $output) { return $null }

        $info = ConvertFrom-Json -InputObject ($output -join [Environment]::NewLine) -ErrorAction Stop
        if ($info -is [System.Collections.IDictionary]) {
            if ($info.App) {
                if ($info.App.Version) { return ($info.App.Version).ToString().Trim() }
                if ($info.App.'Latest Version') { return ($info.App.'Latest Version').ToString().Trim() }
            }

            if ($info.Version) { return $info.Version.ToString().Trim() }
            if ($info.version) { return $info.version.ToString().Trim() }
        }
    }
    catch { }

    try {
        $fallback = & $exe 'info' $PackageId 2>$null
        foreach ($line in @($fallback)) {
            if ($line -match '^\s*Latest Version\s*:\s*(.+)$') { return $matches[1].Trim() }
            if ($line -match '^\s*Version\s*:\s*(.+)$') { return $matches[1].Trim() }
        }
    }
    catch { }

    return $null
}

function Reset-ScoopWorkspaceManifestIfOutdated {
    param(
        [string] $PackageId,
        [string] $LatestVersion
    )

    if ([string]::IsNullOrWhiteSpace($PackageId) -or [string]::IsNullOrWhiteSpace($LatestVersion)) {
        return
    }

    $paths = Get-ScoopManifestPaths -PackageId $PackageId
    if (-not $paths) { return }

    $bucketPath = $paths.BucketPath
    if (-not $bucketPath -or -not (Test-Path -Path $bucketPath)) {
        return
    }

    if (-not $bucketPath.EndsWith('.json', [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $workspacePath = $paths.WorkspacePath
    if (-not $workspacePath) {
        if ([string]::IsNullOrWhiteSpace($paths.Root)) { return }
        $workspacePath = Join-Path -Path (Join-Path -Path $paths.Root -ChildPath 'workspace') -ChildPath "$PackageId.json"
    }

    $workspaceDir = Split-Path -Parent $workspacePath
    if (-not (Test-Path -Path $workspaceDir)) {
        try { New-Item -Path $workspaceDir -ItemType Directory -Force | Out-Null } catch { return }
    }

    $bucketVersion = $null
    $workspaceVersion = $null

    try {
        $bucketManifest = (Get-Content -Path $bucketPath -Raw -ErrorAction Stop) | ConvertFrom-Json -ErrorAction Stop
        if ($bucketManifest.version) { $bucketVersion = $bucketManifest.version }
        elseif ($bucketManifest.Version) { $bucketVersion = $bucketManifest.Version }
    }
    catch { return }

    if ([string]::IsNullOrWhiteSpace($bucketVersion)) {
        return
    }

    if (Test-Path -Path $workspacePath) {
        try {
            $workspaceManifest = (Get-Content -Path $workspacePath -Raw -ErrorAction Stop) | ConvertFrom-Json -ErrorAction Stop
            if ($workspaceManifest.version) { $workspaceVersion = $workspaceManifest.version }
            elseif ($workspaceManifest.Version) { $workspaceVersion = $workspaceManifest.Version }
        }
        catch {
            # If the workspace manifest cannot be parsed we will overwrite it with the bucket manifest.
        }
    }

    if (-not (Test-Path -Path $workspacePath)) {
        try {
            Copy-Item -Path $bucketPath -Destination $workspacePath -Force
        }
        catch { }
        return
    }

    $status = Get-Status -Installed $workspaceVersion -Latest $bucketVersion
    if ($status -eq 'UpdateAvailable' -or [string]::IsNullOrWhiteSpace($workspaceVersion)) {
        try {
            Copy-Item -Path $bucketPath -Destination $workspacePath -Force
        }
        catch { }
    }
}

function Get-ManagerInstalledVersion {
    param(
        [string] $ManagerKey,
        [string] $PackageId
    )

    switch ($ManagerKey) {
        'winget' { return Get-WingetInstalledVersion -PackageId $PackageId }
        'choco' { return Get-ChocoInstalledVersion -PackageId $PackageId }
        'chocolatey' { return Get-ChocoInstalledVersion -PackageId $PackageId }
        'scoop' { return Get-ScoopInstalledVersion -PackageId $PackageId }
        default { return $null }
    }
}

function Get-ManagerAvailableVersion {
    param(
        [string] $ManagerKey,
        [string] $PackageId
    )

    switch ($ManagerKey) {
        'winget' { return Get-WingetAvailableVersion -PackageId $PackageId }
        'choco' { return Get-ChocoAvailableVersion -PackageId $PackageId }
        'chocolatey' { return Get-ChocoAvailableVersion -PackageId $PackageId }
        'scoop' { return Get-ScoopAvailableVersion -PackageId $PackageId }
        default { return $null }
    }
}

function Invoke-ManagerUpdate {
    param(
        [string] $ManagerKey,
        [string] $PackageId,
        [string] $TargetVersion,
        [string] $InstalledVersion
    )

    $exe = Resolve-ManagerExecutable -Key $ManagerKey
    $hasTarget = -not [string]::IsNullOrWhiteSpace($TargetVersion)

    $logs = [System.Collections.Generic.List[string]]::new()
    $errors = [System.Collections.Generic.List[string]]::new()

    $normalizedInstalled = Normalize-VersionString -Value $InstalledVersion
    $normalizedTarget = Normalize-VersionString -Value $TargetVersion

    if ($ManagerKey -eq 'scoop' -and $hasTarget -and -not [string]::IsNullOrWhiteSpace($normalizedInstalled) -and -not [string]::IsNullOrWhiteSpace($normalizedTarget) -and ($normalizedInstalled -ne $normalizedTarget)) {
        $uninstallArgs = @('uninstall', $PackageId)
        $uninstallOutput = & $exe @uninstallArgs 2>&1
        $uninstallExit = $LASTEXITCODE

        foreach ($entry in @($uninstallOutput)) {
            if ($null -eq $entry) {
                continue
            }

            $message = [string]$entry
            if ([string]::IsNullOrWhiteSpace($message)) {
                continue
            }

            if ($entry -is [System.Management.Automation.ErrorRecord]) {
                [void]$errors.Add($message)
            }
            else {
                [void]$logs.Add($message)
            }
        }

        if ($uninstallExit -ne 0) {
            $summary = "Uninstall command exited with code $uninstallExit."
            return [pscustomobject]@{
                Attempted  = $true
                ExitCode   = $uninstallExit
                Logs       = $logs.ToArray()
                Errors     = $errors.ToArray()
                Executable = $exe
                Arguments  = $uninstallArgs
                Summary    = $summary
            }
        }
    }

    $arguments = switch ($ManagerKey) {
        'winget' {
            if ($hasTarget) {
                @('install', '--id', $PackageId, '-e', '--version', $TargetVersion, '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity', '--force')
            }
            else {
                @('upgrade', '--id', $PackageId, '-e', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
            }
        }
        'choco' {
            $args = @('upgrade', $PackageId, '-y', '--no-progress')
            if ($hasTarget) {
                $args += @('--version', $TargetVersion, '--allow-downgrade')
            }

            $args
        }
        'chocolatey' {
            $args = @('upgrade', $PackageId, '-y', '--no-progress')
            if ($hasTarget) {
                $args += @('--version', $TargetVersion, '--allow-downgrade')
            }

            $args
        }
        'scoop' {
            if ($hasTarget) {
                @('install', "${PackageId}@${TargetVersion}")
            }
            else {
                @('update', $PackageId)
            }
        }
        default { throw "Unsupported package manager '$ManagerKey' for update." }
    }

    $rawOutput = & $exe @arguments 2>&1
    $exitCode = $LASTEXITCODE

    foreach ($entry in @($rawOutput)) {
        if ($null -eq $entry) {
            continue
        }

        $message = [string]$entry
        if ([string]::IsNullOrWhiteSpace($message)) {
            continue
        }

        if ($entry -is [System.Management.Automation.ErrorRecord]) {
            [void]$errors.Add($message)
        }
        else {
            [void]$logs.Add($message)
        }
    }

    $summary = if ($exitCode -eq 0) {
        if ($hasTarget) {
            "Update command completed for version $TargetVersion."
        }
        else {
            'Update command completed.'
        }
    }
    else {
        "Update command exited with code $exitCode."
    }

    return [pscustomobject]@{
        Attempted  = $true
        ExitCode   = $exitCode
        Logs       = $logs.ToArray()
        Errors     = $errors.ToArray()
        Executable = $exe
        Arguments  = $arguments
        Summary    = $summary
    }
}

if ([string]::IsNullOrWhiteSpace($PackageId)) {
    throw 'PackageId must be provided.'
}

if ([string]::IsNullOrWhiteSpace($Manager)) {
    throw 'Manager must be provided.'
}

if ([string]::IsNullOrWhiteSpace($DisplayName)) {
    $DisplayName = $PackageId
}

$normalizedManager = $Manager.Trim()
$managerKey = $normalizedManager.ToLowerInvariant()

switch ($managerKey) {
    'winget' { }
    'choco' { }
    'chocolatey' { $managerKey = 'choco' }
    'scoop' { }
    default { throw "Unsupported package manager '$Manager'." }
}

$needsElevation = $RequiresAdmin.IsPresent -or $managerKey -in @('winget', 'choco')

$installedBefore = $null
$latestBefore = 'Unknown'
$statusBefore = 'Unknown'
$installedAfter = $null
$latestAfter = 'Unknown'
$statusAfter = 'Unknown'
$attempted = $false
$exitCode = 0
$operationSucceeded = $false
$summary = $null
$executionInfo = $null

try {
    if ($needsElevation -and -not $Elevated.IsPresent -and -not (Test-TidyAdmin)) {
        if ([string]::IsNullOrWhiteSpace($callerScriptPath)) {
            throw 'Unable to determine script path for elevation.'
        }

        $result = Request-TidyElevation -ScriptPath $callerScriptPath -Manager $normalizedManager -PackageId $PackageId -DisplayName $DisplayName -IncludeRequiresAdmin ($RequiresAdmin.IsPresent) -TargetVersion $targetVersionValue
        $script:ResultPayload = $result
        $script:OperationSucceeded = [bool]($result.succeeded)

        $resultOutput = $result.output
        if ($resultOutput) {
            foreach ($line in @($resultOutput)) {
                if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
                    Write-TidyOutput -Message ([string]$line)
                }
            }
        }

        $resultErrors = $result.errors
        if ($resultErrors) {
            foreach ($line in @($resultErrors)) {
                if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
                    Write-TidyError -Message ([string]$line)
                }
            }
        }

        Save-TidyResult
        $result | ConvertTo-Json -Depth 6
        return
    }

    Write-TidyLog -Level Information -Message "Updating '$DisplayName' using manager '$normalizedManager'."

    $forceTargetVersion = -not [string]::IsNullOrWhiteSpace($targetVersionValue)
    if ($forceTargetVersion) {
        Write-TidyLog -Level Information -Message "Requested target version: '$targetVersionValue'."
    }

    $installedBefore = Get-ManagerInstalledVersion -ManagerKey $managerKey -PackageId $PackageId

    $latestBeforeRaw = Get-ManagerAvailableVersion -ManagerKey $managerKey -PackageId $PackageId
    if (-not $latestBeforeRaw -and $installedBefore) {
        $latestBeforeRaw = $installedBefore
    }
    if ([string]::IsNullOrWhiteSpace($latestBeforeRaw)) {
        $latestBefore = 'Unknown'
    }
    else {
        $latestBefore = $latestBeforeRaw
    }

    $statusComparisonBefore = if ($forceTargetVersion) { $targetVersionValue } else { $latestBefore }
    if ([string]::IsNullOrWhiteSpace($statusComparisonBefore)) {
        $statusComparisonBefore = $latestBefore
    }

    $statusBefore = Get-Status -Installed $installedBefore -Latest $statusComparisonBefore

    if ($forceTargetVersion) {
        $statusBefore = 'UpdateAvailable'
    }

    if ($statusBefore -eq 'UpdateAvailable') {
        if ($managerKey -eq 'scoop' -and -not $forceTargetVersion) {
            Reset-ScoopWorkspaceManifestIfOutdated -PackageId $PackageId -LatestVersion $latestBefore
        }
        $attempted = $true
    $executionInfo = Invoke-ManagerUpdate -ManagerKey $managerKey -PackageId $PackageId -TargetVersion $targetVersionValue -InstalledVersion $installedBefore
        $exitCode = $executionInfo.ExitCode

        foreach ($line in @($executionInfo.Logs)) {
            Write-TidyOutput -Message $line
        }

        foreach ($line in @($executionInfo.Errors)) {
            Write-TidyError -Message $line
        }

        if (-not [string]::IsNullOrWhiteSpace($executionInfo.Summary)) {
            $summary = $executionInfo.Summary
        }

        $operationSucceeded = ($exitCode -eq 0) -and ($script:TidyErrorLines.Count -eq 0)
    }
    elseif ($statusBefore -eq 'UpToDate') {
        $summary = "Package '$DisplayName' is already up to date."
        $operationSucceeded = $true
    }
    elseif ($statusBefore -eq 'NotInstalled') {
        $summary = "Package '$DisplayName' is not installed."
        $operationSucceeded = $true
    }
    else {
        $summary = "Unable to determine update state for '$DisplayName'."
        $operationSucceeded = $false
    }

    $installedAfter = Get-ManagerInstalledVersion -ManagerKey $managerKey -PackageId $PackageId
    $latestAfterRaw = Get-ManagerAvailableVersion -ManagerKey $managerKey -PackageId $PackageId
    if (-not $latestAfterRaw -and $installedAfter) {
        $latestAfterRaw = $installedAfter
    }
    if ([string]::IsNullOrWhiteSpace($latestAfterRaw)) {
        $latestAfter = 'Unknown'
    }
    else {
        $latestAfter = $latestAfterRaw
    }

    $statusComparisonAfter = if ($forceTargetVersion) { $targetVersionValue } else { $latestAfter }
    if ([string]::IsNullOrWhiteSpace($statusComparisonAfter)) {
        $statusComparisonAfter = $latestAfter
    }

    $statusAfter = Get-Status -Installed $installedAfter -Latest $statusComparisonAfter

    if ($attempted) {
        if ($statusAfter -eq 'UpToDate' -and $exitCode -eq 0 -and ($script:TidyErrorLines.Count -eq 0)) {
            $operationSucceeded = $true
            if ([string]::IsNullOrWhiteSpace($summary)) {
                $summary = "Package '$DisplayName' updated successfully."
            }
        }
        elseif ($statusAfter -eq 'UpToDate' -and $exitCode -ne 0) {
            $summary = "Package '$DisplayName' appears updated but command returned exit code $exitCode."
        }
        elseif ($statusAfter -eq 'UpdateAvailable') {
            $operationSucceeded = $false
            if ([string]::IsNullOrWhiteSpace($summary)) {
                $summary = "Package '$DisplayName' still reports an available update."
            }
        }
    }

    if ($operationSucceeded -and $forceTargetVersion -and -not [string]::IsNullOrWhiteSpace($targetVersionValue)) {
        if ([string]::IsNullOrWhiteSpace($summary) -or $summary -eq "Update command completed." -or $summary -eq "Package '$DisplayName' updated successfully." -or $summary -eq "Update completed for '$DisplayName'." -or $summary -eq "Update command completed for version $targetVersionValue.") {
            $summary = "Package '$DisplayName' updated to version $targetVersionValue."
        }
    }

    if ([string]::IsNullOrWhiteSpace($summary)) {
        if ($operationSucceeded) {
            if ($forceTargetVersion -and -not [string]::IsNullOrWhiteSpace($targetVersionValue)) {
                $summary = "Update completed for '$DisplayName' (version $targetVersionValue)."
            }
            else {
                $summary = "Update completed for '$DisplayName'."
            }
        }
        else {
            $summary = "Update failed for '$DisplayName'."
        }
    }

    Write-TidyOutput -Message $summary

    $installedResult = if ([string]::IsNullOrWhiteSpace($installedAfter)) { $installedBefore } else { $installedAfter }
    if ([string]::IsNullOrWhiteSpace($installedResult)) { $installedResult = $null }

    $script:ResultPayload = [pscustomobject]@{
        operation        = 'update'
        manager          = $normalizedManager
        managerKey       = $managerKey
        packageId        = $PackageId
        displayName      = $DisplayName
        requiresAdmin    = $needsElevation
        statusBefore     = $statusBefore
        statusAfter      = $statusAfter
        installedVersion = $installedResult
        latestVersion    = $latestAfter
        updateAttempted  = [bool]$attempted
        exitCode         = [int]$exitCode
        succeeded        = [bool]($operationSucceeded -and ($script:TidyErrorLines.Count -eq 0))
        requestedVersion = $targetVersionValue
        summary          = $summary
        executable       = if ($attempted -and $executionInfo) { $executionInfo.Executable } else { $null }
        arguments        = if ($attempted -and $executionInfo) { $executionInfo.Arguments } else { @() }
        output           = $script:TidyOutputLines
        errors           = $script:TidyErrorLines
    }

    $script:OperationSucceeded = $script:ResultPayload.succeeded
}
catch {
    $message = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($message)) {
        $message = $_.ToString()
    }

    Write-TidyError -Message $message

    if (-not $script:ResultPayload) {
        $script:ResultPayload = [pscustomobject]@{
            operation        = 'update'
            manager          = $normalizedManager
            managerKey       = $managerKey
            packageId        = $PackageId
            displayName      = $DisplayName
            requiresAdmin    = $needsElevation
            statusBefore     = $statusBefore
            statusAfter      = 'Unknown'
            installedVersion = $installedBefore
            latestVersion    = $latestBefore
            updateAttempted  = [bool]$attempted
            exitCode         = [int]$exitCode
            succeeded        = $false
            requestedVersion = $targetVersionValue
            summary          = $message
            executable       = if ($executionInfo) { $executionInfo.Executable } else { $null }
            arguments        = if ($executionInfo) { $executionInfo.Arguments } else { @() }
            output           = $script:TidyOutputLines
            errors           = $script:TidyErrorLines
        }
    }

    $script:OperationSucceeded = $false
}
finally {
    Save-TidyResult
}

$script:ResultPayload | ConvertTo-Json -Depth 6
