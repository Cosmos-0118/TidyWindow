param(
    [string]$CatalogPath,
    [string[]]$PackageIds
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:SplitColumnsRegex = [System.Text.RegularExpressions.Regex]::new('\s{2,}', [System.Text.RegularExpressions.RegexOptions]::Compiled)

$wingetCommand = Get-Command -Name 'winget' -ErrorAction SilentlyContinue
$chocoCommand = Get-Command -Name 'choco' -ErrorAction SilentlyContinue
$scoopCommand = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue

function New-StringDictionary {
    return New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::OrdinalIgnoreCase)
}

function New-UpgradeDictionary {
    return New-Object 'System.Collections.Generic.Dictionary[string,psobject]' ([System.StringComparer]::OrdinalIgnoreCase)
}

function Split-TableColumns {
    param([string]$Line)

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return @()
    }

    return $script:SplitColumnsRegex.Split($Line.Trim()) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

$callerModulePath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath)) {
    $callerModulePath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

function Get-WingetInstalledCache {
    $cache = New-StringDictionary
    if (-not $wingetCommand) {
        return $cache
    }

    $exe = if ($wingetCommand.Source) { $wingetCommand.Source } else { 'winget' }

    try {
        $lines = & $exe 'list' '--accept-source-agreements' '--disable-interactivity' 2>$null
        if ($LASTEXITCODE -eq 0 -and $lines) {
            foreach ($line in $lines) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                if ($line -match '^-{3,}') { continue }
                if ($line -match '^\s*Name\s+Id\s+Version') { continue }

                $parts = Split-TableColumns -Line $line
                if ($parts.Length -lt 4) { continue }

                $source = $parts[$parts.Length - 1].Trim()
                if (-not [string]::IsNullOrWhiteSpace($source)) {
                    $normalizedSource = $source.ToLowerInvariant()
                    if ($normalizedSource -ne 'winget' -and $normalizedSource -ne 'msstore') { continue }
                }

                $id = $parts[1].Trim()
                $version = $parts[2].Trim()

                if (-not [string]::IsNullOrWhiteSpace($id) -and -not [string]::IsNullOrWhiteSpace($version)) {
                    $cache[$id] = $version
                }
            }
        }
    }
    catch {
        # ignore failures and return partial cache
    }

    return $cache
}

function Get-WingetUpgradeCache {
    $cache = New-UpgradeDictionary
    if (-not $wingetCommand) {
        return $cache
    }

    $exe = if ($wingetCommand.Source) { $wingetCommand.Source } else { 'winget' }

    try {
        $lines = & $exe 'upgrade' '--include-unknown' '--accept-source-agreements' '--disable-interactivity' 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $lines) {
            return $cache
        }

        foreach ($line in $lines) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ($line -match '^\s*Name\s+Id\s+Version') { continue }
            if ($line -match '^-{3,}') { continue }
            if ($line -match '^\d+\s+upgrades?\s+available\.?$') { continue }

            $parts = Split-TableColumns -Line $line
            if ($parts.Length -lt 5) { continue }

            $source = $parts[$parts.Length - 1].Trim()
            if (-not [string]::IsNullOrWhiteSpace($source)) {
                $normalizedSource = $source.ToLowerInvariant()
                if ($normalizedSource -ne 'winget' -and $normalizedSource -ne 'msstore') { continue }
            }

            $id = $parts[1].Trim()
            $installed = $parts[2].Trim()
            $available = $parts[3].Trim()

            if (-not [string]::IsNullOrWhiteSpace($id)) {
                $cache[$id] = [pscustomobject]@{
                    Installed = $installed
                    Available = $available
                }
            }
        }
    }
    catch {
        # ignore failures
    }

    return $cache
}

function Get-ChocoInstalledCache {
    $cache = New-StringDictionary
    if (-not $chocoCommand) {
        return $cache
    }

    $installRoot = $env:ChocolateyInstall
    if ([string]::IsNullOrWhiteSpace($installRoot)) {
        $installRoot = 'C:\ProgramData\chocolatey'
    }

    $libRoot = Join-Path -Path $installRoot -ChildPath 'lib'
    if (-not (Test-Path -LiteralPath $libRoot)) {
        return $cache
    }

    try {
        $packageDirs = Get-ChildItem -Path $libRoot -Directory -ErrorAction Stop
    }
    catch {
        return $cache
    }

    foreach ($dir in @($packageDirs)) {
        if (-not $dir) { continue }

        $packageId = $dir.Name
        if ([string]::IsNullOrWhiteSpace($packageId)) { continue }

        try {
            $version = Get-TidyInstalledPackageVersion -Manager 'choco' -PackageId $packageId
        }
        catch {
            $version = $null
        }

        if (-not [string]::IsNullOrWhiteSpace($version)) {
            $cache[$packageId] = $version.Trim()
        }
    }

    return $cache
}

