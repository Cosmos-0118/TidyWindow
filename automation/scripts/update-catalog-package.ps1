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

$script:CommandPathCache = @{}
$script:AnsiEscapeRegex = [System.Text.RegularExpressions.Regex]::new('\x1B\[[0-9;]*[A-Za-z]', [System.Text.RegularExpressions.RegexOptions]::Compiled)
$script:WhitespaceCollapseRegex = [System.Text.RegularExpressions.Regex]::new('\s{2,}', [System.Text.RegularExpressions.RegexOptions]::Compiled)

function Get-CachedCommandPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CommandName
    )

    if ([string]::IsNullOrWhiteSpace($CommandName)) {
        return $null
    }

    $key = $CommandName.ToLowerInvariant()
    if ($script:CommandPathCache.ContainsKey($key)) {
        $value = $script:CommandPathCache[$key]
        if ([string]::IsNullOrWhiteSpace([string]$value)) {
            return $null
        }

        return [string]$value
    }

    $resolved = Get-Command -Name $CommandName -ErrorAction SilentlyContinue
    $path = if ($resolved) {
        if (-not [string]::IsNullOrWhiteSpace($resolved.Source)) { $resolved.Source } else { $CommandName }
    } else {
        $null
    }

    $script:CommandPathCache[$key] = $path
    return $path
}

function Set-CachedCommandPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CommandName,
        [Parameter()]
        [string] $CommandPath
    )

    if ([string]::IsNullOrWhiteSpace($CommandName)) {
        return
    }

    $key = $CommandName.ToLowerInvariant()
    $script:CommandPathCache[$key] = $CommandPath
}

function Remove-TidyAnsiSequences {
    param(
        [Parameter()]
        [AllowNull()]
        [string] $Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $Text
    }

    return $script:AnsiEscapeRegex.Replace($Text, '').Replace("`r", '').Trim()
}

function Get-TidyScoopRootCandidates {
    $roots = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    $add = {
        param([string] $Value)

        if ([string]::IsNullOrWhiteSpace($Value)) {
            return
        }

        $trimmed = $Value.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            return
        }

        try {
            $normalized = [System.IO.Path]::GetFullPath($trimmed)
        }
        catch {
            $normalized = $trimmed
        }

        if ($seen.Add($normalized)) {
            [void]$roots.Add($normalized)
        }
    }

    foreach ($candidate in @($env:SCOOP, $env:SCOOP_GLOBAL)) {
        & $add $candidate
    }

    $programData = [Environment]::GetFolderPath('CommonApplicationData')
    if (-not [string]::IsNullOrWhiteSpace($programData)) {
        & $add (Join-Path -Path $programData -ChildPath 'scoop')
    }

    $userProfile = [Environment]::GetFolderPath('UserProfile')
    if (-not [string]::IsNullOrWhiteSpace($userProfile)) {
        & $add (Join-Path -Path $userProfile -ChildPath 'scoop')
    }

    return $roots
}

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
}
else {
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
        Success   = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
        Output    = $script:TidyOutputLines
        Errors    = $script:TidyErrorLines
        Result    = $script:ResultPayload
        Attempted = if ($script:ResultPayload -and $script:ResultPayload.PSObject.Properties.Match('attempted')) { [bool]$script:ResultPayload.attempted } else { $false }
    }

    $json = $payload | ConvertTo-Json -Depth 6
    Set-Content -Path $ResultPath -Value $json -Encoding UTF8
}

function Test-TidyAdmin {
    return [bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-TidyPowerShellExecutable {
    if ($PSVersionTable.PSEdition -eq 'Core') {
        $pwshPath = Get-CachedCommandPath -CommandName 'pwsh'
        if ($pwshPath) {
            return $pwshPath
        }
    }

    $legacyPath = Get-CachedCommandPath -CommandName 'powershell.exe'
    if ($legacyPath) {
        return $legacyPath
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
            $path = Get-CachedCommandPath -CommandName 'winget'
            if (-not $path) { throw 'winget CLI was not found on this machine.' }
            return $path
        }
        'choco' {
            $path = Get-CachedCommandPath -CommandName 'choco'
            if (-not $path) { throw 'Chocolatey (choco) CLI was not found on this machine.' }
            return $path
        }
        'chocolatey' {
            return Resolve-ManagerExecutable -Key 'choco'
        }
        'scoop' {
            $path = Get-CachedCommandPath -CommandName 'scoop'
            if (-not $path) { throw 'Scoop CLI was not found on this machine.' }
            return $path
        }
        default {
            throw "Unsupported package manager '$Key'."
        }
    }
}

function Select-PreferredVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Candidates
    )

    if (-not $Candidates) {
        return $null
    }

    $unique = [System.Collections.Generic.List[pscustomobject]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $trimmed = $candidate.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        if ($seen.Add($trimmed)) {
            $normalized = Normalize-VersionString -Value $trimmed
            $unique.Add([pscustomobject]@{
                    Original   = $trimmed
                    Normalized = $normalized
                })
        }
    }

    if ($unique.Count -eq 0) {
        return $null
    }

    $numeric = [System.Collections.Generic.List[pscustomobject]]::new()
    foreach ($entry in $unique) {
        if ([string]::IsNullOrWhiteSpace($entry.Normalized)) {
            continue
        }

        $parsed = $null
        if ([version]::TryParse($entry.Normalized, [ref]$parsed)) {
            $numeric.Add([pscustomobject]@{
                    Original = $entry.Original
                    Version  = $parsed
                })
        }
    }

    if ($numeric.Count -gt 0) {
        $best = $numeric | Sort-Object Version -Descending | Select-Object -First 1
        return $best.Original
    }

    return ($unique | Select-Object -First 1).Original
}

