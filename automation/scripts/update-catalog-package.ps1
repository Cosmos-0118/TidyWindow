param(
    [string]$Manager,
    [string]$PackageId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Manager)) {
    throw 'Package manager must be provided.'
}

if ([string]::IsNullOrWhiteSpace($PackageId)) {
    throw 'Package identifier must be provided.'
}

$normalizedManager = $Manager.Trim()
$managerKey = $normalizedManager.ToLowerInvariant()

$wingetCommand = Get-Command -Name 'winget' -ErrorAction SilentlyContinue
$chocoCommand = Get-Command -Name 'choco' -ErrorAction SilentlyContinue
$scoopCommand = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue

function Resolve-ManagerExecutable {
    param(
        [string]$Key
    )

    switch ($Key) {
        'winget' {
            if (-not $wingetCommand) {
                throw 'winget CLI was not found on this machine.'
            }
            return if ($wingetCommand.Source) { $wingetCommand.Source } else { 'winget' }
        }
        'choco' {
            if (-not $chocoCommand) {
                throw 'Chocolatey (choco) CLI was not found on this machine.'
            }
            return if ($chocoCommand.Source) { $chocoCommand.Source } else { 'choco' }
        }
        'chocolatey' {
            if (-not $chocoCommand) {
                throw 'Chocolatey (choco) CLI was not found on this machine.'
            }
            return if ($chocoCommand.Source) { $chocoCommand.Source } else { 'choco' }
        }
        'scoop' {
            if (-not $scoopCommand) {
                throw 'Scoop CLI was not found on this machine.'
            }
            return if ($scoopCommand.Source) { $scoopCommand.Source } else { 'scoop' }
        }
        default {
            throw "Unsupported package manager '$Key'."
        }
    }
}

