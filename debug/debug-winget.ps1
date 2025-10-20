param(
    [string] $PackageId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($scriptPath)) {
    $scriptPath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $scriptPath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$command = Get-Command -Name 'winget' -ErrorAction Stop
$exe = if (-not [string]::IsNullOrWhiteSpace($command.Source)) { $command.Source } else { $command.Name }

function Get-DebugWingetPropertyValue {
    param(
        [psobject] $Candidate,
        [string[]] $Names
    )

    if (-not $Candidate -or -not $Names) {
        return $null
    }

    foreach ($name in $Names) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $property = $Candidate.PSObject.Properties[$name]
        if ($property -and $null -ne $property.Value) {
            $value = $property.Value
            if ($value -is [string]) {
                $trimmed = $value.Trim()
                if (-not [string]::IsNullOrWhiteSpace($trimmed)) { return $trimmed }
            }
            else {
                return $value
            }
        }
    }

    return $null
}

function Get-DebugWingetCandidates {
    param($Payload)

    $results = @()
    if ($null -eq $Payload) { return ,$results }

    if ($Payload -is [System.Collections.IEnumerable] -and $Payload -isnot [string]) {
        $results = @($Payload)
    }
    elseif ($Payload -is [System.Collections.IDictionary]) {
        foreach ($key in @('InstalledPackages', 'installedPackages', 'Packages', 'packages', 'Items', 'items')) {
            if ($Payload.Contains($key) -and $Payload[$key]) {
                $results = @($Payload[$key])
                break
            }
        }

        if ($results.Count -eq 0) {
            foreach ($entry in $Payload.Values) {
                if ($entry -is [System.Collections.IEnumerable] -and $entry -isnot [string]) {
                    $results += @($entry)
                }
                elseif ($null -ne $entry) {
                    $results += ,$entry
                }
            }
        }
    }

    return ,@($results)
}

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

        $sanitizedLines = @()
        foreach ($segment in ($entryText -split "`n")) {
            $clean = if ($segment -is [string]) {
                [System.Text.RegularExpressions.Regex]::Replace($segment, '\x1B\[[0-9;]*[A-Za-z]', '')
            } else {
                $segment
            }

            if ($clean -is [string]) {
                $clean = $clean.Replace("`r", '')
            }

            $sanitizedLines += ,$clean
        }

        foreach ($line in $sanitizedLines) {
            if ($line -is [string]) {
                if (-not [string]::IsNullOrWhiteSpace($line)) {
                    Write-Host "    $line"
                }
                elseif ($line.Length -gt 0) {
                    Write-Host ("    <whitespace len={0}>" -f $line.Length)
                }
                else {
                    Write-Host '    '
                }
            }
            else {
                Write-Host "    $line"
            }
        }
    }
}

Write-DebugSection -Title 'winget --version'
try {
    & $exe '--version' | ForEach-Object { Write-Host "    $_" }
}
catch {
    Write-Host "    <error> $_"
}

if ([string]::IsNullOrWhiteSpace($PackageId)) {
    Write-DebugSection -Title 'winget list (default)'
    $listOutput = & $exe 'list' '--disable-interactivity' '--accept-source-agreements' 2>&1
}
else {
    Write-DebugSection -Title "winget list --id $PackageId"
    $listOutput = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>&1
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
    Write-DebugSection -Title "winget list --id $PackageId --output json"
    $jsonOutput = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' '--output' 'json' 2>&1
    if ($jsonOutput) {
        $index = 0
        foreach ($entry in $jsonOutput) {
            $index++
            Write-DebugEntry -Index $index -Entry $entry
        }

        Write-DebugSection -Title 'Parse JSON payload'
        try {
            $payload = ConvertFrom-Json -InputObject ($jsonOutput -join [Environment]::NewLine) -ErrorAction Stop
            $packages = Get-DebugWingetCandidates -Payload $payload
            if ($packages.Count -eq 0) {
                Write-Host '    <no candidates>'
            }
            else {
                $idx = 0
                foreach ($pkg in $packages) {
                    $idx++
                    $id = Get-DebugWingetPropertyValue -Candidate $pkg -Names @('PackageIdentifier', 'Id', 'Package', 'Name')
                    $installed = Get-DebugWingetPropertyValue -Candidate $pkg -Names @('InstalledVersion', 'installedVersion', 'Version')
                    $available = Get-DebugWingetPropertyValue -Candidate $pkg -Names @('AvailableVersion', 'availableVersion', 'Version')
                    Write-Host ("    [{0}] Id={1} Installed={2} Available={3}" -f $idx, $id, $installed, $available)
                }
            }
        }
        catch {
            Write-Host "    <parse error> $_"
        }
    }
    else {
        Write-Host '    <no output>'
    }

    Write-DebugSection -Title "Get-TidyWingetInstalledVersion $PackageId"
    try {
        $detected = Get-TidyInstalledPackageVersion -Manager 'winget' -PackageId $PackageId
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

    Write-DebugSection -Title "winget show --id $PackageId --output json"
    $showOutput = & $exe 'show' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' '--output' 'json' 2>&1
    if ($showOutput) {
        $index = 0
        foreach ($entry in $showOutput) {
            $index++
            Write-DebugEntry -Index $index -Entry $entry
        }
    }
    else {
        Write-Host '    <no output>'
    }
}
