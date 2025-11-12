param(
    [switch] $SkipComponentCleanup,
    [switch] $ResetBase,
    [switch] $IncludeDeliveryOptimization,
    [switch] $IncludeWindowsUpdateCache,
    [switch] $IncludePrefetch,
    [switch] $IncludeErrorReports,
    [switch] $IncludeRecycleBin,
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
$script:DryRunMode = $DryRun.IsPresent
$script:CleanupEntries = [System.Collections.Generic.List[pscustomobject]]::new()
$script:CleanupWarnings = [System.Collections.Generic.List[string]]::new()
$script:TotalTargetBytes = 0L
$script:TotalFreedBytes = 0L

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $timestamp = (Get-Date).ToString('yyyyMMdd_HHmmss')
    $LogPath = Join-Path -Path $env:TEMP -ChildPath "TidyWindow_StorageCleanup_$timestamp.json"
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
        [switch] $RequireSuccess,
        [int[]] $AcceptableExitCodes = @()
    )

    Write-TidyLog -Level Information -Message $Description

    if ($script:DryRunMode) {
        Write-TidyOutput -Message "[DryRun] Would run: $Description"
        if ($Arguments -and $Arguments.Count -gt 0) {
            Write-TidyOutput -Message ("[DryRun] Arguments: {0}" -f ($Arguments -join ', '))
        }
        return 0
    }

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

