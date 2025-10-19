param(
    [switch] $IncludeScoop,
    [switch] $IncludeChocolatey
)

$callerModulePath = $PSCmdlet.MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath)) {
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

Write-TidyLog -Level Information -Message 'Detecting package managers available on this machine.'
Write-TidyLog -Level Information -Message ("IncludeChocolatey switch present: {0}; IncludeScoop switch present: {1}" -f $IncludeChocolatey.IsPresent, $IncludeScoop.IsPresent)

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
        if (Test-Path -Path $path) {
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
        if (Test-Path -Path $path) {
            return $true
        }
    }

    return $false
}

$results = @()

$wingetFound = Test-TidyCommand -CommandName 'winget'
Write-TidyLog -Level Information -Message ("winget detected: {0}" -f $wingetFound)
$results += [pscustomobject]@{
    Name   = 'winget'
    Found  = $wingetFound
    Notes  = 'Windows Package Manager client'
}

if ($IncludeChocolatey) {
    $chocoFound = Test-ChocolateyInstalled
    Write-TidyLog -Level Information -Message ("Chocolatey detected: {0}" -f $chocoFound)
    $results += [pscustomobject]@{
        Name   = 'choco'
        Found  = $chocoFound
        Notes  = 'Chocolatey CLI'
    }
}

if ($IncludeScoop) {
    $scoopFound = Test-ScoopInstalled
    Write-TidyLog -Level Information -Message ("Scoop detected: {0}" -f $scoopFound)
    $results += [pscustomobject]@{
        Name   = 'scoop'
        Found  = $scoopFound
        Notes  = 'Scoop package manager'
    }
}

$resultsJson = ConvertTo-Json -InputObject @($results) -Depth 3 -Compress
Write-Output $resultsJson
