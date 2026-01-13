param(
    [switch] $SkipAdapterReset,
    [switch] $SkipDisplayServicesRestart,
    [switch] $SkipHdrNightLightRefresh,
    [switch] $SkipResolutionReapply,
    [switch] $SkipEdidRefresh,
    [switch] $SkipDwmRestart,
    [switch] $SkipColorProfileReapply,
    [switch] $SkipGpuControlPanelReset,
    [switch] $AllowRiskyDisplayActions,
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
$script:IsTidyWindowHost = $false
$script:RiskyActionsAllowed = $AllowRiskyDisplayActions.IsPresent

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

try {
    $procName = [System.Diagnostics.Process]::GetCurrentProcess().ProcessName
    if ($procName -and $procName -match 'tidywindow') {
        $script:IsTidyWindowHost = $true
    }
}
catch {
    $script:IsTidyWindowHost = $false
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

function Wait-TidyServiceState {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [string] $DesiredStatus = 'Running',
        [int] $TimeoutSeconds = 10
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq $DesiredStatus) {
            return $true
        }

        Start-Sleep -Milliseconds 300
    }

    return $false
}

function Reset-DisplayAdapter {
    try {
        if (-not $script:RiskyActionsAllowed) {
            Write-TidyOutput -Message 'Skipping display adapter disable/enable because risky actions are not allowed.'
            return
        }

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
    # Handle template services that have per-user instances (e.g., UdkUserSvc_xxxxx) and skip disabled templates cleanly.
    $serviceGroups = @(
        @{ BaseName = 'DisplayEnhancementService'; Pattern = 'DisplayEnhancementService' },
        @{ BaseName = 'UdkUserSvc'; Pattern = 'UdkUserSvc*' }
    )

    if (-not $script:RiskyActionsAllowed) {
        Write-TidyOutput -Message 'Skipping display service restart because risky actions are not allowed.'
        return
    }

    foreach ($group in $serviceGroups) {
        try {
            $candidates = Get-Service -Name $group.Pattern -ErrorAction SilentlyContinue | Sort-Object -Property Name -Unique
            if (-not $candidates) {
                Write-TidyOutput -Message ("Service {0} not found. Skipping." -f $group.BaseName)
                continue
            }

            $attempted = $false
            $succeeded = $false

            foreach ($service in $candidates) {
                $svcName = $service.Name

                $startMode = $null
                $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name='$svcName'" -ErrorAction SilentlyContinue
                if ($serviceInfo) {
                    $startMode = $serviceInfo.StartMode
                }

                if ($startMode -and $startMode -eq 'Disabled') {
                    Write-TidyOutput -Message ("Service {0} ({1}) is disabled. Skipping restart." -f $group.BaseName, $svcName)
                    continue
                }

                $attempted = $true
                $actionDescription = if ($service.Status -eq 'Stopped') { ("Starting {0} ({1})." -f $group.BaseName, $svcName) } else { ("Restarting {0} ({1})." -f $group.BaseName, $svcName) }
                Write-TidyOutput -Message $actionDescription

                $started = $false
                try {
                    if ($service.Status -eq 'Stopped') {
                        Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($svcName) -Description $actionDescription
                    }
                    else {
                        Invoke-TidyCommand -Command { param($name) Restart-Service -Name $name -ErrorAction Stop } -Arguments @($svcName) -Description $actionDescription
                    }
                    $started = $true
                }
                catch {
                    Write-TidyOutput -Message ("Primary restart/start for {0} ({1}) failed or was blocked: {2}" -f $group.BaseName, $svcName, $_.Exception.Message)
                    try {
                        Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($svcName) -Description ("Ensuring {0} ({1}) is running." -f $group.BaseName, $svcName)
                        $started = $true
                    }
                    catch {
                        $script:OperationSucceeded = $false
                        Write-TidyError -Message ("Failed to restart service {0} ({1}): {2}" -f $group.BaseName, $svcName, $_.Exception.Message)
                    }
                }

                if ($started) {
                    if (Wait-TidyServiceState -Name $svcName -DesiredStatus 'Running' -TimeoutSeconds 15) {
                        $succeeded = $true
                    }
                    else {
                        $script:OperationSucceeded = $false
                        Write-TidyError -Message ("Service {0} ({1}) did not reach Running state after restart attempt." -f $group.BaseName, $svcName)
                    }
                }
            }

            if ($attempted -and -not $succeeded) {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("No {0} instances reached Running state. Verify the service is enabled and available." -f $group.BaseName)
            }
            elseif (-not $attempted) {
                Write-TidyOutput -Message ("All discovered instances of {0} are disabled; leaving unchanged." -f $group.BaseName)
            }
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to restart service {0}: {1}" -f $group.BaseName, $_.Exception.Message)
        }
    }
}

function Refresh-HdrNightLight {
    try {
        Write-TidyOutput -Message 'Refreshing display enhancement service to re-apply HDR/night light policies.'
        $service = Get-Service -Name 'DisplayEnhancementService' -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            Write-TidyOutput -Message 'DisplayEnhancementService not found. Skipping HDR/night light refresh.'
            return
        }

        if ($service.StartType -eq 'Disabled') {
            Write-TidyOutput -Message 'DisplayEnhancementService is disabled. Skipping HDR/night light refresh.'
            return
        }

        Invoke-TidyCommand -Command { Restart-Service -Name DisplayEnhancementService -ErrorAction Stop } -Description 'Restarting DisplayEnhancementService.'
        if (-not (Wait-TidyServiceState -Name 'DisplayEnhancementService' -DesiredStatus 'Running' -TimeoutSeconds 10)) {
            $script:OperationSucceeded = $false
            Write-TidyError -Message 'DisplayEnhancementService did not reach Running state after restart.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("HDR/night light refresh failed: {0}" -f $_.Exception.Message)
    }
}

function Reapply-Resolution {
    try {
        if (-not $script:RiskyActionsAllowed) {
            Write-TidyOutput -Message 'Skipping resolution reapply because risky actions are not allowed.'
            return
        }

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
        if (-not $script:RiskyActionsAllowed) {
            Write-TidyOutput -Message 'Skipping EDID/PnP rescan because risky actions are not allowed.'
            return
        }

        Write-TidyOutput -Message 'Triggering Plug and Play rescan for display stack/EDID refresh.'
        Invoke-TidyCommand -Command { pnputil /scan-devices } -Description 'PnP rescan for displays.' -AcceptableExitCodes @(0,259)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("EDID/PnP refresh failed: {0}" -f $_.Exception.Message)
    }
}

function Restart-Dwm {
    try {
        if (-not $script:RiskyActionsAllowed) {
            Write-TidyOutput -Message 'Skipping DWM restart because risky actions are not allowed.'
            return
        }

        Write-TidyOutput -Message 'Restarting Desktop Window Manager (dwm.exe) to clear compositor glitches.'
        Stop-Process -Name dwm -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("DWM restart failed: {0}" -f $_.Exception.Message)
    }
}

function Reapply-ColorProfiles {
    $tempPath = $null
    try {
        if (-not $script:RiskyActionsAllowed) {
            Write-TidyOutput -Message 'Skipping color profile export/reload because risky actions are not allowed.'
            return
        }

        $dispdiagPath = Join-Path -Path $env:SystemRoot -ChildPath 'System32\\dispdiag.exe'
        if (-not (Test-Path -LiteralPath $dispdiagPath)) {
            Write-TidyOutput -Message 'dispdiag.exe not found; skipping color profile export/reload.'
            return
        }

        $tempPath = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ("dispdiag-{0}.dat" -f ([guid]::NewGuid()))

        Write-TidyOutput -Message 'Exporting current color profile via dispdiag.'
        Invoke-TidyCommand -Command { param($exe, $path) Start-Process -FilePath $exe -ArgumentList @('-out', $path) -WindowStyle Hidden -Wait } -Arguments @($dispdiagPath, $tempPath) -Description 'dispdiag export color profile.' -AcceptableExitCodes @(0)

        Write-TidyOutput -Message 'Reloading color profile from export via dispdiag.'
        Invoke-TidyCommand -Command { param($exe, $path) Start-Process -FilePath $exe -ArgumentList @('-load', $path) -WindowStyle Hidden -Wait } -Arguments @($dispdiagPath, $tempPath) -Description 'dispdiag load color profile.' -AcceptableExitCodes @(0)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Color profile export/reload failed: {0}" -f $_.Exception.Message)
    }
    finally {
        if ($tempPath -and (Test-Path -LiteralPath $tempPath)) {
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue | Out-Null
        }
    }
}

function Reset-GpuControlPanel {
    try {
        if (-not $script:RiskyActionsAllowed) {
            Write-TidyOutput -Message 'Skipping GPU control panel resets because risky actions are not allowed.'
            return
        }

        $videoControllers = @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction SilentlyContinue)
        if (-not $videoControllers -or $videoControllers.Count -eq 0) {
            Write-TidyOutput -Message 'No video controllers detected; skipping GPU control panel resets.'
            return
        }

        $hasNvidia = $videoControllers | Where-Object { $_.Name -match 'NVIDIA' -or $_.AdapterCompatibility -match 'NVIDIA' }
        $hasAmd    = $videoControllers | Where-Object { $_.Name -match 'AMD|Radeon' -or $_.AdapterCompatibility -match 'Advanced Micro Devices|AMD' }

        if ($hasNvidia) {
            $nvidiaCmd = Get-Command -Name 'nvidia-smi.exe' -ErrorAction SilentlyContinue
            if (-not $nvidiaCmd) {
                Write-TidyOutput -Message 'nvidia-smi not found; skipping NVIDIA control panel reset.'
            }
            else {
                Write-TidyOutput -Message 'Resetting NVIDIA app clocks (nvidia-smi --reset-app-clocks).' 
                Invoke-TidyCommand -Command { nvidia-smi --reset-app-clocks } -Description 'NVIDIA app clocks reset.' -AcceptableExitCodes @(0)
            }
        }

        if ($hasAmd) {
            $amdService = Get-Service -Name 'AMDRSServ' -ErrorAction SilentlyContinue
            $amdCmd = $null
            if (-not $amdService) {
                $amdCmd = Get-Command -Name 'AMDRSServ.exe' -ErrorAction SilentlyContinue
            }

            if ($amdService) {
                Write-TidyOutput -Message 'Restarting AMD Radeon Settings service (AMDRSServ).' 
                Invoke-TidyCommand -Command { Restart-Service -Name AMDRSServ -Force -ErrorAction Stop } -Description 'Restarting AMDRSServ.'
            }
            elseif ($amdCmd) {
                Write-TidyOutput -Message 'Invoking AMDRSServ reset entry point.'
                Invoke-TidyCommand -Command { param($path) Start-Process -FilePath $path -ArgumentList '-reset' -WindowStyle Hidden -Wait } -Arguments @($amdCmd.Source) -Description 'AMDRSServ reset invocation.' -AcceptableExitCodes @(0)
            }
            else {
                Write-TidyOutput -Message 'AMDRSServ not found; skipping AMD control panel reset.'
            }
        }

        if (-not $hasNvidia -and -not $hasAmd) {
            Write-TidyOutput -Message 'GPU vendor not identified as NVIDIA or AMD; skipping control panel resets.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("GPU control panel reset failed: {0}" -f $_.Exception.Message)
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

    if (-not $SkipDwmRestart.IsPresent) {
        Restart-Dwm
    }
    else {
        Write-TidyOutput -Message 'Skipping DWM restart per operator request.'
    }

    if (-not $SkipColorProfileReapply.IsPresent) {
        Reapply-ColorProfiles
    }
    else {
        Write-TidyOutput -Message 'Skipping color profile export/reload per operator request.'
    }

    if (-not $SkipGpuControlPanelReset.IsPresent) {
        Reset-GpuControlPanel
    }
    else {
        Write-TidyOutput -Message 'Skipping GPU control panel reset per operator request.'
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