function Wait-TidyServiceStatus {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ServiceName,
        [Parameter(Mandatory = $true)]
        [System.ServiceProcess.ServiceControllerStatus] $DesiredStatus,
        [int] $TimeoutSeconds = 25
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

function Stop-TidyServices {
    param(
        [string[]] $ServiceNames,
        [string] $Context
    )

    if (-not $ServiceNames -or $ServiceNames.Count -eq 0) {
        return @()
    }

    $records = [System.Collections.Generic.List[pscustomobject]]::new()

    foreach ($serviceName in $ServiceNames) {
        if ([string]::IsNullOrWhiteSpace($serviceName)) {
            continue
        }

        try {
            $service = Get-Service -Name $serviceName -ErrorAction Stop
        }
        catch {
            $message = "Service $serviceName not found for ${Context}: $($_.Exception.Message)"
            $script:CleanupWarnings.Add($message) | Out-Null
            Write-TidyLog -Level Warning -Message $message
            $records.Add([pscustomobject]@{
                Name = $serviceName
                WasRunning = $false
                RestartNeeded = $false
                Action = 'Missing'
            }) | Out-Null
            continue
        }

        $wasRunning = $service.Status -in @(
            [System.ServiceProcess.ServiceControllerStatus]::Running,
            [System.ServiceProcess.ServiceControllerStatus]::StartPending,
            [System.ServiceProcess.ServiceControllerStatus]::ContinuePending
        )

        if ($script:DryRunMode) {
            Write-TidyOutput -Message ("[DryRun] Would stop service {0} (context: {1})." -f $serviceName, $Context)
            $records.Add([pscustomobject]@{
                Name = $serviceName
                WasRunning = $wasRunning
                RestartNeeded = $wasRunning
                Action = if ($wasRunning) { 'PlannedStop' } else { 'AlreadyStopped' }
            }) | Out-Null
            continue
        }

        if (-not $wasRunning) {
            Write-TidyOutput -Message ("Service {0} already stopped (context: {1})." -f $serviceName, $Context)
            $records.Add([pscustomobject]@{
                Name = $serviceName
                WasRunning = $false
                RestartNeeded = $false
                Action = 'AlreadyStopped'
            }) | Out-Null
            continue
        }

        try {
            Write-TidyOutput -Message ("Stopping service {0} for {1}." -f $serviceName, $Context)
            Stop-Service -Name $serviceName -Force -ErrorAction Stop
            if (-not (Wait-TidyServiceStatus -ServiceName $serviceName -DesiredStatus ([System.ServiceProcess.ServiceControllerStatus]::Stopped))) {
                $warning = "Timeout waiting for $serviceName to stop during $Context."
                $script:CleanupWarnings.Add($warning) | Out-Null
                Write-TidyLog -Level Warning -Message $warning
            }

            $records.Add([pscustomobject]@{
                Name = $serviceName
                WasRunning = $true
                RestartNeeded = $true
                Action = 'Stopped'
            }) | Out-Null
        }
        catch {
            $warning = "Failed to stop $serviceName for ${Context}: $($_.Exception.Message)"
            $script:CleanupWarnings.Add($warning) | Out-Null
            Write-TidyLog -Level Warning -Message $warning
            $records.Add([pscustomobject]@{
                Name = $serviceName
                WasRunning = $wasRunning
                RestartNeeded = $false
                Action = 'Failed'
            }) | Out-Null
        }
    }

    return $records.ToArray()
}

function Start-TidyServices {
    param(
        [pscustomobject[]] $ServiceRecords,
        [string] $Context
    )

    if (-not $ServiceRecords -or $ServiceRecords.Count -eq 0) {
        return
    }

    foreach ($record in $ServiceRecords) {
        if (-not $record) { continue }

        $name = $record.Name
        if ([string]::IsNullOrWhiteSpace($name)) { continue }

        if (-not $record.RestartNeeded) {
            continue
        }

        if ($script:DryRunMode) {
            Write-TidyOutput -Message ("[DryRun] Would start service {0} after {1}." -f $name, $Context)
            continue
        }

        try {
            Write-TidyOutput -Message ("Starting service {0} after {1}." -f $name, $Context)
            Start-Service -Name $name -ErrorAction Stop
            if (-not (Wait-TidyServiceStatus -ServiceName $name -DesiredStatus ([System.ServiceProcess.ServiceControllerStatus]::Running))) {
                $warning = "Timeout waiting for $name to start after $Context."
                $script:CleanupWarnings.Add($warning) | Out-Null
                Write-TidyLog -Level Warning -Message $warning
            }
        }
        catch {
            $warning = "Failed to start $name after ${Context}: $($_.Exception.Message)"
            $script:CleanupWarnings.Add($warning) | Out-Null
            Write-TidyLog -Level Warning -Message $warning
        }
    }
}

function Format-TidySize {
    param(
        [Parameter(Mandatory = $true)]
        [long] $Bytes
    )

    if ($Bytes -lt 1KB) { return "$Bytes B" }
    if ($Bytes -lt 1MB) { return "{0:N1} KB" -f ($Bytes / 1KB) }
    if ($Bytes -lt 1GB) { return "{0:N1} MB" -f ($Bytes / 1MB) }
    if ($Bytes -lt 1TB) { return "{0:N1} GB" -f ($Bytes / 1GB) }
    return "{0:N2} TB" -f ($Bytes / 1TB)
}

function Get-TidyPathSize {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath
    )

    try {
        if (-not (Test-Path -LiteralPath $LiteralPath)) {
            return 0L
        }

        $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
        if ($item.PSIsContainer) {
            $total = 0L
            $directory = [System.IO.DirectoryInfo]::new($item.FullName)
            $enumerationOptions = [System.IO.EnumerationOptions]::new()
            $enumerationOptions.RecurseSubdirectories = $true
            $enumerationOptions.AttributesToSkip = [System.IO.FileAttributes]::ReparsePoint
            $enumerationOptions.IgnoreInaccessible = $true

            foreach ($file in $directory.EnumerateFiles('*', $enumerationOptions)) {
                $total += $file.Length
            }

            return $total
        }

        return [long]$item.Length
    }
    catch {
        $script:CleanupWarnings.Add("Failed to measure size for '$LiteralPath': $($_.Exception.Message)") | Out-Null
        return 0L
    }
}

function Clear-TidyDirectoryContents {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $TargetName
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $true
    }

    try {
        $children = Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop
    }
    catch {
        $script:CleanupWarnings.Add("Unable to enumerate '$Path': $($_.Exception.Message)") | Out-Null
        return $false
    }

    $result = $true

    foreach ($child in $children) {
        try {
            if ($script:DryRunMode) {
                Write-TidyOutput -Message "[DryRun] Would remove $($child.FullName)"
            }
            else {
                Remove-Item -LiteralPath $child.FullName -Recurse -Force -ErrorAction Stop
            }
        }
        catch {
            $result = $false
            $script:CleanupWarnings.Add("Failed to remove '$($child.FullName)': $($_.Exception.Message)") | Out-Null
        }
    }

    return $result
}

