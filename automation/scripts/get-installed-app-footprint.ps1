[CmdletBinding()]
param(
    [string[]] $Managers = @('winget', 'choco', 'scoop'),
    [switch] $IncludeAppx = $true,
    [switch] $IncludeAllUsersAppx = $true,
    [switch] $SkipProcessScan,
    [int] $MaxProcessHints = 5,
    [int] $MaxServiceHints = 5,
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-StringSet {
    return New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
}

function Convert-SetToArray {
    param([System.Collections.Generic.HashSet[string]] $Set)

    if (-not $Set -or $Set.Count -eq 0) { return @() }

    $buffer = New-Object string[] $Set.Count
    $Set.CopyTo($buffer)
    return $buffer
}

function Resolve-AppFootprintPath {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }

    $expanded = [Environment]::ExpandEnvironmentVariables($Value.Trim().Trim('"'))
    if ($expanded.StartsWith('~')) {
        $home = $env:USERPROFILE
        if ($home) { $expanded = $home + $expanded.Substring(1) }
    }

    try {
        return [System.IO.Path]::GetFullPath($expanded)
    }
    catch {
        return $expanded
    }
}

function ConvertTo-NormalizedKey {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $lower = $Value.ToLowerInvariant()
    $collapsed = [System.Text.RegularExpressions.Regex]::Replace($lower, '[^a-z0-9]', '')
    if ([string]::IsNullOrWhiteSpace($collapsed)) { return $null }
    return $collapsed
}

function Get-ScriptRelativePath {
    param([string] $Relative)

    $root = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { (Get-Location).Path }
    return [System.IO.Path]::GetFullPath((Join-Path -Path $root -ChildPath $Relative))
}

$modulePath = Get-ScriptRelativePath -Relative '..\modules\TidyWindow.Automation.psm1'
if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "Automation module not found at '$modulePath'."
}

Import-Module $modulePath -Force

$inventoryScript = Get-ScriptRelativePath -Relative 'get-package-inventory.ps1'
if (-not (Test-Path -LiteralPath $inventoryScript)) {
    throw "Dependent inventory script not found at '$inventoryScript'."
}

function Try-ParseJson {
    param([string] $Json)

    if ([string]::IsNullOrWhiteSpace($Json)) { return $null }
    try { return $Json | ConvertFrom-Json -Depth 8 -ErrorAction Stop } catch { return $null }
}

function Resolve-EstimatedSizeBytes {
    param([object] $Value)

    if ($null -eq $Value) { return $null }
    try {
        $numeric = [double]$Value
        if ($numeric -le 0) { return $null }
        return [math]::Round($numeric * 1024)
    }
    catch {
        return $null
    }
}

function New-AppRecord {
    param(
        [string] $AppId,
        [string] $Name,
        [string] $Version,
        [string] $Source,
        [string] $Scope,
        [string] $InstallRoot,
        [string] $UninstallCommand,
        [string] $QuietUninstallCommand,
        [string] $PackageFamily,
        [System.Collections.IEnumerable] $Tags
    )

    return [pscustomobject]@{
        appId                 = $AppId
        name                  = $Name
        version               = $Version
        publisher             = $null
        source                = $Source
        scope                 = $Scope
        installRoot           = $InstallRoot
        installRoots          = if ($InstallRoot) { @($InstallRoot) } else { @() }
        uninstallCommand      = $UninstallCommand
        quietUninstallCommand = $QuietUninstallCommand
        packageFamilyName     = $PackageFamily
        estimatedSizeBytes    = $null
        artifactHints         = @()
        managerHints          = @()
        processHints          = @()
        serviceHints          = @()
        registry              = $null
        tags                  = @($Tags)
        confidence            = $Source
        _normalizedName       = ConvertTo-NormalizedKey -Value $Name
    }
}

function Resolve-InstallRootFromCommand {
    param([string] $Command)

    if (-not $Command) { return $null }
    if ($Command -match '"(?<path>[A-Za-z]:[^" ]+)"') {
        return Split-Path -Parent $matches['path']
    }
    return $null
}