function Normalize-VersionString {
    param(
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $trimmed = $Value.Trim()

    if ($trimmed -match '([0-9]+(?:\.[0-9]+)*)') {
        return $matches[1]
    }

    return $trimmed
}

function Get-Status {
    param(
        [string]$Installed,
        [string]$Latest
    )

    $normalizedInstalled = Normalize-VersionString -Value $Installed
    $normalizedLatest = Normalize-VersionString -Value $Latest

    if ([string]::IsNullOrWhiteSpace($normalizedInstalled)) {
        return 'NotInstalled'
    }

    if ([string]::IsNullOrWhiteSpace($normalizedLatest) -or $normalizedLatest.Trim().ToLowerInvariant() -eq 'unknown') {
        return 'Unknown'
    }

    $installedVersion = $null
    $latestVersion = $null
    if ([version]::TryParse($normalizedInstalled, [ref]$installedVersion) -and [version]::TryParse($normalizedLatest, [ref]$latestVersion)) {
        if ($installedVersion -lt $latestVersion) {
            return 'UpdateAvailable'
        }
        return 'UpToDate'
    }

    if ($normalizedInstalled -eq $normalizedLatest) {
        return 'UpToDate'
    }

    return 'UpdateAvailable'
}

function Get-WingetInstalledVersion {
    param(
        [string]$PackageId
    )

    if (-not $wingetCommand) {
        return $null
    }

    $exe = if ($wingetCommand.Source) { $wingetCommand.Source } else { 'winget' }
    $arguments = @('list', '--id', $PackageId, '-e', '--disable-interactivity', '--accept-source-agreements', '--output', 'json')

    try {
        $output = & $exe @arguments 2>$null
        if ($LASTEXITCODE -eq 0 -and $output) {
            $json = ($output -join [Environment]::NewLine)
            $data = ConvertFrom-Json -InputObject $json -ErrorAction Stop
            if ($data -and $data.InstalledPackages -and $data.InstalledPackages.Count -gt 0) {
                $package = $data.InstalledPackages | Select-Object -First 1
                if ($package.Version) {
                    return $package.Version.Trim()
                }
            }
        }
    }
    catch {
        # fall back to text parsing below
    }

    try {
        $fallback = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null
        foreach ($line in $fallback) {
            if ($line -match '\s+' + [Regex]::Escape($PackageId) + '\s+([\w\.\-]+)') {
                return $matches[1].Trim()
            }
        }
    }
    catch {
        return $null
    }

    return $null
}

function Get-WingetAvailableVersion {
    param(
        [string]$PackageId
    )

    if (-not $wingetCommand) {
        return $null
    }

    $exe = if ($wingetCommand.Source) { $wingetCommand.Source } else { 'winget' }
    $arguments = @('show', '--id', $PackageId, '-e', '--disable-interactivity', '--accept-source-agreements', '--output', 'json')

    try {
        $output = & $exe @arguments 2>$null
        if ($LASTEXITCODE -eq 0 -and $output) {
            $json = ($output -join [Environment]::NewLine)
            $data = ConvertFrom-Json -InputObject $json -ErrorAction Stop
            if ($data -and $data.Versions -and $data.Versions.Count -gt 0) {
                $latest = $data.Versions | Select-Object -First 1
                if ($latest.Version) {
                    return $latest.Version.Trim()
                }
            }
            elseif ($data -and $data.Version) {
                return $data.Version.Trim()
            }
        }
    }
    catch {
        # fall back to text parsing below
    }

    try {
        $fallback = & $exe 'show' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null
        foreach ($line in $fallback) {
            if ($line -match '^\s*Version\s*:\s*(.+)$') {
                return $matches[1].Trim()
            }
        }
    }
    catch {
        return $null
    }

    return $null
}

function Get-ChocoInstalledVersion {
    param(
        [string]$PackageId
    )

    if (-not $chocoCommand) {
        return $null
    }

    $exe = if ($chocoCommand.Source) { $chocoCommand.Source } else { 'choco' }

    try {
        $output = & $exe 'list' $PackageId '--local-only' '--exact' '--limit-output' 2>$null
        foreach ($line in $output) {
            if ($line -match '^\s*' + [Regex]::Escape($PackageId) + '\|(.+)$') {
                return $matches[1].Trim()
            }
        }
    }
    catch {
        return $null
    }

    return $null
}

function Get-ChocoAvailableVersion {
    param(
        [string]$PackageId
    )

    if (-not $chocoCommand) {
        return $null
    }

    $exe = if ($chocoCommand.Source) { $chocoCommand.Source } else { 'choco' }

    try {
        $output = & $exe 'search' $PackageId '--exact' '--limit-output' 2>$null
        foreach ($line in $output) {
            if ($line -match '^\s*' + [Regex]::Escape($PackageId) + '\|(.+)$') {
                return $matches[1].Trim()
            }
        }
    }
    catch {
        return $null
    }

    return $null
}

function Get-ScoopInstalledVersion {
    param(
        [string]$PackageId
    )

    if (-not $scoopCommand) {
        return $null
    }

    $exe = if ($scoopCommand.Source) { $scoopCommand.Source } else { 'scoop' }

    try {
        $output = & $exe 'list' '--json' 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $output) {
            return $null
        }

        $json = ($output -join [Environment]::NewLine)
        $apps = ConvertFrom-Json -InputObject $json -ErrorAction Stop
        foreach ($app in $apps) {
            if ($app.Name -and ($app.Name -eq $PackageId)) {
                return $app.Version
            }
            if ($app.name -and ($app.name -eq $PackageId)) {
                return $app.version
            }
        }
    }
    catch {
        return $null
    }

    return $null
}

