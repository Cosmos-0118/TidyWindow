param(
    [string[]]$RuntimeIds,
    [string]$CatalogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-DotNetDesktopRuntimeVersion {
    try {
        $dotnetCmd = Get-Command -Name dotnet -ErrorAction SilentlyContinue
        if (-not $dotnetCmd) {
            return $null
        }

        $lines = & $dotnetCmd.Source --list-runtimes 2>$null
        if (-not $lines) {
            return $null
        }

        $versions = foreach ($line in $lines) {
            if ($line -match '^Microsoft\.WindowsDesktop\.App\s+([0-9\.]+)') {
                $matches[1]
            }
        }

        if ($versions) {
            return ($versions | Sort-Object {[version]$_} -Descending | Select-Object -First 1)
        }
    }
    catch {
        # ignore detection failures and treat as not installed
    }

    return $null
}

function Get-PowerShellRuntimeVersion {
    try {
        $pwshCommand = Get-Command -Name 'pwsh' -ErrorAction SilentlyContinue
        if (-not $pwshCommand) {
            return $null
        }

        $exe = if ($pwshCommand.Source) { $pwshCommand.Source } else { 'pwsh' }
    $output = & $exe -NoLogo -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>$null
        if ($output) {
            return ($output | Select-Object -First 1).Trim()
        }
    }
    catch {
        # ignore
    }

    return $null
}

function Get-NodeRuntimeVersion {
    try {
        $nodeCmd = Get-Command -Name node -ErrorAction SilentlyContinue
        if (-not $nodeCmd) {
            return $null
        }

        $output = & $nodeCmd.Source --version 2>$null
        if ($output -match 'v([0-9\.]+)') {
            return $matches[1]
        }
    }
    catch {
        # ignore
    }

    return $null
}

$wingetCommand = Get-Command -Name 'winget' -ErrorAction SilentlyContinue
$chocoCommand = Get-Command -Name 'choco' -ErrorAction SilentlyContinue
$scoopCommand = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue

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
                if ($info.App."Latest Version") { return ($info.App."Latest Version").ToString().Trim() }
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

function Get-RuntimeCatalog {
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
            Write-Verbose "Failed to read runtime catalog payload: $_"
        }
    }

    return @(
        [pscustomobject]@{
            Id = 'dotnet-desktop'
            DisplayName = '.NET Desktop Runtime'
            DownloadUrl = 'https://dotnet.microsoft.com/en-us/download/dotnet'
            Notes = 'Installs Microsoft.WindowsDesktop.App for WPF/WinForms applications.'
            Detector = 'dotnet-desktop'
            Manager = 'winget'
            PackageId = 'Microsoft.DotNet.DesktopRuntime.8'
            FallbackLatestVersion = '8.0.10'
        }
        [pscustomobject]@{
            Id = 'powershell'
            DisplayName = 'PowerShell 7'
            DownloadUrl = 'https://aka.ms/powershell-release'
            Notes = 'Provides the latest cross-platform automation shell.'
            Detector = 'powershell'
            Manager = 'winget'
            PackageId = 'Microsoft.PowerShell'
            FallbackLatestVersion = '7.5.3'
        }
        [pscustomobject]@{
            Id = 'node-lts'
            DisplayName = 'Node.js LTS'
            DownloadUrl = 'https://nodejs.org/en/download'
            Notes = 'Used by JavaScript tooling and popular CLI experiences.'
            Detector = 'node-lts'
            Manager = 'winget'
            PackageId = 'OpenJS.NodeJS.LTS'
            FallbackLatestVersion = '22.20.0'
        }
    )
}

function Invoke-RuntimeDetector {
    param(
        [psobject]$Entry
    )

    $detectorKey = ($Entry.Detector ?? '').ToString().ToLowerInvariant()

    switch ($detectorKey) {
        'dotnet-desktop' { return Get-DotNetDesktopRuntimeVersion }
        'powershell' { return Get-PowerShellRuntimeVersion }
        'node-lts' { return Get-NodeRuntimeVersion }
        default { return $null }
    }
}

function Get-ManagerInstalledVersion {
    param(
        [psobject]$Entry
    )

    $manager = ($Entry.Manager ?? '').ToString().ToLowerInvariant()
    $packageId = ($Entry.PackageId ?? '').ToString()

    if ([string]::IsNullOrWhiteSpace($manager) -or [string]::IsNullOrWhiteSpace($packageId)) {
        return $null
    }

    switch ($manager) {
        'winget' { return Get-WingetInstalledVersion -PackageId $packageId }
        'choco' { return Get-ChocoInstalledVersion -PackageId $packageId }
        'chocolatey' { return Get-ChocoInstalledVersion -PackageId $packageId }
        'scoop' { return Get-ScoopInstalledVersion -PackageId $packageId }
        default { return $null }
    }
}

function Get-ManagerAvailableVersion {
    param(
        [psobject]$Entry
    )

    $manager = ($Entry.Manager ?? '').ToString().ToLowerInvariant()
    $packageId = ($Entry.PackageId ?? '').ToString()

    if ([string]::IsNullOrWhiteSpace($manager) -or [string]::IsNullOrWhiteSpace($packageId)) {
        return $null
    }

    switch ($manager) {
        'winget' { return Get-WingetAvailableVersion -PackageId $packageId }
        'choco' { return Get-ChocoAvailableVersion -PackageId $packageId }
        'chocolatey' { return Get-ChocoAvailableVersion -PackageId $packageId }
        'scoop' { return Get-ScoopAvailableVersion -PackageId $packageId }
        default { return $null }
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

$catalogEntries = Get-RuntimeCatalog -CatalogPath $CatalogPath
if ($RuntimeIds -and $RuntimeIds.Count -gt 0) {
    $catalogEntries = $catalogEntries | Where-Object { $RuntimeIds -contains $_.Id }
}

$results = @()
foreach ($entry in $catalogEntries) {
    $installedVersion = Invoke-RuntimeDetector -Entry $entry
    if (-not $installedVersion) {
        $installedVersion = Get-ManagerInstalledVersion -Entry $entry
    }

    $latestVersion = Get-ManagerAvailableVersion -Entry $entry

    if (-not $latestVersion -and $entry.FallbackLatestVersion) {
        $latestVersion = $entry.FallbackLatestVersion
    }

    if ([string]::IsNullOrWhiteSpace($latestVersion)) {
        $latestVersion = 'Unknown'
    }

    $status = Get-Status -Installed $installedVersion -Latest $latestVersion

    $results += [pscustomobject]@{
        Id = $entry.Id
        Status = $status
        InstalledVersion = if ($installedVersion) { $installedVersion } else { $null }
        LatestVersion = $latestVersion
        DownloadUrl = $entry.DownloadUrl
        Notes = $entry.Notes
    }
}

$results | ConvertTo-Json -Depth 4
