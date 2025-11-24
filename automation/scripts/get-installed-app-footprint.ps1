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

function Resolve-TidyPath {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Value.Trim().Trim('"'))
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

function Normalize-NameKey {
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

function Get-ModulePath {
    param([string] $Relative)

    $scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { (Get-Location).Path }
    $candidate = Join-Path -Path $scriptRoot -ChildPath $Relative
    return [System.IO.Path]::GetFullPath($candidate)
}

$modulePath = Get-ModulePath -Relative '..\modules\TidyWindow.Automation.psm1'
if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "Automation module not found at '$modulePath'."
}

Import-Module $modulePath -Force

$inventoryScript = Get-ModulePath -Relative 'get-package-inventory.ps1'
if (-not (Test-Path -LiteralPath $inventoryScript)) {
    throw "Dependent inventory script not found at '$inventoryScript'."
}

function Try-ConvertToJsonObject {
    param([string] $Json)

    if ([string]::IsNullOrWhiteSpace($Json)) {
        return $null
    }

    try {
        return $Json | ConvertFrom-Json -Depth 8 -ErrorAction Stop
    }
    catch {
        return $null
    }
}

function Invoke-ManagerInventory {
    param([string[]] $Managers, [System.Collections.Generic.List[string]] $Warnings)

    if (-not $Managers -or $Managers.Count -eq 0) {
        return @()
    }

    $command = @($inventoryScript)
    foreach ($manager in $Managers) {
        if ([string]::IsNullOrWhiteSpace($manager)) { continue }
        $command += '-Managers'
        $command += $manager
    }

    try {
        $json = & $inventoryScript -Managers $Managers 2>$null
        $payload = Try-ConvertToJsonObject -Json $json
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

function Resolve-SizeBytes {
    param([object] $Value)

    if ($null -eq $Value) { return $null }
    try {
        $numeric = [double]$Value
        if ($numeric -le 0) { return $null }
        # EstimatedSize is reported in KB
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
        _normalizedName       = Normalize-NameKey -Value $Name
    }
}

function Get-RegistryApplications {
    param(
        [string] $HivePath,
        [string] $Scope,
        [System.Collections.Generic.List[string]] $Warnings
    )

    $items = @()
    try {
        $items = Get-ChildItem -Path $HivePath -ErrorAction Stop
    }
    catch {
        if ($Warnings) { $Warnings.Add("Failed to enumerate $HivePath: $($_.Exception.Message)") | Out-Null }
        return @()
    }

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    foreach ($item in $items) {
        try {
            $displayName = $item.GetValue('DisplayName')
        }
        catch {
            $displayName = $null
        }

        if ([string]::IsNullOrWhiteSpace($displayName)) {
            continue
        }

        try {
            $systemComponent = $item.GetValue('SystemComponent')
            if ($systemComponent -eq 1) { continue }
        }
        catch { }

        $installLocation = $null
        try { $installLocation = $item.GetValue('InstallLocation') } catch { }
        $installRoot = Resolve-TidyPath -Value $installLocation

        $uninstallCommand = $null
        try { $uninstallCommand = $item.GetValue('UninstallString') } catch { }
        $quietCommand = $null
        try { $quietCommand = $item.GetValue('QuietUninstallString') } catch { }

        if (-not $installRoot -and $uninstallCommand -and ($uninstallCommand -match '"(?<path>[A-Za-z]:[^" ]+)"')) {
            $installRoot = Split-Path -Parent $matches['path']
        }

        $appId = "registry:$Scope:$($item.Name)"
        $record = New-AppRecord -AppId $appId -Name $displayName -Version ($item.GetValue('DisplayVersion')) -Source 'registry' -Scope $Scope -InstallRoot $installRoot -UninstallCommand $uninstallCommand -QuietUninstallCommand $quietCommand -PackageFamily $null -Tags 'registry','win32'
        $record.publisher = try { $item.GetValue('Publisher') } catch { $null }
        $record.estimatedSizeBytes = Resolve-SizeBytes -Value (try { $item.GetValue('EstimatedSize') } catch { $null })
        $record.registry = [pscustomobject]@{
            hive      = $HivePath
            keyPath   = $item.Name
            displayIcon = try { $item.GetValue('DisplayIcon') } catch { $null }
            installDate = try { $item.GetValue('InstallDate') } catch { $null }
            installLocation = $installLocation
        }

        if ($installRoot) {
            $record.artifactHints = @($installRoot)
        }

        $results.Add($record) | Out-Null
    }

    return $results
}

function Get-AppxApplications {
    param([switch] $AllUsers, [System.Collections.Generic.List[string]] $Warnings)

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    $targets = @()
    try {
        $targets += Get-AppxPackage -AllUsers:$AllUsers -ErrorAction Stop
    }
    catch {
        if ($Warnings) { $Warnings.Add("Get-AppxPackage failed: $($_.Exception.Message)") | Out-Null }
    }

    foreach ($pkg in $targets) {
        if ($null -eq $pkg) { continue }
        $name = if ($pkg.Name) { $pkg.Name } elseif ($pkg.PackageFamilyName) { $pkg.PackageFamilyName } else { $pkg.PackageFullName }
        $installRoot = $null
        if ($pkg.InstallLocation -and (Test-Path -LiteralPath $pkg.InstallLocation)) {
            $installRoot = Resolve-TidyPath -Value $pkg.InstallLocation
        }

        $appId = "appx:$($pkg.PackageFullName)"
        $uninstallCommand = "Remove-AppxPackage -Package '$($pkg.PackageFullName)'"
        $pkgVersion = $null
        if ($pkg.Version) {
            try { $pkgVersion = $pkg.Version.ToString() } catch { $pkgVersion = $null }
        }

        $record = New-AppRecord -AppId $appId -Name $name -Version $pkgVersion -Source 'appx' -Scope (if ($pkg.IsFramework) { 'Framework' } else { 'User' }) -InstallRoot $installRoot -UninstallCommand $uninstallCommand -QuietUninstallCommand $null -PackageFamily $pkg.PackageFamilyName -Tags 'appx'
        $record.publisher = $pkg.Publisher
        if ($installRoot) {
            $record.artifactHints = @($installRoot)
        }

        $results.Add($record) | Out-Null
    }

    return $results
}

function Add-ManagerHints {
    param(
        [psobject[]] $Apps,
        [psobject[]] $ManagerPackages
    )

    if (-not $Apps -or -not $ManagerPackages) { return }

    $lookupByName = New-Object 'System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[psobject]]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($pkg in $ManagerPackages) {
        if (-not $pkg.Name) { continue }
        $key = Normalize-NameKey -Value $pkg.Name
        if (-not $key) { continue }

        if (-not $lookupByName.ContainsKey($key)) {
            $lookupByName[$key] = New-Object 'System.Collections.Generic.List[psobject]'
        }

        $lookupByName[$key].Add($pkg) | Out-Null
    }

    foreach ($app in $Apps) {
        if (-not $app._normalizedName) { continue }
        if (-not $lookupByName.ContainsKey($app._normalizedName)) { continue }

        foreach ($pkg in $lookupByName[$app._normalizedName]) {
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
    $results = New-Object 'System.Collections.Generic.List[psobject]'
    try {
        Get-Process | ForEach-Object {
            $path = $null
            try { $path = $_.Path } catch { $path = $null }
            $results.Add([pscustomobject]@{
                id   = $_.Id
                name = $_.ProcessName
                path = $path
            }) | Out-Null
        }
    }
    catch { }

    return $results
}

function Get-ServiceSnapshot {
    $results = New-Object 'System.Collections.Generic.List[psobject]'
    try {
        Get-CimInstance -ClassName Win32_Service | ForEach-Object {
            $results.Add([pscustomobject]@{
                name        = $_.Name
                displayName = $_.DisplayName
                path        = $_.PathName
                state       = $_.State
            }) | Out-Null
        }
    }
    catch { }

    return $results
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
        $processSet = New-StringSet
        $serviceSet = New-StringSet
        $root = $app.installRoot
        $nameKey = Normalize-NameKey -Value $app.name

        if ($ProcessSnapshot -and $root) {
            foreach ($proc in $ProcessSnapshot) {
                if ($processSet.Count -ge $MaxProcessHints) { break }
                if ([string]::IsNullOrWhiteSpace($proc.path)) { continue }
                if ($proc.path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
                    [void]$processSet.Add($proc.name)
                }
            }
        }

        if ($ProcessSnapshot -and $processSet.Count -lt $MaxProcessHints -and $nameKey) {
            foreach ($proc in $ProcessSnapshot) {
                if ($processSet.Count -ge $MaxProcessHints) { break }
                if ([string]::IsNullOrWhiteSpace($proc.name)) { continue }
                $procKey = Normalize-NameKey -Value $proc.name
                if ($procKey -and $procKey.Contains($nameKey)) {
                    [void]$processSet.Add($proc.name)
                }
            }
        }

        if ($ServiceSnapshot -and $root) {
            foreach ($svc in $ServiceSnapshot) {
                if ($serviceSet.Count -ge $MaxServiceHints) { break }
                if ([string]::IsNullOrWhiteSpace($svc.path)) { continue }
                if ($svc.path.IndexOf($root, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    [void]$serviceSet.Add($svc.name)
                }
            }
        }

        if ($ServiceSnapshot -and $serviceSet.Count -lt $MaxServiceHints -and $nameKey) {
            foreach ($svc in $ServiceSnapshot) {
                if ($serviceSet.Count -ge $MaxServiceHints) { break }
                $svcKey = Normalize-NameKey -Value $svc.displayName
                if ($svcKey -and $svcKey.Contains($nameKey)) {
                    [void]$serviceSet.Add($svc.name)
                }
            }
        }

        $app.processHints = @($processSet.ToArray())
        $app.serviceHints = @($serviceSet.ToArray())

        $artifactRoots = New-StringSet
        if (-not [string]::IsNullOrWhiteSpace($app.installRoot)) {
            [void]$artifactRoots.Add($app.installRoot)
        }

        $registryInstall = $null
        if ($app.registry -and $app.registry.PSObject.Properties['installLocation']) {
            $registryInstall = $app.registry.installLocation
        }

        if (-not [string]::IsNullOrWhiteSpace($registryInstall)) {
            $resolvedRegistryInstall = Resolve-TidyPath -Value $registryInstall
            if (-not [string]::IsNullOrWhiteSpace($resolvedRegistryInstall)) {
                [void]$artifactRoots.Add($resolvedRegistryInstall)
            }
        }

        if ($app.packageFamilyName) {
            $windowsApps = Join-Path -Path $env:ProgramFiles -ChildPath "WindowsApps\$($app.packageFamilyName)*"
            [void]$artifactRoots.Add($windowsApps)
        }

        $app.artifactHints = @($artifactRoots.ToArray())
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
    $resolved = Resolve-TidyPath -Value $OutputPath
    $directory = Split-Path -Parent $resolved
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
        [void](New-Item -Path $directory -ItemType Directory -Force)
    }

    $json | Out-File -FilePath $resolved -Encoding utf8 -Force
}

$json