function Get-WingetAvailableVersion {
    param([string] $PackageId)

    $exe = Get-CachedCommandPath -CommandName 'winget'
    if (-not $exe) { return $null }
    $versions = [System.Collections.Generic.List[string]]::new()

    try {
        $showOutput = & $exe 'show' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null
        if ($LASTEXITCODE -eq 0 -and $showOutput) {
            foreach ($line in @($showOutput)) {
                if ($null -eq $line) { continue }

                $clean = Remove-TidyAnsiSequences -Text [string]$line
                if ([string]::IsNullOrWhiteSpace($clean)) { continue }

                if ($clean -match '^\s*(Available Version|Latest Version|Version)\s*:\s*(.+)$') {
                    $versions.Add($matches[2].Trim())
                }
            }

            if ($versions.Count -gt 0) {
                return Select-PreferredVersion -Candidates $versions.ToArray()
            }
        }
    }
    catch {
        # Ignore failures on primary show command and continue.
    }

    try {
        $versionsOutput = & $exe 'show' '--id' $PackageId '-e' '--versions' '--disable-interactivity' '--accept-source-agreements' 2>$null
        if ($LASTEXITCODE -eq 0 -and $versionsOutput) {
            foreach ($line in @($versionsOutput)) {
                if ($null -eq $line) { continue }

                $clean = Remove-TidyAnsiSequences -Text [string]$line
                if ([string]::IsNullOrWhiteSpace($clean)) { continue }
                if ($clean -match '^(?i:found\s)') { continue }
                if ($clean -match '^(?i:version)$') { continue }
                if ($clean -match '^[-\s]+$') { continue }

                $match = [Regex]::Match($clean, '^([0-9][0-9A-Za-z\._\-+]*)')
                if ($match.Success) {
                    $versions.Add($match.Groups[1].Value.Trim())
                }
            }

            if ($versions.Count -gt 0) {
                return Select-PreferredVersion -Candidates $versions.ToArray()
            }
        }
    }
    catch {
        # Continue to additional fallbacks.
    }

    try {
        $searchOutput = & $exe 'search' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null
        if ($LASTEXITCODE -eq 0 -and $searchOutput) {
            foreach ($line in @($searchOutput)) {
                if ($null -eq $line) { continue }

                $clean = Remove-TidyAnsiSequences -Text [string]$line
                if ([string]::IsNullOrWhiteSpace($clean)) { continue }
                if ($clean -match '^(?i)\s*Name\s+Id\s+Version') { continue }
                if ($clean -match '^-{3,}') { continue }

                $trimmed = $clean.Trim()
                if ($trimmed -match '^(?<name>.+?)\s+' + [Regex]::Escape($PackageId) + '\s+(?<version>[^\s]+)') {
                    $versions.Add($matches['version'].Trim())
                }
            }

            if ($versions.Count -gt 0) {
                return Select-PreferredVersion -Candidates $versions.ToArray()
            }
        }
    }
    catch {
        # No additional fallback available.
    }

    return $null
}