function Invoke-TidyCleanupTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [Parameter(Mandatory = $true)]
        [string[]] $Paths,
        [string[]] $RequiredServices
    )

    $existing = [System.Collections.Generic.List[string]]::new()
    $unique = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($path in $Paths) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        $expanded = [System.Environment]::ExpandEnvironmentVariables($path)
        if ([string]::IsNullOrWhiteSpace($expanded)) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath($expanded)
        if (-not (Test-Path -LiteralPath $fullPath)) {
            continue
        }

        if ($unique.Add($fullPath)) {
            [void]$existing.Add($fullPath)
        }
    }

    if ($existing.Count -eq 0) {
        Write-TidyOutput -Message ("No items found for {0}." -f $Name)
        $script:CleanupEntries.Add([pscustomobject]@{
            Target = $Name
            Paths = @()
            TargetBytes = 0
            FreedBytes = 0
            DryRun = $script:DryRunMode
            Notes = 'Nothing to clean'
        })
        return
    }

    $targetBytes = 0L
    foreach ($candidate in $existing) {
        $targetBytes += Get-TidyPathSize -LiteralPath $candidate
    }

    $script:TotalTargetBytes += $targetBytes
    Write-TidyOutput -Message ("{0}: targeting approximately {1}." -f $Name, (Format-TidySize -Bytes $targetBytes))

    $freedBytes = 0L
    $notes = $null

    $serviceRecords = $null
    $serviceWarning = $false

    try {
        if ($RequiredServices -and $RequiredServices.Count -gt 0) {
            $serviceRecords = Stop-TidyServices -ServiceNames $RequiredServices -Context $Name
            if ($serviceRecords) {
                foreach ($record in $serviceRecords) {
                    if ($record.Action -in @('Failed', 'Missing')) {
                        $serviceWarning = $true
                        break
                    }
                }
            }
        }

        if ($script:DryRunMode) {
            $notes = 'Dry-run only'
        }
        else {
            foreach ($candidate in $existing) {
                $before = Get-TidyPathSize -LiteralPath $candidate
                if ($before -eq 0) {
                    continue
                }

                $success = Clear-TidyDirectoryContents -Path $candidate -TargetName $Name
                if (-not $success) {
                    $notes = 'Completed with warnings'
                }

                $after = Get-TidyPathSize -LiteralPath $candidate
                if ($after -lt $before) {
                    $freedBytes += ($before - $after)
                }
            }
        }
    }
    finally {
        if ($serviceRecords) {
            Start-TidyServices -ServiceRecords $serviceRecords -Context $Name
        }
    }

    if ($script:DryRunMode) {
        $freedBytes = 0
    }

    if ($serviceWarning) {
        $notes = if ($notes) { "$notes; service control warnings" } else { 'Service control warnings' }
    }

    if ($script:DryRunMode) {
        Write-TidyOutput -Message ("[DryRun] {0}: would attempt to remove {1}." -f $Name, (Format-TidySize -Bytes $targetBytes))
    }
    elseif ($freedBytes -gt 0) {
        Write-TidyOutput -Message ("{0}: reclaimed {1}." -f $Name, (Format-TidySize -Bytes $freedBytes))
    }
    else {
        Write-TidyOutput -Message ("{0}: nothing to remove." -f $Name)
    }

    $script:TotalFreedBytes += $freedBytes

    $script:CleanupEntries.Add([pscustomobject]@{
        Target = $Name
        Paths = $existing.ToArray()
        TargetBytes = $targetBytes
        FreedBytes = $freedBytes
        DryRun = $script:DryRunMode
        Notes = if ($notes) { $notes } elseif ($freedBytes -gt 0) { 'Completed' } else { 'No changes required' }
    })
}

