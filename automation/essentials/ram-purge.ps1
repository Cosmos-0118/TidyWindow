param(
    [switch] $Silent,
    [switch] $SkipStandbyClear,
    [switch] $SkipWorkingSetTrim,
    [switch] $SkipSysMainToggle,
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
$script:SysMainWasRunning = $null
$script:MemoryPrivilegesEnabled = $false
$script:PrivilegeHelperReady = $false

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
    if (-not $Silent.IsPresent) {
        Write-Output $text
    }
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



function Format-TidyBytes {
    param([Nullable[int64]] $Value)

    if ($null -eq $Value -or $Value -lt 0) {
        return 'n/a'
    }

    $gib = [Math]::Round($Value / 1GB, 2)
    $mib = [Math]::Round($Value / 1MB, 0)

    return "{0} GiB ({1} MB)" -f $gib, $mib
}

function Format-TidyBytesDelta {
    param([Nullable[int64]] $Value)

    if ($null -eq $Value -or $Value -eq 0) {
        return '0'
    }

    $sign = if ($Value -ge 0) { '+' } else { '-' }
    $magnitude = [Math]::Abs($Value)
    $gib = [Math]::Round($magnitude / 1GB, 2)
    $mib = [Math]::Round($magnitude / 1MB, 0)

    return "{0}{1} GiB ({0}{2} MB)" -f $sign, $gib, $mib
}

function Get-TidyMemorySnapshot {
    $snapshot = [pscustomobject]@{
        Timestamp              = Get-Date
        TotalPhysicalBytes     = $null
        AvailableBytes         = $null
        StandbyBytes           = $null
        ModifiedBytes          = $null
        CacheBytes             = $null
        CommittedBytes         = $null
        CommitLimitBytes       = $null
        CompressionStoreBytes  = $null
    }

    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $snapshot.TotalPhysicalBytes = [int64]$os.TotalVisibleMemorySize * 1KB
        $snapshot.AvailableBytes = [int64]$os.FreePhysicalMemory * 1KB
    }
    catch {
        # Leave defaults when WMI is unavailable.
    }

    try {
        $perf = Get-CimInstance -Namespace 'root/cimv2' -ClassName 'Win32_PerfFormattedData_PerfOS_Memory' -ErrorAction Stop
        $standby = 0
        if ($perf.PSObject.Properties['StandbyCacheNormalPriorityBytes']) { $standby += [int64]$perf.StandbyCacheNormalPriorityBytes }
        if ($perf.PSObject.Properties['StandbyCacheReserveBytes']) { $standby += [int64]$perf.StandbyCacheReserveBytes }
        if ($perf.PSObject.Properties['StandbyCacheCoreBytes']) { $standby += [int64]$perf.StandbyCacheCoreBytes }
        $snapshot.StandbyBytes = $standby

        if ($perf.PSObject.Properties['ModifiedPageListBytes']) {
            $snapshot.ModifiedBytes = [int64]$perf.ModifiedPageListBytes
        }

        if ($perf.PSObject.Properties['CacheBytes']) {
            $snapshot.CacheBytes = [int64]$perf.CacheBytes
        }

        if ($perf.PSObject.Properties['CommitLimit']) {
            $snapshot.CommitLimitBytes = [int64]$perf.CommitLimit
        }

        if ($perf.PSObject.Properties['CommittedBytes']) {
            $snapshot.CommittedBytes = [int64]$perf.CommittedBytes
        }

        if ($perf.PSObject.Properties['PoolPagedBytes'] -and $perf.PSObject.Properties['PoolNonpagedBytes']) {
            $snapshot.CompressionStoreBytes = [int64]($perf.PoolPagedBytes + $perf.PoolNonpagedBytes)
        }
    }
    catch {
        # Some Windows SKUs lack the performance counters we rely on.
    }

    return $snapshot
}

function Write-TidyMemorySnapshot {
    param(
        [pscustomobject] $Snapshot,
        [string] $Label
    )

    if ($null -eq $Snapshot) {
        Write-TidyOutput -Message ("{0}: memory telemetry unavailable." -f $Label)
        return
    }

    Write-TidyOutput -Message ("{0} (captured {1:u})" -f $Label, $Snapshot.Timestamp)
    Write-TidyOutput -Message ("  ↳ Available physical: {0}" -f (Format-TidyBytes $Snapshot.AvailableBytes))

    if ($Snapshot.StandbyBytes -ne $null) {
        Write-TidyOutput -Message ("  ↳ Standby cache: {0}" -f (Format-TidyBytes $Snapshot.StandbyBytes))
    }

    if ($Snapshot.ModifiedBytes -ne $null) {
        Write-TidyOutput -Message ("  ↳ Modified pages: {0}" -f (Format-TidyBytes $Snapshot.ModifiedBytes))
    }

    if ($Snapshot.CacheBytes -ne $null) {
        Write-TidyOutput -Message ("  ↳ File cache: {0}" -f (Format-TidyBytes $Snapshot.CacheBytes))
    }

    if ($Snapshot.CommittedBytes -ne $null -and $Snapshot.CommitLimitBytes -ne $null) {
        Write-TidyOutput -Message ("  ↳ Commit: {0} of {1}" -f (Format-TidyBytes $Snapshot.CommittedBytes), (Format-TidyBytes $Snapshot.CommitLimitBytes))
    }
}

