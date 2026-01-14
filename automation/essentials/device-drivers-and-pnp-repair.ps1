param(
    [switch] $SkipPnPRescan,
    [switch] $SkipStaleDriverCleanup,
    [switch] $SkipPnPStackRestart,
    [switch] $SkipSelectiveSuspendDisable,
    [int] $UnusedDriverAgeDays = 30,
    [string] $DriverBackupPath,
    [int] $DriverBackupRetentionDays = 30,
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
$script:DriverBackupRoot = $null
$script:DriverBackupRetentionDays = if ($DriverBackupRetentionDays -lt 1) { 30 } else { $DriverBackupRetentionDays }

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

if (-not [string]::IsNullOrWhiteSpace($DriverBackupPath)) {
    try {
        $script:DriverBackupRoot = [System.IO.Path]::GetFullPath($DriverBackupPath)
        New-Item -Path $script:DriverBackupRoot -ItemType Directory -Force | Out-Null
        Write-TidyOutput -Message ("Driver backup enabled. Exporting removed packages to {0}." -f $script:DriverBackupRoot)
        Cleanup-DriverBackups -BackupRoot $script:DriverBackupRoot -RetentionDays $script:DriverBackupRetentionDays
    }
    catch {
        Write-TidyOutput -Message ("Driver backup path could not be created; backups disabled. Details: {0}" -f $_.Exception.Message)
        $script:DriverBackupRoot = $null
    }
}

function Write-TidyOutput {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) { return }

    if ($script:TidyOutputLines -is [System.Collections.IList]) {
        [void]$script:TidyOutputLines.Add($text)
    }

    TidyWindow.Automation\Write-TidyLog -Level Information -Message $text
}

function Write-TidyError {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) { return }

    if ($script:TidyErrorLines -is [System.Collections.IList]) {
        [void]$script:TidyErrorLines.Add($text)
    }

    TidyWindow.Automation\Write-TidyError -Message $text
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
        [Parameter(Mandatory = $true)][string] $Name,
        [string] $DesiredStatus = 'Running',
        [int] $TimeoutSeconds = 12
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

function Rescan-PnpDevices {
    try {
        Invoke-TidyCommand -Command { pnputil /scan-devices } -Description 'Triggering Plug and Play device rescan.' -RequireSuccess
        Write-TidyOutput -Message 'PnP device rescan requested.'
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("PnP rescan failed: {0}" -f $_.Exception.Message)
    }
}

function Get-OemDriverEntries {
    $raw = & pnputil /enum-drivers 2>&1
    foreach ($line in @($raw)) { Write-TidyOutput -Message $line }

    $entries = @()
    $current = @{}

    foreach ($line in @($raw)) {
        $trim = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trim)) {
            if ($current.ContainsKey('PublishedName') -and -not [string]::IsNullOrWhiteSpace($current['PublishedName'])) {
                $entries += [pscustomobject]$current
            }
            $current = @{}
            continue
        }

        if ($trim -match '^Published Name\s*:\s*(.+)$') { $current['PublishedName'] = $matches[1].Trim() }
        elseif ($trim -match '^Driver Package Provider\s*:\s*(.+)$') { $current['Provider'] = $matches[1].Trim() }
        elseif ($trim -match '^Class\s*:\s*(.+)$') { $current['Class'] = $matches[1].Trim() }
    }

    if ($current.ContainsKey('PublishedName') -and -not [string]::IsNullOrWhiteSpace($current['PublishedName'])) {
        $entries += [pscustomobject]$current
    }

    $entries = $entries |
        Where-Object { $_.PSObject.Properties['PublishedName'] -and -not [string]::IsNullOrWhiteSpace($_.PublishedName) } |
        ForEach-Object {
            $provider = if ($_.PSObject.Properties['Provider']) { $_.Provider } else { '' }
            $class = if ($_.PSObject.Properties['Class']) { $_.Class } else { '' }
            [pscustomobject]@{
                PublishedName = $_.PublishedName
                Provider      = $provider
                Class         = $class
            }
        }
    return $entries
}

