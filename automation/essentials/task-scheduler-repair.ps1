param(
    [switch] $SkipTaskCacheRebuild,
    [switch] $SkipUsoTaskEnable,
    [switch] $SkipScheduleReset,
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
    param([Parameter(Mandatory = $true)][object] $Message)
    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) { return }
    [void]$script:TidyOutputLines.Add($text)
    Write-Output $text
}

function Write-TidyError {
    param([Parameter(Mandatory = $true)][object] $Message)
    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) { return }
    [void]$script:TidyErrorLines.Add($text)
    Write-Error -Message $text
}

function Save-TidyResult {
    if (-not $script:UsingResultFile) { return }
    $payload = [pscustomobject]@{
        Success = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
        Output  = $script:TidyOutputLines
        Errors  = $script:TidyErrorLines
    }
    $json = $payload | ConvertTo-Json -Depth 5
    Set-Content -Path $ResultPath -Value $json -Encoding UTF8
}

function Test-TidyAdmin {
    return [bool](New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-TidyServiceState {
    param([Parameter(Mandatory = $true)][string] $Name,[string] $DesiredStatus = 'Running',[int] $TimeoutSeconds = 12)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq $DesiredStatus) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Restart-ScheduleService {
    try {
        Write-TidyOutput -Message 'Restarting Task Scheduler (Schedule) service.'
        Invoke-TidyCommand -Command { Restart-Service -Name 'Schedule' -Force -ErrorAction Stop } -Description 'Restarting Schedule service.' -RequireSuccess | Out-Null
        if (-not (Wait-TidyServiceState -Name 'Schedule' -DesiredStatus 'Running' -TimeoutSeconds 20)) {
            Write-TidyOutput -Message 'Schedule service did not reach Running state after restart.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Failed to restart Schedule service: {0}" -f $_.Exception.Message)
    }
}

function Stop-ScheduleServiceForRepair {
    try {
        $svc = Get-Service -Name 'Schedule' -ErrorAction SilentlyContinue
        if ($null -eq $svc) {
            Write-TidyOutput -Message 'Schedule service not found; skipping stop.'
            return
        }

        if ($svc.Status -ne 'Stopped') {
            Write-TidyOutput -Message 'Stopping Schedule service for repair.'
            Invoke-TidyCommand -Command { Stop-Service -Name 'Schedule' -Force -ErrorAction Stop } -Description 'Stopping Schedule service.' -RequireSuccess | Out-Null
            Start-Sleep -Seconds 2
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Failed to stop Schedule service: {0}" -f $_.Exception.Message)
    }
}

function Rebuild-TaskCache {
    try {
        $taskRoot = Join-Path -Path $env:SystemRoot -ChildPath 'System32\Tasks'
        $cachePath = Join-Path -Path $taskRoot -ChildPath 'TaskCache'
        if (-not (Test-Path -LiteralPath $cachePath)) {
            Write-TidyOutput -Message 'TaskCache not found; nothing to rebuild.'
            return
        }

        $timestamp = Get-Date -Format 'yyyyMMddHHmmss'
        $backup = "$cachePath.bak.$timestamp"

        Stop-ScheduleServiceForRepair

        Write-TidyOutput -Message ("Backing up TaskCache to {0}." -f $backup)
        try {
            Move-Item -LiteralPath $cachePath -Destination $backup -Force -ErrorAction Stop
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to backup TaskCache: {0}" -f $_.Exception.Message)
            return
        }

        Write-TidyOutput -Message 'Starting Schedule service to rebuild TaskCache from Tasks tree.'
        Invoke-TidyCommand -Command { Start-Service -Name 'Schedule' -ErrorAction Stop } -Description 'Starting Schedule service.' -RequireSuccess | Out-Null
        if (-not (Wait-TidyServiceState -Name 'Schedule' -DesiredStatus 'Running' -TimeoutSeconds 20)) {
            Write-TidyOutput -Message 'Schedule service did not reach Running state after TaskCache rebuild start.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Task cache rebuild failed: {0}" -f $_.Exception.Message)
    }
}

function Enable-UsoTasks {
    $targets = @(
        'Microsoft\\Windows\\UpdateOrchestrator\\Schedule Scan',
        'Microsoft\\Windows\\UpdateOrchestrator\\UpdateModel',
        'Microsoft\\Windows\\UpdateOrchestrator\\Universal Orchestrator Start',
        'Microsoft\\Windows\\UpdateOrchestrator\\USO_UxBroker',
        'Microsoft\\Windows\\WindowsUpdate\\Scheduled Start'
    )

    foreach ($task in $targets) {
        try {
            Write-TidyOutput -Message ("Enabling scheduled task {0}." -f $task)
            Enable-ScheduledTask -TaskName $task -TaskPath '\\' -ErrorAction Stop | Out-Null
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to enable task {0}: {1}" -f $task, $_.Exception.Message)
        }
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Task Scheduler repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting Task Scheduler and automation repair pack.'

    if (-not $SkipTaskCacheRebuild.IsPresent) {
        Rebuild-TaskCache
    }
    else {
        Write-TidyOutput -Message 'Skipping TaskCache rebuild per operator request.'
    }

    if (-not $SkipUsoTaskEnable.IsPresent) {
        Enable-UsoTasks
    }
    else {
        Write-TidyOutput -Message 'Skipping USO/Windows Update task enablement per operator request.'
    }

    if (-not $SkipScheduleReset.IsPresent) {
        Restart-ScheduleService
    }
    else {
        Write-TidyOutput -Message 'Skipping Schedule service restart per operator request.'
    }

    Write-TidyOutput -Message 'Task Scheduler repair completed.'
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message ("Task Scheduler repair failed: {0}" -f $_.Exception.Message)
}
finally {
    Save-TidyResult
}
