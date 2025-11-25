function Get-OblivionAnchorReasonSet {
    return @('InstallRoot', 'Hint', 'RegistryInstallLocation', 'PackageFamilyData', 'WindowsAppsPayload')
}

function Resolve-OblivionFullPath {
    [CmdletBinding()]
    param([string] $Path)

    $resolved = Resolve-TidyPath -Path $Path
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return $null
    }

    return $resolved.TrimEnd('\','/')
}

function Get-OblivionBlockedRootSet {
    [CmdletBinding()]
    param()

    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $candidates = @()
    if ($env:SystemRoot) { $candidates += $env:SystemRoot }
    if ($env:WINDIR) { $candidates += $env:WINDIR }
    $candidates += 'C:\Windows'
    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles 'System32')
        $candidates += (Join-Path $env:ProgramFiles 'Common Files')
        $candidates += (Join-Path $env:ProgramFiles 'WindowsApps')
    }
    $pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ($pf86) {
        $candidates += $pf86
        $candidates += (Join-Path $pf86 'System32')
        $candidates += (Join-Path $pf86 'Common Files')
    }

    foreach ($candidate in $candidates) {
        $normalized = Resolve-OblivionFullPath -Path $candidate
        if ($normalized) { $null = $set.Add($normalized) }
    }

    return $set
}