$script:PnputilDriverFilterSupported = $null
function Test-PnputilDriverFilterSupport {
    if ($null -ne $script:PnputilDriverFilterSupported) {
        return $script:PnputilDriverFilterSupported
    }

    try {
        $help = & pnputil /enum-devices /? 2>&1
        $script:PnputilDriverFilterSupported = ($help -match '/driver')
    }
    catch {
        $script:PnputilDriverFilterSupported = $false
    }

    return $script:PnputilDriverFilterSupported
}

function Test-TidyOffline {
    try {
        $profile = Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { $_.IPv4Connectivity -eq 'Internet' -or $_.IPv6Connectivity -eq 'Internet' }
        if ($profile) { return $false }

        $ping = Test-Connection -ComputerName 1.1.1.1 -Count 1 -Quiet -ErrorAction SilentlyContinue
        return -not $ping
    }
    catch {
        return $true
    }
}

function Get-DriverBindings {
    param(
        [Parameter(Mandatory = $true)][string] $PublishedName
    )

    $bindings = [System.Collections.Generic.List[pscustomobject]]::new()
    $canFilterByDriver = Test-PnputilDriverFilterSupport
    $isOffline = Test-TidyOffline

    if (-not $isOffline -and $canFilterByDriver) {
        try {
            $enumOutput = & pnputil /enum-devices /driver $PublishedName 2>&1
            $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { $LASTEXITCODE } else { 0 }
            $looksLikeHelp = ($enumOutput -match '^PNPUTIL \[/add-driver') -or (($enumOutput -match '/add-driver') -and -not ($enumOutput -match 'Instance ID'))
            if ($exitCode -ne 0 -or $looksLikeHelp) {
                Write-TidyOutput -Message ("pnputil /enum-devices /driver unsupported or returned help for {0} (exit {1}); falling back to WMI." -f $PublishedName, $exitCode)
            }
            else {
                foreach ($line in @($enumOutput)) { Write-TidyOutput -Message $line }
                foreach ($line in @($enumOutput)) {
                    if ($line -match 'Instance ID\s*:\s*(.+)$') {
                        $id = $matches[1].Trim()
                        if (-not [string]::IsNullOrWhiteSpace($id)) {
                            $bindings.Add([pscustomobject]@{ InstanceId = $id; Source = 'pnputil' })
                        }
                    }
                }
            }
        }
        catch {
            Write-TidyOutput -Message ("pnputil device enumeration for {0} failed: {1}" -f $PublishedName, $_.Exception.Message)
        }
    }

    if ($bindings.Count -eq 0) {
        try {
            $filterName = $PublishedName.Replace("'", "''")
            $signedDrivers = Get-CimInstance -ClassName Win32_PnPSignedDriver -Filter ("InfName = '{0}'" -f $filterName) -ErrorAction Stop
            foreach ($drv in @($signedDrivers)) {
                $instanceId = $null
                if ($drv.PSObject.Properties['DeviceID']) { $instanceId = $drv.DeviceID }
                elseif ($drv.PSObject.Properties['InstanceID']) { $instanceId = $drv.InstanceID }
                $deviceName = $null
                if ($drv.PSObject.Properties['DeviceName']) { $deviceName = $drv.DeviceName }

                if ([string]::IsNullOrWhiteSpace($instanceId) -and [string]::IsNullOrWhiteSpace($deviceName)) { continue }

                $bindings.Add([pscustomobject]@{
                        InstanceId = $instanceId
                        DeviceName = $deviceName
                        Source     = 'Win32_PnPSignedDriver'
                    })
            }
        }
        catch {
            Write-TidyOutput -Message ("Win32_PnPSignedDriver lookup for {0} failed: {1}" -f $PublishedName, $_.Exception.Message)
        }
    }

    # Filter out any accidental non-object entries that lack usable identifiers
    $bindings = @($bindings | Where-Object {
        ($_ -is [psobject]) -and (
            ($_.PSObject.Properties['InstanceId'] -and -not [string]::IsNullOrWhiteSpace($_.InstanceId)) -or
            ($_.PSObject.Properties['DeviceName'] -and -not [string]::IsNullOrWhiteSpace($_.DeviceName))
        )
    })

    return ,$bindings
}

