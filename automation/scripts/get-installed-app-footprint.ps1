[CmdletBinding()]
param(
    [string[]] $Managers = @('winget', 'choco', 'scoop'),
    [switch] $IncludeAppx = $true,
    [switch] $IncludeAllUsersAppx = $true,
    [switch] $IncludeSteam = $true,
    [switch] $IncludeEpic = $true,
    [switch] $IncludePortable = $true,
    [switch] $IncludeShortcuts = $true,
    [string[]] $PortableRoots = @(),
    [string] $PortableManifestPath,
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

$modulePath = Get-ScriptRelativePath -Relative '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "Automation module not found at '$modulePath'."
}

Import-Module $modulePath -Force

$inventoryScript = Get-ScriptRelativePath -Relative 'get-package-inventory.ps1'
if (-not (Test-Path -LiteralPath $inventoryScript)) {
    throw "Dependent inventory script not found at '$inventoryScript'."
}

if (-not $PortableManifestPath) {
    $PortableManifestPath = Get-ScriptRelativePath -Relative '..\..\data\catalog\oblivion-portables.json'
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

function Get-PortableAppId {
    param([string] $Seed)

    if ([string]::IsNullOrWhiteSpace($Seed)) {
        return "portable:$([Guid]::NewGuid().ToString('n'))"
    }

    $normalized = $Seed.Trim().ToLowerInvariant()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($normalized)
        $hashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $hashAlgorithm.ComputeHash($bytes)
        }
        finally {
            $hashAlgorithm.Dispose()
        }

        $token = [BitConverter]::ToString($hash).Replace('-', '').Substring(0, 20)
        return "portable:$token"
    }
    catch {
        return "portable:$([Guid]::NewGuid().ToString('n'))"
    }
}