function Invoke-TidyRecycleBinCleanup {
    if ($script:DryRunMode) {
        Write-TidyOutput -Message '[DryRun] Would clear recycle bin for all drives.'
        $script:CleanupEntries.Add([pscustomobject]@{
            Target = 'Recycle Bin'
            Paths = @('Recycle Bin')
            TargetBytes = 0
            FreedBytes = 0
            DryRun = $true
            Notes = 'Dry-run only'
        })
        return
    }

    try {
        $before = 0L
        try {
            $recycleRoot = Join-Path -Path $env:SystemDrive -ChildPath '$Recycle.Bin'
            if (Test-Path -LiteralPath $recycleRoot) {
                $before = Get-TidyPathSize -LiteralPath $recycleRoot
            }
        }
        catch {
            # Measuring recycle bin size is best effort.
        }

        Clear-RecycleBin -Force -ErrorAction Stop | Out-Null

        $after = 0L
        try {
            $recycleRoot = Join-Path -Path $env:SystemDrive -ChildPath '$Recycle.Bin'
            if (Test-Path -LiteralPath $recycleRoot) {
                $after = Get-TidyPathSize -LiteralPath $recycleRoot
            }
        }
        catch {
            # Ignore measurement errors post-cleanup.
        }

        $freed = if ($before -gt $after) { $before - $after } else { 0 }
        $script:TotalTargetBytes += $before
        $script:TotalFreedBytes += $freed

        $script:CleanupEntries.Add([pscustomobject]@{
            Target = 'Recycle Bin'
            Paths = @('Recycle Bin')
            TargetBytes = $before
            FreedBytes = $freed
            DryRun = $false
            Notes = 'Completed'
        })

        Write-TidyOutput -Message ("Recycle Bin cleared: reclaimed {0}." -f (Format-TidySize -Bytes $freed))
    }
    catch {
        $script:CleanupWarnings.Add("Failed to clear recycle bin: $($_.Exception.Message)") | Out-Null
        Write-TidyError -Message "Recycle bin cleanup failed: $($_.Exception.Message)"
    }
}

