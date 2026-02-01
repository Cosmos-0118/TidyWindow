param(
    [switch] $ResetServices,
    [switch] $ResetComponents,
    [switch] $ReRegisterLibraries,
    [switch] $RunDismRestoreHealth,
    [switch] $RunSfc,
    [switch] $TriggerScan,
    [switch] $ResetPolicies,
    [switch] $ResetNetwork,
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
$script:AdministratorsGroupName = $null
$script:RestorePointCreated = $false
$operationSwitches = @('ResetServices', 'ResetComponents', 'ReRegisterLibraries', 'RunDismRestoreHealth', 'RunSfc', 'TriggerScan', 'ResetPolicies', 'ResetNetwork')
$anySwitchProvided = $false
foreach ($name in $operationSwitches) {
    if ($PSBoundParameters.ContainsKey($name)) {
        $anySwitchProvided = $true
        break
    }
}

if (-not $anySwitchProvided) {
    foreach ($name in $operationSwitches) {
        Set-Variable -Name $name -Value ([System.Management.Automation.SwitchParameter]$true) -Scope Script
    }
}

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
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

    # Clear sticky LASTEXITCODE from prior native calls to avoid false failures.
    if (Test-Path -Path 'variable:LASTEXITCODE') {
        $global:LASTEXITCODE = 0
    }

    $output = & $Command @Arguments 2>&1
    $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { $LASTEXITCODE } else { 0 }

    # If the scriptblock emitted a numeric code while LASTEXITCODE stayed 0, honor it.
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

function Stop-WindowsUpdateServices {
    Write-TidyOutput -Message 'Stopping Windows Update related services.'
    $services = @('wuauserv', 'bits', 'cryptsvc', 'appidsvc', 'msiserver')
    foreach ($service in $services) {
        Invoke-TidyCommand -Command {
                param($name)

                try {
                    $svc = Get-Service -Name $name -ErrorAction Stop
                }
                catch {
                    return ("Service {0} not found. Skipping." -f $name)
                }

                if ($svc.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                    return ("Service {0} already stopped." -f $name)
                }

                Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
                $svc.Refresh()
                try {
                    $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(10)) | Out-Null
                }
                catch {
                    return ("Issued stop for {0}; waiting timed out but operation will continue." -f $name)
                }

                return ("Service {0} stopped." -f $name)
            } -Arguments @($service) -Description ("Stopping service {0}" -f $service)
    }
}

function Start-WindowsUpdateServices {
    Write-TidyOutput -Message 'Starting Windows Update related services.'
    $services = @('wuauserv', 'bits', 'cryptsvc', 'appidsvc', 'msiserver')
    foreach ($service in $services) {
        Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction SilentlyContinue } -Arguments @($service) -Description ("Starting service {0}" -f $service)
    }
}

function Get-AdministratorsGroupName {
    if ($script:AdministratorsGroupName) {
        return $script:AdministratorsGroupName
    }

    $sid = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-544'
    $script:AdministratorsGroupName = $sid.Translate([System.Security.Principal.NTAccount]).Value
    return $script:AdministratorsGroupName
}

function Get-TidyWindowsVersionInfo {
    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        return [pscustomobject]@{
            Caption     = $os.Caption
            Version     = $os.Version
            BuildNumber = $os.BuildNumber
        }
    }
    catch {
        $fallback = [System.Environment]::OSVersion
        return [pscustomobject]@{
            Caption     = $null
            Version     = $fallback.Version.ToString()
            BuildNumber = $fallback.Version.Build
        }
    }
}

function Test-TidyPendingReboot {
    $checkPaths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
    )

    foreach ($path in $checkPaths) {
        if (Test-Path -LiteralPath $path) {
            return $true
        }
    }

    $sessionManagerKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
    try {
        $pending = (Get-ItemProperty -Path $sessionManagerKey -Name 'PendingFileRenameOperations' -ErrorAction Stop).PendingFileRenameOperations
        if ($pending) {
            return $true
        }
    }
    catch {
        # no action, property not present
    }

    return $false
}

