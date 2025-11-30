param(
    [string] $RestorePointName,
    [switch] $BypassFrequencyCheck,
    [switch] $SkipVerification
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Convert-ArgumentList {
    param([hashtable] $Arguments)

    $list = New-Object System.Collections.Generic.List[string]

    foreach ($entry in $Arguments.GetEnumerator()) {
        $key = $entry.Key
        $value = $entry.Value

        if ($null -eq $value) {
            continue
        }

        if ($value -is [System.Management.Automation.SwitchParameter] -or $value -is [bool]) {
            if ([bool]$value) {
                $list.Add("-$key") | Out-Null
            }
            continue
        }

        if ($value -is [string] -and [string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        $list.Add("-$key") | Out-Null
        if ($value -is [string]) {
            $list.Add('"' + $value.Replace('"', '``"') + '"') | Out-Null
        }
        else {
            $list.Add('"' + ($value.ToString().Replace('"', '``"')) + '"') | Out-Null
        }
    }

    return $list.ToArray()
}

function Restart-Elevated {
    param(
        [string] $ScriptPath,
        [hashtable] $Arguments
    )

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', '"' + $ScriptPath + '"')
    $argList += Convert-ArgumentList -Arguments $Arguments

    Write-Host 'Requesting elevation...' -ForegroundColor Yellow
    $process = Start-Process -FilePath 'pwsh' -Verb RunAs -ArgumentList $argList -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        Write-Warning "Elevated run exited with code $($process.ExitCode)."
        exit $process.ExitCode
    }

    exit 0
}

$scriptPath = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
if (-not (Test-IsAdministrator)) {
    Restart-Elevated -ScriptPath $scriptPath -Arguments $PSBoundParameters
}

$scriptDirectory = Split-Path -Parent $scriptPath
$repoRoot = (Resolve-Path (Join-Path $scriptDirectory '..')).Path
$managerScript = Join-Path $repoRoot 'automation/essentials/system-restore-manager.ps1'
if (-not (Test-Path -Path $managerScript)) {
    throw "System restore manager script not found at '$managerScript'."
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$restoreName = if ([string]::IsNullOrWhiteSpace($RestorePointName)) { "TidyWindow-Restore-Test-$timestamp" } else { $RestorePointName }
$resultDirectory = Join-Path $repoRoot 'debug'
if (-not (Test-Path -Path $resultDirectory)) {
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
}
$resultFile = Join-Path $resultDirectory "restore-test-$timestamp.json"

$frequencyKey = 'HKLM:\\Software\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore'
$originalFrequency = $null
$frequencyOverridden = $false

if ($BypassFrequencyCheck) {
    try {
        $originalFrequency = (Get-ItemProperty -Path $frequencyKey -Name 'SystemRestorePointCreationFrequency' -ErrorAction Stop).SystemRestorePointCreationFrequency
    }
    catch {
        $originalFrequency = $null
    }

    Write-Host 'Temporarily disabling System Restore frequency lockout...'
    New-ItemProperty -Path $frequencyKey -Name 'SystemRestorePointCreationFrequency' -PropertyType DWord -Value 0 -Force | Out-Null
    $frequencyOverridden = $true
}

$invokeParams = @{
    Create           = $true
    RestorePointName = $restoreName
    RestorePointType = 'MODIFY_SETTINGS'
    ResultPath       = $resultFile
}

try {
    Write-Host "Creating restore point '$restoreName'..." -ForegroundColor Cyan
    & $managerScript @invokeParams
}
finally {
    if ($frequencyOverridden) {
        Write-Host 'Restoring System Restore frequency policy...'
        if ($null -eq $originalFrequency) {
            Remove-ItemProperty -Path $frequencyKey -Name 'SystemRestorePointCreationFrequency' -ErrorAction SilentlyContinue
        }
        else {
            Set-ItemProperty -Path $frequencyKey -Name 'SystemRestorePointCreationFrequency' -Value $originalFrequency | Out-Null
        }
    }
}

if (Test-Path -Path $resultFile) {
    $payload = Get-Content -Path $resultFile -Raw | ConvertFrom-Json
    if ($payload.Success) {
        Write-Host 'System restore manager reported success.' -ForegroundColor Green
    }
    else {
        Write-Warning 'System restore manager reported failure. See details below.'
        $payload.Errors | ForEach-Object { Write-Warning "  $_" }
    }
}
else {
    Write-Warning 'Result file was not produced. Check script output for errors.'
}

if (-not $SkipVerification) {
    Write-Host 'Verifying restore point presence...' -ForegroundColor Cyan
    $matching = Get-CimInstance -ClassName SystemRestore -ErrorAction SilentlyContinue |
        Where-Object { $_.Description -eq $restoreName } |
        Sort-Object -Property CreationTime -Descending

    if ($matching) {
        $entry = $matching | Select-Object -First 1
        Write-Host "Restore point confirmed: #$($entry.SequenceNumber) @ $($entry.CreationTime)" -ForegroundColor Green
    }
    else {
        Write-Warning 'Restore point not found via SystemRestore CIM class. It may have failed or verification was skipped.'
    }
}

Write-Host "Result path: $resultFile"
