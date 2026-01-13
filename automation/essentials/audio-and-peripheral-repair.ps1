param(
    [switch] $SkipAudioStackRestart,
    [switch] $SkipEndpointRescan,
    [switch] $SkipBluetoothReset,
    [switch] $SkipUsbHubReset,
    [switch] $SkipMicEnable,
    [switch] $SkipCameraReset,
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

function Restart-AudioStack {
    $services = @('AudioSrv', 'AudioEndpointBuilder')
    foreach ($svc in $services) {
        try {
            $service = Get-Service -Name $svc -ErrorAction SilentlyContinue
            if ($null -eq $service) {
                Write-TidyOutput -Message ("Service {0} not found. Skipping." -f $svc)
                continue
            }

            Write-TidyOutput -Message ("Restarting {0}." -f $svc)
            Invoke-TidyCommand -Command { param($name) Restart-Service -Name $name -Force -ErrorAction Stop } -Arguments @($svc) -Description ("Restarting service {0}." -f $svc) -RequireSuccess
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to restart {0}: {1}" -f $svc, $_.Exception.Message)
        }
    }
}

function Rescan-AudioEndpoints {
    try {
        Write-TidyOutput -Message 'Enumerating audio endpoints.'
        Invoke-TidyCommand -Command { pnputil /enum-devices /class AudioEndpoint } -Description 'Listing AudioEndpoint devices.' -AcceptableExitCodes @(0)

        Write-TidyOutput -Message 'Triggering device rescan.'
        Invoke-TidyCommand -Command { pnputil /scan-devices } -Description 'Rescanning devices.' -AcceptableExitCodes @(0,259)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Endpoint rescan failed: {0}" -f $_.Exception.Message)
    }
}

function Reset-BluetoothAvctp {
    $serviceName = 'BthAvctpSvc'
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        Write-TidyOutput -Message 'Bluetooth AVCTP service not found. Skipping.'
        return
    }

    try {
        Write-TidyOutput -Message 'Restarting Bluetooth AVCTP service.'
        Invoke-TidyCommand -Command { param($name) Restart-Service -Name $name -Force -ErrorAction Stop } -Arguments @($serviceName) -Description 'Restarting Bluetooth AVCTP service.' -RequireSuccess
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Bluetooth AVCTP restart failed: {0}" -f $_.Exception.Message)
    }
}

function Wait-TidyPnpDeviceHealthy {
    param(
        [Parameter(Mandatory = $true)]
        [string] $InstanceId,
        [int] $TimeoutSeconds = 8,
        [switch] $TreatMissingAsHealthy
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $dev = Get-PnpDevice -InstanceId $InstanceId -ErrorAction SilentlyContinue
        if ($dev -and ($dev.Status -match '^OK$' -or $dev.ConfigManagerErrorCode -eq 0)) {
            return $true
        }

        if (-not $dev -and $TreatMissingAsHealthy.IsPresent) {
            return $true
        }

        Start-Sleep -Milliseconds 350
    }

    return $false
}

function Restart-PnpDevice {
    param(
        [Parameter(Mandatory = $true)]
        [string] $InstanceId,
        [string] $Label,
        [int] $MaxAttempts = 4
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $attemptLabel = "{0} (attempt {1}/{2})" -f $Label, $attempt, $MaxAttempts
        try {
            Write-TidyOutput -Message ("Disabling {0}" -f $attemptLabel)
            Invoke-TidyCommand -Command { param($id) Disable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop } -Arguments @($InstanceId) -Description ("Disabling {0}" -f $attemptLabel)
            Start-Sleep -Milliseconds 700

            Write-TidyOutput -Message ("Enabling {0}" -f $attemptLabel)
            Invoke-TidyCommand -Command { param($id) Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop } -Arguments @($InstanceId) -Description ("Enabling {0}" -f $attemptLabel)
        }
        catch {
            Write-TidyOutput -Message ("Primary disable/enable path failed for {0}: {1}" -f $attemptLabel, $_.Exception.Message)
            try {
                Write-TidyOutput -Message ("Attempting pnputil restart-device for {0}" -f $attemptLabel)
                Invoke-TidyCommand -Command { param($id) pnputil /restart-device $id } -Arguments @($InstanceId) -Description ("Restarting {0} via pnputil" -f $attemptLabel) -AcceptableExitCodes @(0,259,3010)
            }
            catch {
                if ($attempt -ge $MaxAttempts) { break }
            }
        }

        if (Wait-TidyPnpDeviceHealthy -InstanceId $InstanceId -TimeoutSeconds 12 -TreatMissingAsHealthy) {
            return
        }

        if ($attempt -lt $MaxAttempts) {
            Write-TidyOutput -Message ("{0} not healthy yet; triggering PnP rescan and retrying." -f $attemptLabel)
            Invoke-TidyCommand -Command { pnputil /scan-devices } -Description 'PnP rescan for USB retry.' -AcceptableExitCodes @(0,259)
            Start-Sleep -Milliseconds 900
        }
    }

    Write-TidyOutput -Message ("{0} did not return to a healthy state after restart. Continuing with remaining devices." -f $Label)
}

