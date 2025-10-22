param(
    [switch] $SkipSfc,
    [switch] $SkipDism,
    [switch] $RunRestoreHealth,
    [switch] $ComponentCleanup,
    [switch] $AnalyzeComponentStore,
    [string] $ResultPath
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

$script:TidyOutputLines = [System.Collections.Generic.List[string]]::new()
$script:TidyErrorLines = [System.Collections.Generic.List[string]]::new()
$script:OperationSucceeded = $true
$script:UsingResultFile = -not [string]::IsNullOrWhiteSpace($ResultPath)

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
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

    [void]$script:TidyOutputLines.Add($text)
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

    [void]$script:TidyErrorLines.Add($text)
    Write-Error -Message $text
}

function Save-TidyResult {
    if (-not $script:UsingResultFile) {
        return
    }

    $payload = [pscustomobject]@{
        Success = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
        Output  = $script:TidyOutputLines
        Errors  = $script:TidyErrorLines
    }

    $json = $payload | ConvertTo-Json -Depth 5
    Set-Content -Path $ResultPath -Value $json -Encoding UTF8
}

function Invoke-TidyCommand {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock] $Command,
        [string] $Description = 'Running command.',
        [object[]] $Arguments = @(),
        [switch] $RequireSuccess
    )

    Write-TidyLog -Level Information -Message $Description

    $output = & $Command @Arguments 2>&1
    $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { $LASTEXITCODE } else { 0 }

    foreach ($entry in @($output)) {
        if ($null -eq $entry) {
            continue
        }

        if ($entry -is [System.Management.Automation.ErrorRecord]) {
            Write-TidyError -Message $entry
        }
        else {
            Write-TidyOutput -Message $entry
        }
    }

    if ($RequireSuccess -and $exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }

    return $exitCode
}

function Test-TidyAdmin {
    return [bool](New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'System health scan requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting Windows system health scanner.'

    if (-not $SkipSfc.IsPresent) {
        Write-TidyOutput -Message 'Running System File Checker (this can take 5-15 minutes).'
        Invoke-TidyCommand -Command { sfc /scannow } -Description 'Running SFC /scannow.' -RequireSuccess | Out-Null
    }
    else {
        Write-TidyOutput -Message 'Skipping SFC scan per operator request.'
    }

    if (-not $SkipDism.IsPresent) {
        Write-TidyOutput -Message 'Checking Windows component store health.'
        Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /CheckHealth } -Description 'DISM CheckHealth' -RequireSuccess | Out-Null

        Write-TidyOutput -Message 'Scanning Windows component store for corruption.'
        Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /ScanHealth } -Description 'DISM ScanHealth' -RequireSuccess | Out-Null

        if ($RunRestoreHealth.IsPresent) {
            Write-TidyOutput -Message 'Repairing Windows component store corruption (RestoreHealth).'
            Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /RestoreHealth } -Description 'DISM RestoreHealth' -RequireSuccess | Out-Null
        }
        else {
            Write-TidyOutput -Message 'Skipping RestoreHealth. Re-run with -RunRestoreHealth to attempt repairs.'
        }

        if ($ComponentCleanup.IsPresent) {
            Write-TidyOutput -Message 'Cleaning up superseded components.'
            Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /StartComponentCleanup } -Description 'DISM StartComponentCleanup' -RequireSuccess | Out-Null
        }

        if ($AnalyzeComponentStore.IsPresent) {
            Write-TidyOutput -Message 'Analyzing component store (provides size and reclaim recommendations).'
            Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /AnalyzeComponentStore } -Description 'DISM AnalyzeComponentStore' | Out-Null
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping DISM checks per operator request.'
    }

    Write-TidyOutput -Message 'System health scan completed.'
}
catch {
    $script:OperationSucceeded = $false
    $message = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($message)) {
        $message = $_ | Out-String
    }

    Write-TidyLog -Level Error -Message $message
    Write-TidyError -Message $message
    if (-not $script:UsingResultFile) {
        throw
    }
}
finally {
    Save-TidyResult
    Write-TidyLog -Level Information -Message 'System health scanner finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