function Get-PortableRootCandidates {
    param([string[]] $AdditionalRoots)

    $roots = New-StringSet

    $defaultCandidates = @()
    if ($env:LOCALAPPDATA) {
        $defaultCandidates += (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Programs')
        $defaultCandidates += (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Apps')
        $defaultCandidates += (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'PortableApps')
    }
    if ($env:USERPROFILE) {
        $defaultCandidates += (Join-Path -Path $env:USERPROFILE -ChildPath 'PortableApps')
        $defaultCandidates += (Join-Path -Path $env:USERPROFILE -ChildPath 'Apps')
        $defaultCandidates += (Join-Path -Path $env:USERPROFILE -ChildPath 'Tools')
    }
    if ($env:ProgramFiles) {
        $defaultCandidates += (Join-Path -Path $env:ProgramFiles -ChildPath 'PortableApps')
    }
    $candidates = $defaultCandidates + @($AdditionalRoots)

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $resolved = Resolve-AppFootprintPath -Value $candidate
        if ($resolved) { [void]$roots.Add($resolved) }
    }

    return Convert-SetToArray -Set $roots
}

function Get-PortableApplications {
    param(
        [string[]] $PortableRoots,
        [System.Collections.Generic.HashSet[string]] $KnownInstallRoots,
        [System.Collections.Generic.List[string]] $Warnings
    )

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    if (-not $PortableRoots -or $PortableRoots.Count -eq 0) {
        return $results
    }

    foreach ($root in $PortableRoots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        if (-not (Test-Path -LiteralPath $root)) { continue }

        $directories = @()
        try {
            $directories = Get-ChildItem -LiteralPath $root -Directory -ErrorAction Stop
        }
        catch {
            if ($Warnings) { $Warnings.Add("Portable scan failed at $($root): $($_.Exception.Message)") | Out-Null }
            continue
        }

        foreach ($dir in $directories) {
            $installRoot = $dir.FullName
            if ($KnownInstallRoots.Contains($installRoot)) { continue }

            $exe = Get-ChildItem -LiteralPath $installRoot -Filter '*.exe' -File -Recurse -ErrorAction SilentlyContinue |
                Sort-Object Length -Descending |
                Select-Object -First 1
            if (-not $exe) { continue }

            $name = $exe.VersionInfo.FileDescription
            if ([string]::IsNullOrWhiteSpace($name)) { $name = $dir.Name }
            $version = $exe.VersionInfo.ProductVersion
            $publisher = $exe.VersionInfo.CompanyName

            $appId = Get-PortableAppId -Seed $installRoot
            $record = New-AppRecord -AppId $appId -Name $name -Version $version -Source 'portable' -Scope 'User' -InstallRoot $installRoot -UninstallCommand $null -QuietUninstallCommand $null -PackageFamily $null -Tags @('portable','manual')
            if ($publisher) { $record.publisher = $publisher }
            $record.artifactHints = @($installRoot)

            $sizeBytes = Measure-TidyDirectoryBytes -Path $installRoot
            if ($sizeBytes -gt 0) { $record.estimatedSizeBytes = [long]$sizeBytes }

            $results.Add($record) | Out-Null
            [void]$KnownInstallRoots.Add($installRoot)
        }
    }

    return $results
}

function Get-ShortcutApplications {
    param(
        [System.Collections.Generic.HashSet[string]] $KnownInstallRoots,
        [System.Collections.Generic.List[string]] $Warnings
    )

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    $shortcutDirs = New-StringSet
    if ($env:ProgramData) {
        [void]$shortcutDirs.Add((Join-Path -Path $env:ProgramData -ChildPath 'Microsoft\Windows\Start Menu\Programs'))
    }
    if ($env:APPDATA) {
        [void]$shortcutDirs.Add((Join-Path -Path $env:APPDATA -ChildPath 'Microsoft\Windows\Start Menu\Programs'))
    }

    $shell = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
    }
    catch {
        if ($Warnings) { $Warnings.Add("Shortcut scan unavailable: $($_.Exception.Message)") | Out-Null }
        return $results
    }

    $seenTargets = New-StringSet
    foreach ($dir in Convert-SetToArray -Set $shortcutDirs) {
        if (-not $dir -or -not (Test-Path -LiteralPath $dir)) { continue }
        $links = Get-ChildItem -LiteralPath $dir -Filter '*.lnk' -File -Recurse -ErrorAction SilentlyContinue
        foreach ($link in @($links)) {
            $target = $null
            try {
                $shortcut = $shell.CreateShortcut($link.FullName)
                $target = $shortcut.TargetPath
            }
            catch {
                continue
            }

            if ([string]::IsNullOrWhiteSpace($target)) { continue }
            if (-not $target.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            $resolved = Resolve-AppFootprintPath -Value $target
            if (-not $resolved -or -not (Test-Path -LiteralPath $resolved)) { continue }
            if (-not $seenTargets.Add($resolved)) { continue }

            $installRoot = Split-Path -Parent $resolved
            if ([string]::IsNullOrWhiteSpace($installRoot)) { continue }
            if ($KnownInstallRoots.Contains($installRoot)) { continue }

            try { $fileInfo = Get-Item -LiteralPath $resolved -ErrorAction Stop } catch { continue }
            $versionInfo = $fileInfo.VersionInfo
            $name = if ($versionInfo.FileDescription) { $versionInfo.FileDescription } else { [System.IO.Path]::GetFileNameWithoutExtension($resolved) }
            $version = $versionInfo.ProductVersion
            $publisher = $versionInfo.CompanyName

            $appId = Get-PortableAppId -Seed $resolved
            $record = New-AppRecord -AppId $appId -Name $name -Version $version -Source 'shortcut' -Scope 'User' -InstallRoot $installRoot -UninstallCommand $null -QuietUninstallCommand $null -PackageFamily $null -Tags @('shortcut','manual')
            if ($publisher) { $record.publisher = $publisher }
            $record.artifactHints = @($installRoot, $resolved) | Where-Object { $_ }

            $results.Add($record) | Out-Null
            [void]$KnownInstallRoots.Add($installRoot)
        }
    }

    if ($shell) {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }

    return $results
}

function Get-CustomPortableApplications {
    param(
        [string] $ManifestPath,
        [System.Collections.Generic.HashSet[string]] $KnownInstallRoots,
        [System.Collections.Generic.List[string]] $Warnings
    )

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    if (-not $ManifestPath -or -not (Test-Path -LiteralPath $ManifestPath)) {
        return $results
    }

    $json = $null
    try {
        $json = Get-Content -LiteralPath $ManifestPath -Raw -ErrorAction Stop
    }
    catch {
        if ($Warnings) { $Warnings.Add("Failed to read portable manifest: $($_.Exception.Message)") | Out-Null }
        return $results
    }

    $payload = Try-ParseJson -Json $json
    if (-not $payload) {
        if ($Warnings) { $Warnings.Add('Portable manifest JSON could not be parsed.') | Out-Null }
        return $results
    }

    $entries = @($payload)
    foreach ($entry in $entries) {
        if (-not $entry) { continue }
        $name = $entry.name
        if ([string]::IsNullOrWhiteSpace($name)) { $name = $entry.displayName }
        if ([string]::IsNullOrWhiteSpace($name)) { continue }

        $paths = @()
        if ($entry.paths) { $paths = @($entry.paths) }
        elseif ($entry.path) { $paths = @($entry.path) }

        $installRoot = $null
        foreach ($candidate in $paths) {
            if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
            $resolved = Resolve-AppFootprintPath -Value $candidate
            if ($resolved -and (Test-Path -LiteralPath $resolved)) {
                $installRoot = $resolved
                break
            }
        }

        $seed = if ($installRoot) { "$name|$installRoot" } else { $name }
        $appId = $entry.appId
        if ([string]::IsNullOrWhiteSpace($appId)) {
            $appId = Get-PortableAppId -Seed $seed
        }

        $source = if ($entry.source) { $entry.source } else { 'manifest' }
        $scope = if ($entry.scope) { $entry.scope } else { 'User' }
        $uninstallCommand = $entry.uninstallCommand
        $quietUninstallCommand = $entry.quietUninstallCommand
        $version = $entry.version

        $record = New-AppRecord -AppId $appId -Name $name -Version $version -Source $source -Scope $scope -InstallRoot $installRoot -UninstallCommand $uninstallCommand -QuietUninstallCommand $quietUninstallCommand -PackageFamily $null -Tags @('portable','manifest')

        if ($entry.publisher) { $record.publisher = $entry.publisher }
        if ($entry.tags) { $record.tags = @('portable','manifest') + @($entry.tags) }

        if ($entry.artifactHints) {
            $record.artifactHints = @($entry.artifactHints)
        }
        elseif ($installRoot) {
            $record.artifactHints = @($installRoot)
        }

        if ($entry.estimatedSizeBytes) {
            $record.estimatedSizeBytes = [long]$entry.estimatedSizeBytes
        }
        elseif ($entry.estimatedSizeMb) {
            try {
                $record.estimatedSizeBytes = [long]($entry.estimatedSizeMb * 1MB)
            }
            catch { }
        }

        $results.Add($record) | Out-Null
        if ($installRoot) { [void]$KnownInstallRoots.Add($installRoot) }
    }

    return $results
}

function Get-SteamLibraryFolders {
    param([System.Collections.Generic.List[string]] $Warnings)

    $paths = New-StringSet
    $rootCandidates = New-StringSet

    $programFiles = $env:ProgramFiles
    $programFilesX86 = ${env:ProgramFiles(x86)}
    $localAppData = $env:LOCALAPPDATA

    if ($programFiles) {
        $resolved = Resolve-AppFootprintPath -Value (Join-Path -Path $programFiles -ChildPath 'Steam')
        if ($resolved) { [void]$rootCandidates.Add($resolved) }
        $resolved = Resolve-AppFootprintPath -Value (Join-Path -Path $programFiles -ChildPath 'SteamLibrary')
        if ($resolved) { [void]$rootCandidates.Add($resolved) }
    }
    if ($programFilesX86) {
        $resolved = Resolve-AppFootprintPath -Value (Join-Path -Path $programFilesX86 -ChildPath 'Steam')
        if ($resolved) { [void]$rootCandidates.Add($resolved) }
        $resolved = Resolve-AppFootprintPath -Value (Join-Path -Path $programFilesX86 -ChildPath 'SteamLibrary')
        if ($resolved) { [void]$rootCandidates.Add($resolved) }
    }
    if ($localAppData) {
        $resolved = Resolve-AppFootprintPath -Value (Join-Path -Path $localAppData -ChildPath 'Steam')
        if ($resolved) { [void]$rootCandidates.Add($resolved) }
    }
    [void]$rootCandidates.Add('C:\Program Files (x86)\Steam')

    foreach ($root in Convert-SetToArray -Set $rootCandidates) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        if (Test-Path -LiteralPath $root) {
            [void]$paths.Add($root)
        }

        $libraryFile = Join-Path -Path $root -ChildPath 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $libraryFile)) { continue }

        try {
            foreach ($line in Get-Content -LiteralPath $libraryFile -ErrorAction Stop) {
                if ($line -match '"path"\s+"(?<path>[^"]+)"') {
                    $raw = $matches['path'].Replace('\\', '\')
                    $resolved = Resolve-AppFootprintPath -Value $raw
                    if ($resolved -and (Test-Path -LiteralPath $resolved)) {
                        [void]$paths.Add($resolved)
                    }
                }
            }
        }
        catch {
            if ($Warnings) { $Warnings.Add("Failed to read ${libraryFile}: $($_.Exception.Message)") | Out-Null }
        }
    }

    return Convert-SetToArray -Set $paths
}