function Get-ChocoUpgradeCache {
    $cache = New-UpgradeDictionary
    if (-not $chocoCommand) {
        return $cache
    }

    $exe = if ($chocoCommand.Source) { $chocoCommand.Source } else { 'choco' }

    try {
        $lines = & $exe 'outdated' '--limit-output' 2>$null
        if ($LASTEXITCODE -eq 0 -and $lines) {
            foreach ($line in $lines) {
                if ($line -match '^\s*([^|]+)\|([^|]*)\|([^|]*)') {
                    $id = $matches[1].Trim()
                    $installed = $matches[2].Trim()
                    $available = $matches[3].Trim()
                    if ($id) {
                        $cache[$id] = [pscustomobject]@{
                            Installed = $installed
                            Available = $available
                        }
                    }
                }
            }
        }
    }
    catch {
        # ignore failures
    }

    return $cache
}

function Get-ScoopInstalledCache {
    $cache = New-StringDictionary
    if (-not $scoopCommand) {
        return $cache
    }

    $exe = if ($scoopCommand.Source) { $scoopCommand.Source } else { 'scoop' }

    try {
        $lines = & $exe 'list' 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $lines) {
            return $cache
        }

        $started = $false
        foreach ($line in $lines) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if (-not $started) {
                if ($line -match '^----') { $started = $true }
                continue
            }

            $parts = Split-TableColumns -Line $line
            if ($parts.Length -lt 2) { continue }

            $id = $parts[0].Trim()
            $version = $parts[1].Trim()

            if ($id -and $version) {
                $cache[$id] = $version
            }
        }
    }
    catch {
        # ignore
    }

    return $cache
}

function Get-ScoopUpgradeCache {
    $cache = New-UpgradeDictionary
    if (-not $scoopCommand) {
        return $cache
    }

    $exe = if ($scoopCommand.Source) { $scoopCommand.Source } else { 'scoop' }

    try {
        $lines = & $exe 'status' 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $lines) {
            return $cache
        }

        $started = $false
        foreach ($line in $lines) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ($line -like 'WARN*') { continue }
            if (-not $started) {
                if ($line -match '^----') { $started = $true }
                continue
            }

            $parts = Split-TableColumns -Line $line
            if ($parts.Length -lt 3) { continue }

            $id = $parts[0].Trim()
            $installed = $parts[1].Trim()
            $available = $parts[2].Trim()

            if ($id) {
                $cache[$id] = [pscustomobject]@{
                    Installed = $installed
                    Available = $available
                }
            }
        }
    }
    catch {
        # ignore failures
    }

    return $cache
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

function Get-CatalogEntries {
    param(
        [string]$CatalogPath
    )

    if (-not [string]::IsNullOrWhiteSpace($CatalogPath) -and (Test-Path -LiteralPath $CatalogPath)) {
        try {
            $json = Get-Content -LiteralPath $CatalogPath -Raw -ErrorAction Stop
            $data = ConvertFrom-Json -InputObject $json -ErrorAction Stop
            if ($null -ne $data) {
                return @($data)
            }
        }
        catch {
            Write-Verbose "Failed to read package catalog payload: $_"
        }
    }

    return @()
}

$catalogEntries = Get-CatalogEntries -CatalogPath $CatalogPath
if ($PackageIds -and $PackageIds.Count -gt 0) {
    $idSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidateId in $PackageIds) {
        if (-not [string]::IsNullOrWhiteSpace($candidateId)) {
            [void]$idSet.Add($candidateId.Trim())
        }
    }

    if ($idSet.Count -gt 0) {
        $catalogEntries = $catalogEntries | Where-Object { $_.Id -and $idSet.Contains($_.Id) }
    }
}

$managerSet = $catalogEntries |
    ForEach-Object { ($_.Manager ?? '').ToString().ToLowerInvariant() } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Sort-Object -Unique

$wingetInstalledCache = $null
$wingetUpgradeCache = $null
if ($managerSet -contains 'winget') {
    $wingetInstalledCache = Get-WingetInstalledCache
    $wingetUpgradeCache = Get-WingetUpgradeCache
    $wingetInstalledCount = if ($wingetInstalledCache) { $wingetInstalledCache.Count } else { 0 }
    $wingetUpgradeCount = if ($wingetUpgradeCache) { $wingetUpgradeCache.Count } else { 0 }
    Write-Verbose ("winget installed entries: {0}; upgrades: {1}" -f $wingetInstalledCount, $wingetUpgradeCount)
}

$chocoInstalledCache = $null
$chocoUpgradeCache = $null
if ($managerSet -contains 'choco' -or $managerSet -contains 'chocolatey') {
    $chocoInstalledCache = Get-ChocoInstalledCache
    $chocoUpgradeCache = Get-ChocoUpgradeCache
    $chocoInstalledCount = if ($chocoInstalledCache) { $chocoInstalledCache.Count } else { 0 }
    $chocoUpgradeCount = if ($chocoUpgradeCache) { $chocoUpgradeCache.Count } else { 0 }
    Write-Verbose ("choco installed entries: {0}; upgrades: {1}" -f $chocoInstalledCount, $chocoUpgradeCount)
}