function Reset-UsbHubs {
    try {
        $devices = @(Get-PnpDevice -Class USB -ErrorAction SilentlyContinue)
        if (-not $devices -or $devices.Count -eq 0) {
            Write-TidyOutput -Message 'No USB class devices found for reset.'
        }
        else {
            $problemDevices = $devices | Where-Object { ($_.Status -and $_.Status -notmatch '^OK$') -or ($_.ConfigManagerErrorCode -ne 0) }
            if (-not $problemDevices -or $problemDevices.Count -eq 0) {
                Write-TidyOutput -Message 'No unhealthy USB devices detected; performing a PnP rescan only.'
            }
            else {
                foreach ($dev in $problemDevices) {
                    $label = if ($dev.FriendlyName) { $dev.FriendlyName } elseif ($dev.Name) { $dev.Name } else { $dev.InstanceId }
                    Restart-PnpDevice -InstanceId $dev.InstanceId -Label $label -MaxAttempts 4
                }
            }
        }

        Write-TidyOutput -Message 'Rescanning Plug and Play tree.'
        Invoke-TidyCommand -Command { pnputil /scan-devices } -Description 'PnP rescan for USB hub reset.' -AcceptableExitCodes @(0,259)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("USB hub reset failed: {0}" -f $_.Exception.Message)
    }
}

function Enable-Microphones {
    try {
        $targets = @(Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.Status -and $_.Status -notmatch '^OK$' })
        if (-not $targets -or $targets.Count -eq 0) {
            Write-TidyOutput -Message 'No disabled or error audio endpoints detected.'
            return
        }

        foreach ($dev in $targets) {
            try {
                Write-TidyOutput -Message ("Enabling audio endpoint {0}" -f $dev.InstanceId)
                Invoke-TidyCommand -Command { param($id) Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop } -Arguments @($dev.InstanceId) -Description ("Enabling audio endpoint {0}" -f $dev.InstanceId)
                if (-not (Wait-TidyPnpDeviceHealthy -InstanceId $dev.InstanceId -TimeoutSeconds 8 -TreatMissingAsHealthy)) {
                    Write-TidyOutput -Message ("Audio endpoint {0} did not report healthy after enable; attempting pnputil restart." -f $dev.InstanceId)
                    Invoke-TidyCommand -Command { param($id) pnputil /restart-device $id } -Arguments @($dev.InstanceId) -Description ("Restarting audio endpoint {0} via pnputil" -f $dev.InstanceId) -AcceptableExitCodes @(0,259,3010)
                    if (-not (Wait-TidyPnpDeviceHealthy -InstanceId $dev.InstanceId -TimeoutSeconds 10 -TreatMissingAsHealthy)) {
                        Write-TidyOutput -Message ("Audio endpoint {0} still not healthy after pnputil restart; continuing." -f $dev.InstanceId)
                    }
                }
            }
            catch {
                Write-TidyOutput -Message ("Failed to enable audio endpoint {0}: {1}" -f $dev.InstanceId, $_.Exception.Message)
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Microphone endpoint enable failed: {0}" -f $_.Exception.Message)
    }
}

function Reset-CameraStack {
    $serviceName = 'FrameServer'
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        Write-TidyOutput -Message 'Camera FrameServer service not found. Skipping.'
    }
    else {
        try {
            Write-TidyOutput -Message 'Restarting camera service (FrameServer).'
            Invoke-TidyCommand -Command { param($name) Restart-Service -Name $name -Force -ErrorAction Stop } -Arguments @($serviceName) -Description 'Restarting FrameServer service.' -RequireSuccess
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Camera service restart failed: {0}" -f $_.Exception.Message)
        }
    }

    try {
        Write-TidyOutput -Message 'Rescanning devices for camera stack refresh.'
        Invoke-TidyCommand -Command { pnputil /scan-devices } -Description 'PnP rescan for camera devices.' -AcceptableExitCodes @(0,259)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Camera device rescan failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Audio and peripheral repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting audio and peripheral repair pack.'

    if (-not $SkipAudioStackRestart.IsPresent) {
        Restart-AudioStack
    }
    else {
        Write-TidyOutput -Message 'Skipping audio stack restart per operator request.'
    }

    if (-not $SkipEndpointRescan.IsPresent) {
        Rescan-AudioEndpoints
    }
    else {
        Write-TidyOutput -Message 'Skipping audio endpoint rescan per operator request.'
    }

    if (-not $SkipBluetoothReset.IsPresent) {
        Reset-BluetoothAvctp
    }
    else {
        Write-TidyOutput -Message 'Skipping Bluetooth AVCTP reset per operator request.'
    }

    if (-not $SkipUsbHubReset.IsPresent) {
        Reset-UsbHubs
    }
    else {
        Write-TidyOutput -Message 'Skipping USB hub reset per operator request.'
    }

    if (-not $SkipMicEnable.IsPresent) {
        Enable-Microphones
    }
    else {
        Write-TidyOutput -Message 'Skipping microphone enablement per operator request.'
    }

    if (-not $SkipCameraReset.IsPresent) {
        Reset-CameraStack
    }
    else {
        Write-TidyOutput -Message 'Skipping camera reset per operator request.'
    }

    Write-TidyOutput -Message 'Audio and peripheral repair pack completed.'
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
    Write-TidyLog -Level Information -Message 'Audio and peripheral repair script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}