function Get-OblivionTrustedRoots {
    [CmdletBinding()]
    param([psobject] $App)

    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if (-not $App) { return $set }

    $addRoot = {
        param([string] $Value)

        if ([string]::IsNullOrWhiteSpace($Value)) { return }
        $normalized = Resolve-OblivionFullPath -Path $Value
        if (-not $normalized) { return }

        if (-not (Test-Path -LiteralPath $normalized)) {
            try {
                $parent = Split-Path -Path $normalized -Parent -ErrorAction Stop
                if ($parent) { $normalized = $parent.TrimEnd('\','/') }
            }
            catch { }
        }

        if ($normalized) { $null = $set.Add($normalized) }
    }

    if ($App.PSObject.Properties['installRoot']) {
        foreach ($root in @($App.installRoot)) { & $addRoot $root }
    }

    if ($App.PSObject.Properties['artifactHints']) {
        foreach ($hint in @($App.artifactHints)) { & $addRoot $hint }
    }

    if (
        $App.PSObject.Properties['registry'] -and
        $App.registry -and
        $App.registry.PSObject.Properties['installLocation'] -and
        $App.registry.installLocation
    ) {
        & $addRoot $App.registry.installLocation
    }

    if ($App.PSObject.Properties['packageFamilyName'] -and $App.packageFamilyName) {
        $pkg = $App.packageFamilyName.Trim()
        if ($pkg) {
            if ($env:LOCALAPPDATA) {
                $packageData = Join-Path -Path $env:LOCALAPPDATA -ChildPath (Join-Path -Path 'Packages' -ChildPath $pkg)
                & $addRoot $packageData
            }

            if ($env:ProgramFiles) {
                $windowsApps = Join-Path -Path $env:ProgramFiles -ChildPath (Join-Path -Path 'WindowsApps' -ChildPath $pkg)
                & $addRoot $windowsApps
            }
        }
    }

    return $set
}

function Test-OblivionPathUnderRoots {
    [CmdletBinding()]
    param(
        [string] $Path,
        [System.Collections.Generic.HashSet[string]] $Roots
    )

    if (-not $Path -or -not $Roots -or $Roots.Count -eq 0) { return $false }
    $normalized = Resolve-OblivionFullPath -Path $Path
    if (-not $normalized) { return $false }

    foreach ($root in $Roots) {
        if ($root -and $normalized.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Test-OblivionPathBlocked {
    [CmdletBinding()]
    param(
        [string] $Path,
        [System.Collections.Generic.HashSet[string]] $BlockedRoots
    )

    if (-not $Path -or -not $BlockedRoots -or $BlockedRoots.Count -eq 0) { return $false }

    $normalized = Resolve-OblivionFullPath -Path $Path
    if (-not $normalized) { return $false }

    foreach ($blocked in $BlockedRoots) {
        if ($blocked -and $normalized.StartsWith($blocked, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Find-TidyRelatedProcesses {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $App,
        [psobject[]] $Snapshot,
        [int] $MaxMatches = 25
    )

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    if (-not $Snapshot) {
        return $results
    }

    $maxMatches = [math]::Max(0, $MaxMatches)
    $trustedRoots = Get-OblivionTrustedRoots -App $App
    $blockedRoots = Get-OblivionBlockedRootSet

    $hintPathSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $hintNameSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if ($App.PSObject.Properties['processHints']) {
        foreach ($hint in @($App.processHints)) {
            if ([string]::IsNullOrWhiteSpace($hint)) { continue }
            if ($hint -match '[\\/]' -or $hint.Contains(':')) {
                $normalizedHint = Resolve-OblivionFullPath -Path $hint
                if ($normalizedHint) { $null = $hintPathSet.Add($normalizedHint) }
            }
            else {
                $null = $hintNameSet.Add($hint.ToLowerInvariant())
            }
        }
    }

    foreach ($proc in $Snapshot) {
        if ($results.Count -ge $maxMatches) { break }
        if (-not $proc) { continue }

        $procPath = $null
        if ($proc.PSObject.Properties['path']) {
            $procPath = $proc.path
        }

        $normalizedPath = if ($procPath) { Resolve-OblivionFullPath -Path $procPath } else { $null }
        if ($normalizedPath -and (Test-OblivionPathBlocked -Path $normalizedPath -BlockedRoots $blockedRoots)) {
            continue
        }

        $matched = $false
        if ($normalizedPath -and ($trustedRoots.Count -gt 0) -and (Test-OblivionPathUnderRoots -Path $normalizedPath -Roots $trustedRoots)) {
            $matched = $true
        }
        elseif ($normalizedPath -and $hintPathSet.Count -gt 0) {
            foreach ($hintPath in $hintPathSet) {
                if ($normalizedPath.StartsWith($hintPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $matched = $true
                    break
                }
            }
        }

        if (-not $matched -and $hintNameSet.Count -gt 0 -and $proc.PSObject.Properties['name']) {
            $procName = $proc.name
            if (-not [string]::IsNullOrWhiteSpace($procName)) {
                if ($hintNameSet.Contains($procName.ToLowerInvariant())) {
                    $matched = $true
                }
            }
        }

        if ($matched) {
            $results.Add($proc) | Out-Null
        }
    }

    return $results
}

function Stop-TidyProcesses {
    [CmdletBinding()]
    param(
        [psobject[]] $Processes,
        [switch] $DryRun,
        [switch] $Force
    )

    if (-not $Processes -or $Processes.Count -eq 0) {
        return 0
    }

    $stopped = 0
    foreach ($proc in $Processes) {
        if ($DryRun) {
            $stopped++
            continue
        }

        try {
            Stop-Process -Id $proc.id -Force:$Force -ErrorAction Stop
            $stopped++
        }
        catch {
            Write-TidyLog -Level 'Warning' -Message "Failed to stop process $($proc.name) ($($proc.id)): $($_.Exception.Message)"
        }
    }

    return $stopped
}

function Test-OblivionProcessAlive {
    [CmdletBinding()]
    param([int] $ProcessId)

    if ($ProcessId -le 0) {
        return $false
    }

    try {
        $proc = Get-Process -Id $ProcessId -ErrorAction Stop
        return -not $proc.HasExited
    }
    catch {
        return $false
    }
}

function Invoke-OblivionProcessTermination {
    [CmdletBinding()]
    param([psobject] $Process)

    if (-not $Process -or -not $Process.id) {
        return [pscustomobject]@{ success = $true; strategy = 'NoProcess'; error = $null }
    }

    $id = [int]$Process.id
    $strategies = @(
        @{ name = 'CloseMainWindow'; action = {
                $target = Get-Process -Id $id -ErrorAction Stop
                if ($target.HasExited) { return $true }
                if ($target.MainWindowHandle -eq 0) { return $false }
                if (-not $target.CloseMainWindow()) { return $false }
                try { Wait-Process -Id $id -Timeout 5 -ErrorAction SilentlyContinue } catch { }
                return -not (Test-OblivionProcessAlive -ProcessId $id)
            }
        },
        @{ name = 'StopProcess'; action = {
                Stop-Process -Id $id -ErrorAction Stop
                return -not (Test-OblivionProcessAlive -ProcessId $id)
            }
        },
        @{ name = 'StopProcessForce'; action = {
                Stop-Process -Id $id -Force -ErrorAction Stop
                return -not (Test-OblivionProcessAlive -ProcessId $id)
            }
        },
        @{ name = 'TaskKill'; action = {
                $arguments = @('/PID', $id, '/T', '/F')
                $null = & taskkill.exe @arguments 2>$null 1>$null
                if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 128) {
                    throw "taskkill.exe exited with code $LASTEXITCODE"
                }
                return -not (Test-OblivionProcessAlive -ProcessId $id)
            }
        },
        @{ name = 'CimTerminate'; action = {
                $terminate = Invoke-CimMethod -ClassName Win32_Process -MethodName Terminate -Arguments @{ ProcessId = $id } -ErrorAction Stop
                if ($terminate.ReturnValue -ne 0 -and $terminate.ReturnValue -ne 2) {
                    throw "Win32_Process.Terminate returned $($terminate.ReturnValue)"
                }
                return -not (Test-OblivionProcessAlive -ProcessId $id)
            }
        }
    )

    $lastError = $null
    foreach ($strategy in $strategies) {
        try {
            if (& $strategy.action) {
                return [pscustomobject]@{ success = $true; strategy = $strategy.name; error = $null }
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }
    }

    return [pscustomobject]@{ success = $false; strategy = 'Exhausted'; error = $lastError }
}

function Invoke-OblivionProcessSweep {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $App,
        [switch] $DryRun,
        [int] $MaxPasses = 3,
        [int] $WaitSeconds = 3
    )

    $snapshot = Get-TidyProcessSnapshot
    $related = Find-TidyRelatedProcesses -App $App -Snapshot $snapshot -MaxMatches 200

    $detected = if ($related) { $related.Count } else { 0 }
    $resultLog = New-Object 'System.Collections.Generic.List[psobject]'
    $stopped = 0
    $attempts = 0

    if (-not $DryRun -and $detected -gt 0) {
        Assert-TidyAdmin
    }

    if ($DryRun -or $detected -eq 0) {
        return [pscustomobject]@{
            Detected      = $detected
            Stopped       = $detected
            Attempts      = [math]::Max(1, $MaxPasses)
            Remaining     = @()
            AttemptLog    = $resultLog
        }
    }

    $active = @($related)
    while ($active.Count -gt 0 -and $attempts -lt [math]::Max(1, $MaxPasses)) {
        $attempts++
        $nextActive = @()
        foreach ($proc in $active) {
            $termination = Invoke-OblivionProcessTermination -Process $proc
            $record = [pscustomobject]@{
                attempt   = $attempts
                processId = $proc.id
                name      = $proc.name
                success   = $termination.success
                strategy  = $termination.strategy
                error     = $termination.error
            }
            $resultLog.Add($record) | Out-Null

            if ($termination.success) {
                $stopped++
            }
            else {
                if (Test-OblivionProcessAlive -ProcessId $proc.id) {
                    try {
                        $live = Get-Process -Id $proc.id -ErrorAction Stop
                        $path = $proc.path
                        try { $path = $live.Path } catch { }
                        $nextActive += [pscustomobject]@{ id = $live.Id; name = $live.ProcessName; path = $path }
                    }
                    catch {
                        $nextActive += $proc
                    }
                }
            }
        }

        if ($nextActive.Count -eq 0) {
            break
        }

        $active = $nextActive
        if ($attempts -lt $MaxPasses) {
            Start-Sleep -Seconds ([math]::Max(1, $WaitSeconds))
        }
    }

    return [pscustomobject]@{
        Detected   = $detected
        Stopped    = $stopped
        Attempts   = $attempts
        Remaining  = $active
        AttemptLog = $resultLog
    }
}

function Invoke-OblivionArtifactDiscovery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $App,
        [psobject[]] $ProcessSnapshot,
        [int] $MaxProgramFilesMatches = 15
    )

    $pathSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $artifacts = New-Object 'System.Collections.Generic.List[psobject]'
    $anchorReasonSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($reason in Get-OblivionAnchorReasonSet) {
        $null = $anchorReasonSet.Add($reason)
    }

    $trustedAnchorDirectories = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $anchorTokenSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $blockedRootSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    $addBlockedRoot = {
        param([string] $Path)

        if ([string]::IsNullOrWhiteSpace($Path)) { return }
        $resolved = Resolve-TidyPath -Path $Path
        if ([string]::IsNullOrWhiteSpace($resolved)) { return }
        $resolved = $resolved.TrimEnd('\','/')
        if ($resolved) { $null = $blockedRootSet.Add($resolved) }
    }

    & $addBlockedRoot $env:SystemRoot
    & $addBlockedRoot $env:WINDIR
    & $addBlockedRoot 'C:\Windows'
    if ($env:ProgramFiles) {
        & $addBlockedRoot (Join-Path -Path $env:ProgramFiles -ChildPath 'Common Files')
        & $addBlockedRoot (Join-Path -Path $env:ProgramFiles -ChildPath 'WindowsApps')
    }
    $pf86Root = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ($pf86Root) {
        & $addBlockedRoot (Join-Path -Path $pf86Root -ChildPath 'Common Files')
    }

    $normalizeDirectory = {
        param([string] $Path)

        if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
        $resolved = Resolve-TidyPath -Path $Path
        if ([string]::IsNullOrWhiteSpace($resolved)) { return $null }
        $candidate = $resolved.TrimEnd('\','/')
        if (-not $candidate) { return $null }

        try {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $candidate = Split-Path -Path $candidate -Parent -ErrorAction Stop
            }
        }
        catch {
            return $null
        }

        return $candidate.TrimEnd('\','/')
    }

    $isUnderBlockedRoot = {
        param([string] $Path)

        if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
        foreach ($blocked in $blockedRootSet) {
            if ($blocked -and $Path.StartsWith($blocked, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }

        return $false
    }

    $addAnchorTokens = {
        param([string] $Path)

        if ([string]::IsNullOrWhiteSpace($Path)) { return }
        try {
            $leaf = Split-Path -Path $Path -Leaf -ErrorAction Stop
        }
        catch {
            $leaf = $Path
        }

        $normalized = [System.Text.RegularExpressions.Regex]::Replace($leaf, '[^A-Za-z0-9]+', ' ')
        foreach ($token in $normalized.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)) {
            if ($token.Length -lt 3) { continue }
            $null = $anchorTokenSet.Add($token)
        }
    }

    $recordAnchor = {
        param([string] $Path)

        $anchorDir = & $normalizeDirectory $Path
        if (-not $anchorDir) { return }
        $null = $trustedAnchorDirectories.Add($anchorDir)
        & $addAnchorTokens $anchorDir
        try {
            $parent = Split-Path -Path $anchorDir -Parent -ErrorAction Stop
            if ($parent) { & $addAnchorTokens $parent }
        }
        catch { }
    }

    $getAnchorForPath = {
        param([string] $Path)

        if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
        foreach ($anchor in $trustedAnchorDirectories) {
            if ($Path.StartsWith($anchor, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $anchor
            }
        }

        return $null
    }

    $getAnchoredScopes = {
        param([string] $Root)

        if ([string]::IsNullOrWhiteSpace($Root)) { return @() }
        $scopes = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($anchor in $trustedAnchorDirectories) {
            if (-not $anchor.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            $relative = $anchor.Substring($Root.Length).TrimStart('\','/')
            if (-not $relative) { continue }
            $segments = $relative.Split('\', [System.StringSplitOptions]::RemoveEmptyEntries)
            if ($segments.Length -eq 0) { continue }
            $scope = Join-Path -Path $Root -ChildPath $segments[0]
            $null = $scopes.Add($scope)
        }

        return @($scopes)
    }
    foreach ($baseArtifact in @(Get-TidyArtifacts -App $App)) {
        if (-not $baseArtifact) { continue }

        $artifacts.Add($baseArtifact) | Out-Null
        if ($baseArtifact.path) {
            $null = $pathSet.Add($baseArtifact.path)
            if (
                $baseArtifact.metadata -and
                $baseArtifact.metadata.reason -and
                $anchorReasonSet.Contains($baseArtifact.metadata.reason)
            ) {
                & $recordAnchor $baseArtifact.path
            }
        }
    }

    $added = 0
    $details = New-Object 'System.Collections.Generic.List[psobject]'
    $maxMatches = [math]::Max(0, $MaxProgramFilesMatches)

    $addArtifact = {
        param(
            [string] $Kind,
            [string] $Path,
            [string] $Reason,
            [switch] $IsCandidate,
            [string] $SourceAnchor
        )

        if ([string]::IsNullOrWhiteSpace($Path)) { return $null }

        $confidence = if ($IsCandidate) { 'heuristic' } else { 'anchor' }
        $candidate = switch ($Kind) {
            'Registry' { New-TidyRegistryArtifact -KeyPath $Path }
            'Service'  { New-TidyServiceArtifact -ServiceName $Path }
            Default    { New-TidyFileArtifact -Path $Path -Reason $Reason -DefaultSelected:(!$IsCandidate) -Confidence $confidence -SourceAnchor $SourceAnchor }
        }

        if (-not $candidate -or -not $candidate.path) { return $null }
        if ($IsCandidate -and (& $isUnderBlockedRoot $candidate.path)) { return $null }
        if ($pathSet.Contains($candidate.path)) { return $null }

        $null = $pathSet.Add($candidate.path)
        $artifacts.Add($candidate) | Out-Null
        $details.Add([pscustomobject]@{
            path         = $candidate.path
            type         = $candidate.type
            reason       = $Reason
            confidence   = $confidence
            sourceAnchor = $SourceAnchor
        }) | Out-Null
        Set-Variable -Name 'added' -Scope 1 -Value ($added + 1)
        if (-not $IsCandidate -and $anchorReasonSet.Contains($Reason)) {
            & $recordAnchor $candidate.path
        }

        return $candidate
    }

    if (
        $App.PSObject.Properties['registry'] -and
        $App.registry -and
        $App.registry.PSObject -and
        $App.registry.PSObject.Properties['installLocation'] -and
        $App.registry.installLocation
    ) {
        $null = & $addArtifact 'File' $App.registry.installLocation 'RegistryInstallLocation' -SourceAnchor $App.registry.installLocation
    }

    if (
        $App.PSObject.Properties['registry'] -and
        $App.registry -and
        $App.registry.PSObject -and
        $App.registry.PSObject.Properties['displayIcon'] -and
        $App.registry.displayIcon
    ) {
        $displayIcon = $App.registry.displayIcon.Split(',')[0]
        try {
            $parent = Split-Path -Path $displayIcon -Parent -ErrorAction Stop
            $null = & $addArtifact 'File' $parent 'RegistryDisplayIcon' -SourceAnchor $parent
        }
        catch {
            $null = & $addArtifact 'File' $displayIcon 'RegistryDisplayIcon' -SourceAnchor $displayIcon
        }
    }

    if ($App.PSObject.Properties['packageFamilyName'] -and $App.packageFamilyName) {
        $pkg = $App.packageFamilyName.Trim()
        if ($pkg) {
            $localPackages = if ($env:LOCALAPPDATA) { Join-Path -Path $env:LOCALAPPDATA -ChildPath (Join-Path -Path 'Packages' -ChildPath $pkg) } else { $null }
            if ($localPackages) { $null = & $addArtifact 'File' $localPackages 'PackageFamilyData' -SourceAnchor $localPackages }

            $windowsApps = if ($env:ProgramFiles) { Join-Path -Path $env:ProgramFiles -ChildPath (Join-Path -Path 'WindowsApps' -ChildPath $pkg) } else { $null }
            if ($windowsApps) { $null = & $addArtifact 'File' $windowsApps 'WindowsAppsPayload' -SourceAnchor $windowsApps }
        }
    }

    if (-not $ProcessSnapshot) {
        $ProcessSnapshot = Get-TidyProcessSnapshot
    }
    $related = Find-TidyRelatedProcesses -App $App -Snapshot $ProcessSnapshot -MaxMatches 200
    foreach ($proc in $related) {
        if (-not $proc.path) { continue }
        try {
            $parent = Split-Path -Path $proc.path -Parent -ErrorAction Stop
        }
        catch {
            continue
        }

        if (-not $parent) { continue }
        $anchorMatch = & $getAnchorForPath $parent
        if (-not $anchorMatch) { continue }
        $null = & $addArtifact 'File' $parent 'ProcessImageDirectory' -IsCandidate -SourceAnchor $anchorMatch
    }

    $tokens = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $primaryTokens = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    $noiseTokens = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($noise in @('microsoft', 'windows', 'corporation', 'software', 'installer', 'setup', 'update', 'utility', 'helper', 'system', 'apps', 'app', 'tools', 'suite')) {
        $null = $noiseTokens.Add($noise)
    }

    $addTokens = {
        param(
            [string] $Source,
            [System.Collections.Generic.HashSet[string][]] $TargetSets
        )

        if ([string]::IsNullOrWhiteSpace($Source) -or -not $TargetSets -or $TargetSets.Count -eq 0) { return }

        $normalized = [System.Text.RegularExpressions.Regex]::Replace($Source, '[^A-Za-z0-9]+', ' ')
        foreach ($token in $normalized.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)) {
            $trimmed = $token.Trim()
            if ($trimmed.Length -lt 3) { continue }
            if ($noiseTokens.Contains($trimmed)) { continue }
            foreach ($set in $TargetSets) {
                if ($set) { $null = $set.Add($trimmed) }
            }
        }
    }

    & $addTokens $App.name @($tokens, $primaryTokens)

    if ($App.PSObject.Properties['artifactHints']) {
        foreach ($hint in @($App.artifactHints)) {
            if ([string]::IsNullOrWhiteSpace($hint)) { continue }
            try {
                $leaf = Split-Path -Path $hint -Leaf -ErrorAction Stop
            }
            catch {
                $leaf = $hint
            }
            & $addTokens $leaf @($tokens, $primaryTokens)
        }
    }

    if ($App.PSObject.Properties['processHints']) {
        foreach ($hint in @($App.processHints)) {
            if ([string]::IsNullOrWhiteSpace($hint)) { continue }
            try {
                $leaf = Split-Path -Path $hint -Leaf -ErrorAction Stop
            }
            catch {
                $leaf = $hint
            }
            & $addTokens $leaf @($tokens, $primaryTokens)
        }
    }

    if ($App.PSObject.Properties['tags']) {
        foreach ($tag in @($App.tags)) {
            & $addTokens $tag @($tokens)
        }
    }

    & $addTokens $App.publisher @($tokens)
    & $addTokens $App.appId @($tokens)

    $directoryTokens = if ($primaryTokens.Count -gt 0) { $primaryTokens } else { $tokens }
    $heuristicTokens = if ($anchorTokenSet.Count -gt 0) { $anchorTokenSet } else { $directoryTokens }

    $scanDirectories = {
        param(
            [string] $Root,
            [string] $Reason,
            [int] $Budget,
            [System.Collections.Generic.HashSet[string]] $TokenSet,
            [string] $SourceAnchor
        )

        if (
            $Budget -le 0 -or
            -not $TokenSet -or
            $TokenSet.Count -eq 0 -or
            [string]::IsNullOrWhiteSpace($Root) -or
            -not (Test-Path -LiteralPath $Root)
        ) {
            return 0
        }

        $addedLocal = 0
        try {
            $directories = Get-ChildItem -LiteralPath $Root -Directory -ErrorAction Stop
        }
        catch {
            return 0
        }

        foreach ($dir in $directories) {
            if ($addedLocal -ge $Budget) { break }

            foreach ($token in $TokenSet) {
                if ($dir.Name.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $anchorPath = if ($SourceAnchor) { $SourceAnchor } else { $Root }
                    $artifactCandidate = & $addArtifact 'File' $dir.FullName $Reason -IsCandidate -SourceAnchor $anchorPath
                    if ($artifactCandidate) {
                        $addedLocal++
                    }
                    break
                }
            }
        }

        return $addedLocal
    }

    if ($trustedAnchorDirectories.Count -gt 0 -and $heuristicTokens.Count -gt 0 -and $maxMatches -gt 0) {
        $programRoots = @()
        if ($env:ProgramFiles) { $programRoots += $env:ProgramFiles }
        $pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
        if ($pf86) { $programRoots += $pf86 }

        foreach ($root in $programRoots) {
            if ($maxMatches -le 0) { break }
            $scopes = & $getAnchoredScopes $root
            foreach ($scope in $scopes) {
                if ($maxMatches -le 0) { break }
                $budget = [math]::Min(5, $maxMatches)
                $maxMatches -= & $scanDirectories $scope 'ProgramFilesHeuristic' $budget $heuristicTokens $scope
            }
        }
    }

    $userProfile = [Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)
    $localLowPath = $null
    if ($userProfile) {
        $localLowPath = Join-Path $userProfile 'AppData\LocalLow'
    }
    $profileBudgets = @(
        @{ path = $env:LOCALAPPDATA; reason = 'LocalAppDataToken'; budget = 6 },
        @{ path = $env:APPDATA; reason = 'RoamingAppDataToken'; budget = 4 },
        @{ path = $localLowPath; reason = 'LocalLowToken'; budget = 3 }
    )

    foreach ($profile in $profileBudgets) {
        if (-not $profile.path -or -not (Test-Path -LiteralPath $profile.path)) { continue }
        if ($trustedAnchorDirectories.Count -eq 0 -or $heuristicTokens.Count -eq 0) { break }

        $scopes = & $getAnchoredScopes $profile.path
        foreach ($scope in $scopes) {
            if ($profile['budget'] -le 0) { break }
            $consumed = & $scanDirectories $scope $profile.reason $profile['budget'] $heuristicTokens $scope
            $profile['budget'] -= $consumed
        }
    }

    $programDataBudgets = @()
    if ($env:ProgramData) {
        $programDataBudgets += @{ path = (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'); reason = 'StartMenuGroup'; budget = 5 }
        $programDataBudgets += @{ path = (Join-Path $env:ProgramData 'Package Cache'); reason = 'PackageCache'; budget = 4 }
    }

    foreach ($entry in $programDataBudgets) {
        if (-not $entry.path -or -not (Test-Path -LiteralPath $entry.path)) { continue }
        if ($trustedAnchorDirectories.Count -eq 0 -or $heuristicTokens.Count -eq 0) { break }

        $scopes = & $getAnchoredScopes $entry.path
        foreach ($scope in $scopes) {
            if ($entry['budget'] -le 0) { break }
            $consumed = & $scanDirectories $scope $entry.reason $entry['budget'] $heuristicTokens $scope
            $entry['budget'] -= $consumed
        }
    }

    $startMenuRoots = @()
    if ($env:ProgramData) { $startMenuRoots += (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs') }
    if ($env:APPDATA) { $startMenuRoots += (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') }

    $scanStartMenu = {
        param([string] $Root, [int] $Budget)

        if (
            $Budget -le 0 -or
            -not (Test-Path -LiteralPath $Root) -or
            $trustedAnchorDirectories.Count -eq 0
        ) {
            return
        }

        try {
            $shortcuts = Get-ChildItem -LiteralPath $Root -Filter '*.lnk' -Recurse -ErrorAction Stop
        }
        catch {
            return
        }

        $shell = $null
        foreach ($shortcut in $shortcuts) {
            if ($Budget -le 0) { break }

            $matched = $false
            $target = $null
            foreach ($token in $tokens) {
                if ($shortcut.Name.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $matched = $true
                    break
                }
            }

            if (-not $matched -or -not $target) {
                try {
                    if (-not $shell) { $shell = New-Object -ComObject WScript.Shell }
                    $target = $shell.CreateShortcut($shortcut.FullName).TargetPath
                    foreach ($token in $tokens) {
                        if ($target -and $target.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                            $matched = $true
                            break
                        }
                    }
                }
                catch {
                    $target = $null
                    if (-not $matched) { $matched = $false }
                }
            }

            if (-not $matched) { continue }

            $targetParent = $null
            if ($target) {
                try {
                    $targetParent = Split-Path -Path $target -Parent -ErrorAction Stop
                }
                catch {
                    $targetParent = $null
                }
            }

            if (-not $targetParent) { continue }
            $anchorForShortcut = & $getAnchorForPath $targetParent
            if (-not $anchorForShortcut) { continue }

            $null = & $addArtifact 'File' $shortcut.FullName 'StartMenuShortcut' -IsCandidate -SourceAnchor $anchorForShortcut
            $null = & $addArtifact 'File' $targetParent 'StartMenuShortcutTarget' -IsCandidate -SourceAnchor $anchorForShortcut
            $Budget--
        }
    }

    if ($trustedAnchorDirectories.Count -gt 0) {
        foreach ($root in $startMenuRoots) {
            & $scanStartMenu $root 6
        }
    }

    if ($App.PSObject.Properties['serviceHints']) {
        foreach ($svc in @($App.serviceHints)) {
            $null = & $addArtifact 'Service' $svc 'ServiceHint'
        }
    }

    return [pscustomobject]@{
        Artifacts = $artifacts
        AddedCount = $added
        Details = $details
    }
}

