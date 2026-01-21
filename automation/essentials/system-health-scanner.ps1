param(
    [switch] $SkipSfc,
    [switch] $SkipDism,
    [switch] $RunRestoreHealth,
    [switch] $SkipRestoreHealth,
    [switch] $ComponentCleanup,
    [switch] $AnalyzeComponentStore,
    [int] $SfcTimeoutSeconds = 3600,
    [switch] $PreferDismBeforeSfc,
    [string] $ResultPath,

    # New safety/automation options
    [switch] $DryRun,
    [switch] $CreateSystemRestorePoint,
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
$script:RestorePointCreated = $false

# Default log path if not supplied
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $time = (Get-Date).ToString('yyyyMMdd_HHmmss')
    $LogPath = Join-Path -Path $env:TEMP -ChildPath "TidyWindow_SystemHealth_$time.log"
}

# Transcript file (human-friendly) - put next to log
$transcriptPath = [System.IO.Path]::ChangeExtension($LogPath, '.transcript.txt')

# Track dry-run mode
$script:DryRunMode = $DryRun.IsPresent

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

    if ($script:DryRunMode) {
        Write-TidyOutput -Message "[DryRun] Would run: $Description"
        Write-TidyOutput -Message "[DryRun] Command: $Command $($Arguments -join ' ')"
        return 0
    }

    $output = & $Command @Arguments 2>&1
    $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { $LASTEXITCODE } else { 0 }

    # Respect numeric return values emitted by the scriptblock when LASTEXITCODE stays 0.
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