function New-TidySystemRestorePoint {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $checkpointCmd = Get-Command -Name 'Checkpoint-Computer' -ErrorAction SilentlyContinue
    if (-not $checkpointCmd) {
        Write-TidyLog -Level Warning -Message 'Checkpoint-Computer unavailable; skipping system restore point.'
        return $false
    }

    try {
        # Capture warning stream to prevent raw WARNING: output and handle gracefully.
        $warningOutput = $null
        Checkpoint-Computer -Description $Description -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop -WarningVariable warningOutput -WarningAction SilentlyContinue
        
        if ($warningOutput) {
            # Handle the 24-hour frequency limit warning gracefully.
            $warningText = $warningOutput | Out-String
            if ($warningText -match 'already been created within the past') {
                Write-TidyLog -Level Information -Message 'System Restore point skipped: recent checkpoint already exists (24-hour frequency limit).'
                Write-TidyOutput -Message 'Using existing System Restore checkpoint (created within the last 24 hours).'
                return $true
            }
            else {
                Write-TidyLog -Level Warning -Message ('System Restore warning: {0}' -f $warningText.Trim())
            }
        }
        else {
            Write-TidyOutput -Message 'Created a System Restore checkpoint.'
        }
        return $true
    }
    catch {
        $message = $_.Exception.Message
        Write-TidyLog -Level Warning -Message ('System Restore point skipped: {0}' -f $message)
        Write-TidyOutput -Message ('Restore point not created: {0}' -f $message)
        return $false
    }
}

function Ensure-WindowsUpdateServiceDefaults {
    Write-TidyOutput -Message 'Validating Windows Update service startup configuration.'
    $serviceTargets = @(
        @{ Name = 'wuauserv'; StartupType = 'Manual' },
        @{ Name = 'bits'; StartupType = 'Automatic' },
        @{ Name = 'cryptsvc'; StartupType = 'Automatic' },
        @{ Name = 'appidsvc'; StartupType = 'Manual' },
        @{ Name = 'msiserver'; StartupType = 'Manual' }
    )

    foreach ($target in $serviceTargets) {
        try {
            $service = Get-Service -Name $target.Name -ErrorAction Stop
        }
        catch {
            Write-TidyLog -Level Warning -Message ('Service {0} missing; skipping default reset.' -f $target.Name)
            continue
        }

        if ($service.StartType -eq 'Disabled') {
            try {
                Set-Service -Name $target.Name -StartupType $target.StartupType -ErrorAction Stop
                Write-TidyOutput -Message ('Service {0} startup mode reset to {1}.' -f $target.Name, $target.StartupType)
            }
            catch {
                Write-TidyLog -Level Warning -Message ('Failed to update startup mode for {0}: {1}' -f $target.Name, $_.Exception.Message)
            }
        }
    }
}

function Invoke-DismComponentCleanup {
    Write-TidyOutput -Message 'Running DISM component cleanup to remove superseded payloads.'
    Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /StartComponentCleanup } -Description 'DISM StartComponentCleanup' | Out-Null
}

