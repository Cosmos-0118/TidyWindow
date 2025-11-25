param(
    [switch] $IncludeScoop,
    [switch] $IncludeChocolatey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$callerModulePath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath) -and (Get-Variable -Name PSCommandPath -Scope Script -ErrorAction SilentlyContinue)) {
    $callerModulePath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -Path $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$script:CommandPathCache = @{}

function Get-CachedTidyCommandPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CommandName
    )

    if ([string]::IsNullOrWhiteSpace($CommandName)) {
        return $null
    }

    $key = $CommandName.ToLowerInvariant()
    if ($script:CommandPathCache.ContainsKey($key)) {
        $cached = $script:CommandPathCache[$key]
        if ([string]::IsNullOrWhiteSpace([string]$cached)) {
            return $null
        }

        return [string]$cached
    }

    $resolved = Get-TidyCommandPath -CommandName $CommandName
    $script:CommandPathCache[$key] = $resolved
    return $resolved
}

function Set-CachedTidyCommandPath {
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

function Write-TidyOutput {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    Write-Output $text
}

function Write-TidyError {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    Write-Error -Message $text
}

function Get-TidyCommandVersion {
    param(
        [Parameter(Mandatory = $false)]
        [string] $CommandPath,
        [Parameter(Mandatory = $true)]
        [string] $CommandName
    )

    if ([string]::IsNullOrWhiteSpace($CommandPath)) {
        $CommandPath = Get-CachedTidyCommandPath -CommandName $CommandName
    }
    elseif (-not [string]::IsNullOrWhiteSpace($CommandName)) {
        Set-CachedTidyCommandPath -CommandName $CommandName -CommandPath $CommandPath
    }

    if ([string]::IsNullOrWhiteSpace($CommandPath)) {
        return $null
    }

    try {
        $versionOutput = & $CommandPath '--version' 2>$null | Select-Object -First 1
        $candidate = $versionOutput
        if ($candidate -is [System.Management.Automation.ErrorRecord]) {
            $candidate = $candidate.ToString()
        }

        $candidateText = Convert-TidyLogMessage -InputObject $candidate
        if (-not [string]::IsNullOrWhiteSpace($candidateText) -and $candidateText -match '\d') {
            return $candidateText.Trim()
        }
    }
    catch {
        # fall back to file version
    }

    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($CommandPath)
        if ($info -and -not [string]::IsNullOrWhiteSpace($info.FileVersion)) {
            return $info.FileVersion.Trim()
        }
    }
    catch {
        return $null
    }

    return $null
}

function Test-TidyCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CommandName
    )

    $commandPath = Get-CachedTidyCommandPath -CommandName $CommandName
    -not [string]::IsNullOrWhiteSpace($commandPath)
}

function Test-ChocolateyInstalled {
    if (Test-TidyCommand -CommandName 'choco') {
        return $true
    }

    $candidatePaths = @()

    if ($env:ChocolateyInstall) {
        $candidatePaths += Join-Path -Path $env:ChocolateyInstall -ChildPath 'bin\choco.exe'
    }

    $candidatePaths += 'C:\ProgramData\chocolatey\bin\choco.exe'

    foreach ($path in $candidatePaths) {
        if (Test-Path -LiteralPath $path) {
            Set-CachedTidyCommandPath -CommandName 'choco' -CommandPath $path
            return $true
        }
    }

    return $false
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

function Test-ScoopInstalled {
    if (Test-TidyCommand -CommandName 'scoop') {
        return $true
    }

    $candidatePaths = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($root in Get-TidyScoopRootCandidates) {
        foreach ($relative in @('shims\scoop.exe', 'shims\scoop.cmd')) {
            $path = Join-Path -Path $root -ChildPath $relative
            if ($seen.Add($path)) {
                [void]$candidatePaths.Add($path)
            }
        }
    }

    foreach ($path in $candidatePaths) {
        if (Test-Path -LiteralPath $path) {
            Set-CachedTidyCommandPath -CommandName 'scoop' -CommandPath $path
            return $true
        }
    }

    return $false
}

$results = New-Object System.Collections.Generic.List[object]

Write-TidyLog -Level Information -Message 'Detecting package manager availability.'
Write-TidyLog -Level Information -Message ("Include Chocolatey: {0}; Include Scoop: {1}" -f $IncludeChocolatey.IsPresent, $IncludeScoop.IsPresent)

function Add-TidyManagerResult {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [Parameter(Mandatory = $true)]
        [string] $DisplayName,
        [Parameter(Mandatory = $true)]
        [bool] $IsInstalled,
        [Parameter(Mandatory = $true)]
        [string] $Notes,
        [Parameter(Mandatory = $false)]
        [string] $CommandPath,
        [Parameter(Mandatory = $false)]
        [string] $Version
    )

    $summary = if ($IsInstalled) {
        if (-not [string]::IsNullOrWhiteSpace($Version)) {
            "{0} detected • version {1}" -f $DisplayName, $Version
        }
        elseif (-not [string]::IsNullOrWhiteSpace($CommandPath)) {
            "{0} detected at {1}" -f $DisplayName, $CommandPath
        }
        else {
            "{0} detected." -f $DisplayName
        }
    }
    else {
        "{0} not detected." -f $DisplayName
    }

    Write-TidyLog -Level Information -Message $summary
    Write-TidyOutput -Message $summary

    $payload = [pscustomobject]@{
        Name             = $Name
        DisplayName      = $DisplayName
        Found            = $IsInstalled
        Notes            = $Notes
        CommandPath      = $CommandPath
        InstalledVersion = $Version
    }

    $results.Add($payload) | Out-Null
}

