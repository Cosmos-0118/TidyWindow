param(
    [string[]]$RuntimeIds
)

Set-StrictMode -Version 2
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

        $versions = @()
        foreach ($line in $lines) {
            if ($line -match '^Microsoft\.WindowsDesktop\.App\s+([0-9\.]+)') {
                $versions += $matches[1]
            }
        }

        if ($versions.Count -eq 0) {
            return $null
        }

        return ($versions | Sort-Object {[version]$_} -Descending | Select-Object -First 1)
    }
    catch {
        return $null
    }
}

function Get-PowerShellRuntimeVersion {
    try {
        if ($PSVersionTable -and $PSVersionTable.PSVersion) {
            return $PSVersionTable.PSVersion.ToString()
        }
    }
    catch {
        return $null
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
        return $null
    }
    return $null
}

function Get-Status {
    param(
        [string]$Installed,
        [string]$Latest
    )

    if ([string]::IsNullOrWhiteSpace($Installed)) {
        return 'NotInstalled'
    }

    $installedVersion = $null
    $latestVersion = $null
    if ([version]::TryParse($Installed, [ref]$installedVersion) -and [version]::TryParse($Latest, [ref]$latestVersion)) {
        if ($installedVersion -lt $latestVersion) {
            return 'UpdateAvailable'
        }
        return 'UpToDate'
    }

    if ($Installed.Trim() -eq $Latest.Trim()) {
        return 'UpToDate'
    }

    return 'UpdateAvailable'
}

$catalog = @(
    [pscustomobject]@{
        Id = 'dotnet-desktop'
        DisplayName = '.NET Desktop Runtime'
        LatestVersion = '8.0.10'
        DownloadUrl = 'https://dotnet.microsoft.com/en-us/download/dotnet'
        Notes = 'Installs Microsoft.WindowsDesktop.App for WPF/WinForms applications.'
        Detector = { Get-DotNetDesktopRuntimeVersion }
    }
    [pscustomobject]@{
        Id = 'powershell'
        DisplayName = 'PowerShell 7'
        LatestVersion = '7.4.3'
        DownloadUrl = 'https://aka.ms/powershell-release'
        Notes = 'Provides the latest cross-platform automation shell.'
        Detector = { Get-PowerShellRuntimeVersion }
    }
    [pscustomobject]@{
        Id = 'node-lts'
        DisplayName = 'Node.js LTS'
        LatestVersion = '20.11.1'
        DownloadUrl = 'https://nodejs.org/en/download'
        Notes = 'Used by JavaScript tooling and popular CLI experiences.'
        Detector = { Get-NodeRuntimeVersion }
    }
)

if ($RuntimeIds -and $RuntimeIds.Count -gt 0) {
    $catalog = $catalog | Where-Object { $RuntimeIds -contains $_.Id }
}

$results = @()
foreach ($entry in $catalog) {
    $installedVersion = $null
    try {
        $installedVersion = & $entry.Detector
    }
    catch {
        $installedVersion = $null
    }

    if (-not $installedVersion) {
        $installedVersion = ''
    }

    $status = Get-Status -Installed $installedVersion -Latest $entry.LatestVersion

    $results += [pscustomobject]@{
        Id = $entry.Id
        Status = $status
        InstalledVersion = if ($installedVersion) { $installedVersion } else { $null }
        LatestVersion = $entry.LatestVersion
        DownloadUrl = $entry.DownloadUrl
        Notes = $entry.Notes
    }
}

$results | ConvertTo-Json -Depth 4
