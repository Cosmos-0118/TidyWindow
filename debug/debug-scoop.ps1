param(
    [string] $PackageId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$command = Get-Command -Name 'scoop' -ErrorAction Stop
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

Write-DebugSection -Title 'scoop --version'
try {
    & $exe '--version' | ForEach-Object { Write-Host "    $_" }
}
catch {
    Write-Host "    <error> $_"
}

if ([string]::IsNullOrWhiteSpace($PackageId)) {
    Write-DebugSection -Title 'scoop list'
    $listOutput = & $exe 'list' 2>&1
}
else {
    Write-DebugSection -Title "scoop list $PackageId"
    $listOutput = & $exe 'list' $PackageId 2>&1
}

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

if (-not [string]::IsNullOrWhiteSpace($PackageId)) {
    Write-DebugSection -Title "scoop info $PackageId"
    $infoOutput = & $exe 'info' $PackageId 2>&1
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