function Get-RegistryApplications {
    param(
        [string] $HivePath,
        [string] $Scope,
        [System.Collections.Generic.List[string]] $Warnings
    )

    try {
        $items = Get-ChildItem -Path $HivePath -ErrorAction Stop
    }
    catch {
        if ($Warnings) { $Warnings.Add("Failed to enumerate $($HivePath): $($_.Exception.Message)") | Out-Null }
        return @()
    }

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    foreach ($item in $items) {
        $displayName = $null
        try { $displayName = $item.GetValue('DisplayName') } catch { $displayName = $null }
        if ([string]::IsNullOrWhiteSpace($displayName)) { continue }

        try {
            if ($item.GetValue('SystemComponent') -eq 1) { continue }
        }
        catch { }

        $installLocation = $null
        try { $installLocation = $item.GetValue('InstallLocation') } catch { }
        $installRoot = Resolve-AppFootprintPath -Value $installLocation

        $uninstallCommand = $null
        try { $uninstallCommand = $item.GetValue('UninstallString') } catch { }

        if (-not $installRoot) {
            $installRoot = Resolve-InstallRootFromCommand -Command $uninstallCommand
        }

        $quietCommand = $null
        try { $quietCommand = $item.GetValue('QuietUninstallString') } catch { }

        $displayVersion = $null
        try { $displayVersion = $item.GetValue('DisplayVersion') } catch { }
        $publisher = $null
        try { $publisher = $item.GetValue('Publisher') } catch { }
        $estimatedSizeRaw = $null
        try { $estimatedSizeRaw = $item.GetValue('EstimatedSize') } catch { }
        $displayIcon = $null
        try { $displayIcon = $item.GetValue('DisplayIcon') } catch { }
        $installDate = $null
        try { $installDate = $item.GetValue('InstallDate') } catch { }

        $appId = "registry:$($Scope):$($item.Name)"
        $record = New-AppRecord -AppId $appId -Name $displayName -Version $displayVersion -Source 'registry' -Scope $Scope -InstallRoot (Resolve-AppFootprintPath -Value $installRoot) -UninstallCommand $uninstallCommand -QuietUninstallCommand $quietCommand -PackageFamily $null -Tags 'registry','win32'
        $record.publisher = $publisher
        $record.estimatedSizeBytes = Resolve-EstimatedSizeBytes -Value $estimatedSizeRaw
        $record.registry = [pscustomobject]@{
            hive            = $HivePath
            keyPath         = $item.Name
            displayIcon     = $displayIcon
            installDate     = $installDate
            installLocation = $installLocation
        }

        if ($record.installRoot) {
            $record.artifactHints = @($record.installRoot)
        }

        $results.Add($record) | Out-Null
    }

    return $results
}

function Get-AppxApplications {
    param([switch] $AllUsers, [System.Collections.Generic.List[string]] $Warnings)

    $packages = @()
    try {
        $packages = Get-AppxPackage -AllUsers:$AllUsers -ErrorAction Stop
    }
    catch {
        if ($Warnings) { $Warnings.Add("Get-AppxPackage failed: $($_.Exception.Message)") | Out-Null }
    }

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    foreach ($pkg in $packages) {
        if ($null -eq $pkg) { continue }
        $name = if ($pkg.Name) { $pkg.Name } elseif ($pkg.PackageFamilyName) { $pkg.PackageFamilyName } else { $pkg.PackageFullName }
        $installRoot = $null
        if ($pkg.InstallLocation -and (Test-Path -LiteralPath $pkg.InstallLocation)) {
            $installRoot = Resolve-AppFootprintPath -Value $pkg.InstallLocation
        }

        $appId = "appx:$($pkg.PackageFullName)"
        $scope = if ($pkg.IsFramework) { 'Framework' } else { 'User' }
        $version = $null
        if ($pkg.Version) {
            try { $version = $pkg.Version.ToString() } catch { }
        }

        $record = New-AppRecord -AppId $appId -Name $name -Version $version -Source 'appx' -Scope $scope -InstallRoot $installRoot -UninstallCommand "Remove-AppxPackage -Package '$($pkg.PackageFullName)'" -QuietUninstallCommand $null -PackageFamily $pkg.PackageFamilyName -Tags 'appx'
        $record.publisher = $pkg.Publisher
        if ($installRoot) {
            $record.artifactHints = @($installRoot)
        }

        $results.Add($record) | Out-Null
    }

    return $results
}