function Invoke-RobustRename {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $Destination,
        [Parameter(Mandatory = $true)]
        [string] $DisplayName
    )

    $maxAttempts = 3
    $attempt = 0
    $lastError = $null

    while ($attempt -lt $maxAttempts) {
        $attempt++
        try {
            Move-Item -LiteralPath $Path -Destination $Destination -Force -ErrorAction Stop
            $suffix = if ($attempt -eq 1) { 'Renamed' } else { "Renamed after retry" }
            Write-TidyOutput -Message ("{0} {1} to {2}." -f $suffix, $DisplayName, $Destination)
            return $true
        }
        catch {
            $lastError = $_.Exception.Message
            if ($attempt -eq 1) {
                Write-TidyOutput -Message ("Initial rename of {0} failed: {1}. Attempting ownership reset." -f $DisplayName, $lastError)
                break
            }

            if ($attempt -lt $maxAttempts) {
                Write-TidyOutput -Message ("Retry {0} to move {1} still blocked: {2}." -f $attempt, $DisplayName, $lastError)
                Start-Sleep -Seconds 2
            }
        }
    }
    $adminGroup = Get-AdministratorsGroupName

    try {
        Invoke-TidyCommand -Command { param($target) attrib.exe -s -h -r $target } -Arguments @($Path) -Description ("Clearing attributes on {0}" -f $DisplayName) | Out-Null
    }
    catch {
        Write-TidyOutput -Message ("  ↳ Attribute reset warning: {0}" -f $_.Exception.Message)
    }

    try {
        Invoke-TidyCommand -Command { param($target) takeown.exe /f $target /r /d y } -Arguments @($Path) -Description ("Taking ownership of {0}" -f $DisplayName) | Out-Null
    }
    catch {
        Write-TidyOutput -Message ("  ↳ Ownership reset warning: {0}" -f $_.Exception.Message)
    }

    try {
        Invoke-TidyCommand -Command { param($target, $principal) icacls $target /grant "$principal":F /t /c } -Arguments @($Path, $adminGroup) -Description ("Granting full control on {0}" -f $DisplayName) | Out-Null
    }
    catch {
        Write-TidyOutput -Message ("  ↳ ACL update warning: {0}" -f $_.Exception.Message)
    }

    $lastError = $null
    while ($attempt -lt $maxAttempts) {
        $attempt++
        try {
            Move-Item -LiteralPath $Path -Destination $Destination -Force -ErrorAction Stop
            Write-TidyOutput -Message ("Renamed {0} to {1} after resetting permissions." -f $DisplayName, $Destination)
            return $true
        }
        catch {
            $lastError = $_.Exception.Message
            if ($attempt -lt $maxAttempts) {
                Write-TidyOutput -Message ("Retry {0} to move {1} still blocked: {2}." -f $attempt, $DisplayName, $lastError)
                Start-Sleep -Seconds 2
            }
        }
    }

    Write-TidyOutput -Message ("Scheduling rename of {0} for the next system reboot." -f $DisplayName)

    try {
        $resolvedSource = [System.IO.Path]::GetFullPath($Path)
        $resolvedDestination = [System.IO.Path]::GetFullPath($Destination)
        $pendingKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
        $existing = (Get-ItemProperty -Path $pendingKey -Name 'PendingFileRenameOperations' -ErrorAction SilentlyContinue).PendingFileRenameOperations
        $operations = [System.Collections.Generic.List[string]]::new()
        if ($existing) {
            $operations.AddRange($existing)
        }

        $operations.Add("\??\$resolvedSource")
        $operations.Add("\??\$resolvedDestination")
        Set-ItemProperty -Path $pendingKey -Name 'PendingFileRenameOperations' -Value $operations.ToArray()

        if (-not (Test-Path -LiteralPath $Path)) {
            New-Item -Path $Path -ItemType Directory -Force | Out-Null
        }

        Write-TidyOutput -Message ("Queued rename of {0} to {1}. Reboot required to complete cache reset." -f $DisplayName, $Destination)
        return $true
    }
    catch {
        $errorMessage = if ($lastError) { $lastError } else { $_.Exception.Message }
        Write-TidyError -Message ("Failed to rename {0}: {1}" -f $DisplayName, $errorMessage)
        return $false
    }
}

function Reset-WindowsUpdateComponents {
    Write-TidyOutput -Message 'Resetting SoftwareDistribution and Catroot2 caches.'
    $softwareDistribution = Join-Path -Path $env:SystemRoot -ChildPath 'SoftwareDistribution'
    $catroot = Join-Path -Path $env:SystemRoot -ChildPath 'System32\catroot2'

    if (Test-Path -LiteralPath $softwareDistribution) {
        $backup = $softwareDistribution + '.bak-' + (Get-Date -Format 'yyyyMMddHHmmss')
        Invoke-RobustRename -Path $softwareDistribution -Destination $backup -DisplayName 'SoftwareDistribution' | Out-Null
    }

    if (Test-Path -LiteralPath $catroot) {
        $backup = $catroot + '.bak-' + (Get-Date -Format 'yyyyMMddHHmmss')
        Invoke-RobustRename -Path $catroot -Destination $backup -DisplayName 'Catroot2' | Out-Null
    }

    $qmgrPath = Join-Path -Path $env:ALLUSERSPROFILE -ChildPath 'Microsoft\Network\Downloader'
    if (Test-Path -LiteralPath $qmgrPath) {
        Get-ChildItem -Path $qmgrPath -Filter 'qmgr*.dat' -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
                Write-TidyOutput -Message ("Removed job cache file {0}." -f $_.Name)
            }
            catch {
                Write-TidyError -Message ("Failed to remove {0}: {1}" -f $_.Name, $_.Exception.Message)
            }
        }
    }

    try {
        $bitsTransfers = Get-BitsTransfer -AllUsers -ErrorAction Stop
        foreach ($transfer in @($bitsTransfers)) {
            $jobLabel = if (-not [string]::IsNullOrWhiteSpace($transfer.DisplayName)) { $transfer.DisplayName } else { $transfer.JobId }
            try {
                Remove-BitsTransfer -BitsJob $transfer -ErrorAction Stop
                Write-TidyOutput -Message ("Cancelled stale BITS job {0}." -f $jobLabel)
            }
            catch {
                Write-TidyError -Message ("Failed to cancel BITS job {0}: {1}" -f $jobLabel, $_.Exception.Message)
            }
        }
    }
    catch {
        if ($_.Exception -and -not ($_.Exception.Message -like '*cannot find a BITS job*')) {
            Write-TidyLog -Level Warning -Message ('BITS cleanup notice: {0}' -f $_.Exception.Message)
        }
    }
}