# Runs native executables with a hard timeout and captures stdout/stderr.
# Avoids rare DISM/SFC hangs (e.g., stuck contacting Windows Update or AV scanning) by killing the process if it exceeds the timeout.
function Invoke-NativeWithTimeout {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,
        [Parameter(Mandatory = $true)]
        [string] $Arguments,
        [string] $Description = 'Running native command.',
        [int] $TimeoutSeconds = 1800,
        [int[]] $AcceptableExitCodes = @(0)
    )

    if ($script:DryRunMode) {
        Write-TidyOutput -Message ("[DryRun] Would run: {0} {1}" -f $FilePath, $Arguments)
        return 0
    }

    if (-not (Test-Path -LiteralPath $FilePath)) {
        throw "Command not found: $FilePath"
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $proc.EnableRaisingEvents = $true

    Write-TidyOutput -Message $Description

    if (-not $proc.Start()) {
        throw "$Description failed to start."
    }

    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()

    $exited = $proc.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        try { $proc.Kill() } catch {}
        throw "$Description timed out after $TimeoutSeconds seconds and was terminated."
    }

    # Ensure remaining buffered output is drained before inspecting ExitCode.
    $proc.WaitForExit()
    [void][System.Threading.Tasks.Task]::WaitAll(@($stdoutTask, $stderrTask), 5000)

    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result

    $exitCode = $proc.ExitCode

    foreach ($line in ($stdout -split "`r?`n")) { if ($line) { Write-TidyOutput -Message $line } }
    foreach ($line in ($stderr -split "`r?`n")) { if ($line) { Write-TidyError -Message $line } }

    if ($AcceptableExitCodes -and -not ($AcceptableExitCodes -contains $exitCode)) {
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

    # Relaunch elevated with same bound parameters
    try {
        $scriptPath = $PSCommandPath
        if ([string]::IsNullOrWhiteSpace($scriptPath)) { $scriptPath = $MyInvocation.MyCommand.Path }

        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$scriptPath")
        foreach ($k in $PSBoundParameters.Keys) {
            $val = $PSBoundParameters[$k]
            if ($val -is [switch]) {
                if ($val.IsPresent) { $argList += "-$k" }
            }
            else {
                # Quote string-ish values
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

function New-SystemRestorePointSafe {
    param(
        [string] $Description = 'TidyWindow system-health snapshot (automatic)'
    )

    if ($script:DryRunMode) {
        Write-TidyOutput -Message "[DryRun] Would create system restore point: $Description"
        return $true
    }

    try {
        if (-not (Get-Command -Name Checkpoint-Computer -ErrorAction SilentlyContinue)) {
            Write-TidyLog -Level Warning -Message 'Checkpoint-Computer not available on this system. Skipping restore point creation.'
            return $false
        }

        Write-TidyOutput -Message "Creating system restore point: $Description"
        Checkpoint-Computer -Description $Description -RestorePointType "APPLICATION_INSTALL" -ErrorAction Stop | Out-Null
        Write-TidyOutput -Message 'Restore point created (if supported by OS and enabled).'
        return $true
    }
    catch {
        Write-TidyLog -Level Warning -Message "Failed to create restore point: $($_.Exception.Message)"
        return $false
    }
}

$shouldRunRestoreHealth = $true
if ($PSBoundParameters.ContainsKey('RunRestoreHealth')) {
    $shouldRunRestoreHealth = $RunRestoreHealth.IsPresent
}
elseif ($SkipRestoreHealth.IsPresent) {
    $shouldRunRestoreHealth = $false
}

function Invoke-DismHealthPass {
    if (-not $SkipDism.IsPresent) {
        $dismPath = Join-Path -Path $env:SystemRoot -ChildPath 'System32\dism.exe'

        Write-TidyOutput -Message 'Checking Windows component store health.'
        Invoke-NativeWithTimeout -FilePath $dismPath -Arguments '/Online /Cleanup-Image /CheckHealth' -Description 'DISM CheckHealth' -TimeoutSeconds 900 -AcceptableExitCodes @(0) | Out-Null

        Write-TidyOutput -Message 'Scanning Windows component store for corruption.'
        Invoke-NativeWithTimeout -FilePath $dismPath -Arguments '/Online /Cleanup-Image /ScanHealth' -Description 'DISM ScanHealth' -TimeoutSeconds 1800 -AcceptableExitCodes @(0) | Out-Null

        if ($shouldRunRestoreHealth) {
            Write-TidyOutput -Message 'Repairing Windows component store corruption (RestoreHealth, limit network sources).'
            $restoreExit = $null

            try {
                $restoreExit = Invoke-NativeWithTimeout -FilePath $dismPath -Arguments '/Online /Cleanup-Image /RestoreHealth /LimitAccess' -Description 'DISM RestoreHealth' -TimeoutSeconds 2700 -AcceptableExitCodes @(0, 3010)
            }
            catch {
                $exitCode = $null
                if ($_.Exception.Message -match 'exit code (-?\d+)') {
                    $exitCode = [int]$Matches[1]
                }

                # Known DISM source errors (for example, WU disabled or payload missing) benefit from a retry without /LimitAccess.
                $shouldRetryOnline = $exitCode -in @(-2146498283, -2146498298, -2146498529)
                if ($shouldRetryOnline) {
                    Write-TidyError -Message ("DISM RestoreHealth failed with exit code {0} when using /LimitAccess. Retrying with Windows Update as a source." -f $exitCode)
                    $restoreExit = Invoke-NativeWithTimeout -FilePath $dismPath -Arguments '/Online /Cleanup-Image /RestoreHealth' -Description 'DISM RestoreHealth (retry without LimitAccess)' -TimeoutSeconds 2700 -AcceptableExitCodes @(0, 3010)
                }
                else {
                    throw
                }
            }

            if ($restoreExit -eq 3010) {
                Write-TidyOutput -Message 'DISM RestoreHealth completed and requested a reboot (3010).'
            }
        }
        else {
            Write-TidyOutput -Message 'Skipping RestoreHealth per operator request.'
        }

        if ($ComponentCleanup.IsPresent) {
            Write-TidyOutput -Message 'Cleaning up superseded components.'
            Invoke-NativeWithTimeout -FilePath $dismPath -Arguments '/Online /Cleanup-Image /StartComponentCleanup' -Description 'DISM StartComponentCleanup' -TimeoutSeconds 1200 -AcceptableExitCodes @(0) | Out-Null
        }

        if ($AnalyzeComponentStore.IsPresent) {
            Write-TidyOutput -Message 'Analyzing component store (provides size and reclaim recommendations).'
            Invoke-NativeWithTimeout -FilePath $dismPath -Arguments '/Online /Cleanup-Image /AnalyzeComponentStore' -Description 'DISM AnalyzeComponentStore' -TimeoutSeconds 900 -AcceptableExitCodes @(0) | Out-Null
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping DISM checks per operator request.'
    }
}

try {
    # Start transcript and logging
    try {
        Start-Transcript -Path $transcriptPath -Force -ErrorAction SilentlyContinue | Out-Null
    }
    catch {
        # non-fatal
    }

    Write-TidyLog -Level Information -Message 'Starting Windows system health scanner.'

    # Auto-elevate if necessary (unless explicitly disabled)
    if (-not (Test-TidyAdmin)) {
        $elevated = Ensure-Elevation -AllowNoElevate:$false
        if (-not $elevated) {
            throw 'System health scan requires elevated privileges and elevation was disabled.'
        }
    }

    # Create a restore point before repairs if requested (and not already done)
    if ($CreateSystemRestorePoint.IsPresent -and -not $script:DryRunMode -and -not $script:RestorePointCreated) {
        $script:RestorePointCreated = New-SystemRestorePointSafe -Description 'TidyWindow pre-scan snapshot'
    }

    if ($PreferDismBeforeSfc.IsPresent) {
        Invoke-DismHealthPass
    }

    if (-not $SkipSfc.IsPresent) {
        Write-TidyOutput -Message ("Running System File Checker (timeout {0}s; can take 5-20+ minutes)." -f $SfcTimeoutSeconds)
        $sfcPath = Join-Path -Path $env:SystemRoot -ChildPath 'System32\sfc.exe'
        $sfcExit = Invoke-NativeWithTimeout -FilePath $sfcPath -Arguments '/scannow' -Description 'Running SFC /scannow.' -TimeoutSeconds $SfcTimeoutSeconds -AcceptableExitCodes @(0, 1, 2)

        switch ($sfcExit) {
            0 { Write-TidyOutput -Message 'SFC completed without finding integrity violations.' }
            1 { Write-TidyOutput -Message 'SFC found and repaired integrity violations.' }
            2 { Write-TidyOutput -Message 'SFC found integrity violations it could not repair. See CBS.log for details.' }
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping SFC scan per operator request.'
    }

    if (-not $PreferDismBeforeSfc.IsPresent) {
        Invoke-DismHealthPass
    }

    Write-TidyOutput -Message 'System health scan completed.'

    # Optionally create a restore point after successful repairs if one was not created earlier
    if ($CreateSystemRestorePoint.IsPresent -and -not $script:DryRunMode -and -not $script:RestorePointCreated) {
        $script:RestorePointCreated = New-SystemRestorePointSafe -Description 'TidyWindow post-scan snapshot'
    }
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
    Write-TidyLog -Level Information -Message 'System health scanner finished.'
    try { Stop-Transcript -ErrorAction SilentlyContinue } catch {}
    # Write a short run summary to the log path
    try {
        $summary = [pscustomobject]@{
            Time           = (Get-Date).ToString('o')
            Success        = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
            OutputLines    = $script:TidyOutputLines.Count
            ErrorLines     = $script:TidyErrorLines.Count
            TranscriptPath = $transcriptPath
        }
        $summary | ConvertTo-Json -Depth 3 | Out-File -FilePath $LogPath -Encoding UTF8
    }
    catch {
        # non-fatal
    }
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