function Invoke-ManagerInventory {
    param([string[]] $Managers, [System.Collections.Generic.List[string]] $Warnings)

    if (-not $Managers -or $Managers.Count -eq 0) { return @() }
    try {
        $json = & $inventoryScript -Managers $Managers 2>$null
        $payload = Try-ParseJson -Json $json
        if (-not $payload) {
            if ($Warnings) { $Warnings.Add('Failed to parse manager inventory output.') | Out-Null }
            return @()
        }

        return @($payload.packages)
    }
    catch {
        if ($Warnings) { $Warnings.Add("Manager inventory failed: $($_.Exception.Message)") | Out-Null }
        return @()
    }
}

function Add-ManagerHints {
    param(
        [psobject[]] $Apps,
        [psobject[]] $ManagerPackages
    )

    if (-not $Apps -or -not $ManagerPackages) { return }

    $lookup = New-Object 'System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[psobject]]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($pkg in $ManagerPackages) {
        if (-not $pkg.Name) { continue }
        $key = ConvertTo-NormalizedKey -Value $pkg.Name
        if (-not $key) { continue }
        if (-not $lookup.ContainsKey($key)) {
            $lookup[$key] = New-Object 'System.Collections.Generic.List[psobject]'
        }
        $lookup[$key].Add($pkg) | Out-Null
    }

    foreach ($app in $Apps) {
        if (-not $app._normalizedName) { continue }
        if (-not $lookup.ContainsKey($app._normalizedName)) { continue }
        foreach ($pkg in $lookup[$app._normalizedName]) {
            $app.managerHints += [pscustomobject]@{
                manager          = $pkg.Manager
                packageId        = $pkg.Id
                installedVersion = $pkg.InstalledVersion
                availableVersion = $pkg.AvailableVersion
                source           = $pkg.Source
            }
        }
    }
}

function Get-ProcessSnapshot {
    $snapshot = New-Object 'System.Collections.Generic.List[psobject]'
    try {
        Get-Process | ForEach-Object {
            $path = $null
            try { $path = $_.Path } catch { $path = $null }
            $snapshot.Add([pscustomobject]@{
                id   = $_.Id
                name = $_.ProcessName
                path = $path
            }) | Out-Null
        }
    }
    catch { }

    return $snapshot
}

function Get-ServiceSnapshot {
    $snapshot = New-Object 'System.Collections.Generic.List[psobject]'
    try {
        Get-CimInstance -ClassName Win32_Service | ForEach-Object {
            $snapshot.Add([pscustomobject]@{
                name        = $_.Name
                displayName = $_.DisplayName
                path        = $_.PathName
                state       = $_.State
            }) | Out-Null
        }
    }
    catch { }

    return $snapshot
}

function Update-AppArtifactHints {
    param(
        [psobject] $App,
        [System.Collections.Generic.HashSet[string]] $ArtifactRoots
    )

    if (-not $App -or -not $ArtifactRoots) { return }
    if (-not [string]::IsNullOrWhiteSpace($App.installRoot)) {
        [void]$ArtifactRoots.Add($App.installRoot)
    }

    $registryLocation = $null
    if ($App.registry -and $App.registry.PSObject.Properties['installLocation']) {
        $registryLocation = $App.registry.installLocation
    }

    if ($registryLocation) {
        $resolved = Resolve-AppFootprintPath -Value $registryLocation
        if ($resolved) { [void]$ArtifactRoots.Add($resolved) }
    }

    if ($App.packageFamilyName) {
        $wildcard = Join-Path -Path $env:ProgramFiles -ChildPath "WindowsApps\$($App.packageFamilyName)*"
        if ($wildcard) { [void]$ArtifactRoots.Add($wildcard) }
    }
}