function ReRegister-WindowsUpdateLibraries {
    if (-not $ReRegisterLibraries.IsPresent) {
        return
    }

    Write-TidyOutput -Message 'Re-registering Windows Update COM libraries.'
    $libraries = @(
        'atl.dll', 'urlmon.dll', 'mshtml.dll', 'shdocvw.dll', 'browseui.dll', 'jscript.dll', 'vbscript.dll', 'scrrun.dll',
        'msxml.dll', 'msxml2.dll', 'msxml3.dll', 'wuapi.dll', 'wuaueng.dll', 'wuaueng1.dll', 'wucltui.dll', 'wups.dll',
        'wups2.dll', 'wuweb.dll', 'qmgr.dll', 'qmgrprxy.dll', 'wucltux.dll', 'muweb.dll', 'wuwebv.dll'
    )

    foreach ($library in $libraries) {
        Invoke-TidyCommand -Command { param($dll) regsvr32.exe /s $dll } -Arguments @($library) -Description ("regsvr32 {0}" -f $library)
    }
}

function Invoke-DismRestoreHealth {
    if (-not $RunDismRestoreHealth.IsPresent) {
        return
    }

    Write-TidyOutput -Message 'Running DISM /Online /Cleanup-Image /RestoreHealth. This can take 10-30 minutes.'
    Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /RestoreHealth } -Description 'DISM RestoreHealth' -RequireSuccess | Out-Null
}

function Invoke-SystemFileChecker {
    if (-not $RunSfc.IsPresent) {
        return
    }

    Write-TidyOutput -Message 'Running System File Checker (SFC /scannow).' 
    $sfcExitCode = Invoke-TidyCommand -Command { sfc /scannow } -Description 'SFC Scan' -RequireSuccess -AcceptableExitCodes @(1)

    switch ($sfcExitCode) {
        0 { Write-TidyOutput -Message 'System File Checker completed without finding integrity violations.' }
        1 { Write-TidyOutput -Message 'System File Checker found and repaired integrity violations.' }
    }
}

function Reset-WindowsUpdatePolicies {
    if (-not $ResetPolicies.IsPresent) {
        return
    }

    Write-TidyOutput -Message 'Clearing Windows Update policy overrides.'
    $policyRoots = @(
        'Registry::HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate',
        'Registry::HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU'
    )

    foreach ($root in $policyRoots) {
        if (Test-Path -LiteralPath $root) {
            try {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop
                Write-TidyOutput -Message ("Removed policy key {0}." -f $root)
            }
            catch {
                Write-TidyError -Message ("Failed to remove policy key {0}: {1}" -f $root, $_.Exception.Message)
            }
        }
    }
}

function Reset-WindowsUpdateNetwork {
    if (-not $ResetNetwork.IsPresent) {
        return
    }

    Write-TidyOutput -Message 'Resetting Windows Update network stack (Winsock, proxy, IPv4).'
    Invoke-TidyCommand -Command { netsh winsock reset } -Description 'netsh winsock reset' -RequireSuccess | Out-Null
    Invoke-TidyCommand -Command { netsh winhttp reset proxy } -Description 'netsh winhttp reset proxy' | Out-Null
    Invoke-TidyCommand -Command { netsh int ip reset } -Description 'netsh int ip reset' -AcceptableExitCodes @(0,1) | Out-Null
}

