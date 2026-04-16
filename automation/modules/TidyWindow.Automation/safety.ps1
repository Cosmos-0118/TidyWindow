
# safety.ps1 — Shared safety primitives for all TidyWindow automation scripts.
# Provides safe service management, backup/restore, and guarded operations.

function Invoke-TidySafeServiceRestart {
    <#
    .SYNOPSIS
        Stops a service, runs a repair action, then guarantees the service is restarted.
    .DESCRIPTION
        Captures the original service state, stops the service if running, executes the
        repair scriptblock, then restores the service to its original state. If the
        restart fails after two retries, it throws so the caller knows the system
        is in a broken state.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]   $ServiceName,
        [Parameter(Mandatory)] [scriptblock] $RepairAction,
        [int]    $StopTimeoutSeconds  = 30,
        [int]    $StartTimeoutSeconds = 30,
        [switch] $Force
    )

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-TidyLog -Level Warning -Message "Service '$ServiceName' not found on this system. Skipping."
        return
    }

    $originalStatus = $svc.Status

    # Stop the service if it's running.
    if ($originalStatus -eq 'Running') {
        Write-TidyLog -Level Information -Message "Stopping service '$ServiceName'..."
        try {
            Stop-Service -Name $ServiceName -Force:$Force -ErrorAction Stop
            $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($StopTimeoutSeconds))
        }
        catch {
            Write-TidyLog -Level Warning -Message "Failed to stop '$ServiceName': $($_.Exception.Message)"
            throw
        }
    }

    # Run the repair action.
    $repairError = $null
    try {
        & $RepairAction
    }
    catch {
        $repairError = $_
        Write-TidyLog -Level Warning -Message "Repair action failed for '$ServiceName': $($_.Exception.Message)"
    }

    # Always attempt to restart the service if it was originally running.
    if ($originalStatus -eq 'Running') {
        $started = $false
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                Start-Service -Name $ServiceName -ErrorAction Stop
                $svc.Refresh()
                $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds($StartTimeoutSeconds))
                $started = $true
                Write-TidyLog -Level Information -Message "Service '$ServiceName' restarted successfully."
                break
            }
            catch {
                Write-TidyLog -Level Warning -Message "Restart attempt $attempt for '$ServiceName' failed: $($_.Exception.Message)"
                if ($attempt -lt 3) {
                    Start-Sleep -Seconds 2
                }
            }
        }

        if (-not $started) {
            throw "CRITICAL: Failed to restart service '$ServiceName' after 3 attempts. Manual intervention required."
        }
    }

    if ($repairError) {
        throw $repairError
    }
}

function Invoke-TidySafeServiceStop {
    <#
    .SYNOPSIS
        Safely stops a service and returns its original state for later restoration.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ServiceName,
        [int] $TimeoutSeconds = 30,
        [switch] $Force
    )

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { return $null }

    $state = [pscustomobject]@{
        Name           = $ServiceName
        OriginalStatus = $svc.Status
        StartType      = $svc.StartType
    }

    if ($svc.Status -eq 'Running') {
        Stop-Service -Name $ServiceName -Force:$Force -ErrorAction Stop
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($TimeoutSeconds))
    }

    return $state
}

function Restore-TidyServiceState {
    <#
    .SYNOPSIS
        Restores a service to its previously captured state.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [psobject] $State,
        [int] $TimeoutSeconds = 30
    )

    if (-not $State) { return }

    $svc = Get-Service -Name $State.Name -ErrorAction SilentlyContinue
    if (-not $svc) { return }

    if ($State.OriginalStatus -eq 'Running' -and $svc.Status -ne 'Running') {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                Start-Service -Name $State.Name -ErrorAction Stop
                $svc.Refresh()
                $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds($TimeoutSeconds))
                return
            }
            catch {
                if ($attempt -lt 3) { Start-Sleep -Seconds 2 }
            }
        }
        Write-TidyLog -Level Error -Message "Failed to restore service '$($State.Name)' to Running state."
    }
}

function Backup-TidyRegistryKey {
    <#
    .SYNOPSIS
        Exports a registry key to a .reg file before modification.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [Alias('Path')] [string] $KeyPath,
        [string] $BackupDirectory,
        [string] $FeatureName = 'Registry',
        [string] $Label = 'backup'
    )

    if ([string]::IsNullOrWhiteSpace($BackupDirectory)) {
        $BackupDirectory = Get-TidyBackupDirectory -FeatureName $FeatureName
    }

    if (-not (Test-Path -LiteralPath $BackupDirectory)) {
        New-Item -Path $BackupDirectory -ItemType Directory -Force | Out-Null
    }

    $safeName = ($KeyPath -replace '[\\/:*?"<>|]', '_') + "_$Label"
    $backupFile = Join-Path -Path $BackupDirectory -ChildPath "$safeName.reg"

    try {
        $regKeyPath = $KeyPath -replace '^Registry::', '' -replace '^HKCU:\\', 'HKEY_CURRENT_USER\' -replace '^HKLM:\\', 'HKEY_LOCAL_MACHINE\'
        reg export $regKeyPath $backupFile /y 2>$null | Out-Null
        Write-TidyLog -Level Information -Message "Registry backup saved: $backupFile"
        return $backupFile
    }
    catch {
        Write-TidyLog -Level Warning -Message "Registry backup failed for '$KeyPath': $($_.Exception.Message)"
        return $null
    }
}

function Test-TidyGroupPolicyManaged {
    <#
    .SYNOPSIS
        Checks whether a specific registry path is likely managed by Group Policy.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $RegistryPath)

    $gpPrefixes = @(
        'HKLM:\SOFTWARE\Policies\',
        'HKCU:\SOFTWARE\Policies\',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\'
    )

    foreach ($prefix in $gpPrefixes) {
        if ($RegistryPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Wait-TidyServiceStatus {
    <#
    .SYNOPSIS
        Waits for a service to reach a target status with timeout.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ServiceName,
        [Parameter(Mandatory)] [string] $TargetStatus,
        [int] $TimeoutSeconds = 30
    )

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { return $false }

    try {
        $svc.WaitForStatus($TargetStatus, [TimeSpan]::FromSeconds($TimeoutSeconds))
        return $true
    }
    catch {
        return $false
    }
}

function Invoke-TidyNativeCommand {
    <#
    .SYNOPSIS
        Runs a native command with timeout protection.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]   $FilePath,
        [string]   $Arguments = '',
        [int]      $TimeoutSeconds = 120,
        [int[]]    $AcceptableExitCodes = @(0)
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi

    try {
        $null = $proc.Start()
        $stdout = $proc.StandardOutput.ReadToEnd()
        $stderr = $proc.StandardError.ReadToEnd()

        if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
            try { $proc.Kill($true) } catch { }
            return [pscustomobject]@{
                ExitCode = -1
                Output   = $stdout
                Error    = "Process timed out after ${TimeoutSeconds}s"
                TimedOut = $true
            }
        }

        return [pscustomobject]@{
            ExitCode = $proc.ExitCode
            Output   = $stdout
            Error    = $stderr
            TimedOut = $false
            Success  = $AcceptableExitCodes -contains $proc.ExitCode
        }
    }
    finally {
        $proc.Dispose()
    }
}

function Get-TidyBackupDirectory {
    <#
    .SYNOPSIS
        Returns a timestamped backup directory for a given feature name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $FeatureName
    )

    $root = Get-TidyProgramDataDirectory
    $backupRoot = Join-Path -Path $root -ChildPath 'Backups' | Join-Path -ChildPath $FeatureName
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $dir = Join-Path -Path $backupRoot -ChildPath $timestamp

    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
    }

    return $dir
}