function Get-ChocoAvailableVersion {
    param([string] $PackageId)

    $exe = Get-CachedCommandPath -CommandName 'choco'
    if (-not $exe) { return $null }
    $versions = [System.Collections.Generic.List[string]]::new()
    $pipePattern = '^(?i)' + [Regex]::Escape($PackageId) + '\|([^|]+)'
    $spacePattern = '^(?i)' + [Regex]::Escape($PackageId) + '\s+([0-9][^\s]*)'

    try {
        $output = & $exe 'search' $PackageId '--exact' '--limit-output' '--no-color' '--no-progress' 2>$null
        if ($LASTEXITCODE -eq 0 -and $output) {
            foreach ($line in @($output)) {
                if ($null -eq $line) { continue }

                $trimmed = (Remove-TidyAnsiSequences -Text [string]$line)
                if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
                if ($trimmed -match '^(?i)Chocolatey\s') { continue }
                if ($trimmed -match '^\d+\s+packages?\s+(?:found|installed).*') { continue }

                if ($trimmed -match $pipePattern) {
                    $versions.Add($matches[1].Trim())
                    continue
                }

                if ($trimmed -match $spacePattern) {
                    $versions.Add($matches[1].Trim())
                }
            }
        }
    }
    catch { }

    if ($versions.Count -eq 0) {
        try {
            $output = & $exe 'search' $PackageId '--exact' '--all-versions' '--limit-output' '--no-color' '--no-progress' 2>$null
            if ($LASTEXITCODE -eq 0 -and $output) {
                foreach ($line in @($output)) {
                    if ($null -eq $line) { continue }

                    $trimmed = (Remove-TidyAnsiSequences -Text [string]$line)
                    if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
                    if ($trimmed -match '^(?i)Chocolatey\s') { continue }
                    if ($trimmed -match '^\d+\s+packages?\s+(?:found|installed).*') { continue }

                    if ($trimmed -match $pipePattern) {
                        $versions.Add($matches[1].Trim())
                        continue
                    }

                    if ($trimmed -match $spacePattern) {
                        $versions.Add($matches[1].Trim())
                    }
                }
            }
        }
        catch { }
    }

    if ($versions.Count -eq 0) {
        try {
            $infoOutput = & $exe 'info' $PackageId '--no-color' '--no-progress' 2>$null
            if ($LASTEXITCODE -eq 0 -and $infoOutput) {
                foreach ($line in @($infoOutput)) {
                    if ($null -eq $line) { continue }

                    $clean = Remove-TidyAnsiSequences -Text [string]$line
                    if ([string]::IsNullOrWhiteSpace($clean)) { continue }

                    if ($clean -match '^\s*Latest Version\s*:\s*(.+)$') {
                        $versions.Add($matches[1].Trim())
                        continue
                    }

                    if ($clean -match '^\s*Version\s*:\s*(.+)$') {
                        $versions.Add($matches[1].Trim())
                        continue
                    }

                    if ($clean -match '^\s*Available Versions?\s*:\s*(.+)$') {
                        $versions.Add($matches[1].Trim())
                        continue
                    }
                }
            }
        }
        catch { }
    }

    if ($versions.Count -gt 0) {
        return Select-PreferredVersion -Candidates $versions.ToArray()
    }

    return $null
}

function Get-ScoopManifestPaths {
    param([string] $PackageId)

    $root = $null
    $candidates = Get-TidyScoopRootCandidates
    foreach ($candidate in $candidates) {
        if (-not $root) {
            $root = $candidate
        }

        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -Path $candidate)) {
            $root = $candidate
            break
        }
    }

    if (-not $root) {
        $root = $env:SCOOP
    }

    if ([string]::IsNullOrWhiteSpace($root)) {
        $userProfile = [Environment]::GetFolderPath('UserProfile')
        if (-not [string]::IsNullOrWhiteSpace($userProfile)) {
            $root = Join-Path -Path $userProfile -ChildPath 'scoop'
        }
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

function Get-ScoopAvailableVersion {
    param([string] $PackageId)

    $exe = Get-CachedCommandPath -CommandName 'scoop'
    if (-not $exe) { return $null }
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
            $clean = Remove-TidyAnsiSequences -Text [string]$line
            if ([string]::IsNullOrWhiteSpace($clean)) { continue }
            if ($clean -match '^\s*Latest Version\s*:\s*(.+)$') { return $matches[1].Trim() }
            if ($clean -match '^\s*Version\s*:\s*(.+)$') { return $matches[1].Trim() }
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

    if ([string]::IsNullOrWhiteSpace($ManagerKey) -or [string]::IsNullOrWhiteSpace($PackageId)) {
        return $null
    }

    try {
        return Get-TidyInstalledPackageVersion -Manager $ManagerKey -PackageId $PackageId
    }
    catch {
        return $null
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

    if ($exitCode -eq 0) {
        if ($hasTarget) {
            $summary = "Update command completed for version $TargetVersion."
        }
        else {
            $summary = 'Update command completed.'
        }
    }
    else {
        $summary = "Update command exited with code $exitCode."
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
        $result | ConvertTo-Json -Depth 6 -Compress
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
            $canTreatAsSuccess = ($exitCode -eq 0) -and ($script:TidyErrorLines.Count -eq 0)

            if ($canTreatAsSuccess) {
                $retryCount = 0
                $maxRetries = 3

                while ($retryCount -lt $maxRetries -and $statusAfter -eq 'UpdateAvailable') {
                    Start-Sleep -Seconds 2

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
                    $retryCount++
                }

                if ($statusAfter -eq 'UpToDate') {
                    $operationSucceeded = $true
                    if ([string]::IsNullOrWhiteSpace($summary) -or $summary -eq 'Update command completed.') {
                        $summary = "Package '$DisplayName' updated successfully."
                    }
                }
                else {
                    $operationSucceeded = $true
                    if ([string]::IsNullOrWhiteSpace($summary) -or $summary -eq 'Update command completed.') {
                        $summary = "Update command completed for '$DisplayName', but the package still reports an available update. A restart or additional verification may be required."
                    }
                    else {
                        $summary = "$summary (Package still reports an available update; try restarting or refreshing the inventory.)"
                    }
                }
            }
            else {
                $operationSucceeded = $false
                if ([string]::IsNullOrWhiteSpace($summary)) {
                    $summary = "Package '$DisplayName' still reports an available update."
                }
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
        attempted        = [bool]$attempted
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
            attempted        = [bool]$attempted
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

$script:ResultPayload | ConvertTo-Json -Depth 6 -Compress
