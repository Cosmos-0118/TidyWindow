param(
    [switch] $SkipServiceReset,
    [switch] $SkipSpoolPurge,
    [switch] $SkipDriverRefresh,
    [switch] $SkipDllRegistration,
    [switch] $SkipPrintIsolationPolicy,
    [switch] $DryRun,
    [string] $ResultPath,
    [string] $LogPath,
    [switch] $NoElevate
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
$script:DryRunMode = $DryRun.IsPresent
$script:RunLog = [System.Collections.Generic.List[pscustomobject]]::new()
$script:ServicesStoppedForMaintenance = $false

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $timestamp = (Get-Date).ToString('yyyyMMdd_HHmmss')
    $LogPath = Join-Path -Path $env:TEMP -ChildPath "TidyWindow_SpoolerRepair_$timestamp.json"
}
else {
    $LogPath = [System.IO.Path]::GetFullPath($LogPath)
}

$logDirectory = Split-Path -Parent $LogPath
if (-not [string]::IsNullOrWhiteSpace($logDirectory) -and -not (Test-Path -LiteralPath $logDirectory)) {
    [void](New-Item -Path $logDirectory -ItemType Directory -Force)
}

$transcriptPath = [System.IO.Path]::ChangeExtension($LogPath, '.transcript.txt')

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

    # Prevent sticky non-zero $LASTEXITCODE from earlier native calls.
    if (Test-Path -Path 'variable:LASTEXITCODE') {
        $global:LASTEXITCODE = 0
    }

    if ($script:DryRunMode) {
        Write-TidyOutput -Message "[DryRun] Would run: $Description"
        if ($Arguments -and $Arguments.Count -gt 0) {
            Write-TidyOutput -Message ("[DryRun] Arguments: {0}" -f ($Arguments -join ', '))
        }
        return 0
    }

    $output = & $Command @Arguments 2>&1
    $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { $LASTEXITCODE } else { 0 }

    # If a native call returned 0 but the scriptblock emitted an int, respect that value.
    if ($exitCode -eq 0 -and $output) {
        $lastItem = ($output | Select-Object -Last 1)
        if ($lastItem -is [int] -or $lastItem -is [long]) {
            $exitCode = [int]$lastItem
        }
    }

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