$scoopInstalledCache = $null
$scoopUpgradeCache = $null
if ($managerSet -contains 'scoop') {
    $scoopInstalledCache = Get-ScoopInstalledCache
    $scoopUpgradeCache = Get-ScoopUpgradeCache
    $scoopInstalledCount = if ($scoopInstalledCache) { $scoopInstalledCache.Count } else { 0 }
    $scoopUpgradeCount = if ($scoopUpgradeCache) { $scoopUpgradeCache.Count } else { 0 }
    Write-Verbose ("scoop installed entries: {0}; upgrades: {1}" -f $scoopInstalledCount, $scoopUpgradeCount)
}

$results = [System.Collections.Generic.List[psobject]]::new()

foreach ($entry in $catalogEntries) {
    try {
        $manager = ($entry.Manager ?? '').ToString().ToLowerInvariant()
        $packageId = ($entry.PackageId ?? '').ToString()

        $installedVersion = $null
        $latestVersion = $null

        if ($manager -eq 'winget') {
            if ($wingetInstalledCache -and $packageId) {
                $installedLookup = $null
                if ($wingetInstalledCache.TryGetValue($packageId, [ref]$installedLookup)) {
                    $installedVersion = $installedLookup
                }
            }

            if ($wingetUpgradeCache -and $packageId) {
                $upgradeLookup = $null
                if ($wingetUpgradeCache.TryGetValue($packageId, [ref]$upgradeLookup)) {
                    if (-not $installedVersion -and $upgradeLookup.Installed) {
                        $installedVersion = $upgradeLookup.Installed
                    }

                    $latestVersion = $upgradeLookup.Available
                }
            }
        }
        elseif ($manager -in @('choco', 'chocolatey')) {
            if ($chocoInstalledCache -and $packageId) {
                $installedLookup = $null
                if ($chocoInstalledCache.TryGetValue($packageId, [ref]$installedLookup)) {
                    $installedVersion = $installedLookup
                }
            }

            if ($chocoUpgradeCache -and $packageId) {
                $upgradeLookup = $null
                if ($chocoUpgradeCache.TryGetValue($packageId, [ref]$upgradeLookup)) {
                    if (-not $installedVersion -and $upgradeLookup.Installed) {
                        $installedVersion = $upgradeLookup.Installed
                    }

                    $latestVersion = $upgradeLookup.Available
                }
            }
        }
        elseif ($manager -eq 'scoop') {
            if ($scoopInstalledCache -and $packageId) {
                $installedLookup = $null
                if ($scoopInstalledCache.TryGetValue($packageId, [ref]$installedLookup)) {
                    $installedVersion = $installedLookup
                }
            }

            if ($scoopUpgradeCache -and $packageId) {
                $upgradeLookup = $null
                if ($scoopUpgradeCache.TryGetValue($packageId, [ref]$upgradeLookup)) {
                    if (-not $installedVersion -and $upgradeLookup.Installed) {
                        $installedVersion = $upgradeLookup.Installed
                    }

                    $latestVersion = $upgradeLookup.Available
                }
            }
        }

        if (-not $installedVersion) {
            $installedVersion = $null
        }

        if (-not $latestVersion -and $installedVersion) {
            $latestVersion = $installedVersion
        }

        if (-not $latestVersion -and $entry.FallbackLatestVersion) {
            $latestVersion = $entry.FallbackLatestVersion
        }

        if ([string]::IsNullOrWhiteSpace($latestVersion)) {
            $latestVersion = 'Unknown'
        }

        $status = Get-Status -Installed $installedVersion -Latest $latestVersion

        $results.Add([pscustomobject]@{
            Id = $entry.Id
            DisplayName = $entry.DisplayName
            Status = $status
            InstalledVersion = if ($installedVersion) { $installedVersion } else { $null }
            LatestVersion = $latestVersion
            Manager = $entry.Manager
            PackageId = $entry.PackageId
            Notes = $entry.Notes
            RequiresAdmin = $entry.RequiresAdmin
        })
    }
    catch {
        $results.Add([pscustomobject]@{
            Id = $entry.Id
            DisplayName = $entry.DisplayName
            Status = 'Unknown'
            InstalledVersion = $null
            LatestVersion = 'Unknown'
            Manager = $entry.Manager
            PackageId = $entry.PackageId
            Notes = $entry.Notes
            RequiresAdmin = $entry.RequiresAdmin
            Error = $_.Exception.Message
        })
    }
}

$resultsJson = $results | ConvertTo-Json -Depth 4 -Compress

$debugLogPath = $env:TIDYWINDOW_DEBUG_PACKAGE_SCRIPT
if (-not [string]::IsNullOrWhiteSpace($debugLogPath)) {
    try {
        $directory = Split-Path -Parent -Path $debugLogPath
        if ($directory -and -not (Test-Path -LiteralPath $directory)) {
            [System.IO.Directory]::CreateDirectory($directory) | Out-Null
        }

        Set-Content -LiteralPath $debugLogPath -Value $resultsJson -Encoding UTF8
    }
    catch {
        Write-Verbose ("Failed to write debug log: {0}" -f $_)
    }
}

$resultsJson

