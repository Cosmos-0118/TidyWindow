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

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -Path $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

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
        $CommandPath = Get-TidyCommandPath -CommandName $CommandName
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

    $null -ne (Get-Command -Name $CommandName -ErrorAction SilentlyContinue)
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
            return $true
        }
    }

    return $false
}

function Test-ScoopInstalled {
    if (Test-TidyCommand -CommandName 'scoop') {
        return $true
    }

    $candidatePaths = @()

    if ($env:SCOOP) {
        $candidatePaths += Join-Path -Path $env:SCOOP -ChildPath 'shims\scoop.cmd'
    }

    if ($env:USERPROFILE) {
        $candidatePaths += Join-Path -Path $env:USERPROFILE -ChildPath 'scoop\shims\scoop.cmd'
    }

    foreach ($path in $candidatePaths) {
        if (Test-Path -LiteralPath $path) {
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
        Name           = $Name
        DisplayName    = $DisplayName
        Found          = $IsInstalled
        Notes          = $Notes
        CommandPath    = $CommandPath
        InstalledVersion = $Version
    }

    $results.Add($payload) | Out-Null
}

try {
    $wingetPath = Get-TidyCommandPath -CommandName 'winget'
    $wingetFound = -not [string]::IsNullOrWhiteSpace($wingetPath)
    if (-not $wingetFound) {
        $candidate = Join-Path -Path ([Environment]::GetFolderPath('LocalApplicationData')) -ChildPath 'Microsoft\WindowsApps\winget.exe'
        if (Test-Path -LiteralPath $candidate) {
            $wingetPath = $candidate
            $wingetFound = $true
        }
    }

    $wingetVersion = $null
    if ($wingetFound) {
        $wingetVersion = Get-TidyCommandVersion -CommandPath $wingetPath -CommandName 'winget'
    }

    Add-TidyManagerResult -Name 'winget' -DisplayName 'Windows Package Manager client' -IsInstalled:$wingetFound -Notes 'Windows Package Manager client' -CommandPath $wingetPath -Version $wingetVersion

    if ($IncludeChocolatey) {
        $chocoFound = Test-ChocolateyInstalled
        $chocoPath = if ($chocoFound) { Get-TidyCommandPath -CommandName 'choco' } else { $null }
        $chocoVersion = if ($chocoFound -and $chocoPath) {
            Get-TidyCommandVersion -CommandPath $chocoPath -CommandName 'choco'
        } else {
            $null
        }

        Add-TidyManagerResult -Name 'choco' -DisplayName 'Chocolatey CLI' -IsInstalled:$chocoFound -Notes 'Chocolatey CLI' -CommandPath $chocoPath -Version $chocoVersion
    }

    if ($IncludeScoop) {
        $scoopFound = Test-ScoopInstalled
        $scoopPath = if ($scoopFound) { Get-TidyCommandPath -CommandName 'scoop' } else { $null }
        $scoopVersion = if ($scoopFound -and $scoopPath) {
            try {
                (& $scoopPath '--version' 2>$null | Select-Object -First 1).Trim()
            }
            catch {
                $null
            }
        } else {
            $null
        }

        Add-TidyManagerResult -Name 'scoop' -DisplayName 'Scoop package manager' -IsInstalled:$scoopFound -Notes 'Scoop package manager' -CommandPath $scoopPath -Version $scoopVersion
    }

    $installedCount = ($results | Where-Object { $_.Found }).Count
    Write-TidyLog -Level Information -Message ("Package manager detection completed • {0} detected." -f $installedCount)
    Write-TidyOutput -Message ("Detection summary • {0} detected, {1} missing." -f $installedCount, ($results.Count - $installedCount))
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
