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
    TidyWindow.Automation\Write-TidyLog -Level Information -Message $text
}

function Write-TidyError {
    param([Parameter(Mandatory = $true)][object] $Message)
    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) { return }
    if ($script:TidyErrorLines -is [System.Collections.IList]) {
        [void]$script:TidyErrorLines.Add($text)
    }
    TidyWindow.Automation\Write-TidyError -Message $text
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

function Get-BaselineUsoTasks {
    $startBoundary = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

        return @(
                @{
                        Path = '\\Microsoft\\Windows\\UpdateOrchestrator\\'
                        Name = 'Schedule Scan'
                        Xml  = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
    <RegistrationInfo>
        <Description>Scans for available Windows updates.</Description>
    </RegistrationInfo>
    <Triggers>
        <CalendarTrigger>
            <StartBoundary>$startBoundary</StartBoundary>
            <Enabled>true</Enabled>
            <ScheduleByDay>
                <DaysInterval>1</DaysInterval>
            </ScheduleByDay>
        </CalendarTrigger>
    </Triggers>
    <Principals>
        <Principal id="Author">
            <UserId>S-1-5-18</UserId>
            <RunLevel>HighestAvailable</RunLevel>
            <LogonType>ServiceAccount</LogonType>
        </Principal>
    </Principals>
    <Settings>
        <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
        <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
        <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
        <AllowHardTerminate>true</AllowHardTerminate>
        <StartWhenAvailable>true</StartWhenAvailable>
        <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
        <IdleSettings>
            <StopOnIdleEnd>false</StopOnIdleEnd>
            <RestartOnIdle>false</RestartOnIdle>
        </IdleSettings>
        <Enabled>true</Enabled>
        <Hidden>false</Hidden>
        <RunOnlyIfIdle>false</RunOnlyIfIdle>
        <WakeToRun>false</WakeToRun>
        <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
        <Priority>7</Priority>
    </Settings>
    <Actions Context="Author">
        <Exec>
            <Command>%SystemRoot%\system32\usoclient.exe</Command>
            <Arguments>StartScan</Arguments>
        </Exec>
    </Actions>
</Task>
"@
                }
                @{
                        Path = '\\Microsoft\\Windows\\UpdateOrchestrator\\'
                        Name = 'UpdateModel'
                        Xml  = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
    <RegistrationInfo>
        <Description>Maintains Windows Update orchestration.</Description>
    </RegistrationInfo>
    <Triggers>
        <BootTrigger>
            <Enabled>true</Enabled>
            <Delay>PT5M</Delay>
        </BootTrigger>
    </Triggers>
    <Principals>
        <Principal id="Author">
            <UserId>S-1-5-18</UserId>
            <RunLevel>HighestAvailable</RunLevel>
            <LogonType>ServiceAccount</LogonType>
        </Principal>
    </Principals>
    <Settings>
        <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
        <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
        <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
        <AllowHardTerminate>true</AllowHardTerminate>
        <StartWhenAvailable>true</StartWhenAvailable>
        <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
        <IdleSettings>
            <StopOnIdleEnd>false</StopOnIdleEnd>
            <RestartOnIdle>false</RestartOnIdle>
        </IdleSettings>
        <Enabled>true</Enabled>
        <Hidden>false</Hidden>
        <RunOnlyIfIdle>false</RunOnlyIfIdle>
        <WakeToRun>false</WakeToRun>
        <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
        <Priority>7</Priority>
    </Settings>
    <Actions Context="Author">
        <Exec>
            <Command>%SystemRoot%\system32\usoclient.exe</Command>
            <Arguments>StartScan</Arguments>
        </Exec>
    </Actions>
</Task>
"@
                }
                @{
                        Path = '\\Microsoft\\Windows\\UpdateOrchestrator\\'
                        Name = 'Universal Orchestrator Start'
                        Xml  = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
    <RegistrationInfo>
        <Description>Starts the Windows Update Orchestrator service.</Description>
    </RegistrationInfo>
    <Triggers>
        <BootTrigger>
            <Enabled>true</Enabled>
            <Delay>PT2M</Delay>
        </BootTrigger>
    </Triggers>
    <Principals>
        <Principal id="Author">
            <UserId>S-1-5-18</UserId>
            <RunLevel>HighestAvailable</RunLevel>
            <LogonType>ServiceAccount</LogonType>
        </Principal>
    </Principals>
    <Settings>
        <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
        <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
        <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
        <AllowHardTerminate>true</AllowHardTerminate>
        <StartWhenAvailable>true</StartWhenAvailable>
        <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
        <IdleSettings>
            <StopOnIdleEnd>false</StopOnIdleEnd>
            <RestartOnIdle>false</RestartOnIdle>
        </IdleSettings>
        <Enabled>true</Enabled>
        <Hidden>false</Hidden>
        <RunOnlyIfIdle>false</RunOnlyIfIdle>
        <WakeToRun>false</WakeToRun>
        <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
        <Priority>7</Priority>
    </Settings>
    <Actions Context="Author">
        <Exec>
            <Command>%SystemRoot%\system32\sc.exe</Command>
            <Arguments>start UsoSvc</Arguments>
        </Exec>
    </Actions>
</Task>
"@
                }
                @{
                        Path = '\\Microsoft\\Windows\\UpdateOrchestrator\\'
                        Name = 'USO_UxBroker'
                        Xml  = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
    <RegistrationInfo>
        <Description>Handles Windows Update UX brokering.</Description>
    </RegistrationInfo>
    <Triggers>
        <LogonTrigger>
            <Enabled>true</Enabled>
        </LogonTrigger>
    </Triggers>
    <Principals>
        <Principal id="Author">
            <UserId>S-1-5-18</UserId>
            <RunLevel>HighestAvailable</RunLevel>
            <LogonType>ServiceAccount</LogonType>
        </Principal>
    </Principals>
    <Settings>
        <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
        <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
        <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
        <AllowHardTerminate>true</AllowHardTerminate>
        <StartWhenAvailable>true</StartWhenAvailable>
        <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
        <IdleSettings>
            <StopOnIdleEnd>false</StopOnIdleEnd>
            <RestartOnIdle>false</RestartOnIdle>
        </IdleSettings>
        <Enabled>true</Enabled>
        <Hidden>false</Hidden>
        <RunOnlyIfIdle>false</RunOnlyIfIdle>
        <WakeToRun>false</WakeToRun>
        <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
        <Priority>7</Priority>
    </Settings>
    <Actions Context="Author">
        <Exec>
            <Command>%SystemRoot%\system32\usoclient.exe</Command>
            <Arguments>StartScan</Arguments>
        </Exec>
    </Actions>
</Task>
"@
                }
                @{
                        Path = '\\Microsoft\\Windows\\WindowsUpdate\\'
                        Name = 'Scheduled Start'
                        Xml  = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
    <RegistrationInfo>
        <Description>Starts Windows Update scheduled maintenance.</Description>
    </RegistrationInfo>
    <Triggers>
        <CalendarTrigger>
            <StartBoundary>$startBoundary</StartBoundary>
            <Enabled>true</Enabled>
            <ScheduleByDay>
                <DaysInterval>1</DaysInterval>
            </ScheduleByDay>
        </CalendarTrigger>
    </Triggers>
    <Principals>
        <Principal id="Author">
            <UserId>S-1-5-18</UserId>
            <RunLevel>HighestAvailable</RunLevel>
            <LogonType>ServiceAccount</LogonType>
        </Principal>
    </Principals>
    <Settings>
        <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
        <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
        <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
        <AllowHardTerminate>true</AllowHardTerminate>
        <StartWhenAvailable>true</StartWhenAvailable>
        <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
        <IdleSettings>
            <StopOnIdleEnd>false</StopOnIdleEnd>
            <RestartOnIdle>false</RestartOnIdle>
        </IdleSettings>
        <Enabled>true</Enabled>
        <Hidden>false</Hidden>
        <RunOnlyIfIdle>false</RunOnlyIfIdle>
        <WakeToRun>false</WakeToRun>
        <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
        <Priority>7</Priority>
    </Settings>
    <Actions Context="Author">
        <Exec>
            <Command>%SystemRoot%\system32\usoclient.exe</Command>
            <Arguments>StartScan</Arguments>
        </Exec>
    </Actions>
</Task>
"@
                }
        )
}