function Write-TidyMemoryDelta {
    param(
        [pscustomobject] $Before,
        [pscustomobject] $After,
        [string] $Label
    )

    if ($null -eq $Before -or $null -eq $After) {
        return
    }

    $availableDelta = if ($Before.AvailableBytes -ne $null -and $After.AvailableBytes -ne $null) { $After.AvailableBytes - $Before.AvailableBytes } else { $null }
    $standbyDelta = if ($Before.StandbyBytes -ne $null -and $After.StandbyBytes -ne $null) { $After.StandbyBytes - $Before.StandbyBytes } else { $null }

    Write-TidyOutput -Message ("{0} delta:" -f $Label)

    if ($availableDelta -ne $null) {
        Write-TidyOutput -Message ("  ↳ Available physical change: {0}" -f (Format-TidyBytesDelta $availableDelta))
    }

    if ($standbyDelta -ne $null) {
        Write-TidyOutput -Message ("  ↳ Standby cache change: {0}" -f (Format-TidyBytesDelta $standbyDelta))
    }
}



$script:MemoryPurgeHelperReady = $false
$script:WorkingSetHelperReady = $false

function Initialize-PrivilegeHelper {
    if ($script:PrivilegeHelperReady) {
        return
    }

    $typeDefinition = @"
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class PrivilegeNative
{
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const int SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public int LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public LUID Luid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint Zero, IntPtr Null1, IntPtr Null2);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static void EnablePrivilege(string privilege)
    {
        IntPtr tokenHandle;
        var processHandle = Process.GetCurrentProcess().Handle;
        if (!OpenProcessToken(processHandle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var tokenPrivileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            if (!AdjustTokenPrivileges(tokenHandle, false, ref tokenPrivileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var status = Marshal.GetLastWin32Error();
            if (status != 0)
            {
                throw new Win32Exception(status);
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }
}
"@

    Add-Type -TypeDefinition $typeDefinition -ErrorAction Stop | Out-Null
    $script:PrivilegeHelperReady = $true
}

function Enable-MemoryManagementPrivileges {
    if ($script:MemoryPrivilegesEnabled) {
        return
    }

    Initialize-PrivilegeHelper

    $privileges = @(
        'SeIncreaseQuotaPrivilege',
        'SeProfileSingleProcessPrivilege',
        'SeIncreaseBasePriorityPrivilege',
        'SeDebugPrivilege'
    )

    foreach ($privilege in $privileges) {
        try {
            [PrivilegeNative]::EnablePrivilege($privilege)
        }
        catch {
            Write-TidyOutput -Message ("  ↳ Unable to enable privilege {0}: {1}" -f $privilege, $_.Exception.Message)
        }
    }

    $script:MemoryPrivilegesEnabled = $true
}



function Initialize-MemoryPurgeHelper {
    if ($script:MemoryPurgeHelperReady) {
        return
    }

    $typeDefinition = @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class MemoryListNative
{
    private const int SystemMemoryListInformation = 80;

    public enum SystemMemoryListCommand
    {
        MemoryCaptureAccessedBits = 0,
        MemoryCaptureAndResetAccessedBits = 1,
        MemoryEmptyWorkingSets = 2,
        MemoryFlushModifiedList = 3,
        MemoryPurgeStandbyList = 4,
        MemoryPurgeLowPriorityStandbyList = 5,
        MemoryCommandMax = 6
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(
        int SystemInformationClass,
        ref int SystemInformation,
        int SystemInformationLength);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);

    public static void IssueCommand(SystemMemoryListCommand command)
    {
        int data = (int)command;
        int status = NtSetSystemInformation(SystemMemoryListInformation, ref data, sizeof(int));
        if (status != 0)
        {
            int error = RtlNtStatusToDosError(status);
            if (error != 0)
            {
                throw new Win32Exception(error);
            }

            throw new Win32Exception(status);
        }
    }
}
"@

    Add-Type -TypeDefinition $typeDefinition -ErrorAction Stop | Out-Null
    $script:MemoryPurgeHelperReady = $true
}

function Initialize-WorkingSetHelper {
    if ($script:WorkingSetHelperReady) {
        return
    }

    $typeDefinition = @"
using System;
using System.Runtime.InteropServices;

public static class WorkingSetNative
{
    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);
}
"@

    Add-Type -TypeDefinition $typeDefinition -ErrorAction Stop | Out-Null
    $script:WorkingSetHelperReady = $true
}

function Invoke-StandbyMemoryClear {
    Write-TidyOutput -Message 'Clearing standby memory lists.'

    try {
        Initialize-MemoryPurgeHelper
    }
    catch {
        Write-TidyOutput -Message ("Unable to initialize native memory purge helper: {0}" -f $_.Exception.Message)
        Write-TidyOutput -Message 'Skipping standby memory purge. Consider rerunning after confirming administrator privileges.'
        return
    }

    Enable-MemoryManagementPrivileges

    $commands = @(
        @{ Command = [MemoryListNative+SystemMemoryListCommand]::MemoryPurgeStandbyList; Description = 'Purging standby page lists.' },
        @{ Command = [MemoryListNative+SystemMemoryListCommand]::MemoryPurgeLowPriorityStandbyList; Description = 'Purging low-priority standby lists.' },
        @{ Command = [MemoryListNative+SystemMemoryListCommand]::MemoryFlushModifiedList; Description = 'Flushing modified page list.' },
        @{ Command = [MemoryListNative+SystemMemoryListCommand]::MemoryEmptyWorkingSets; Description = 'Emptying working sets via kernel API.' }
    )

    foreach ($entry in $commands) {
        Write-TidyOutput -Message $entry.Description
        try {
            [MemoryListNative]::IssueCommand($entry.Command)
        }
        catch {
            $errorMessage = $_.Exception.Message
            if ($_.Exception -is [System.ComponentModel.Win32Exception] -and $_.Exception.NativeErrorCode -eq 1314) {
                $errorMessage = 'Required privilege still unavailable; run as administrator or enable memory management privileges.'
            }

            Write-TidyOutput -Message ("  ↳ Command skipped: {0}" -f $errorMessage)
        }
    }
}

function Invoke-WorkingSetTrim {
    Write-TidyOutput -Message 'Requesting working set trims for background processes.'

    Initialize-WorkingSetHelper
    Enable-MemoryManagementPrivileges

    $skipNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in 'Idle', 'System', 'Registry', 'MemCompression', 'csrss', 'winlogon', 'services', 'lsass', 'smss') {
        [void]$skipNames.Add($name)
    }

    $trimmed = [System.Collections.Generic.List[pscustomobject]]::new()
    $failures = 0
    $minWorkingSetBytes = 75MB
    $totalTrimmedBytes = 0

    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        if ($process.Id -eq $PID) { continue }
        if ($skipNames.Contains($process.ProcessName)) { continue }

        if ($process.SessionId -eq 0) { continue }

        if ($process.MainWindowHandle -ne 0 -and $process.Responding) { continue }

        if ($process.WorkingSet64 -lt $minWorkingSetBytes) { continue }

        $beforeBytes = $process.WorkingSet64

        try {
            $handle = $process.Handle
            if ([WorkingSetNative]::EmptyWorkingSet($handle)) {
                Start-Sleep -Milliseconds 50
                $afterBytes = $null
                try {
                    $afterBytes = (Get-Process -Id $process.Id -ErrorAction Stop).WorkingSet64
                }
                catch {
                    # Process exited before we could sample again.
                }

                $deltaBytes = $null
                if ($afterBytes -ne $null) {
                    $deltaBytes = $beforeBytes - $afterBytes
                    if ($deltaBytes -lt 0) {
                        $deltaBytes = 0
                    }
                }

                if ($deltaBytes -ne $null) {
                    $totalTrimmedBytes += $deltaBytes
                }

                $record = [pscustomobject]@{
                    Name       = $process.ProcessName
                    Id         = $process.Id
                    Before     = $beforeBytes
                    After      = $afterBytes
                    Delta      = $deltaBytes
                }

                $trimmed.Add($record)
            }
        }
        catch {
            $failures++
        }
    }

    if ($trimmed.Count -gt 0) {
        $effective = $trimmed | Where-Object { $_.Delta -ne $null -and $_.Delta -gt 0 }
        $effectiveCount = ($effective | Measure-Object).Count

        Write-TidyOutput -Message ("Trimmed working sets for {0} processes." -f $trimmed.Count)

        if ($effectiveCount -gt 0) {
            Write-TidyOutput -Message ("  ↳ Estimated memory reclaimed: {0}" -f (Format-TidyBytes $totalTrimmedBytes))

            $topEntries = $effective | Sort-Object -Property Delta -Descending | Select-Object -First 10
            foreach ($entry in $topEntries) {
                Write-TidyOutput -Message ("    · {0} (PID {1}): {2} -> {3} (Δ {4})" -f $entry.Name, $entry.Id, (Format-TidyBytes $entry.Before), (Format-TidyBytes $entry.After), (Format-TidyBytes $entry.Delta))
            }
        }
        else {
            Write-TidyOutput -Message '  ↳ Working set reductions were below measurable thresholds.'
        }
    }
    else {
        Write-TidyOutput -Message 'No processes reported working set reductions.'
    }

    if ($failures -gt 0) {
        Write-TidyOutput -Message ("Skipped {0} processes due to access restrictions." -f $failures)
    }
}

function Invoke-SysMainToggle {
    param([bool] $Disable)

    $serviceName = 'SysMain'
    $present = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $present) {
        Write-TidyOutput -Message 'SysMain service not found. Skipping service toggle.'
        return
    }

    if ($Disable) {
        $script:SysMainWasRunning = $present.Status -eq 'Running'
        if ($present.Status -eq 'Stopped') {
            Write-TidyOutput -Message 'SysMain was already stopped.'
        }
        else {
            Write-TidyOutput -Message 'Stopping SysMain (Superfetch) temporarily to release caches.'
            Invoke-TidyCommand -Command { param($svc) Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue } -Arguments @($serviceName) -Description 'Stopping SysMain.'
        }
    }
    else {
        $shouldRestart = if ($null -ne $script:SysMainWasRunning) { $script:SysMainWasRunning } else { $present.Status -ne 'Running' }
        if ($shouldRestart) {
            Write-TidyOutput -Message 'Starting SysMain service again.'
            Invoke-TidyCommand -Command { param($svc) Start-Service -Name $svc -ErrorAction SilentlyContinue } -Arguments @($serviceName) -Description 'Starting SysMain.'
        }
        else {
            Write-TidyOutput -Message 'SysMain will remain stopped per previous configuration.'
        }

        $script:SysMainWasRunning = $null
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'RAM purge requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting RAM purge sequence.'

    $baselineSnapshot = Get-TidyMemorySnapshot
    Write-TidyOutput -Message 'Baseline memory telemetry:'
    Write-TidyMemorySnapshot -Snapshot $baselineSnapshot -Label 'Before purge'
    $currentSnapshot = $baselineSnapshot

    if (-not $SkipStandbyClear.IsPresent) {
        $beforeStandby = $currentSnapshot
        Invoke-StandbyMemoryClear
        Start-Sleep -Seconds 2
        $currentSnapshot = Get-TidyMemorySnapshot
        Write-TidyMemoryDelta -Before $beforeStandby -After $currentSnapshot -Label 'Standby purge'
        Write-TidyMemorySnapshot -Snapshot $currentSnapshot -Label 'Post-standby purge'
    }
    else {
        Write-TidyOutput -Message 'Skipping standby memory clear per operator request.'
    }

    if (-not $SkipWorkingSetTrim.IsPresent) {
        Write-TidyOutput -Message 'Trimming working sets for background processes.'
        $beforeTrim = $currentSnapshot
        Invoke-WorkingSetTrim
        Start-Sleep -Seconds 1
        $currentSnapshot = Get-TidyMemorySnapshot
        Write-TidyMemoryDelta -Before $beforeTrim -After $currentSnapshot -Label 'Working set trim'
        Write-TidyMemorySnapshot -Snapshot $currentSnapshot -Label 'Post-working-set trim'
    }
    else {
        Write-TidyOutput -Message 'Skipping working set trim per operator request.'
    }

    if (-not $SkipSysMainToggle.IsPresent) {
        $beforeSysMain = $currentSnapshot
        Invoke-SysMainToggle -Disable $true
        Start-Sleep -Seconds 5
        Invoke-SysMainToggle -Disable $false
        Start-Sleep -Seconds 2
        $currentSnapshot = Get-TidyMemorySnapshot
        Write-TidyMemoryDelta -Before $beforeSysMain -After $currentSnapshot -Label 'SysMain toggle'
        Write-TidyMemorySnapshot -Snapshot $currentSnapshot -Label 'Post-SysMain toggle'
    }
    else {
        Write-TidyOutput -Message 'Skipping SysMain service toggle per operator request.'
    }

    Write-TidyOutput -Message 'Final memory telemetry:'
    Write-TidyMemorySnapshot -Snapshot $currentSnapshot -Label 'After purge'
    Write-TidyMemoryDelta -Before $baselineSnapshot -After $currentSnapshot -Label 'Overall purge'

    Write-TidyOutput -Message 'RAM purge sequence completed.'
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
    Write-TidyLog -Level Information -Message 'RAM purge script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

