param(
    [switch] $SkipTaskCacheRebuild,
    [switch] $SkipUsoTaskEnable,
    [switch] $SkipScheduleReset,
    [switch] $SkipUsoTaskRebuild,
    [switch] $SkipTaskCacheRegistryRebuild,
    [switch] $SkipTasksAclRepair,
    [switch] $RepairUpdateServices,
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
$script:UsoTaskRebuildRequested = $false
$script:BaselineRestoreAttempts = 0
$script:BaselineRestoreSucceeded = 0
$script:BaselineRestoreFailed = 0
$script:BaselineLayoutsTried = [System.Collections.Generic.List[string]]::new()
$script:BaselineLayoutsFailed = [System.Collections.Generic.List[string]]::new()
$script:CreatedTasks = [System.Collections.Generic.List[string]]::new()
$script:SkippedTasks = [System.Collections.Generic.List[string]]::new()
$script:FailedTasks = [System.Collections.Generic.List[string]]::new()
$script:TaskFolderAclRepaired = $false
$script:TaskFolderAclRepairErrors = [System.Collections.Generic.List[string]]::new()
$script:TaskCreationErrors = [System.Collections.Generic.List[string]]::new()
$script:BaselineTaskMap = @{
    'schedule-scan.xml'                = @{ Path = '\Microsoft\Windows\UpdateOrchestrator\'; Name = 'Schedule Scan' }
    'update-model.xml'                 = @{ Path = '\Microsoft\Windows\UpdateOrchestrator\'; Name = 'UpdateModel' }
    'universal-orchestrator-start.xml' = @{ Path = '\Microsoft\Windows\UpdateOrchestrator\'; Name = 'Universal Orchestrator Start' }
    'uso-uxbroker.xml'                 = @{ Path = '\Microsoft\Windows\UpdateOrchestrator\'; Name = 'USO_UxBroker' }
    'windowsupdate-scheduled-start.xml' = @{ Path = '\Microsoft\Windows\WindowsUpdate\'; Name = 'Scheduled Start' }
}

# Normalize task folder paths to a single leading/trailing backslash and collapse duplicate separators.
function Normalize-TaskPath {
    param([Parameter(Mandatory = $true)][string] $TaskPath)

    $collapsed = $TaskPath -replace '[\\/]+', '\\'
    $trimmed   = $collapsed.Trim('\\')
    if ([string]::IsNullOrWhiteSpace($trimmed)) { return '\\' }
    return "\\$trimmed\\"
}

function New-UsoTaskPrincipal {
    return New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
}

function New-UsoTaskSettings {
    # Keep to broadly supported switches across builds.
    return New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
}

# Create a task using COM first, then fall back to Register-ScheduledTask (native PowerShell) to avoid schtasks.exe parsing issues on tricky task paths.
function New-UsoTaskWithFallback {
    param(
        [Parameter(Mandatory = $true)][string] $TaskName,
        [Parameter(Mandatory = $true)][string] $TaskPath
    )

    $taskPathNormalized = Normalize-TaskPath -TaskPath $TaskPath
    $tn = "$taskPathNormalized$TaskName"

    try {
        $created = New-UsoTaskViaCom -TaskName $TaskName -TaskPath $taskPathNormalized
        if ($created) { return $true }
        Write-TidyOutput -Message ("COM create returned false for {0}. Trying schtasks fallback." -f $tn)
    }
    catch {
        $errMsg = if ($_.Exception) { $_.Exception.Message } else { ($_.ToString()) }
        Write-TidyOutput -Message ("COM create exception for {0}: {1}. Trying Register-ScheduledTask fallback." -f $tn, $errMsg)
    }

    $definition = $null
    try {
        $definition = New-UsoTaskDefinition -TaskName $TaskName -TaskPath $taskPathNormalized
    }
    catch {
        $errMsg = if ($_.Exception) { $_.Exception.Message } else { ($_.ToString()) }
        Write-TidyOutput -Message ("Building task definition failed for {0}: {1}" -f $tn, $errMsg)
        return $false
    }

    if (-not $definition) {
        Write-TidyOutput -Message ("Could not build task definition for {0}; skipping Register-ScheduledTask fallback." -f $tn)
        return $false
    }

    Ensure-TaskFolderReady -TaskPath $taskPathNormalized

    $registerSucceeded = $false
    for ($attempt = 1; $attempt -le 2 -and -not $registerSucceeded; $attempt++) {
        try {
            if ($attempt -eq 2) {
                $existing = Get-ScheduledTask -TaskPath $taskPathNormalized -TaskName $TaskName -ErrorAction SilentlyContinue
                if ($existing) {
                    Write-TidyOutput -Message ("Removing existing task before retry: {0}" -f $tn)
                    Unregister-ScheduledTask -TaskName $TaskName -TaskPath $taskPathNormalized -Confirm:$false -ErrorAction SilentlyContinue
                }
            }

            Write-TidyOutput -Message ("Creating task via Register-ScheduledTask fallback (attempt {0}): {1}" -f $attempt, $tn)
            Register-ScheduledTask -TaskName $TaskName -TaskPath $taskPathNormalized -InputObject $definition -Force -ErrorAction Stop | Out-Null
            $registerSucceeded = $true
        }
        catch {
            $errMsg = if ($_.Exception) { $_.Exception.Message } else { ($_.ToString()) }
            Write-TidyOutput -Message ("Register-ScheduledTask fallback attempt {0} failed for {1}: {2}" -f $attempt, $tn, $errMsg)
        }
    }

    return $registerSucceeded
}

function Get-UtcStartBoundary {
    param([int] $MinutesFromNow = 1)
    return (Get-Date).ToUniversalTime().AddMinutes($MinutesFromNow).ToString('yyyy-MM-ddTHH:mm:ssZ')
}

function Get-UsoTaskSchtasksArguments {
    param(
        [Parameter(Mandatory = $true)][string] $TaskName,
        [Parameter(Mandatory = $true)][string] $TaskPath
    )

    $tnPath = Normalize-TaskPath -TaskPath $TaskPath
    $tn = "$tnPath$TaskName"

    switch ($TaskName) {
        'Schedule Scan' {
            return @('/create','/f','/tn',"$tn",'/sc','DAILY','/st','00:05','/ru','SYSTEM','/tr',"`"$env:SystemRoot\system32\usoclient.exe`" StartScan")
        }
        'UpdateModel' {
            return @('/create','/f','/tn',"$tn",'/sc','ONSTART','/ru','SYSTEM','/tr',"`"$env:SystemRoot\system32\usoclient.exe`" StartScan")
        }
        'Universal Orchestrator Start' {
            return @('/create','/f','/tn',"$tn",'/sc','ONSTART','/ru','SYSTEM','/tr',"`"$env:SystemRoot\system32\sc.exe`" start UsoSvc")
        }
        'USO_UxBroker' {
            return @('/create','/f','/tn',"$tn",'/sc','ONLOGON','/ru','SYSTEM','/tr',"`"$env:SystemRoot\system32\usoclient.exe`" StartScan")
        }
        'Scheduled Start' {
            return @('/create','/f','/tn',"$tn",'/sc','DAILY','/st','00:10','/ru','SYSTEM','/tr',"`"$env:SystemRoot\system32\usoclient.exe`" StartScan")
        }
        Default { return $null }
    }
}

function Get-UsoComFolder {
    param([Parameter(Mandatory = $true)][__ComObject] $Root, [Parameter(Mandatory = $true)][string] $TaskPath)

    $relative = $TaskPath.Trim('\')
    if ([string]::IsNullOrWhiteSpace($relative)) { return $Root }

    $parts = $relative -split '\\'
    $current = $Root
    $accum = ''
    foreach ($p in $parts) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        $accum = if ([string]::IsNullOrEmpty($accum)) { "\\$p" } else { "$accum\\$p" }
        try {
            $current = $Root.GetFolder($accum)
        }
        catch {
            $current = $Root.CreateFolder($accum)
        }
    }
    return $current
}

function New-UsoTaskViaCom {
    param(
        [Parameter(Mandatory = $true)][string] $TaskName,
        [Parameter(Mandatory = $true)][string] $TaskPath
    )

    $taskPathNormalized = Normalize-TaskPath -TaskPath $TaskPath

    $service = New-Object -ComObject 'Schedule.Service'
    $service.Connect()
    $root = $service.GetFolder('\')
    $folder = Get-UsoComFolder -Root $root -TaskPath $taskPathNormalized

    $definition = $service.NewTask(0)
    $definition.RegistrationInfo.Description = 'Rebuilt Update Orchestrator / Windows Update task'
    $definition.RegistrationInfo.Author = 'TidyWindow'

    $principal = $definition.Principal
    $principal.UserId = 'SYSTEM'
    $principal.LogonType = 5   # TASK_LOGON_SERVICE_ACCOUNT
    $principal.RunLevel = 1    # TASK_RUNLEVEL_HIGHEST

    $settings = $definition.Settings
    $settings.MultipleInstances = 2   # TASK_INSTANCES_IGNORE_NEW
    $settings.ExecutionTimeLimit = 'PT1H'
    $settings.DisallowStartIfOnBatteries = $false
    $settings.StopIfGoingOnBatteries = $false
    $settings.StartWhenAvailable = $true
    $settings.Enabled = $true

    switch ($TaskName) {
        'Schedule Scan' {
            $trigger = $definition.Triggers.Create(2) # DAILY
            $trigger.StartBoundary = Get-UtcStartBoundary -MinutesFromNow 5
            $trigger.DaysInterval = 1
            $action = $definition.Actions.Create(0)
            $action.Path = "$env:SystemRoot\system32\usoclient.exe"
            $action.Arguments = 'StartScan'
        }
        'UpdateModel' {
            $trigger = $definition.Triggers.Create(8) # BOOT
            $trigger.StartBoundary = Get-UtcStartBoundary -MinutesFromNow 2
            $action = $definition.Actions.Create(0)
            $action.Path = "$env:SystemRoot\system32\usoclient.exe"
            $action.Arguments = 'StartScan'
        }
        'Universal Orchestrator Start' {
            $trigger = $definition.Triggers.Create(8) # BOOT
            $trigger.StartBoundary = Get-UtcStartBoundary -MinutesFromNow 2
            $action = $definition.Actions.Create(0)
            $action.Path = "$env:SystemRoot\system32\sc.exe"
            $action.Arguments = 'start UsoSvc'
        }
        'USO_UxBroker' {
            $trigger = $definition.Triggers.Create(9) # LOGON
            $trigger.StartBoundary = Get-UtcStartBoundary -MinutesFromNow 1
            $action = $definition.Actions.Create(0)
            $action.Path = "$env:SystemRoot\system32\usoclient.exe"
            $action.Arguments = 'StartScan'
        }
        'Scheduled Start' {
            $trigger = $definition.Triggers.Create(2) # DAILY
            $trigger.StartBoundary = Get-UtcStartBoundary -MinutesFromNow 10
            $trigger.DaysInterval = 1
            $action = $definition.Actions.Create(0)
            $action.Path = "$env:SystemRoot\system32\usoclient.exe"
            $action.Arguments = 'StartScan'
        }
        Default { return $false }
    }

    $flags = 6   # TASK_CREATE_OR_UPDATE
    $logonType = 5
    $folder.RegisterTaskDefinition($TaskName, $definition, $flags, $null, $null, $logonType, $null) | Out-Null
    return $true
}

function New-UsoTaskDefinition {
    param(
        [Parameter(Mandatory = $true)][string] $TaskName,
        [Parameter(Mandatory = $true)][string] $TaskPath
    )

    $principal = New-UsoTaskPrincipal
    $settings  = New-UsoTaskSettings

    switch ($TaskName) {
        'Schedule Scan' {
            $at = (Get-Date).AddMinutes(5)
            $trigger = New-ScheduledTaskTrigger -Daily -At $at
            $action  = New-ScheduledTaskAction -Execute "$env:SystemRoot\system32\usoclient.exe" -Argument 'StartScan'
        }
        'UpdateModel' {
            $trigger = New-ScheduledTaskTrigger -AtStartup
            $action  = New-ScheduledTaskAction -Execute "$env:SystemRoot\system32\usoclient.exe" -Argument 'StartScan'
        }
        'Universal Orchestrator Start' {
            $trigger = New-ScheduledTaskTrigger -AtStartup
            $action  = New-ScheduledTaskAction -Execute "$env:SystemRoot\system32\sc.exe" -Argument 'start UsoSvc'
        }
        'USO_UxBroker' {
            $trigger = New-ScheduledTaskTrigger -AtLogOn
            $action  = New-ScheduledTaskAction -Execute "$env:SystemRoot\system32\usoclient.exe" -Argument 'StartScan'
        }
        'Scheduled Start' {
            $at = (Get-Date).AddMinutes(10)
            $trigger = New-ScheduledTaskTrigger -Daily -At $at
            $action  = New-ScheduledTaskAction -Execute "$env:SystemRoot\system32\usoclient.exe" -Argument 'StartScan'
        }
        Default { return $null }
    }

    return New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings -Principal $principal
}

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

function Write-TidyOutput {
    param([Parameter(Mandatory = $true)][object] $Message)
    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) { return }
    if ($script:TidyOutputLines -is [System.Collections.IList]) {
        [void]$script:TidyOutputLines.Add($text)
    }
    Write-Host $text
    [void](TidyWindow.Automation\Write-TidyLog -Level Information -Message $text)
}

function Write-TidyError {
    param([Parameter(Mandatory = $true)][object] $Message)
    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) { return }
    if ($script:TidyErrorLines -is [System.Collections.IList]) {
        [void]$script:TidyErrorLines.Add($text)
    }
    Write-Host $text -ForegroundColor Red
    [void](TidyWindow.Automation\Write-TidyError -Message $text)
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

function Invoke-TidyCommand {
    param(
        [Parameter(Mandatory = $true)][scriptblock] $Command,
        [string] $Description = 'Running command.',
        [object[]] $Arguments = @(),
        [switch] $RequireSuccess,
        [switch] $DemoteNativeCommandErrors
    )

    Write-TidyLog -Level Information -Message $Description

    if (Test-Path -Path 'variable:LASTEXITCODE') {
        $global:LASTEXITCODE = 0
    }

    $output = & $Command @Arguments 2>&1

    $exitCode = 0
    if (Test-Path -Path 'variable:LASTEXITCODE') {
        $exitCode = $LASTEXITCODE
    }

    foreach ($entry in @($output)) {
        if ($null -eq $entry) { continue }
        if ($entry -is [System.Management.Automation.ErrorRecord]) {
            if ($DemoteNativeCommandErrors -and ($entry.FullyQualifiedErrorId -like 'NativeCommandError*')) {
                Write-TidyOutput -Message ("[WARN] {0}" -f $entry)
            }
            else {
                Write-TidyError -Message $entry
            }
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
        $svc = Get-Service -Name 'Schedule' -ErrorAction SilentlyContinue
        if ($null -eq $svc) {
            Write-TidyOutput -Message 'Schedule service not found; skipping restart.'
            return
        }

        Write-TidyOutput -Message 'Restarting Task Scheduler (Schedule) service.'
        try {
            if ($svc.Status -eq 'Running') {
                Invoke-TidyCommand -Command { Restart-Service -Name 'Schedule' -Force -ErrorAction Stop } -Description 'Restarting Schedule service.' -RequireSuccess | Out-Null
            }
            else {
                Invoke-TidyCommand -Command { Start-Service -Name 'Schedule' -ErrorAction Stop } -Description 'Starting Schedule service.' -RequireSuccess | Out-Null
            }
        }
        catch {
            Write-TidyOutput -Message ('Direct restart failed: {0}. Attempting start only.' -f $_.Exception.Message)
            try {
                Invoke-TidyCommand -Command { Start-Service -Name 'Schedule' -ErrorAction Stop } -Description 'Starting Schedule service (fallback).' -RequireSuccess | Out-Null
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to restart Schedule service: {0}" -f $_.Exception.Message)
                return
            }
        }

        if (-not (Wait-TidyServiceState -Name 'Schedule' -DesiredStatus 'Running' -TimeoutSeconds 20)) {
            Write-TidyOutput -Message 'Schedule service did not reach Running state after restart.'
        }
        else {
            $status = (Get-Service -Name 'Schedule' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Status)
            Write-TidyOutput -Message ("Schedule service status: {0}" -f $status)
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
            return $true
        }

        if ($svc.Status -ne 'Stopped') {
            Write-TidyOutput -Message 'Stopping Schedule service for repair.'
            Invoke-TidyCommand -Command { Stop-Service -Name 'Schedule' -Force -ErrorAction Stop } -Description 'Stopping Schedule service.' -RequireSuccess | Out-Null
            Start-Sleep -Seconds 2
        }

        return $true
    }
    catch {
        # Log as warning and skip registry rebuild to avoid throwing when ACLs block Schedule stop.
        Write-TidyOutput -Message ("Unable to stop Schedule service (continuing without registry rebuild): {0}" -f $_.Exception.Message)
        return $false
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

function Rebuild-TaskCacheRegistry {
    try {
        $key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache'
        if (-not (Test-Path -LiteralPath $key)) {
            Write-TidyOutput -Message 'TaskCache registry hive not found; skipping registry rebuild.'
            return
        }

        $tempBackup = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ("taskcache-reg-backup-{0}.reg" -f (Get-Date -Format 'yyyyMMddHHmmss'))
        Write-TidyOutput -Message ("Exporting TaskCache registry to {0}." -f $tempBackup)
        Invoke-TidyCommand -Command { param($path) reg.exe export 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache' $path /y } -Arguments @($tempBackup) -Description 'Backing up TaskCache registry hive.'

        $stopped = Stop-ScheduleServiceForRepair
        if (-not $stopped) {
            Write-TidyOutput -Message 'Schedule service could not be stopped; skipping registry hive rebuild to avoid corruption.'
            return
        }

        Write-TidyOutput -Message 'Removing TaskCache registry hive for rebuild.'
        try {
            if (Test-Path -LiteralPath $key) {
                Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction Stop
            }
            else {
                Write-TidyOutput -Message 'TaskCache registry hive already absent; skipping removal.'
            }
        }
        catch {
            $message = $_.Exception.Message
            if ($message -match 'subkey does not exist') {
                Write-TidyOutput -Message ('TaskCache registry hive removal skipped (already removed): {0}' -f $message)
            }
            else {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to remove TaskCache registry hive: {0}" -f $message)
                return
            }
        }

        Write-TidyOutput -Message 'Starting Schedule service to rebuild TaskCache registry.'
        try {
            Invoke-TidyCommand -Command { Start-Service -Name 'Schedule' -ErrorAction Stop } -Description 'Starting Schedule service (registry rebuild).' -RequireSuccess | Out-Null
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to start Schedule service after registry removal: {0}" -f $_.Exception.Message)
            return
        }

        if (-not (Wait-TidyServiceState -Name 'Schedule' -DesiredStatus 'Running' -TimeoutSeconds 20)) {
            Write-TidyOutput -Message 'Schedule service did not reach Running state after registry rebuild start.'
            $script:OperationSucceeded = $false
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("TaskCache registry rebuild failed: {0}" -f $_.Exception.Message)
    }
}

function Repair-TasksAcl {
    try {
        $tasksRoot = Join-Path -Path $env:SystemRoot -ChildPath 'System32\Tasks'
        if (-not (Test-Path -LiteralPath $tasksRoot)) {
            Write-TidyOutput -Message 'Tasks root not found; skipping ACL repair.'
            return
        }

        Write-TidyOutput -Message 'Resetting Tasks folder ACLs and ownership (TrustedInstaller).'
        Invoke-TidyCommand -Command { param($path) icacls $path /setowner "NT SERVICE\TrustedInstaller" /t /c /l } -Arguments @($tasksRoot) -Description 'Setting TrustedInstaller ownership on Tasks tree.' -DemoteNativeCommandErrors
        Invoke-TidyCommand -Command { param($path) icacls $path /reset /t /c /l } -Arguments @($tasksRoot) -Description 'Resetting Tasks ACLs to defaults.' -DemoteNativeCommandErrors
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Tasks ACL repair failed: {0}" -f $_.Exception.Message)
    }
}

function Repair-UpdateServices {
    try {
        $targets = @(
            @{ Name = 'UsoSvc'; StartType = 'Manual' },
            @{ Name = 'WaaSMedicSvc'; StartType = 'Manual' },
            @{ Name = 'BITS'; StartType = 'AutomaticDelayedStart' }
        )

        foreach ($svc in $targets) {
            $service = Get-Service -Name $svc.Name -ErrorAction SilentlyContinue
            if (-not $service) {
                Write-TidyOutput -Message ("Service {0} not found; skipping." -f $svc.Name)
                continue
            }

            try {
                if ($service.StartType -ne $svc.StartType) {
                    Write-TidyOutput -Message ("Setting {0} start type to {1}." -f $svc.Name, $svc.StartType)
                    Set-Service -Name $svc.Name -StartupType $svc.StartType -ErrorAction Stop
                }
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to set start type for {0}: {1}" -f $svc.Name, $_.Exception.Message)
            }

            try {
                if ($service.Status -ne 'Running') {
                    Write-TidyOutput -Message ("Starting service {0}." -f $svc.Name)
                    Start-Service -Name $svc.Name -ErrorAction Stop
                }
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to start service {0}: {1}" -f $svc.Name, $_.Exception.Message)
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Update service repair failed: {0}" -f $_.Exception.Message)
    }
}

function Get-BaselineRoot {
    $root = Join-Path -Path $scriptDirectory -ChildPath 'baselines\tasks'
    return [System.IO.Path]::GetFullPath($root)
}

function Get-BaselineLayouts {
    $root = Get-BaselineRoot
    if (-not (Test-Path -LiteralPath $root)) {
        Write-TidyOutput -Message ("Baseline root not found at {0}." -f $root)
        return @()
    }

    $dirs = Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue | Sort-Object -Property Name
    $names = @($dirs | ForEach-Object { $_.Name })
    Write-TidyOutput -Message ("Baseline root: {0}; layouts discovered: {1}" -f $root, ($names -join ', '))
    return $names
}

function Load-BaselineTasksFromLayout {
    param(
        [Parameter(Mandatory = $true)][string] $Layout,
        [Parameter(Mandatory = $true)][string] $StartBoundary
    )

    $root = Get-BaselineRoot
    $layoutPath = Join-Path -Path $root -ChildPath $Layout
    if (-not (Test-Path -LiteralPath $layoutPath)) {
        Write-TidyOutput -Message ("Baseline layout '{0}' not found at {1}." -f $Layout, $layoutPath)
        return @()
    }

    $tasks = @()
    foreach ($entry in $script:BaselineTaskMap.GetEnumerator()) {
        $fileName = $entry.Key
        $meta = $entry.Value
        $filePath = Join-Path -Path $layoutPath -ChildPath $fileName

        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-TidyOutput -Message ("Baseline file missing for layout {0}: {1}" -f $Layout, $filePath)
            continue
        }

        try {
            # Baselines are stored as UTF-8; read with UTF8 to avoid garbling XML into mojibake.
            $content = Get-Content -LiteralPath $filePath -Raw -Encoding UTF8
            $content = $content.Replace('{{START_BOUNDARY}}', $StartBoundary)
            $tasks += [pscustomobject]@{
                Layout = $Layout
                Path   = (Normalize-TaskPath -TaskPath $meta.Path)
                Name   = $meta.Name
                Xml    = $content
                File   = $filePath
            }
        }
        catch {
            Write-TidyOutput -Message ("Failed reading baseline file {0}: {1}" -f $filePath, $_.Exception.Message)
        }
    }

    if ($tasks.Count -eq 0) {
        Write-TidyOutput -Message ("No tasks were produced from layout '{0}'." -f $Layout)
    }

    return $tasks
}

function Normalize-BaselineLayouts {
    param(
        [Parameter(Mandatory = $true)][object[]] $Layouts,
        [Parameter(Mandatory = $true)][string] $StartBoundary
    )

    $normalized = @()

    foreach ($layout in $Layouts) {
        if (-not $layout) { continue }

        $layoutName = $null
        if ($layout -is [psobject] -and $layout.PSObject.Properties['LayoutName']) {
            $layoutName = $layout.LayoutName
        }
        elseif ($layout -is [psobject] -and $layout.PSObject.Properties['Layout']) {
            $layoutName = $layout.Layout
        }
        elseif ($layout -is [string]) {
            $layoutName = $layout
        }
        else {
            $layoutName = $layout.ToString()
        }

        $hasTasksProp = ($layout -is [psobject]) -and ($layout.PSObject.Properties['Tasks'] -ne $null)
        if ($hasTasksProp) {
            $normalized += $layout
            continue
        }

        if (-not [string]::IsNullOrWhiteSpace($layoutName)) {
            $tasks = @(Load-BaselineTasksFromLayout -Layout $layoutName -StartBoundary $StartBoundary)
            Write-TidyOutput -Message ("Materialized baseline entry '{0}' into {1} task template(s)." -f $layoutName, $tasks.Count)
            $normalized += [pscustomobject]@{
                LayoutName = $layoutName
                Tasks      = $tasks
            }
            continue
        }

        Write-TidyOutput -Message ("Baseline entry of type {0} missing Tasks property and layout name; skipping normalization." -f $layout.GetType().FullName)
    }

    # Final safety: only return objects that carry Tasks to avoid string leakage into callers.
    return @($normalized | Where-Object { $_ -is [psobject] -and $_.PSObject.Properties['Tasks'] })
}

function Get-TaskFolderPathFromTaskName {
    param([Parameter(Mandatory = $true)][string] $TaskPath)

    $normalized = Normalize-TaskPath -TaskPath $TaskPath
    $trimmed = $normalized.TrimStart('\\')
    $fsPath = Join-Path -Path (Join-Path $env:SystemRoot 'System32\Tasks') -ChildPath $trimmed
    return $fsPath
}

function Ensure-TaskFolderReady {
    param([Parameter(Mandatory = $true)][string] $TaskPath)

    $fsPath = Get-TaskFolderPathFromTaskName -TaskPath $TaskPath
    $dir = Split-Path -Parent $fsPath
    try {
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }

        # Repair ACLs for the specific folder to reduce access denied during creation.
        Invoke-TidyCommand -Command { param($path) icacls $path /setowner "NT SERVICE\\TrustedInstaller" /t /c /l } -Arguments @($dir) -Description ("Setting TrustedInstaller ownership on {0}" -f $dir) -DemoteNativeCommandErrors
        Invoke-TidyCommand -Command { param($path) icacls $path /reset /t /c /l } -Arguments @($dir) -Description ("Resetting ACLs on {0}" -f $dir) -DemoteNativeCommandErrors
        $script:TaskFolderAclRepaired = $true
    }
    catch {
        $err = $_
        $errMsg = if ($err -and $err.Exception) { $err.Exception.Message } else { ($err | Out-String).Trim() }
        $script:TaskFolderAclRepairErrors.Add($errMsg) | Out-Null
        Write-TidyOutput -Message ("ACL repair for {0} encountered an issue: {1}" -f $dir, $errMsg)
    }
}

function Restore-UsoTasksFromLayout {
    param(
        [Parameter(Mandatory = $true)][psobject[]] $Tasks,
        [Parameter(Mandatory = $true)][string] $LayoutName
    )

    if (-not $Tasks -or $Tasks.Count -eq 0) { return $false }

    $script:BaselineRestoreAttempts++
    if (-not $script:BaselineLayoutsTried.Contains($LayoutName)) { $script:BaselineLayoutsTried.Add($LayoutName) | Out-Null }

    $tempFiles = @()
    $anySuccess = $false
    Write-TidyOutput -Message ("Layout {0} has {1} baseline task(s) queued for restore." -f $LayoutName, $Tasks.Count)

    # Snapshot presence before attempting creation so missing-present distinctions are logged even if we short-circuit later.
    $preExisting = @{}
    foreach ($task in $Tasks) {
        if (-not $task) { continue }
        $path = [string]$task.Path
        $name = [string]$task.Name
        $preExisting["${path}${name}"] = [bool](Get-ScheduledTask -TaskPath $path -TaskName $name -ErrorAction SilentlyContinue)
    }
    if ($preExisting.Keys.Count -gt 0) {
        $stateLines = $preExisting.GetEnumerator() | ForEach-Object { "  ↳ {0} : {1}" -f $_.Key, ($(if ($_.Value) { 'present' } else { 'missing' })) }
        Write-TidyOutput -Message ("Pre-flight task presence for layout {0}:{1}{2}" -f $LayoutName, [Environment]::NewLine, ($stateLines -join [Environment]::NewLine))
    }

    try {
        $index = 0
        $folderPrepared = @{}
        foreach ($task in $Tasks) {
            $index++
            try {
                Write-TidyOutput -Message ("[{0}/{1}] Entering restore iteration." -f $index, $Tasks.Count)
                if (-not $task) {
                    Write-TidyOutput -Message ("Encountered null task entry in layout {0}; skipping." -f $LayoutName)
                    continue
                }

                $hasPath = ($task -is [psobject]) -and ($task.PSObject.Properties['Path'] -ne $null)
                $hasName = ($task -is [psobject]) -and ($task.PSObject.Properties['Name'] -ne $null)
                $hasXml  = ($task -is [psobject]) -and ($task.PSObject.Properties['Xml'] -ne $null)
                if (-not ($hasPath -and $hasName -and $hasXml)) {
                    Write-TidyOutput -Message ("Task entry in layout {0} missing required fields (Path/Name/Xml); skipping." -f $LayoutName)
                    continue
                }

                # Defensive: coerce to strings so StrictMode doesn't choke on nested property expansions.
                $taskPath = Normalize-TaskPath -TaskPath $task.Path
                $taskName = [string]$task.Name
                $taskXml  = [string]$task.Xml

                # Normalize XML encoding declaration to match the UTF-16 strings we hand to both Register-ScheduledTask and schtasks.exe.
                $taskXmlNormalized = $taskXml -replace 'encoding="[^\"]+"', 'encoding="UTF-16"'
                # Normalize principal to a scheduler-friendly SYSTEM definition for XML path.
                $taskXmlNormalized = $taskXmlNormalized -replace '<LogonType>[^<]+</LogonType>', '<LogonType>S4U</LogonType>'
                $taskXmlNormalized = $taskXmlNormalized -replace '<RunLevel>[^<]+</RunLevel>', '<RunLevel>HighestAvailable</RunLevel>'

                $tn = "${taskPath}${taskName}"
                Write-TidyOutput -Message ("[{0}/{1}] Baseline task target: {2}" -f $index, $Tasks.Count, $tn)

                # If the task already exists, treat as success to avoid failing summaries when nothing needs recreating.
                $existing = Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue
                if ($existing) {
                    $anySuccess = $true
                    Write-TidyOutput -Message ("Task already present; skipping baseline creation: {0}" -f $tn)
                    if (-not $script:CreatedTasks.Contains($tn)) { $script:CreatedTasks.Add($tn) | Out-Null }
                    continue
                }

                Write-TidyOutput -Message ("[{0}/{1}] Task not present, creating via COM API." -f $index, $Tasks.Count)

                if (-not $folderPrepared.ContainsKey($taskPath)) {
                    Write-TidyOutput -Message ("[{0}/{1}] Ensuring folder ACLs for {2}." -f $index, $Tasks.Count, $taskPath)
                    try {
                        Ensure-TaskFolderReady -TaskPath $taskPath
                        Write-TidyOutput -Message ("[{0}/{1}] Folder ACL prep complete for {2}." -f $index, $Tasks.Count, $taskPath)
                    }
                    catch {
                        $err = $_
                        $errMsg = if ($err -and $err.Exception) { $err.Exception.Message } else { ($err | Out-String).Trim() }
                        $script:TaskCreationErrors.Add(("{0} (layout {1}) folder prep error: {2}" -f $tn, $LayoutName, $errMsg)) | Out-Null
                        Write-TidyOutput -Message ("[{0}/{1}] Folder prep error for {2}: {3}" -f $index, $Tasks.Count, $taskPath, $errMsg)
                    }
                    $folderPrepared[$taskPath] = $true
                }

                $created = New-UsoTaskWithFallback -TaskName $taskName -TaskPath $taskPath
                if ($created) {
                    $anySuccess = $true
                    if (-not $script:CreatedTasks.Contains($tn)) { $script:CreatedTasks.Add($tn) | Out-Null }
                    Write-TidyOutput -Message ("Created task {0}." -f $tn)
                    continue
                }

                $script:TaskCreationErrors.Add("${tn} (layout ${LayoutName}) creation failed (COM + Register-ScheduledTask).") | Out-Null
                Write-TidyOutput -Message ("[{0}/{1}] Task creation failed for {2} after COM + Register-ScheduledTask attempts." -f $index, $Tasks.Count, $tn)
                if (-not $script:FailedTasks.Contains($tn)) { $script:FailedTasks.Add($tn) | Out-Null }
                $script:OperationSucceeded = $false
                continue
            }
            catch {
                $err = $_
                $errMsg = if ($err -and $err.Exception) { $err.Exception.Message } else { ($err | Out-String).Trim() }
                $script:TaskCreationErrors.Add(("{0} task index {1} exception: {2}" -f $LayoutName, $index, $errMsg)) | Out-Null
                Write-TidyOutput -Message ("[{0}/{1}] Exception during task restore: {2}" -f $index, $Tasks.Count, $errMsg)
            }
        }

        Write-TidyOutput -Message ("Layout {0} processed {1}/{2} tasks (loop complete)." -f $LayoutName, $index, $Tasks.Count)

        # Post-pass validation: if no explicit success was recorded, but tasks now exist, count as success.
        if (-not $anySuccess) {
            $present = @()
            foreach ($task in $Tasks) {
                if (-not $task) { continue }
                $path = [string]$task.Path
                $name = [string]$task.Name
                $tnCheck = "${path}${name}"
                $existsNow = Get-ScheduledTask -TaskPath $path -TaskName $name -ErrorAction SilentlyContinue
                if ($existsNow) { $present += $tnCheck }
            }

            if ($present.Count -eq $Tasks.Count) {
                Write-TidyOutput -Message ("All {0} tasks for layout {1} are present; treating layout as success." -f $Tasks.Count, $LayoutName)
                $anySuccess = $true
                foreach ($tn in $present) { if (-not $script:CreatedTasks.Contains($tn)) { $script:CreatedTasks.Add($tn) | Out-Null } }
            }
            elseif ($present.Count -gt 0) {
                Write-TidyOutput -Message ("Detected {0}/{1} tasks already present for layout {2}, but no new creations succeeded." -f $present.Count, $Tasks.Count, $LayoutName)
                foreach ($tn in $present) { if (-not $script:CreatedTasks.Contains($tn)) { $script:CreatedTasks.Add($tn) | Out-Null } }
            }
        }

        if ($anySuccess) {
            $script:BaselineRestoreSucceeded++
            return $true
        }

        $script:BaselineRestoreFailed++
        if (-not $script:BaselineLayoutsFailed.Contains($LayoutName)) { $script:BaselineLayoutsFailed.Add($LayoutName) | Out-Null }
        return $false
    }
    catch {
        $script:BaselineRestoreFailed++
        if (-not $script:BaselineLayoutsFailed.Contains($LayoutName)) { $script:BaselineLayoutsFailed.Add($LayoutName) | Out-Null }
        $err = $_
        $errMsg = if ($err -and $err.Exception) { $err.Exception.Message } else { ($err | Out-String).Trim() }
        Write-TidyOutput -Message ("Task baseline restore encountered an error for layout {0}: {1}" -f $LayoutName, $errMsg)
        return $false
    }
    finally {
        foreach ($f in $tempFiles) {
            if (Test-Path -LiteralPath $f) {
                Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
            }
        }
        Write-TidyOutput -Message ("Layout {0} finalize block reached." -f $LayoutName)
    }
}

function Get-BaselineUsoTasks {
    $startBoundary = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $layouts = Get-BaselineLayouts
    if (-not $layouts -or $layouts.Count -eq 0) {
        Write-TidyOutput -Message 'No baseline layouts found on disk; update task recreation will be skipped.'
        return @()
    }

    $all = @()
    foreach ($layout in $layouts) {
        $tasks = @(Load-BaselineTasksFromLayout -Layout $layout -StartBoundary $startBoundary)
        $count = $tasks.Count
        Write-TidyOutput -Message ("Baseline layout '{0}' loaded {1} task template(s)." -f $layout, $count)
        if ($count -eq 0) {
            Write-TidyOutput -Message ("Baseline layout '{0}' contained no tasks; verify files under baselines/tasks/{0}." -f $layout)
        }

        $all += [pscustomobject]@{
            LayoutName = $layout
            Tasks      = $tasks
        }
    }

    $normalized = Normalize-BaselineLayouts -Layouts $all -StartBoundary $startBoundary

    $typeSummary = $normalized | Where-Object { $_ } | Group-Object { $_.GetType().Name } | ForEach-Object { "{0}x{1}" -f $_.Count, $_.Name }
    if ($typeSummary) {
        Write-TidyOutput -Message ("Baseline entries after normalization: {0}" -f ($typeSummary -join '; '))
    }

    return $normalized
}

function Enable-UsoTasks {
    $startBoundary = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $schedule = Get-Service -Name 'Schedule' -ErrorAction SilentlyContinue
    if ($schedule -and $schedule.Status -ne 'Running') {
        Write-TidyOutput -Message 'Schedule service is not running; starting before task enablement.'
        try {
            Start-Service -Name 'Schedule' -ErrorAction Stop
            [void](Wait-TidyServiceState -Name 'Schedule' -DesiredStatus 'Running' -TimeoutSeconds 20)
        }
        catch {
            Write-TidyOutput -Message ("Unable to start Schedule service prior to USO task enablement: {0}" -f $_.Exception.Message)
        }
    }

    $targets = @(
        @{ Path = (Normalize-TaskPath -TaskPath '\Microsoft\Windows\UpdateOrchestrator\'); Name = 'Schedule Scan' },
        @{ Path = (Normalize-TaskPath -TaskPath '\Microsoft\Windows\UpdateOrchestrator\'); Name = 'UpdateModel' },
        @{ Path = (Normalize-TaskPath -TaskPath '\Microsoft\Windows\UpdateOrchestrator\'); Name = 'Universal Orchestrator Start' },
        @{ Path = (Normalize-TaskPath -TaskPath '\Microsoft\Windows\UpdateOrchestrator\'); Name = 'USO_UxBroker' },
        @{ Path = (Normalize-TaskPath -TaskPath '\Microsoft\Windows\WindowsUpdate\'); Name = 'Scheduled Start' }
    )

    $baselineLayouts = @(Get-BaselineUsoTasks)
    # Double-normalize to handle older copies of Get-BaselineUsoTasks that might return raw strings.
    $baselineLayouts = @(Normalize-BaselineLayouts -Layouts $baselineLayouts -StartBoundary $startBoundary)

    $typeSummary = $baselineLayouts | Where-Object { $_ } | Group-Object { $_.GetType().Name } | ForEach-Object { "{0}x{1}" -f $_.Count, $_.Name }
    if ($typeSummary) {
        Write-TidyOutput -Message ("Baseline entries entering enablement: {0}" -f ($typeSummary -join '; '))
    }
    if (-not $baselineLayouts -or $baselineLayouts.Count -eq 0) {
        Write-TidyOutput -Message 'No baseline layouts were available; skipping USO task rebuild.'
        $baselineLayouts = @()
        if ($script:UsoTaskRebuildRequested) {
            return
        }
    }

    if ($script:UsoTaskRebuildRequested -and $baselineLayouts.Count -gt 0) {
        $forcedBaselineApplied = $false
        foreach ($layout in $baselineLayouts) {
            if (-not $layout) { continue }
            if (-not ($layout -is [psobject]) -or -not ($layout.PSObject.Properties['Tasks'])) { continue }
            if (-not $layout.Tasks -or $layout.Tasks.Count -eq 0) { continue }

            Write-TidyOutput -Message ("Forcing baseline registration for all tasks from layout {0}." -f $layout.LayoutName)
            if (Restore-UsoTasksFromLayout -Tasks $layout.Tasks -LayoutName $layout.LayoutName) {
                $forcedBaselineApplied = $true
                break
            }
        }

        if (-not $forcedBaselineApplied) {
            Write-TidyOutput -Message 'Baseline registration attempts across layouts did not succeed.'
        }
    }

    $uoPath = Normalize-TaskPath -TaskPath '\Microsoft\Windows\UpdateOrchestrator\'
    $wuPath = Normalize-TaskPath -TaskPath '\Microsoft\Windows\WindowsUpdate\'

    $uoTasks = Get-ScheduledTask -TaskPath $uoPath -ErrorAction SilentlyContinue
    $wuTasks = Get-ScheduledTask -TaskPath $wuPath -ErrorAction SilentlyContinue
    if (-not $uoTasks -and -not $wuTasks) {
        Write-TidyOutput -Message 'UpdateOrchestrator/WindowsUpdate task folders not found. Tasks may be removed or policy-disabled.'
        if ($script:UsoTaskRebuildRequested) {
            $restored = $false
            foreach ($layout in $baselineLayouts) {
                if (-not $layout) { continue }

                # Defensive: ensure the entry has a Tasks property before use.
                $layoutHasTasksProp = $false
                if ($layout -is [psobject]) {
                    $layoutHasTasksProp = $layout.PSObject.Properties['Tasks'] -ne $null
                }
                if (-not $layoutHasTasksProp) {
                    Write-TidyOutput -Message ("Baseline entry of type {0} missing Tasks; skipping." -f $layout.GetType().FullName)
                    continue
                }
                if (-not $layout.Tasks -or $layout.Tasks.Count -eq 0) {
                    Write-TidyOutput -Message ("Layout {0} has no tasks to restore; skipping." -f $layout.LayoutName)
                    continue
                }
                if (Restore-UsoTasksFromLayout -Tasks $layout.Tasks -LayoutName $layout.LayoutName) {
                    $restored = $true
                    break
                }
            }

            $uoTasks = Get-ScheduledTask -TaskPath $uoPath -ErrorAction SilentlyContinue
            $wuTasks = Get-ScheduledTask -TaskPath $wuPath -ErrorAction SilentlyContinue
            if (-not $restored -or (-not $uoTasks -and -not $wuTasks)) {
                Write-TidyOutput -Message 'Baseline task restore attempted across layouts but no tasks were created.'
                return
            }
        }
        else {
            return
        }
    }

    foreach ($task in $targets) {
        $path = $task.Path
        $name = $task.Name

        $exists = Get-ScheduledTask -TaskPath $path -TaskName $name -ErrorAction SilentlyContinue
        if (-not $exists) {
            if ($script:UsoTaskRebuildRequested) {
                $created = $false
                foreach ($layout in $baselineLayouts) {
                    $baseline = $layout.Tasks | Where-Object { $_.Path -eq $path -and $_.Name -eq $name } | Select-Object -First 1
                    if (-not $baseline) { continue }

                    Write-TidyOutput -Message ("Task {0}{1} missing; creating from baseline layout {2}." -f $path, $name, $layout.LayoutName)
                    if (Restore-UsoTasksFromLayout -Tasks @($baseline) -LayoutName $layout.LayoutName) {
                        $created = $true
                        break
                    }
                }

                if (-not $created) {
                    Write-TidyOutput -Message ("Baseline creation failed for task {0}{1} across all layouts." -f $path, $name)
                    continue
                }

                $exists = Get-ScheduledTask -TaskPath $path -TaskName $name -ErrorAction SilentlyContinue
                if (-not $exists) {
                    Write-TidyOutput -Message ("Task {0}{1} still not present after baseline creation." -f $path, $name)
                    continue
                }
            }
            else {
                Write-TidyOutput -Message ("Task {0}{1} not found. Skipping enable." -f $path, $name)
                continue
            }
        }

        if ($exists.State -eq 'Disabled') {
            Write-TidyOutput -Message ("Task {0}{1} is disabled; enabling." -f $path, $name)
        }

        try {
            Write-TidyOutput -Message ("Enabling scheduled task {0}{1}." -f $path, $name)
            Enable-ScheduledTask -TaskPath $path -TaskName $name -ErrorAction Stop | Out-Null
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to enable task {0}{1}: {2}" -f $path, $name, $_.Exception.Message)
        }
    }
}

function Write-TidyRepairSummary {
    Write-TidyOutput -Message '--- Task Scheduler repair summary ---'
    Write-TidyOutput -Message ("Baseline layouts tried: {0}" -f ($script:BaselineLayoutsTried -join ', '))
    if ($script:BaselineRestoreAttempts -gt 0) {
        Write-TidyOutput -Message ("Baseline attempts: {0}, succeeded: {1}, failed: {2}" -f $script:BaselineRestoreAttempts, $script:BaselineRestoreSucceeded, $script:BaselineRestoreFailed)
    }
    if ($script:CreatedTasks.Count -gt 0) {
        Write-TidyOutput -Message ("Tasks created ({0}):" -f $script:CreatedTasks.Count)
        foreach ($t in $script:CreatedTasks | Sort-Object -Unique) { Write-TidyOutput -Message ("  ↳ {0}" -f $t) }
    }
    if ($script:FailedTasks.Count -gt 0) {
        Write-TidyOutput -Message ("Tasks failed to create/enable ({0}):" -f $script:FailedTasks.Count)
        foreach ($t in $script:FailedTasks | Sort-Object -Unique) { Write-TidyOutput -Message ("  ↳ {0}" -f $t) }
    }
    if ($script:TaskCreationErrors.Count -gt 0) {
        Write-TidyOutput -Message 'Task creation errors:'
        foreach ($e in $script:TaskCreationErrors | Sort-Object -Unique) { Write-TidyOutput -Message ("  ↳ {0}" -f $e) }
    }
    if ($script:TaskFolderAclRepaired) {
        Write-TidyOutput -Message 'Tasks folder ACL repair attempted for creation paths.'
    }
    if ($script:TaskFolderAclRepairErrors.Count -gt 0) {
        Write-TidyOutput -Message 'ACL repair issues encountered:'
        foreach ($e in $script:TaskFolderAclRepairErrors | Sort-Object -Unique) { Write-TidyOutput -Message ("  ↳ {0}" -f $e) }
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Task Scheduler repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting Task Scheduler and automation repair pack.'

    if (-not $SkipUsoTaskEnable.IsPresent -and -not $SkipUsoTaskRebuild.IsPresent) {
        # Default to rebuild missing update tasks unless explicitly skipped.
        $script:UsoTaskRebuildRequested = $true
    }

    if (-not $SkipTaskCacheRebuild.IsPresent) {
        Rebuild-TaskCache
    }
    else {
        Write-TidyOutput -Message 'Skipping TaskCache rebuild per operator request.'
    }

    if (-not $SkipTaskCacheRegistryRebuild.IsPresent) {
        Rebuild-TaskCacheRegistry
    }
    else {
        Write-TidyOutput -Message 'Skipping TaskCache registry rebuild per operator request.'
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

    if (-not $SkipTasksAclRepair.IsPresent) {
        Repair-TasksAcl
    }
    else {
        Write-TidyOutput -Message 'Skipping Tasks folder ACL repair per operator request.'
    }

    if ($RepairUpdateServices.IsPresent) {
        Repair-UpdateServices
    }
    else {
        Write-TidyOutput -Message 'Skipping update service repair (UsoSvc/WaaSMedicSvc/BITS) unless -RepairUpdateServices is specified.'
    }

    Write-TidyRepairSummary

    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        Write-TidyOutput -Message 'Task Scheduler repair completed (service + cache + tasks validated).'
    }
    else {
        Write-TidyOutput -Message 'Task Scheduler repair completed with errors; review transcript for failed steps (tasks may remain missing/blocked).'
    }
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message ("Task Scheduler repair failed: {0}" -f $_.Exception.Message)
}
finally {
    Save-TidyResult
}