function Restore-UsoTasksFromBaseline {
        param(
                [Parameter(Mandatory = $true)]
                [object[]] $Tasks
        )

        $tempFiles = @()
        try {
            foreach ($task in $Tasks) {
                $tempFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), [System.IO.Path]::GetRandomFileName() + '.xml')
                Set-Content -LiteralPath $tempFile -Value $task.Xml -Encoding Unicode
                $tn = "$($task.Path)$($task.Name)"
                Write-TidyOutput -Message ("Creating scheduled task from baseline: {0}" -f $tn)

                $process = Start-Process -FilePath 'schtasks.exe' -ArgumentList @('/create','/f','/tn', $tn, '/xml', $tempFile) -PassThru -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue
                $exit = if ($process) { $process.ExitCode } else { 1 }
                $tempFiles += $tempFile

                if ($exit -ne 0) {
                    $script:OperationSucceeded = $false
                    Write-TidyError -Message ("Baseline creation for {0} exited with {1}." -f $tn, $exit)
                    continue
                }

                Write-TidyOutput -Message ("Created task {0}." -f $tn)
            }
            return $true
        }
        catch {
            Write-TidyOutput -Message ("Task baseline restore encountered an error: {0}" -f $_.Exception.Message)
            return $false
        }
        finally {
                foreach ($f in $tempFiles) {
                        if (Test-Path -LiteralPath $f) {
                                Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
                        }
                }
        }
}