function Test-DriverBindingsPresent {
    param(
        [Parameter(Mandatory = $true)][psobject[]] $Bindings
    )

    $present = $false
    foreach ($binding in @($Bindings)) {
        $instanceId = $null
        if ($binding.PSObject.Properties['InstanceId']) { $instanceId = $binding.InstanceId }
        if ([string]::IsNullOrWhiteSpace($instanceId)) { continue }

        try {
            $dev = Get-PnpDevice -InstanceId $instanceId -ErrorAction SilentlyContinue
            if ($dev -and $dev.PSObject.Properties['Present'] -and $dev.Present) {
                $present = $true
                break
            }
        }
        catch {
            # If we cannot resolve presence, err on the side of caution by treating as present
            $present = $true
            break
        }
    }

    return $present
}

function Get-DriverMetadata {
    param(
        [Parameter(Mandatory = $true)][string] $PublishedName
    )

    try {
        $filterName = $PublishedName.Replace("'", "''")
        $drivers = Get-CimInstance -ClassName Win32_PnPSignedDriver -Filter ("InfName = '{0}'" -f $filterName) -ErrorAction Stop
        if (-not $drivers) { return $null }

        $selected = $drivers | Sort-Object -Property DriverDate -Descending | Select-Object -First 1
        $driverDate = $null
        if ($selected.PSObject.Properties['DriverDate']) {
            try { $driverDate = [System.Management.ManagementDateTimeConverter]::ToDateTime($selected.DriverDate) } catch { $driverDate = $null }
        }

        $driverVersion = $null
        if ($selected.PSObject.Properties['DriverVersion']) { $driverVersion = $selected.DriverVersion }

        return [pscustomobject]@{
            DriverDate    = $driverDate
            DriverVersion = $driverVersion
            Provider      = if ($selected.PSObject.Properties['DriverProviderName']) { $selected.DriverProviderName } else { $null }
            Class         = if ($selected.PSObject.Properties['DeviceClass']) { $selected.DeviceClass } else { $null }
        }
    }
    catch {
        Write-TidyOutput -Message ("Metadata lookup failed for {0}: {1}" -f $PublishedName, $_.Exception.Message)
        return $null
    }
}

function Backup-DriverPackage {
    param(
        [Parameter(Mandatory = $true)][string] $PublishedName,
        [Parameter(Mandatory = $true)][string] $BackupRoot
    )

    if ([string]::IsNullOrWhiteSpace($BackupRoot)) { return $false }

    $targetDir = Join-Path -Path $BackupRoot -ChildPath ($PublishedName -replace '\\','_' -replace '/','_')
    try {
        New-Item -Path $targetDir -ItemType Directory -Force | Out-Null
    }
    catch {
        Write-TidyOutput -Message ("Could not create backup directory for {0}: {1}" -f $PublishedName, $_.Exception.Message)
        return $false
    }

    try {
        $exit = Invoke-TidyCommand -Command { param($pn,$dest) pnputil /export-driver $pn $dest } -Arguments @($PublishedName, $targetDir) -Description ("Exporting driver {0} to backup" -f $PublishedName) -AcceptableExitCodes @(0)
        if ($exit -ne 0) {
            Write-TidyOutput -Message ("pnputil /export-driver for {0} returned exit {1}; backup may be incomplete." -f $PublishedName, $exit)
            return $false
        }
        Write-TidyOutput -Message ("Driver package {0} exported to {1}." -f $PublishedName, $targetDir)
        return $true
    }
    catch {
        Write-TidyOutput -Message ("Driver backup for {0} failed: {1}" -f $PublishedName, $_.Exception.Message)
        return $false
    }
}