try {
    try {
        Start-Transcript -Path $transcriptPath -Force -ErrorAction SilentlyContinue | Out-Null
    }
    catch {
        # Non-fatal if transcript cannot be created.
    }

    Write-TidyLog -Level Information -Message 'Starting storage declutter and temporary cleanup.'

    if (-not (Test-TidyAdmin)) {
        $elevated = Ensure-Elevation -AllowNoElevate:$false
        if (-not $elevated) {
            throw 'Storage declutter requires elevated privileges and elevation was disabled.'
        }
    }

    $cleanupTasks = [System.Collections.Generic.List[pscustomobject]]::new()

    $userTemp = @()
    if (-not [string]::IsNullOrWhiteSpace($env:TEMP)) { $userTemp += $env:TEMP }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { $userTemp += (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Temp') }
    $cleanupTasks.Add([pscustomobject]@{
        Name = 'User temp files'
        Paths = $userTemp
        RequiredServices = @()
    })

    $cleanupTasks.Add([pscustomobject]@{
        Name = 'Windows temp files'
        Paths = @([System.IO.Path]::Combine($env:WINDIR, 'Temp'))
        RequiredServices = @()
    })

    if ($IncludePrefetch.IsPresent) {
        $cleanupTasks.Add([pscustomobject]@{
            Name = 'Prefetch cache'
            Paths = @([System.IO.Path]::Combine($env:WINDIR, 'Prefetch'))
            RequiredServices = @()
        })
    }

    if ($IncludeWindowsUpdateCache.IsPresent) {
        $cleanupTasks.Add([pscustomobject]@{
            Name = 'Windows Update download cache'
            Paths = @([System.IO.Path]::Combine($env:WINDIR, 'SoftwareDistribution', 'Download'))
            RequiredServices = @('wuauserv', 'bits', 'cryptsvc', 'UsoSvc')
        })
    }

    if ($IncludeDeliveryOptimization.IsPresent) {
        $cleanupTasks.Add([pscustomobject]@{
            Name = 'Delivery Optimization cache'
            Paths = @([System.IO.Path]::Combine($env:ProgramData, 'Microsoft', 'Windows', 'DeliveryOptimization', 'Cache'))
            RequiredServices = @('DoSvc')
        })
    }

    if ($IncludeErrorReports.IsPresent) {
        $cleanupTasks.Add([pscustomobject]@{
            Name = 'Windows error report queue'
            Paths = @([System.IO.Path]::Combine($env:ProgramData, 'Microsoft', 'Windows', 'WER', 'ReportQueue'))
            RequiredServices = @()
        })
    }

    foreach ($task in $cleanupTasks) {
        Invoke-TidyCleanupTarget -Name $task.Name -Paths $task.Paths -RequiredServices $task.RequiredServices
    }

    if ($IncludeRecycleBin.IsPresent) {
        Invoke-TidyRecycleBinCleanup
    }

    if (-not $SkipComponentCleanup.IsPresent) {
        $dismCommand = Get-TidyCommandPath -CommandName 'dism.exe'
        if ([string]::IsNullOrWhiteSpace($dismCommand)) {
            $warning = 'DISM executable not found; skipping component store cleanup.'
            Write-TidyLog -Level Warning -Message $warning
            Write-TidyOutput -Message $warning
            $script:CleanupWarnings.Add($warning) | Out-Null
        }
        else {
            Write-TidyOutput -Message 'Running DISM StartComponentCleanup (safe component store cleanup).'
            $cleanupExitCode = Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /StartComponentCleanup } -Description 'DISM StartComponentCleanup' -RequireSuccess -AcceptableExitCodes @(-2146498554)

            if ($cleanupExitCode -eq -2146498554) {
                Write-TidyOutput -Message 'DISM reported StartComponentCleanup is not applicable (often due to deployment being image-based). Continuing without error.'
            }

            if ($ResetBase.IsPresent) {
                Write-TidyOutput -Message 'Running DISM StartComponentCleanup with ResetBase (makes updates permanent).'
                $resetExitCode = Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /StartComponentCleanup /ResetBase } -Description 'DISM StartComponentCleanup /ResetBase' -RequireSuccess -AcceptableExitCodes @(-2146498554)

                if ($resetExitCode -eq -2146498554) {
                    Write-TidyOutput -Message 'DISM reported ResetBase is not applicable for this image. No action required.'
                }
            }
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping component store cleanup per operator request.'
    }

    Write-TidyOutput -Message ("Targeted total: {0}" -f (Format-TidySize -Bytes $script:TotalTargetBytes))
    if ($script:DryRunMode) {
        Write-TidyOutput -Message 'Dry-run mode: no files were deleted.'
    }
    else {
        Write-TidyOutput -Message ("Reclaimed total: {0}" -f (Format-TidySize -Bytes $script:TotalFreedBytes))
    }

    if ($script:CleanupWarnings.Count -gt 0) {
        Write-TidyOutput -Message 'Warnings encountered during cleanup:'
        foreach ($warning in $script:CleanupWarnings) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $warning)
        }
    }

    Write-TidyOutput -Message 'Storage declutter and cleanup routine completed.'
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
            Time             = (Get-Date).ToString('o')
            Success          = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
            DryRun           = $script:DryRunMode
            TotalTargetBytes = $script:TotalTargetBytes
            TotalFreedBytes  = $script:TotalFreedBytes
            Entries          = $script:CleanupEntries
            Warnings         = $script:CleanupWarnings
            TranscriptPath   = $transcriptPath
            ComponentCleanup = -not $SkipComponentCleanup.IsPresent
            ResetBase        = $ResetBase.IsPresent
            IncludeDeliveryOptimization = $IncludeDeliveryOptimization.IsPresent
            IncludeWindowsUpdateCache   = $IncludeWindowsUpdateCache.IsPresent
            IncludePrefetch             = $IncludePrefetch.IsPresent
            IncludeErrorReports         = $IncludeErrorReports.IsPresent
            IncludeRecycleBin           = $IncludeRecycleBin.IsPresent
        }

        $summary | ConvertTo-Json -Depth 6 | Out-File -FilePath $LogPath -Encoding UTF8
    }
    catch {
        # Non-fatal if log write fails.
    }
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