function Enable-UsoTasks {
    $targets = @(
        @{ Path = '\\Microsoft\\Windows\\UpdateOrchestrator\\'; Name = 'Schedule Scan' },
        @{ Path = '\\Microsoft\\Windows\\UpdateOrchestrator\\'; Name = 'UpdateModel' },
        @{ Path = '\\Microsoft\\Windows\\UpdateOrchestrator\\'; Name = 'Universal Orchestrator Start' },
        @{ Path = '\\Microsoft\\Windows\\UpdateOrchestrator\\'; Name = 'USO_UxBroker' },
        @{ Path = '\\Microsoft\\Windows\\WindowsUpdate\\'; Name = 'Scheduled Start' }
    )

    $baselineTasks = Get-BaselineUsoTasks

    $uoTasks = Get-ScheduledTask -TaskPath '\\Microsoft\\Windows\\UpdateOrchestrator\\' -ErrorAction SilentlyContinue
    $wuTasks = Get-ScheduledTask -TaskPath '\\Microsoft\\Windows\\WindowsUpdate\\' -ErrorAction SilentlyContinue
    if (-not $uoTasks -and -not $wuTasks) {
        Write-TidyOutput -Message 'UpdateOrchestrator/WindowsUpdate task folders not found. Tasks may be removed or policy-disabled.'
        if ($script:UsoTaskRebuildRequested) {
            Restore-UsoTasksFromBaseline -Tasks $baselineTasks
            $uoTasks = Get-ScheduledTask -TaskPath '\\Microsoft\\Windows\\UpdateOrchestrator\\' -ErrorAction SilentlyContinue
            $wuTasks = Get-ScheduledTask -TaskPath '\\Microsoft\\Windows\\WindowsUpdate\\' -ErrorAction SilentlyContinue
            if (-not $uoTasks -and -not $wuTasks) {
                Write-TidyOutput -Message 'Baseline task restore attempted but no tasks were created.'
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
                $baseline = $baselineTasks | Where-Object { $_.Path -eq $path -and $_.Name -eq $name } | Select-Object -First 1
                if ($baseline) {
                    Write-TidyOutput -Message ("Task {0}{1} missing; creating from baseline." -f $path, $name)
                    if (-not (Restore-UsoTasksFromBaseline -Tasks @($baseline))) {
                        Write-TidyOutput -Message ("Baseline creation failed for task {0}{1}." -f $path, $name)
                        continue
                    }
                    $exists = Get-ScheduledTask -TaskPath $path -TaskName $name -ErrorAction SilentlyContinue
                    if (-not $exists) {
                        Write-TidyOutput -Message ("Task {0}{1} still not present after baseline creation." -f $path, $name)
                        continue
                    }
                }
                else {
                    Write-TidyOutput -Message ("No baseline found for task {0}{1}. Skipping." -f $path, $name)
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
