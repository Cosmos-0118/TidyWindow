param(
    [switch] $SkipAdapterReset,
    [switch] $SkipDisplayServicesRestart,
    [switch] $SkipHdrNightLightRefresh,
    [switch] $SkipResolutionReapply,
    [switch] $SkipEdidRefresh,
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

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
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
        [switch] $RequireSuccess,
        [int[]] $AcceptableExitCodes = @()
    )

    Write-TidyLog -Level Information -Message $Description

    if (Test-Path -Path 'variable:LASTEXITCODE') {
        $global:LASTEXITCODE = 0
    }

    $output = & $Command @Arguments 2>&1
    $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { $LASTEXITCODE } else { 0 }

    if ($exitCode -eq 0 -and $output) {
        $lastItem = ($output | Select-Object -Last 1)
        if ($lastItem -is [int] -or $lastItem -is [long]) {
            $exitCode = [int]$lastItem
        }
    }

    foreach ($entry in @($output)) {
        if ($null -eq $entry) { continue }

        if ($entry -is [System.Management.Automation.ErrorRecord]) {
            Write-TidyError -Message $entry
        }
        else {
            Write-TidyOutput -Message $entry
        }
    }

    if ($RequireSuccess -and $exitCode -ne 0) {
        $acceptsExitCode = $false
        if ($AcceptableExitCodes -and ($AcceptableExitCodes -contains $exitCode)) {
            $acceptsExitCode = $true
        }

        if (-not $acceptsExitCode) {
            throw "$Description failed with exit code $exitCode."
        }
    }

    return $exitCode
}

function Test-TidyAdmin {
    return [bool](New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Reset-DisplayAdapter {
    try {
        $devices = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId }
        if (-not $devices -or $devices.Count -eq 0) {
            Write-TidyOutput -Message 'No display adapters found to reset.'
            return
        }

        $primary = $devices | Select-Object -First 1
        Write-TidyOutput -Message ("Disabling display adapter {0}." -f $primary.InstanceId)
        Invoke-TidyCommand -Command { param($id) Disable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop } -Arguments @($primary.InstanceId) -Description 'Disabling display adapter.'

        Start-Sleep -Seconds 1

        Write-TidyOutput -Message ("Enabling display adapter {0}." -f $primary.InstanceId)
        Invoke-TidyCommand -Command { param($id) Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop } -Arguments @($primary.InstanceId) -Description 'Enabling display adapter.' -RequireSuccess
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Display adapter reset failed: {0}" -f $_.Exception.Message)
    }
}

function Restart-DisplayServices {
    $services = @('DisplayEnhancementService', 'UdkUserSvc')
    foreach ($svc in $services) {
        try {
            $service = Get-Service -Name $svc -ErrorAction SilentlyContinue
            if ($null -eq $service) {
                Write-TidyOutput -Message ("Service {0} not found. Skipping." -f $svc)
                continue
            }

            Write-TidyOutput -Message ("Restarting display service {0}." -f $svc)
            Invoke-TidyCommand -Command { param($name) Restart-Service -Name $name -Force -ErrorAction Stop } -Arguments @($svc) -Description ("Restarting {0}." -f $svc) -RequireSuccess
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to restart service {0}: {1}" -f $svc, $_.Exception.Message)
        }
    }
}

function Refresh-HdrNightLight {
    try {
        Write-TidyOutput -Message 'Refreshing display enhancement service to re-apply HDR/night light policies.'
        Invoke-TidyCommand -Command { Restart-Service -Name DisplayEnhancementService -Force -ErrorAction Stop } -Description 'Restarting DisplayEnhancementService.' -RequireSuccess
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("HDR/night light refresh failed: {0}" -f $_.Exception.Message)
    }
}

function Reapply-Resolution {
    try {
        $displaySwitch = Join-Path -Path $env:SystemRoot -ChildPath 'System32\\DisplaySwitch.exe'
        if (-not (Test-Path -LiteralPath $displaySwitch)) {
            Write-TidyOutput -Message 'DisplaySwitch.exe not found. Skipping resolution reapply.'
            return
        }

        Write-TidyOutput -Message 'Re-applying current display configuration (DisplaySwitch /internal).'
        Invoke-TidyCommand -Command { param($exe) Start-Process -FilePath $exe -ArgumentList '/internal' -WindowStyle Hidden -Wait } -Arguments @($displaySwitch) -Description 'Reapplying display configuration.' -AcceptableExitCodes @(0)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Resolution reapply failed: {0}" -f $_.Exception.Message)
    }
}

function Refresh-EdidAndPnp {
    try {
        Write-TidyOutput -Message 'Triggering Plug and Play rescan for display stack/EDID refresh.'
        Invoke-TidyCommand -Command { pnputil /scan-devices } -Description 'PnP rescan for displays.' -AcceptableExitCodes @(0,259)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("EDID/PnP refresh failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Graphics and display repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting graphics and display repair pack.'

    if (-not $SkipAdapterReset.IsPresent) {
        Reset-DisplayAdapter
    }
    else {
        Write-TidyOutput -Message 'Skipping display adapter disable/enable per operator request.'
    }

    if (-not $SkipDisplayServicesRestart.IsPresent) {
        Restart-DisplayServices
    }
    else {
        Write-TidyOutput -Message 'Skipping display service restart per operator request.'
    }

    if (-not $SkipHdrNightLightRefresh.IsPresent) {
        Refresh-HdrNightLight
    }
    else {
        Write-TidyOutput -Message 'Skipping HDR/night light refresh per operator request.'
    }

    if (-not $SkipResolutionReapply.IsPresent) {
        Reapply-Resolution
    }
    else {
        Write-TidyOutput -Message 'Skipping resolution reapply per operator request.'
    }

    if (-not $SkipEdidRefresh.IsPresent) {
        Refresh-EdidAndPnp
    }
    else {
        Write-TidyOutput -Message 'Skipping EDID/PnP refresh per operator request.'
    }

    Write-TidyOutput -Message 'Graphics and display repair pack completed.'
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
    Write-TidyLog -Level Information -Message 'Graphics and display repair script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