function Cleanup-DriverBackups {
    param(
        [Parameter(Mandatory = $true)][string] $BackupRoot,
        [Parameter(Mandatory = $true)][int] $RetentionDays
    )

    if ([string]::IsNullOrWhiteSpace($BackupRoot)) { return }
    if (-not (Test-Path -Path $BackupRoot)) { return }

    $days = [Math]::Max(1, $RetentionDays)
    $threshold = (Get-Date).AddDays(-$days)
    try {
        $dirs = Get-ChildItem -Path $BackupRoot -Directory -ErrorAction SilentlyContinue
        foreach ($dir in @($dirs)) {
            $last = if ($dir.LastWriteTime -gt $dir.CreationTime) { $dir.LastWriteTime } else { $dir.CreationTime }
            if ($last -gt $threshold) { continue }

            try {
                Remove-Item -Path $dir.FullName -Recurse -Force -ErrorAction Stop
                Write-TidyOutput -Message ("Removed backup folder older than {0} days: {1}" -f $days, $dir.FullName)
            }
            catch {
                Write-TidyOutput -Message ("Could not remove backup folder {0}: {1}" -f $dir.FullName, $_.Exception.Message)
            }
        }
    }
    catch {
        Write-TidyOutput -Message ("Backup cleanup failed: {0}" -f $_.Exception.Message)
    }
}

function Cleanup-StaleDrivers {
    try {
        $entries = Get-OemDriverEntries
        $candidates = $entries | Where-Object {
            $_.PSObject.Properties['PublishedName'] -and $_.PublishedName -like 'oem*.inf' -and -not ($_.Provider -match '^Microsoft')
        }

        if (-not $candidates -or $candidates.Count -eq 0) {
            Write-TidyOutput -Message 'No non-Microsoft oem*.inf packages eligible for cleanup.'
            return
        }

        $removed = 0
        $inUse = 0
        $skippedDueToBindings = 0
        $skippedTooRecent = 0
        $skippedNoDate = 0
        $attempted = if ($candidates) { $candidates.Count } else { 0 }
        $isOffline = Test-TidyOffline
        $ageDays = if ($UnusedDriverAgeDays -lt 1) { 30 } else { $UnusedDriverAgeDays }
        $thresholdDate = (Get-Date).AddDays(-[Math]::Max(1, $ageDays))
        Write-TidyOutput -Message ("Drivers with no present bindings and older than {0} days will be removed." -f ([Math]::Max(1,$ageDays)))
        if ($isOffline) {
            Write-TidyOutput -Message 'Network appears offline; skipping pnputil filter help probe; will still use CIM fallback for bindings.'
        }
        foreach ($entry in $candidates) {
            $name = $entry.PublishedName

            Write-TidyOutput -Message ("Devices using {0}:" -f $name)
            $bindings = @(Get-DriverBindings -PublishedName $name)
            if (-not $bindings -or $bindings.Count -eq 0) {
                Write-TidyOutput -Message 'No bindings detected for this driver package.'
            }
            else {
                foreach ($binding in $bindings | Select-Object -First 5) {
                    $hasDeviceName = $binding -and $binding.PSObject -and $binding.PSObject.Properties['DeviceName'] -and -not [string]::IsNullOrWhiteSpace($binding.DeviceName)
                    $hasInstanceId = $binding -and $binding.PSObject -and $binding.PSObject.Properties['InstanceId'] -and -not [string]::IsNullOrWhiteSpace($binding.InstanceId)
                    $label = if ($hasDeviceName) { $binding.DeviceName }
                        elseif ($hasInstanceId) { $binding.InstanceId }
                        else { ($binding | Out-String).Trim() }
                    $sourceLabel = if ($binding -and $binding.PSObject -and $binding.PSObject.Properties['Source']) { $binding.Source } else { 'unknown' }
                    Write-TidyOutput -Message ("{0} (source: {1})" -f $label, $sourceLabel)
                }
                if ($bindings.Count -gt 5) {
                    Write-TidyOutput -Message ("...and {0} more bindings" -f ($bindings.Count - 5))
                }
                $bindingsPresent = Test-DriverBindingsPresent -Bindings $bindings
                if ($bindingsPresent) {
                    Write-TidyOutput -Message ("Driver package {0} has present devices bound ({1}); skipping removal to avoid impacting active hardware." -f $name, $bindings.Count)
                    $skippedDueToBindings++
                    continue
                }
                else {
                    Write-TidyOutput -Message ("Driver package {0} bindings are non-present; safe to attempt removal." -f $name)
                }
            }

            $meta = Get-DriverMetadata -PublishedName $name
            if (-not $meta -or -not $meta.DriverDate) {
                Write-TidyOutput -Message ("Driver package {0} has no reliable driver date; skipping removal for safety." -f $name)
                $skippedNoDate++
                continue
            }

            if ($meta.DriverDate -gt $thresholdDate) {
                Write-TidyOutput -Message ("Driver package {0} is newer than threshold ({1:yyyy-MM-dd}); skipping removal." -f $name, $meta.DriverDate)
                $skippedTooRecent++
                continue
            }

            try {
                if ($script:DriverBackupRoot) {
                    [void](Backup-DriverPackage -PublishedName $name -BackupRoot $script:DriverBackupRoot)
                }

                Invoke-TidyCommand -Command { param($pn) pnputil /delete-driver $pn /force } -Arguments @($name) -Description ("Removing driver package {0}" -f $name) -RequireSuccess
                $removed++
                $providerLabel = if ($entry.PSObject.Properties['Provider']) { $entry.Provider } else { '' }
                $classLabel = if ($entry.PSObject.Properties['Class']) { $entry.Class } else { '' }
                Write-TidyOutput -Message ("Removed driver package {0} (Provider: {1}; Class: {2})." -f $name, $providerLabel, $classLabel)
            }
            catch {
                $inUse++
                Write-TidyOutput -Message ("Driver package {0} is in use or protected; removal skipped. Details: {1}" -f $name, $_.Exception.Message)
            }
        }

        Write-TidyOutput -Message ("Driver cleanup summary: candidates {0}, skipped (bindings) {1}, skipped (no date) {2}, skipped (too recent) {3}, in-use/protected {4}, removed {5}." -f $attempted, $skippedDueToBindings, $skippedNoDate, $skippedTooRecent, $inUse, $removed)
        Write-TidyOutput -Message 'Guidance: removal performed automatically for non-present drivers older than the threshold; recent or dated-unknown drivers are skipped for safety.'
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Stale driver cleanup failed: {0}" -f $_.Exception.Message)
    }
}