function Ensure-Elevation {
    param(
        [switch] $AllowNoElevate
    )

    if (Test-TidyAdmin) { return $true }
    if ($AllowNoElevate -or $NoElevate.IsPresent) { return $false }

    try {
        $scriptPath = $PSCommandPath
        if ([string]::IsNullOrWhiteSpace($scriptPath)) { $scriptPath = $MyInvocation.MyCommand.Path }

        $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"$scriptPath")
        foreach ($k in $PSBoundParameters.Keys) {
            $val = $PSBoundParameters[$k]
            if ($val -is [switch]) {
                if ($val.IsPresent) { $argList += "-$k" }
            }
            else {
                $argList += "-$k"; $argList += "`"$val`""
            }
        }

        Start-Process -FilePath (Get-Command powershell).Source -ArgumentList $argList -Verb RunAs -WindowStyle Hidden
        Write-TidyOutput -Message 'Elevating and re-launching as administrator. Exiting current process.'
        exit 0
    }
    catch {
        Write-TidyError -Message "Failed to elevate: $($_.Exception.Message)"
        throw
    }
}

function Track-TidyStep {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Step,
        [string] $Status = 'Completed',
        [string] $Details = ''
    )

    $script:RunLog.Add([pscustomobject]@{
        Step = $Step
        Status = $Status
        Details = $Details
        DryRun = $script:DryRunMode
    }) | Out-Null
}

function Get-SpoolerServiceDescriptors {
    return @(
        @{ Name = 'PrintNotify'; Friendly = 'Print Notifications'; StopOrder = 1; StartOrder = 2 },
        @{ Name = 'Spooler'; Friendly = 'Print Spooler'; StopOrder = 2; StartOrder = 1 }
    )
}

function Get-TidyServiceInstance {
    param([string] $ServiceName)

    try {
        return Get-Service -Name $ServiceName -ErrorAction Stop
    }
    catch {
        Write-TidyLog -Level Warning -Message "Service $ServiceName not found: $($_.Exception.Message)"
        Track-TidyStep -Step "Locate $ServiceName" -Status 'Missing' -Details $_.Exception.Message
        return $null
    }
}

function Wait-TidyServiceStatus {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ServiceName,
        [Parameter(Mandatory = $true)]
        [System.ServiceProcess.ServiceControllerStatus] $DesiredStatus,
        [int] $TimeoutSeconds = 20
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $svc = Get-Service -Name $ServiceName -ErrorAction Stop
            if ($svc.Status -eq $DesiredStatus) {
                return $true
            }
        }
        catch {
            return $false
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Stop-SpoolerServices {
    $descriptors = Get-SpoolerServiceDescriptors | Sort-Object -Property StopOrder

    if ($script:DryRunMode) {
        foreach ($svc in $descriptors) {
            Write-TidyOutput -Message ("[DryRun] Would stop {0} service ({1})." -f $svc.Friendly, $svc.Name)
            Track-TidyStep -Step ("Stop {0}" -f $svc.Name) -Status 'DryRun'
        }
        return $true
    }

    $stoppedAny = $false

    foreach ($svc in $descriptors) {
        $service = Get-TidyServiceInstance -ServiceName $svc.Name
        if (-not $service) { continue }

        if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Write-TidyOutput -Message ("Service {0} ({1}) already stopped." -f $svc.Friendly, $svc.Name)
            Track-TidyStep -Step ("Stop {0}" -f $svc.Name) -Status 'Skipped' -Details 'Already stopped'
            continue
        }

        Write-TidyOutput -Message ("Stopping {0} service ({1})." -f $svc.Friendly, $svc.Name)
        try {
            Stop-Service -Name $svc.Name -Force -ErrorAction Stop
            if (Wait-TidyServiceStatus -ServiceName $svc.Name -DesiredStatus ([System.ServiceProcess.ServiceControllerStatus]::Stopped)) {
                Track-TidyStep -Step ("Stop {0}" -f $svc.Name)
                $stoppedAny = $true
            }
            else {
                Track-TidyStep -Step ("Stop {0}" -f $svc.Name) -Status 'Warning' -Details 'Timeout waiting for stopped state'
                Write-TidyLog -Level Warning -Message "Timeout waiting for $($svc.Name) to report Stopped."
            }
        }
        catch {
            Track-TidyStep -Step ("Stop {0}" -f $svc.Name) -Status 'Warning' -Details $_.Exception.Message
            Write-TidyLog -Level Warning -Message "Failed to stop $($svc.Name): $($_.Exception.Message)"
        }
    }

    return $stoppedAny
}

function Start-SpoolerServices {
    $descriptors = Get-SpoolerServiceDescriptors | Sort-Object -Property StartOrder

    if ($script:DryRunMode) {
        foreach ($svc in $descriptors) {
            Write-TidyOutput -Message ("[DryRun] Would start {0} service ({1})." -f $svc.Friendly, $svc.Name)
            Track-TidyStep -Step ("Start {0}" -f $svc.Name) -Status 'DryRun'
        }
        return $true
    }

    $startedAny = $false

    foreach ($svc in $descriptors) {
        $service = Get-TidyServiceInstance -ServiceName $svc.Name
        if (-not $service) { continue }

        if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
            Track-TidyStep -Step ("Start {0}" -f $svc.Name) -Status 'Skipped' -Details 'Already running'
            continue
        }

        Write-TidyOutput -Message ("Starting {0} service ({1})." -f $svc.Friendly, $svc.Name)
        try {
            Start-Service -Name $svc.Name -ErrorAction Stop
            if (Wait-TidyServiceStatus -ServiceName $svc.Name -DesiredStatus ([System.ServiceProcess.ServiceControllerStatus]::Running)) {
                Track-TidyStep -Step ("Start {0}" -f $svc.Name)
                $startedAny = $true
            }
            else {
                Track-TidyStep -Step ("Start {0}" -f $svc.Name) -Status 'Warning' -Details 'Timeout waiting for running state'
                Write-TidyLog -Level Warning -Message "Timeout waiting for $($svc.Name) to report Running."
            }
        }
        catch {
            Track-TidyStep -Step ("Start {0}" -f $svc.Name) -Status 'Warning' -Details $_.Exception.Message
            Write-TidyLog -Level Warning -Message "Failed to start $($svc.Name): $($_.Exception.Message)"
        }
    }
}

function Clear-SpoolerQueue {
    $spoolPath = Join-Path -Path $env:SystemRoot -ChildPath 'System32\spool\PRINTERS'

    if ($script:DryRunMode) {
        Write-TidyOutput -Message "[DryRun] Would clear spooler queue at $spoolPath."
        Track-TidyStep -Step 'Clear spool queue' -Status 'DryRun'
        return
    }

    if (-not $script:ServicesStoppedForMaintenance) {
        $spooler = Get-TidyServiceInstance -ServiceName 'Spooler'
        if ($spooler -and $spooler.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Write-TidyOutput -Message 'Print Spooler still running; stopping temporarily for queue cleanup.'
            if (Stop-SpoolerServices) {
                $script:ServicesStoppedForMaintenance = $true
            }
        }
    }

    try {
        if (-not (Test-Path -LiteralPath $spoolPath)) {
            Write-TidyOutput -Message 'Spool queue directory not found; nothing to clear.'
            Track-TidyStep -Step 'Clear spool queue' -Status 'Skipped' -Details 'Directory missing'
            return
        }

        $files = Get-ChildItem -LiteralPath $spoolPath -Force -ErrorAction Stop
        if (-not $files -or $files.Count -eq 0) {
            Write-TidyOutput -Message 'Spool queue already empty.'
            Track-TidyStep -Step 'Clear spool queue' -Status 'Skipped' -Details 'Already empty'
            return
        }

        $removed = 0
        $failed = 0
        foreach ($file in $files) {
            try {
                Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
                $removed++
            }
            catch {
                Track-TidyStep -Step 'Clear spool queue' -Status 'Warning' -Details $_.Exception.Message
                $failed++
                Write-TidyLog -Level Warning -Message "Failed to remove $($file.FullName): $($_.Exception.Message)"
            }
        }

        $statusMessage = if ($failed -gt 0) { "Removed $removed item(s); $failed failed." } else { "Removed $removed item(s)." }
        Track-TidyStep -Step 'Clear spool queue' -Status (if ($failed -gt 0) { 'Warning' } else { 'Completed' }) -Details $statusMessage
        Write-TidyOutput -Message "Cleared spool queue at $spoolPath ($statusMessage)"
    }
    catch {
        Track-TidyStep -Step 'Clear spool queue' -Status 'Error' -Details $_.Exception.Message
        throw
    }
}

function Remove-OfflinePrinterDrivers {
    if ($script:DryRunMode) {
        Write-TidyOutput -Message '[DryRun] Would enumerate and remove stale printer drivers.'
        Track-TidyStep -Step 'Driver cleanup' -Status 'DryRun'
        return
    }

    $spooler = Get-TidyServiceInstance -ServiceName 'Spooler'
    if (-not $spooler) {
        Track-TidyStep -Step 'Driver cleanup' -Status 'Warning' -Details 'Print Spooler service not found.'
        Write-TidyLog -Level Warning -Message 'Print Spooler service not found; skipping driver cleanup.'
        return
    }

    $temporarilyStartedSpooler = $false
    if ($spooler.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        Write-TidyOutput -Message 'Print Spooler is offline; starting temporarily for driver maintenance.'
        try {
            Start-Service -Name 'Spooler' -ErrorAction Stop | Out-Null
            $temporarilyStartedSpooler = $true
            if (-not (Wait-TidyServiceStatus -ServiceName 'Spooler' -DesiredStatus ([System.ServiceProcess.ServiceControllerStatus]::Running))) {
                Track-TidyStep -Step 'Driver cleanup' -Status 'Warning' -Details 'Timed out waiting for Print Spooler to report running.'
                Write-TidyLog -Level Warning -Message 'Timed out waiting for Print Spooler to report Running; skipping driver cleanup.'
                return
            }
        }
        catch {
            Track-TidyStep -Step 'Driver cleanup' -Status 'Warning' -Details $_.Exception.Message
            Write-TidyLog -Level Warning -Message "Failed to start Print Spooler for driver cleanup: $($_.Exception.Message)"
            return
        }
    }

    try {
        $printers = @()
        try {
            $printers = Get-Printer -ErrorAction Stop
        }
        catch {
            # Continue even if printer enumeration fails; driver removal still attempted.
            Write-TidyLog -Level Warning -Message "Failed to enumerate printers: $($_.Exception.Message)"
        }

        $driversInUse = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($printer in $printers) {
            if ($printer -and $printer.DriverName) {
                [void]$driversInUse.Add($printer.DriverName)
            }
        }

        try {
            $drivers = Get-PrinterDriver -ErrorAction Stop
        }
        catch {
            Track-TidyStep -Step 'Driver cleanup' -Status 'Warning' -Details $_.Exception.Message
            Write-TidyLog -Level Warning -Message "Failed to enumerate printer drivers: $($_.Exception.Message)"
            return
        }

        if (-not $drivers -or $drivers.Count -eq 0) {
            Track-TidyStep -Step 'Driver cleanup' -Status 'Skipped' -Details 'No drivers found'
            return
        }

        $removed = 0
        $skipped = 0
        foreach ($driver in $drivers) {
            $driverName = $driver.Name
            if ([string]::IsNullOrWhiteSpace($driverName)) {
                continue
            }

            if ($driversInUse.Contains($driverName)) {
                $skipped++
                Track-TidyStep -Step ('Driver in use') -Status 'Skipped' -Details $driverName
                continue
            }

            $manufacturer = $driver.Manufacturer
            if ($manufacturer -and $manufacturer.StartsWith('Microsoft', [System.StringComparison]::OrdinalIgnoreCase)) {
                $skipped++
                Track-TidyStep -Step ('Driver cleanup skip') -Status 'Skipped' -Details "System driver $driverName"
                continue
            }

            Write-TidyOutput -Message ("Removing printer driver {0}." -f $driverName)
            try {
                $params = @{ Name = $driverName; ErrorAction = 'Stop' }
                if ($driver.PSObject.Properties['Environment'] -and $driver.Environment) {
                    $params['Environment'] = $driver.Environment
                }

                Remove-PrinterDriver @params
                $removed++
            }
            catch {
                $skipped++
                Track-TidyStep -Step 'Driver cleanup' -Status 'Warning' -Details "Failed to remove ${driverName}: $($_.Exception.Message)"
                Write-TidyLog -Level Warning -Message "Failed to remove driver ${driverName}: $($_.Exception.Message)"
            }
        }

        $detail = "Removed $removed driver(s); $skipped skipped."
        $status = if ($removed -gt 0) { 'Completed' } else { 'No changes' }
        Track-TidyStep -Step 'Driver cleanup' -Status $status -Details $detail
    }
    finally {
        if ($temporarilyStartedSpooler) {
            Write-TidyOutput -Message 'Restoring Print Spooler to stopped state after driver maintenance.'
            try {
                Stop-Service -Name 'Spooler' -Force -ErrorAction Stop
                if (-not (Wait-TidyServiceStatus -ServiceName 'Spooler' -DesiredStatus ([System.ServiceProcess.ServiceControllerStatus]::Stopped))) {
                    Write-TidyLog -Level Warning -Message 'Timed out waiting for Print Spooler to stop after driver maintenance.'
                }
            }
            catch {
                Write-TidyLog -Level Warning -Message "Failed to stop Print Spooler after driver maintenance: $($_.Exception.Message)"
            }
        }
    }
}

function Register-SpoolerDlls {
    $dlls = @(
        'spoolss.dll',
        'win32spl.dll',
        'localspl.dll',
        'printui.dll'
    )

    $targets = @()
    foreach ($dll in $dlls) {
        $targets += [pscustomobject]@{ Path = Join-Path -Path $env:SystemRoot -ChildPath ("System32\{0}" -f $dll); Label = $dll }
        if ([Environment]::Is64BitOperatingSystem) {
            $targets += [pscustomobject]@{ Path = Join-Path -Path $env:SystemRoot -ChildPath ("SysWOW64\{0}" -f $dll); Label = "$dll (SysWOW64)" }
        }
    }

    foreach ($entry in $targets) {
        $dllPath = $entry.Path
        $label = $entry.Label
        if (-not (Test-Path -LiteralPath $dllPath)) {
            Track-TidyStep -Step ("Register $label") -Status 'Skipped' -Details 'Missing file'
            continue
        }

        Write-TidyOutput -Message ("Registering {0}." -f $label)
        $result = Invoke-TidyCommand -Command {
            param($Path)
            & regsvr32.exe /s $Path
        } -Arguments @($dllPath) -Description ("Registering {0}" -f $label)

        if ($result -eq 0) {
            Track-TidyStep -Step ("Register $label")
        }
        else {
            Track-TidyStep -Step ("Register $label") -Status 'Warning' -Details "Exit code $result"
        }
    }
}

function Reset-PrintIsolationPolicies {
    $registryPaths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Print\Providers',
        'HKLM:\SYSTEM\CurrentControlSet\Control\Print\Printers'
    )

    foreach ($path in $registryPaths) {
        Write-TidyOutput -Message ("Auditing print isolation policies at {0}." -f $path)
    }

    if ($script:DryRunMode) {
        Track-TidyStep -Step 'Reset print isolation policy' -Status 'DryRun'
        return
    }

    try {
        # Restore default print isolation settings by removing custom AppContainer policies.
        Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Print' -Name 'PrintDriverIsolationExecutionPolicy' -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Print' -Name 'PrintDriverIsolationTracing' -ErrorAction SilentlyContinue
        Track-TidyStep -Step 'Reset print isolation policy'
    }
    catch {
        Track-TidyStep -Step 'Reset print isolation policy' -Status 'Warning' -Details $_.Exception.Message
        Write-TidyLog -Level Warning -Message "Failed to reset print isolation policy: $($_.Exception.Message)"
    }
}

try {
    try {
        Start-Transcript -Path $transcriptPath -Force -ErrorAction SilentlyContinue | Out-Null
    }
    catch {
        # Transcript may fail if already running; non-fatal.
    }

    Write-TidyLog -Level Information -Message 'Starting print spooler recovery suite.'

    if (-not (Test-TidyAdmin)) {
        $elevated = Ensure-Elevation -AllowNoElevate:$false
        if (-not $elevated) {
            throw 'Print spooler recovery requires elevated privileges and elevation was disabled.'
        }
    }

    $skipRestartRequested = $SkipServiceReset.IsPresent
    $requiresMaintenanceStop = -not $SkipSpoolPurge.IsPresent -or -not $SkipDriverRefresh.IsPresent
    $requiresServiceReset = -not $SkipServiceReset.IsPresent

    if ($requiresServiceReset -or $requiresMaintenanceStop) {
        Write-TidyOutput -Message 'Stopping print services for maintenance.'
        if (Stop-SpoolerServices) {
            $script:ServicesStoppedForMaintenance = $true
        }
    }

    if (-not $SkipSpoolPurge.IsPresent) {
        Write-TidyOutput -Message 'Clearing print spooler queue.'
        Clear-SpoolerQueue
    }
    else {
        Track-TidyStep -Step 'Clear spool queue' -Status 'Skipped' -Details 'Per operator request'
        Write-TidyOutput -Message 'Skipping spool queue purge per operator request.'
    }

    if (-not $SkipDriverRefresh.IsPresent) {
        Write-TidyOutput -Message 'Enumerating and removing stale printer drivers (if any).'
        Remove-OfflinePrinterDrivers
    }
    else {
        Track-TidyStep -Step 'Driver cleanup' -Status 'Skipped' -Details 'Per operator request'
        Write-TidyOutput -Message 'Skipping driver cleanup per operator request.'
    }

    if (-not $SkipDllRegistration.IsPresent) {
        Write-TidyOutput -Message 'Re-registering core spooler DLLs.'
        Register-SpoolerDlls
    }
    else {
        Track-TidyStep -Step 'Register spooler DLLs' -Status 'Skipped' -Details 'Per operator request'
        Write-TidyOutput -Message 'Skipping spooler DLL registration per operator request.'
    }

    if (-not $SkipPrintIsolationPolicy.IsPresent) {
        Write-TidyOutput -Message 'Resetting print driver isolation policies to defaults.'
        Reset-PrintIsolationPolicies
    }
    else {
        Track-TidyStep -Step 'Reset print isolation policy' -Status 'Skipped' -Details 'Per operator request'
        Write-TidyOutput -Message 'Skipping print isolation policy reset per operator request.'
    }

    if ($script:ServicesStoppedForMaintenance) {
        if ($skipRestartRequested) {
            Write-TidyOutput -Message 'Print services were stopped for maintenance; restoring them despite restart skip request.'
        }
        else {
            Write-TidyOutput -Message 'Starting print spooler services.'
        }

        Start-SpoolerServices
        $script:ServicesStoppedForMaintenance = $false
    }
    elseif ($skipRestartRequested) {
        Track-TidyStep -Step 'Restart spooler services' -Status 'Skipped' -Details 'Per operator request'
        Write-TidyOutput -Message 'Skipping spooler service restart per operator request.'

        if (-not $script:DryRunMode) {
            $spooler = Get-TidyServiceInstance -ServiceName 'Spooler'
            if ($spooler -and $spooler.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
                Write-TidyOutput -Message 'Print Spooler was not running; starting to ensure printing remains available.'
                Start-SpoolerServices
            }
        }
    }
    else {
        Write-TidyOutput -Message 'Ensuring print spooler services are running.'
        Start-SpoolerServices
    }

    Write-TidyOutput -Message 'Print spooler recovery suite completed.'
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
    try { Stop-Transcript -ErrorAction SilentlyContinue | Out-Null } catch {}

    try {
        $summary = [pscustomobject]@{
            Time       = (Get-Date).ToString('o')
            Success    = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
            DryRun     = $script:DryRunMode
            Steps      = $script:RunLog
            Transcript = $transcriptPath
            SkipServiceReset       = $SkipServiceReset.IsPresent
            SkipSpoolPurge         = $SkipSpoolPurge.IsPresent
            SkipDriverRefresh      = $SkipDriverRefresh.IsPresent
            SkipDllRegistration    = $SkipDllRegistration.IsPresent
            SkipPrintIsolationPolicy = $SkipPrintIsolationPolicy.IsPresent
        }

        $summary | ConvertTo-Json -Depth 6 | Out-File -FilePath $LogPath -Encoding UTF8
    }
    catch {
        # non-fatal if summary write fails
    }
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