function Read-SteamManifestProperties {
    param([string] $Path)

    $map = @{}
    try {
        foreach ($line in Get-Content -LiteralPath $Path -ErrorAction Stop) {
            if ($line -match '"(?<key>[^"]+)"\s+"(?<value>[^"]*)"') {
                $map[$matches['key']] = $matches['value']
            }
        }
    }
    catch {
        return $map
    }

    return $map
}

function Get-SteamApplications {
    param([System.Collections.Generic.List[string]] $Warnings)

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    $libraries = Get-SteamLibraryFolders -Warnings $Warnings
    foreach ($library in $libraries) {
        if ([string]::IsNullOrWhiteSpace($library)) { continue }
        $steamAppsRoot = Join-Path -Path $library -ChildPath 'steamapps'
        if (-not (Test-Path -LiteralPath $steamAppsRoot)) { continue }

        $commonRoot = Join-Path -Path $steamAppsRoot -ChildPath 'common'
        $manifests = Get-ChildItem -LiteralPath $steamAppsRoot -Filter 'appmanifest_*.acf' -File -ErrorAction SilentlyContinue
        foreach ($manifest in @($manifests)) {
            $data = Read-SteamManifestProperties -Path $manifest.FullName
            $appId = $data['appid']
            $name = $data['name']
            if ([string]::IsNullOrWhiteSpace($appId) -or [string]::IsNullOrWhiteSpace($name)) { continue }

            $installDirName = $data['installdir']
            $installRoot = $null
            if ($installDirName) {
                $installRootCandidate = Join-Path -Path $commonRoot -ChildPath $installDirName
                if ($installRootCandidate) {
                    $installRoot = Resolve-AppFootprintPath -Value $installRootCandidate
                }
            }

            $sizeBytes = $null
            if ($data['SizeOnDisk']) {
                try {
                    $parsed = [long]$data['SizeOnDisk']
                    if ($parsed -gt 0) { $sizeBytes = $parsed }
                }
                catch {
                    $sizeBytes = Resolve-EstimatedSizeBytes -Value $data['SizeOnDisk']
                }
            }

            $record = New-AppRecord -AppId "steam:$appId" -Name $name -Version $data['buildid'] -Source 'steam' -Scope 'User' -InstallRoot $installRoot -UninstallCommand "steam://uninstall/$appId" -QuietUninstallCommand $null -PackageFamily $null -Tags @('steam','game')
            $record.publisher = 'Valve'
            if ($sizeBytes) { $record.estimatedSizeBytes = $sizeBytes }

            $hints = New-Object 'System.Collections.Generic.List[string]'
            if ($installRoot) { $hints.Add($installRoot) | Out-Null }
            $compatRootBase = Join-Path -Path $steamAppsRoot -ChildPath 'compatdata'
            $compatRoot = Join-Path -Path $compatRootBase -ChildPath $appId
            if (Test-Path -LiteralPath $compatRoot) { $hints.Add($compatRoot) | Out-Null }
            $record.artifactHints = @($hints.ToArray())

            $results.Add($record) | Out-Null
        }
    }

    return $results
}