function Restart-PnpStack {
    $serviceNames = @('DPS', 'WudfSvc', 'PlugPlay')
    foreach ($name in $serviceNames) {
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if (-not $svc) {
            Write-TidyOutput -Message ("Service {0} not found; skipping." -f $name)
            continue
        }

        try {
            $description = if ($svc.Status -eq 'Stopped') { "Starting {0}" -f $name } else { "Restarting {0}" -f $name }
            if ($svc.Status -eq 'Stopped') {
                Invoke-TidyCommand -Command { param($svcName) Start-Service -Name $svcName -ErrorAction Stop } -Arguments @($name) -Description $description -RequireSuccess
            }
            else {
                Invoke-TidyCommand -Command { param($svcName) Restart-Service -Name $svcName -Force -ErrorAction Stop } -Arguments @($name) -Description $description -RequireSuccess
            }

            if (-not (Wait-TidyServiceState -Name $name -DesiredStatus 'Running' -TimeoutSeconds 10)) {
                Write-TidyOutput -Message ("{0} did not report Running state after restart." -f $name)
            }
            else {
                Write-TidyOutput -Message ("{0} is running." -f $name)
            }
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("{0} restart failed: {1}" -f $name, $_.Exception.Message)
        }
    }

    Write-TidyOutput -Message 'PnP stack refresh (services) attempted. DcomLaunch restart is intentionally skipped for safety.'
}