function Trigger-WindowsUpdateScan {
    if (-not $TriggerScan.IsPresent) {
        return
    }

    Write-TidyOutput -Message 'Triggering Windows Update scan (UsoClient / wuauclt).'
    Invoke-TidyCommand -Command { UsoClient StartScan } -Description 'UsoClient StartScan' | Out-Null
    Start-Sleep -Seconds 2
    Invoke-TidyCommand -Command { UsoClient StartDownload } -Description 'UsoClient StartDownload' | Out-Null
    Start-Sleep -Seconds 2
    Invoke-TidyCommand -Command { UsoClient StartInstall } -Description 'UsoClient StartInstall' | Out-Null
    Invoke-TidyCommand -Command { wuauclt.exe /reportnow } -Description 'wuauclt /reportnow' | Out-Null
}

$hasActiveOperation = $false
foreach ($name in $operationSwitches) {
    $switchValue = Get-Variable -Name $name -Scope Script -ValueOnly
    if ($switchValue.IsPresent) {
        $hasActiveOperation = $true
        break
    }
}

if (-not $hasActiveOperation) {
    foreach ($name in $operationSwitches) {
        Set-Variable -Name $name -Value ([System.Management.Automation.SwitchParameter]$true) -Scope Script
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Windows Update repair toolkit requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting Windows Update repair sequence.'

    $versionInfo = Get-TidyWindowsVersionInfo
    $caption = if ($null -ne $versionInfo.Caption) { $versionInfo.Caption.Trim() } else { $null }
    if (-not [string]::IsNullOrWhiteSpace($caption)) {
        $summary = '{0} (Version {1}, Build {2})' -f $caption, $versionInfo.Version, $versionInfo.BuildNumber
    }
    else {
        $summary = 'Version {0}, Build {1}' -f $versionInfo.Version, $versionInfo.BuildNumber
    }
    Write-TidyOutput -Message ('Detected operating system: {0}.' -f $summary)

    $pendingBefore = Test-TidyPendingReboot
    if ($pendingBefore) {
        Write-TidyLog -Level Warning -Message 'Pending reboot detected before repairs.'
        Write-TidyOutput -Message 'Pending reboot detected. Restart after this routine if issues persist.'
    }

    Ensure-WindowsUpdateServiceDefaults

    if (-not $script:RestorePointCreated) {
        $script:RestorePointCreated = New-TidySystemRestorePoint -Description 'TidyWindow Windows Update repair safety checkpoint'
    }

    if ($ResetServices.IsPresent -or $ResetComponents.IsPresent -or $ReRegisterLibraries.IsPresent) {
        Stop-WindowsUpdateServices
    }

    if ($ResetComponents.IsPresent) {
        Reset-WindowsUpdateComponents
    }

    if ($ResetComponents.IsPresent -or $RunDismRestoreHealth.IsPresent) {
        Invoke-DismComponentCleanup
    }

    ReRegister-WindowsUpdateLibraries
    Reset-WindowsUpdatePolicies
    Reset-WindowsUpdateNetwork

    if ($ResetServices.IsPresent -or $ResetComponents.IsPresent -or $ReRegisterLibraries.IsPresent) {
        Start-WindowsUpdateServices
    }

    Invoke-DismRestoreHealth
    Invoke-SystemFileChecker
    Trigger-WindowsUpdateScan

    $pendingAfter = Test-TidyPendingReboot
    if ($pendingAfter) {
        $note = if ($pendingBefore) { 'Pending reboot still detected after repairs.' } else { 'Pending reboot required to finalize repairs.' }
        Write-TidyLog -Level Warning -Message $note
        Write-TidyOutput -Message ($note + ' Restart the system to complete the process.')
    }
    else {
        Write-TidyOutput -Message 'No pending reboot state detected after repair routine.'
    }

    Write-TidyOutput -Message 'Windows Update repair routine completed.'
    Write-TidyOutput -Message 'If updates still fail, review WindowsUpdate.log and the Activity log for detailed errors.'
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
    Write-TidyLog -Level Information -Message 'Windows Update repair toolkit finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