function Add-ProcessServiceHints {
    param(
        [psobject[]] $Apps,
        [psobject[]] $ProcessSnapshot,
        [psobject[]] $ServiceSnapshot,
        [int] $MaxProcessHints,
        [int] $MaxServiceHints
    )

    if (-not $Apps) { return }

    foreach ($app in $Apps) {
        $processHints = New-StringSet
        $serviceHints = New-StringSet
        $nameKey = ConvertTo-NormalizedKey -Value $app.name

        if ($ProcessSnapshot -and $app.installRoot) {
            foreach ($proc in $ProcessSnapshot) {
                if ($processHints.Count -ge $MaxProcessHints) { break }
                if ([string]::IsNullOrWhiteSpace($proc.path)) { continue }
                if ($proc.path.StartsWith($app.installRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                    [void]$processHints.Add($proc.name)
                }
            }
        }

        if ($ProcessSnapshot -and $processHints.Count -lt $MaxProcessHints -and $nameKey) {
            foreach ($proc in $ProcessSnapshot) {
                if ($processHints.Count -ge $MaxProcessHints) { break }
                if ([string]::IsNullOrWhiteSpace($proc.name)) { continue }
                $procKey = ConvertTo-NormalizedKey -Value $proc.name
                if ($procKey -and $procKey.Contains($nameKey)) {
                    [void]$processHints.Add($proc.name)
                }
            }
        }

        if ($ServiceSnapshot -and $app.installRoot) {
            foreach ($svc in $ServiceSnapshot) {
                if ($serviceHints.Count -ge $MaxServiceHints) { break }
                if ([string]::IsNullOrWhiteSpace($svc.path)) { continue }
                if ($svc.path.IndexOf($app.installRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    [void]$serviceHints.Add($svc.name)
                }
            }
        }

        if ($ServiceSnapshot -and $serviceHints.Count -lt $MaxServiceHints -and $nameKey) {
            foreach ($svc in $ServiceSnapshot) {
                if ($serviceHints.Count -ge $MaxServiceHints) { break }
                if ([string]::IsNullOrWhiteSpace($svc.displayName)) { continue }
                $svcKey = ConvertTo-NormalizedKey -Value $svc.displayName
                if ($svcKey -and $svcKey.Contains($nameKey)) {
                    [void]$serviceHints.Add($svc.name)
                }
            }
        }

        $app.processHints = @(Convert-SetToArray -Set $processHints)
        $app.serviceHints = @(Convert-SetToArray -Set $serviceHints)

        $artifactRoots = New-StringSet
        Update-AppArtifactHints -App $app -ArtifactRoots $artifactRoots
        $app.artifactHints = @(Convert-SetToArray -Set $artifactRoots)
    }
}

$warnings = New-Object 'System.Collections.Generic.List[string]'
$managerPackages = Invoke-ManagerInventory -Managers $Managers -Warnings $warnings

$registryApps = @()
$registryApps += Get-RegistryApplications -HivePath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -Scope 'Machine' -Warnings $warnings
$registryApps += Get-RegistryApplications -HivePath 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' -Scope 'Machine' -Warnings $warnings
$registryApps += Get-RegistryApplications -HivePath 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -Scope 'User' -Warnings $warnings

$appxApps = @()
if ($IncludeAppx) {
    $appxApps = Get-AppxApplications -AllUsers:$IncludeAllUsersAppx -Warnings $warnings
}

$allApps = @($registryApps + $appxApps)
Add-ManagerHints -Apps $allApps -ManagerPackages $managerPackages

if (-not $SkipProcessScan) {
    $processSnapshot = Get-ProcessSnapshot
    $serviceSnapshot = Get-ServiceSnapshot
    Add-ProcessServiceHints -Apps $allApps -ProcessSnapshot $processSnapshot -ServiceSnapshot $serviceSnapshot -MaxProcessHints $MaxProcessHints -MaxServiceHints $MaxServiceHints
}
else {
    foreach ($app in $allApps) {
        $app.artifactHints = @(($app.installRoot) | Where-Object { $_ })
        $app.processHints = @()
        $app.serviceHints = @()
    }
}

foreach ($app in $allApps) {
    $app.PSObject.Properties.Remove('_normalizedName') | Out-Null
}

$payload = [pscustomobject]@{
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    apps        = $allApps
    warnings    = $warnings
}

$json = $payload | ConvertTo-Json -Depth 6
if ($OutputPath) {
    $resolved = Resolve-AppFootprintPath -Value $OutputPath
    $directory = Split-Path -Parent $resolved
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        [void](New-Item -Path $directory -ItemType Directory -Force)
    }
    $json | Out-File -FilePath $resolved -Encoding utf8 -Force
}

$json
