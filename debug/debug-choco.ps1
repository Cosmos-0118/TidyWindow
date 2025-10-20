param(
    [string] $PackageId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$callerModulePath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath)) {
    $callerModulePath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$command = Get-Command -Name 'choco' -ErrorAction Stop
$exe = if (-not [string]::IsNullOrWhiteSpace($command.Source)) { $command.Source } else { $command.Name }

function Write-DebugSection {
    param([string] $Title)
    Write-Host "`n=== $Title ==="
}

function Write-DebugEntry {
    param(
        [int] $Index,
        $Entry
    )

    $typeName = if ($null -eq $Entry) { '<null>' } else { $Entry.GetType().FullName }
    Write-Host "[$Index] Type=$typeName"

    if ($Entry -is [pscustomobject]) {
        foreach ($prop in $Entry.PSObject.Properties) {
            Write-Host ("    {0}: {1}" -f $prop.Name, $prop.Value)
        }
    }
    else {
        $entryText = $Entry
        if ($Entry -isnot [string]) {
            try { $entryText = $Entry | Out-String } catch { $entryText = $Entry.ToString() }
        }

        foreach ($line in ($entryText -split "`n")) {
            Write-Host "    $line"
        }
    }
}

Write-DebugSection -Title 'choco --version'
try {
    & $exe '--version' | ForEach-Object { Write-Host "    $_" }
}
catch {
    Write-Host "    <error> $_"
}

if ([string]::IsNullOrWhiteSpace($PackageId)) {
    Write-DebugSection -Title 'Local Chocolatey packages'
}
else {
    Write-DebugSection -Title "choco list $PackageId --exact --limit-output"
    $listOutput = & $exe 'list' $PackageId '--exact' '--limit-output' 2>&1

    if ($listOutput) {
        $index = 0
        foreach ($entry in $listOutput) {
            $index++
            Write-DebugEntry -Index $index -Entry $entry
        }
    }
    else {
        Write-Host '    <no output>'
    }

    Write-DebugSection -Title "Get-TidyChocoInstalledVersion $PackageId"
    try {
        $detected = Get-TidyInstalledPackageVersion -Manager 'choco' -PackageId $PackageId
        if ([string]::IsNullOrWhiteSpace($detected)) {
            Write-Host '    <not detected>'
        }
        else {
            Write-Host "    $detected"
        }
    }
    catch {
        Write-Host "    <error> $_"
    }
}

if ([string]::IsNullOrWhiteSpace($PackageId)) {
    $listOutput = $null
}

$installRoot = $env:ChocolateyInstall
if ([string]::IsNullOrWhiteSpace($installRoot)) {
    $installRoot = 'C:\ProgramData\chocolatey'
}

$libRoot = Join-Path -Path $installRoot -ChildPath 'lib'
Write-DebugSection -Title "Chocolatey lib directory ($libRoot)"

if (-not (Test-Path -LiteralPath $libRoot)) {
    Write-Host '    <path not found>'
}
else {
    try {
        $libEntries = Get-ChildItem -Path $libRoot -Directory -ErrorAction Stop | Sort-Object Name
    }
    catch {
        $libEntries = @()
        Write-Host "    <error enumerating lib> $_"
    }

    if (-not $libEntries -or $libEntries.Count -eq 0) {
        Write-Host '    <no packages>'
    }
    else {
        $index = 0
        $limit = if ([string]::IsNullOrWhiteSpace($PackageId)) { 20 } else { $libEntries.Count }

        foreach ($entry in $libEntries) {
            $index++
            if ($index -gt $limit) {
                Write-Host "    <truncated after $limit entries>"
                break
            }

            $version = $null
            try {
                $version = Get-TidyInstalledPackageVersion -Manager 'choco' -PackageId $entry.Name
            }
            catch {
                $version = $null
            }

            $versionText = if ([string]::IsNullOrWhiteSpace($version)) { '<unknown>' } else { $version }
            Write-Host ("    [{0}] {1} :: {2}" -f $index, $entry.Name, $versionText)
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($PackageId)) {
    Write-DebugSection -Title "choco info $PackageId"
    $infoOutput = & $exe 'info' $PackageId '--limit-output' 2>&1
    if ($infoOutput) {
        $index = 0
        foreach ($entry in $infoOutput) {
            $index++
            Write-DebugEntry -Index $index -Entry $entry
        }
    }
    else {
        Write-Host '    <no output>'
    }
}