try {
    $wingetPath = Get-CachedTidyCommandPath -CommandName 'winget'
    $wingetFound = -not [string]::IsNullOrWhiteSpace($wingetPath)
    if (-not $wingetFound) {
        $candidate = Join-Path -Path ([Environment]::GetFolderPath('LocalApplicationData')) -ChildPath 'Microsoft\WindowsApps\winget.exe'
        if (Test-Path -LiteralPath $candidate) {
            $wingetPath = $candidate
            $wingetFound = $true
            Set-CachedTidyCommandPath -CommandName 'winget' -CommandPath $wingetPath
        }
    }

    $wingetVersion = $null
    if ($wingetFound) {
        $wingetVersion = Get-TidyCommandVersion -CommandPath $wingetPath -CommandName 'winget'
    }

    Add-TidyManagerResult -Name 'winget' -DisplayName 'Windows Package Manager client' -IsInstalled:$wingetFound -Notes 'Windows Package Manager client' -CommandPath $wingetPath -Version $wingetVersion

    if ($IncludeChocolatey) {
        $chocoFound = Test-ChocolateyInstalled
        $chocoPath = if ($chocoFound) { Get-CachedTidyCommandPath -CommandName 'choco' } else { $null }
        $chocoVersion = if ($chocoFound -and $chocoPath) {
            Get-TidyCommandVersion -CommandPath $chocoPath -CommandName 'choco'
        }
        else {
            $null
        }

        Add-TidyManagerResult -Name 'choco' -DisplayName 'Chocolatey CLI' -IsInstalled:$chocoFound -Notes 'Chocolatey CLI' -CommandPath $chocoPath -Version $chocoVersion
    }

    if ($IncludeScoop) {
        $scoopFound = Test-ScoopInstalled
        $scoopPath = if ($scoopFound) { Get-CachedTidyCommandPath -CommandName 'scoop' } else { $null }
        $scoopVersion = if ($scoopFound -and $scoopPath) {
            try {
                (& $scoopPath '--version' 2>$null | Select-Object -First 1).Trim()
            }
            catch {
                $null
            }
        }
        else {
            $null
        }

        Add-TidyManagerResult -Name 'scoop' -DisplayName 'Scoop package manager' -IsInstalled:$scoopFound -Notes 'Scoop package manager' -CommandPath $scoopPath -Version $scoopVersion
    }

    $installedCount = 0
    foreach ($entry in $results) {
        if ($null -ne $entry -and $entry.Found) {
            $installedCount++
        }
    }

    $missingCount = $results.Count - $installedCount

    Write-TidyLog -Level Information -Message ("Package manager detection completed • {0} detected." -f $installedCount)
    Write-TidyOutput -Message ("Detection summary • {0} detected, {1} missing." -f $installedCount, $missingCount)
}
catch {
    $message = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($message)) {
        $message = $_ | Out-String
    }

    Write-TidyLog -Level Error -Message $message
    Write-TidyError -Message $message
    throw
}

$resultsJson = $results | ConvertTo-Json -Depth 5 -Compress
Write-Output $resultsJson

