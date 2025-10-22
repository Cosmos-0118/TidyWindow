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
$script:SysMainWasRunning = $null

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



$script:MemoryPurgeHelperReady = $false
$script:WorkingSetHelperReady = $false

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
            Write-TidyOutput -Message ("  ↳ Command skipped: {0}" -f $_.Exception.Message)
        }
    }
}

function Invoke-WorkingSetTrim {
    Write-TidyOutput -Message 'Requesting working set trims for background processes.'

    Initialize-WorkingSetHelper

    $skipNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in 'Idle', 'System', 'Registry', 'MemCompression', 'csrss', 'winlogon', 'services', 'lsass', 'smss') {
        [void]$skipNames.Add($name)
    }

    $trimmed = [System.Collections.Generic.List[string]]::new()
    $failures = 0

    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        if ($process.Id -eq $PID) { continue }
        if ($skipNames.Contains($process.ProcessName)) { continue }

        try {
            $handle = $process.Handle
            if ([WorkingSetNative]::EmptyWorkingSet($handle)) {
                [void]$trimmed.Add(("{0} (PID {1})" -f $process.ProcessName, $process.Id))
            }
        }
        catch {
            $failures++
        }
    }

    if ($trimmed.Count -gt 0) {
        Write-TidyOutput -Message ("Trimmed working sets for {0} processes." -f $trimmed.Count)
        foreach ($entry in $trimmed) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $entry)
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

    if (-not $SkipStandbyClear.IsPresent) {
        Invoke-StandbyMemoryClear
    }
    else {
        Write-TidyOutput -Message 'Skipping standby memory clear per operator request.'
    }

    if (-not $SkipWorkingSetTrim.IsPresent) {
        Write-TidyOutput -Message 'Trimming working sets for background processes.'
        Invoke-WorkingSetTrim
    }
    else {
        Write-TidyOutput -Message 'Skipping working set trim per operator request.'
    }

    if (-not $SkipSysMainToggle.IsPresent) {
        Invoke-SysMainToggle -Disable $true
        Start-Sleep -Seconds 5
        Invoke-SysMainToggle -Disable $false
    }
    else {
        Write-TidyOutput -Message 'Skipping SysMain service toggle per operator request.'
    }

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