function Disable-UsbSelectiveSuspend {
    try {
        $subUsb = '2a737441-1930-4402-8d77-b2bebba308a3' # SUB_USB
        $usbSelective = '4faab71a-92e5-4726-b531-224559672d19' # USBSELECTIVE SUSPEND

        $planList = powercfg /list 2>&1
        $highPerfGuid = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'
        $ultimateGuid = 'e9a42b02-d5df-448d-aa00-03f14749eb61'
        $hasPerfPlan = $planList -match [regex]::Escape($highPerfGuid) -or $planList -match [regex]::Escape($ultimateGuid)
        if (-not $hasPerfPlan) {
            Write-TidyOutput -Message 'High Performance or Ultimate plan not present; skipping USB selective suspend tweak.'
            return
        }

        $activeScheme = (powercfg /getactivescheme 2>&1 | Select-Object -First 1) -replace '.*GUID:\s*([0-9a-fA-F-]+).*','$1'
        if ([string]::IsNullOrWhiteSpace($activeScheme) -or -not ($activeScheme -match '^[0-9a-fA-F-]{36}$')) {
            Write-TidyOutput -Message 'Could not resolve active power scheme GUID; skipping USB selective suspend change.'
            return
        }

        $hasSetting = (Invoke-TidyCommand -Command { param($scheme, $sub, $setting) & cmd /c "powercfg /q $scheme $sub $setting 2>&1" } -Arguments @($activeScheme, $subUsb, $usbSelective) -Description 'Validating USB selective suspend setting presence.' -AcceptableExitCodes @(0,1)) -eq 0
        if (-not $hasSetting) {
            Write-TidyOutput -Message 'USB selective suspend setting not found for active plan; skipping tweak.'
            return
        }

        $acExit = Invoke-TidyCommand -Command { param($scheme, $sub, $setting) & cmd /c "powercfg /setacvalueindex $scheme $sub $setting 0 2>&1" } -Arguments @($activeScheme, $subUsb, $usbSelective) -Description 'Disabling USB selective suspend (AC).' -AcceptableExitCodes @(0)
        $dcExit = Invoke-TidyCommand -Command { param($scheme, $sub, $setting) & cmd /c "powercfg /setdcvalueindex $scheme $sub $setting 0 2>&1" } -Arguments @($activeScheme, $subUsb, $usbSelective) -Description 'Disabling USB selective suspend (DC).' -AcceptableExitCodes @(0)

        if ($acExit -ne 0 -or $dcExit -ne 0) {
            Write-TidyOutput -Message "powercfg reported errors while setting USB selective suspend (AC exit $acExit, DC exit $dcExit). Skipping commit; settings likely unsupported on this platform."
            return
        }

        $setActiveExit = Invoke-TidyCommand -Command { param($scheme) powercfg /setactive $scheme } -Arguments @($activeScheme) -Description 'Reapplying active power scheme to commit USB changes.' -AcceptableExitCodes @(0)
        if ($setActiveExit -ne 0) {
            Write-TidyOutput -Message "powercfg /setactive returned exit code $setActiveExit. USB selective suspend changes may not be applied."
            return
        }

        Write-TidyOutput -Message 'USB selective suspend disabled for AC/DC power schemes.'
    }
    catch {
        Write-TidyOutput -Message ("USB selective suspend disable skipped: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Device drivers and PnP repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting device drivers and PnP repair pack.'

    if (-not $SkipPnPRescan.IsPresent) {
        Rescan-PnpDevices
    }
    else {
        Write-TidyOutput -Message 'Skipping PnP rescan per operator request.'
    }

    if (-not $SkipStaleDriverCleanup.IsPresent) {
        Cleanup-StaleDrivers
    }
    else {
        Write-TidyOutput -Message 'Skipping stale driver package cleanup per operator request.'
    }

    if (-not $SkipPnPStackRestart.IsPresent) {
        Restart-PnpStack
    }
    else {
        Write-TidyOutput -Message 'Skipping PnP stack service refresh per operator request.'
    }

    if (-not $SkipSelectiveSuspendDisable.IsPresent) {
        Disable-UsbSelectiveSuspend
    }
    else {
        Write-TidyOutput -Message 'Skipping USB selective suspend disable per operator request.'
    }

    Write-TidyOutput -Message 'Device drivers and PnP repair pack completed.'
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
    Write-TidyLog -Level Information -Message 'Device drivers and PnP repair script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