function Get-ScoopAvailableVersion {
    param(
        [string]$PackageId
    )

    if (-not $scoopCommand) {
        return $null
    }

    $exe = if ($scoopCommand.Source) { $scoopCommand.Source } else { 'scoop' }

    try {
        $output = & $exe 'info' $PackageId '--json' 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $output) {
            return $null
        }

        $json = ($output -join [Environment]::NewLine)
        $info = ConvertFrom-Json -InputObject $json -ErrorAction Stop

        if ($info -is [System.Collections.IDictionary]) {
            if ($info.App) {
                if ($info.App.Version) { return ($info.App.Version).ToString().Trim() }
                if ($info.App.'Latest Version') { return ($info.App.'Latest Version').ToString().Trim() }
            }

            if ($info.Version) { return $info.Version.ToString().Trim() }
            if ($info.version) { return $info.version.ToString().Trim() }
        }
        elseif ($info -and $info.Version) {
            return $info.Version.ToString().Trim()
        }
    }
    catch {
        # fall back to text parsing
    }

    try {
        $fallback = & $exe 'info' $PackageId 2>$null
        foreach ($line in $fallback) {
            if ($line -match '^\s*Latest Version\s*:\s*(.+)$') {
                return $matches[1].Trim()
            }
            if ($line -match '^\s*Version\s*:\s*(.+)$') {
                return $matches[1].Trim()
            }
        }
    }
    catch {
        return $null
    }

    return $null
}

function Get-ManagerInstalledVersion {
    param(
        [string]$ManagerKey,
        [string]$PackageId
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
        [string]$ManagerKey,
        [string]$PackageId
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
        [string]$ManagerKey,
        [string]$PackageId
    )

    $exe = Resolve-ManagerExecutable -Key $ManagerKey

    switch ($ManagerKey) {
        'winget' {
            $arguments = @('upgrade', '--id', $PackageId, '-e', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
        }
        'choco' { $arguments = @('upgrade', $PackageId, '-y', '--no-progress') }
        'chocolatey' { $arguments = @('upgrade', $PackageId, '-y', '--no-progress') }
        'scoop' { $arguments = @('update', $PackageId) }
        default { throw "Unsupported package manager '$ManagerKey' for update." }
    }

    $output = & $exe @arguments 2>&1
    $exitCode = $LASTEXITCODE

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = @($output)
        Executable = $exe
        Arguments = $arguments
    }
}

$installedBefore = Get-ManagerInstalledVersion -ManagerKey $managerKey -PackageId $PackageId
$latestBefore = Get-ManagerAvailableVersion -ManagerKey $managerKey -PackageId $PackageId
if ([string]::IsNullOrWhiteSpace($latestBefore)) {
    $latestBefore = 'Unknown'
}

$statusBefore = Get-Status -Installed $installedBefore -Latest $latestBefore

$updateAttempted = $false
$commandExitCode = 0
$commandOutput = @()
$commandExecutable = $null
$commandArguments = @()

if ($statusBefore -eq 'UpdateAvailable') {
    $updateAttempted = $true
    $execution = Invoke-ManagerUpdate -ManagerKey $managerKey -PackageId $PackageId
    $commandExitCode = $execution.ExitCode
    $commandOutput = $execution.Output
    $commandExecutable = $execution.Executable
    $commandArguments = $execution.Arguments
}
else {
    $commandOutput = @('No update attempted because the package is not reporting an available update.')
}

$installedAfter = Get-ManagerInstalledVersion -ManagerKey $managerKey -PackageId $PackageId
$latestAfter = Get-ManagerAvailableVersion -ManagerKey $managerKey -PackageId $PackageId
if ([string]::IsNullOrWhiteSpace($latestAfter)) {
    $latestAfter = 'Unknown'
}

$statusAfter = Get-Status -Installed $installedAfter -Latest $latestAfter

$result = [pscustomobject]@{
    Manager = $normalizedManager
    ManagerKey = $managerKey
    PackageId = $PackageId
    StatusBefore = $statusBefore
    StatusAfter = $statusAfter
    InstalledVersion = if ($installedAfter) { $installedAfter } else { $null }
    LatestVersion = $latestAfter
    UpdateAttempted = $updateAttempted
    ExitCode = $commandExitCode
    Output = $commandOutput
    Executable = $commandExecutable
    Arguments = $commandArguments
}

$result | ConvertTo-Json -Depth 6