function Get-EpicApplications {
    param([System.Collections.Generic.List[string]] $Warnings)

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    $manifestDirs = New-StringSet

    if ($env:ProgramData) {
        [void]$manifestDirs.Add((Join-Path -Path $env:ProgramData -ChildPath 'Epic\EpicGamesLauncher\Data\Manifests'))
    }
    [void]$manifestDirs.Add('C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests')
    if ($env:LOCALAPPDATA) {
        [void]$manifestDirs.Add((Join-Path -Path $env:LOCALAPPDATA -ChildPath 'EpicGamesLauncher\Saved\Catalog'))
    }

    foreach ($dir in Convert-SetToArray -Set $manifestDirs) {
        if (-not $dir -or -not (Test-Path -LiteralPath $dir)) { continue }
        $manifests = Get-ChildItem -LiteralPath $dir -Filter '*.item' -File -ErrorAction SilentlyContinue
        foreach ($file in @($manifests)) {
            $payload = $null
            try { $payload = Try-ParseJson -Json (Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop) } catch { $payload = $null }
            if (-not $payload) { continue }

            $name = $null
            if ($payload -and $payload.PSObject.Properties['DisplayName']) { $name = $payload.DisplayName }
            if (-not $name -and $payload -and $payload.PSObject.Properties['AppName']) { $name = $payload.AppName }
            if (-not $name) { $name = $file.BaseName }
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $appKey = $null
            if ($payload -and $payload.PSObject.Properties['AppName']) { $appKey = $payload.AppName }
            if ([string]::IsNullOrWhiteSpace($appKey) -and $payload -and $payload.PSObject.Properties['CatalogItemId']) { $appKey = $payload.CatalogItemId }
            if ([string]::IsNullOrWhiteSpace($appKey)) { $appKey = $file.BaseName }

            $installLocation = $null
            if ($payload -and $payload.PSObject.Properties['InstallLocation']) { $installLocation = $payload.InstallLocation }
            if (-not $installLocation -and $payload -and $payload.PSObject.Properties['InstallFolder']) { $installLocation = $payload.InstallFolder }
            $installRoot = Resolve-AppFootprintPath -Value $installLocation
            $uninstallCommand = $null
            if ($payload -and $payload.PSObject.Properties['UninstallCommand']) {
                $uninstallCommand = $payload.UninstallCommand
            }
            if (-not $uninstallCommand -and $payload -and $payload.PSObject.Properties['AppName']) {
                $uninstallCommand = "start epicgames://uninstall/$($payload.AppName)"
            }

            $appVersion = $null
            if ($payload -and $payload.PSObject.Properties['AppVersion']) { $appVersion = $payload.AppVersion }

            $record = New-AppRecord -AppId "epic:$appKey" -Name $name -Version $appVersion -Source 'epic' -Scope 'User' -InstallRoot $installRoot -UninstallCommand $uninstallCommand -QuietUninstallCommand $null -PackageFamily $null -Tags @('epic','game')
            if ($payload -and $payload.PSObject.Properties['Publisher']) {
                $record.publisher = $payload.Publisher
            }

            if ($payload -and $payload.PSObject.Properties['InstallSize'] -and $payload.InstallSize) {
                try {
                    $installBytes = [long]$payload.InstallSize
                    if ($installBytes -gt 0) { $record.estimatedSizeBytes = $installBytes }
                }
                catch {
                    $record.estimatedSizeBytes = Resolve-EstimatedSizeBytes -Value $payload.InstallSize
                }
            }

            $record.artifactHints = @(($installRoot) | Where-Object { $_ })

            $results.Add($record) | Out-Null
        }
    }

    return $results
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

$steamApps = @()
if ($IncludeSteam) {
    $steamApps = Get-SteamApplications -Warnings $warnings
}

$epicApps = @()
if ($IncludeEpic) {
    $epicApps = Get-EpicApplications -Warnings $warnings
}

$allApps = @($registryApps + $appxApps + $steamApps + $epicApps)

$knownInstallRoots = New-StringSet
foreach ($app in $allApps) {
    if ($app.installRoot) { [void]$knownInstallRoots.Add($app.installRoot) }
    if ($app.installRoots) {
        foreach ($root in $app.installRoots) {
            if ($root) { [void]$knownInstallRoots.Add($root) }
        }
    }
}

$portableApps = @()
if ($IncludePortable) {
    $portableRootCandidates = Get-PortableRootCandidates -AdditionalRoots $PortableRoots
    $portableApps = Get-PortableApplications -PortableRoots $portableRootCandidates -KnownInstallRoots $knownInstallRoots -Warnings $warnings
}

$shortcutApps = @()
if ($IncludeShortcuts) {
    $shortcutApps = Get-ShortcutApplications -KnownInstallRoots $knownInstallRoots -Warnings $warnings
}

$manifestApps = Get-CustomPortableApplications -ManifestPath $PortableManifestPath -KnownInstallRoots $knownInstallRoots -Warnings $warnings

$allApps = @($allApps + $portableApps + $shortcutApps + $manifestApps)

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

