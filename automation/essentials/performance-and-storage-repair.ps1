param(
    [switch] $SkipSysMainDisable,
    [switch] $ForceSysMainDisable,
    [switch] $SkipPagefileTune,
    [switch] $UseManualPagefileSizing,
    [ValidateRange(16, 1048576)]
    [int] $PagefileInitialMB = 1024,
    [ValidateRange(16, 1048576)]
    [int] $PagefileMaximumMB = 4096,
    [switch] $SkipCacheCleanup,
    [switch] $ApplyPrefetchCleanup,
    [switch] $SkipEventLogTrim,
    [switch] $SkipPowerPlanReset,
    [switch] $ActivateHighPerformancePlan,
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

function Disable-SysMainService {
    $serviceName = 'SysMain'
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        Write-TidyOutput -Message 'SysMain service not found. Skipping service changes.'
        return
    }

    Write-TidyOutput -Message ("SysMain current state: {0} ({1})." -f $service.Status, $service.StartType)

    $heuristic = Get-SysMainHeuristic -Service $service

    if (-not $ForceSysMainDisable.IsPresent) {
        if (-not $heuristic.ShouldDisable) {
            if ($heuristic.ShouldEnable) {
                try {
                    if ($service.StartType -ne 'Automatic') {
                        Invoke-TidyCommand -Command { param($name) Set-Service -Name $name -StartupType Automatic -ErrorAction Stop } -Arguments @($serviceName) -Description 'Setting SysMain startup to Automatic.' -RequireSuccess
                    }

                    if ($service.Status -ne 'Running') {
                        Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($serviceName) -Description 'Starting SysMain (SSD/RAM detected).' -RequireSuccess
                    }

                    Write-TidyOutput -Message 'SysMain left enabled (SSD + adequate RAM).'
                }
                catch {
                    $script:OperationSucceeded = $false
                    Write-TidyError -Message ("SysMain enable step failed: {0}" -f $_.Exception.Message)
                }
            }
            else {
                Write-TidyOutput -Message 'Skipping SysMain disable due to SSD/adequate RAM. (Use -ForceSysMainDisable to override.)'
            }
            return
        }
    }

    try {
        if ($service.Status -ne 'Stopped') {
            Invoke-TidyCommand -Command { param($name) Stop-Service -Name $name -Force -ErrorAction SilentlyContinue } -Arguments @($serviceName) -Description 'Stopping SysMain to curb disk usage.'
        }

        if ($service.StartType -ne 'Disabled') {
            Invoke-TidyCommand -Command { param($name) Set-Service -Name $name -StartupType Disabled -ErrorAction Stop } -Arguments @($serviceName) -Description 'Disabling SysMain service.' -RequireSuccess
        }

        Write-TidyOutput -Message 'SysMain disabled successfully.'
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("SysMain disable step failed: {0}" -f $_.Exception.Message)
    }
}

function Get-SysMainHeuristic {
    param([Parameter(Mandatory=$true)] [System.ServiceProcess.ServiceController] $Service)

    $result = [pscustomobject]@{
        ShouldDisable = $true
        ShouldEnable  = $false
        MediaType     = $null
        TotalRamGb    = $null
    }

    try {
        $computer = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
        $result.TotalRamGb = [math]::Round(($computer.TotalPhysicalMemory / 1GB), 1)
    }
    catch {
        $result.TotalRamGb = $null
    }

    $systemDrive = (Get-Item -LiteralPath $env:SystemRoot).PSDrive.Root.TrimEnd('\\')
    $systemLetter = $systemDrive.TrimEnd(':').Trim()
    try {
        $partition = Get-Partition -DriveLetter $systemLetter -ErrorAction Stop | Select-Object -First 1
        if ($partition) {
            $disk = Get-Disk -Number $partition.DiskNumber -ErrorAction Stop
            if ($disk -and $disk.MediaType) { $result.MediaType = $disk.MediaType }
        }
    }
    catch {
        $result.MediaType = $null
    }

    $isSsd = $false
    if ($result.MediaType -and $result.MediaType.ToString().ToUpperInvariant().Contains('SSD')) {
        $isSsd = $true
    }

    # Default stance: disable on HDD/low-RAM; keep enabled (and gently re-enable) on SSDs with adequate RAM.
    if ($isSsd -and $result.TotalRamGb -and $result.TotalRamGb -ge 8) {
        $result.ShouldDisable = $false
        $result.ShouldEnable = $true
    }
    elseif ($isSsd -and -not $result.TotalRamGb) {
        $result.ShouldDisable = $false
        $result.ShouldEnable = $false
    }
    elseif ($result.TotalRamGb -and $result.TotalRamGb -ge 12) {
        $result.ShouldDisable = $false
        $result.ShouldEnable = $false
    }

    return $result
}

function Configure-Pagefile {
    param([bool] $UseManualSizing)

    try {
        $computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop

        if ($UseManualSizing) {
            if ($PagefileInitialMB -gt $PagefileMaximumMB) {
                throw 'PagefileInitialMB cannot exceed PagefileMaximumMB.'
            }

            Write-TidyOutput -Message ("Enabling manual pagefile sizing: {0} MB initial, {1} MB maximum." -f $PagefileInitialMB, $PagefileMaximumMB)

            Invoke-TidyCommand -Command { param($cs) Set-CimInstance -InputObject $cs -Property @{ AutomaticManagedPagefile = $false } -ErrorAction Stop } -Arguments @($computerSystem) -Description 'Disabling automatic pagefile management.' -RequireSuccess

            $pagefileName = 'C:\\pagefile.sys'
            $settings = Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction SilentlyContinue | Where-Object { $_.Name -ieq $pagefileName }
            if ($settings) {
                foreach ($entry in $settings) {
                    Invoke-TidyCommand -Command { param($item, $initial, $maximum) Set-CimInstance -InputObject $item -Property @{ InitialSize = $initial; MaximumSize = $maximum } -ErrorAction Stop } -Arguments @($entry, $PagefileInitialMB, $PagefileMaximumMB) -Description 'Updating existing pagefile sizing.' -RequireSuccess
                }
            }
            else {
                Invoke-TidyCommand -Command { param($initial, $maximum, $name) New-CimInstance -ClassName Win32_PageFileSetting -Property @{ Name = $name; InitialSize = $initial; MaximumSize = $maximum } -ErrorAction Stop } -Arguments @($PagefileInitialMB, $PagefileMaximumMB, $pagefileName) -Description 'Creating manual pagefile entry.' -RequireSuccess
            }

            Write-TidyOutput -Message 'Manual pagefile sizing configured.'
        }
        else {
            Write-TidyOutput -Message 'Enabling automatic managed pagefile sizing.'
            Invoke-TidyCommand -Command { param($cs) Set-CimInstance -InputObject $cs -Property @{ AutomaticManagedPagefile = $true } -ErrorAction Stop } -Arguments @($computerSystem) -Description 'Setting automatic managed pagefile.' -RequireSuccess
            Write-TidyOutput -Message 'Automatic pagefile management enabled.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Pagefile configuration failed: {0}" -f $_.Exception.Message)
    }
}

function Clear-CacheDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        Write-TidyOutput -Message ("{0} not found. Skipping." -f $Label)
        return
    }

    $removed = 0
    try {
        $items = Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        foreach ($item in $items) {
            try {
                Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop
                $removed++
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyOutput -Message ("  -> Skipped '{0}': {1}" -f $item.FullName, $_.Exception.Message)
            }
        }

        Write-TidyOutput -Message ("{0}: removed {1} item(s)." -f $Label, $removed)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Failed to enumerate {0}: {1}" -f $Label, $_.Exception.Message)
    }
}

function Trim-EventLogs {
    param([int] $MaxSizeKb = 32768)

    $logs = @('System', 'Application', 'Setup')
    foreach ($log in $logs) {
        try {
            Invoke-TidyCommand -Command { param($name, $size) wevtutil sl $name "/ms:$size" } -Arguments @($log, $MaxSizeKb) -Description ("Setting max size for {0} log." -f $log) -RequireSuccess
            Invoke-TidyCommand -Command { param($name) wevtutil cl $name } -Arguments @($log) -Description ("Clearing {0} log." -f $log) -RequireSuccess
            Write-TidyOutput -Message ("{0} log trimmed to {1} KB." -f $log, $MaxSizeKb)
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Event log '{0}' trim failed: {1}" -f $log, $_.Exception.Message)
        }
    }
}

function Reset-PowerPlans {
    try {
        Invoke-TidyCommand -Command { powercfg /restoredefaultschemes } -Description 'Restoring default power schemes.' -RequireSuccess
        Write-TidyOutput -Message 'Power schemes restored to defaults.'

        if ($ActivateHighPerformancePlan.IsPresent) {
            Invoke-TidyCommand -Command { powercfg /setactive scheme_min } -Description 'Setting High Performance power plan.' -RequireSuccess
            Write-TidyOutput -Message 'High Performance power plan activated.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Power plan reset failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Performance and storage repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting performance and storage repair pack.'

    if (-not $SkipSysMainDisable.IsPresent) {
        Disable-SysMainService
    }
    else {
        Write-TidyOutput -Message 'Skipping SysMain disable per operator request.'
    }

    if (-not $SkipPagefileTune.IsPresent) {
        Configure-Pagefile -UseManualSizing:$UseManualPagefileSizing.IsPresent
    }
    else {
        Write-TidyOutput -Message 'Skipping pagefile tuning per operator request.'
    }

    if (-not $SkipCacheCleanup.IsPresent) {
        $tempPaths = @($env:TEMP, $env:TMP, "$env:SystemRoot\Temp") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        foreach ($path in $tempPaths) {
            Clear-CacheDirectory -Path $path -Label "Temp cache ($path)"
        }

        if ($ApplyPrefetchCleanup.IsPresent) {
            $prefetchPath = Join-Path -Path $env:SystemRoot -ChildPath 'Prefetch'
            Clear-CacheDirectory -Path $prefetchPath -Label 'Prefetch cache'
        }
        else {
            Write-TidyOutput -Message 'Prefetch cleanup skipped (opt-in only).'
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping temp and prefetch cleanup per operator request.'
    }

    if (-not $SkipEventLogTrim.IsPresent) {
        Trim-EventLogs
    }
    else {
        Write-TidyOutput -Message 'Skipping event log trim per operator request.'
    }

    if (-not $SkipPowerPlanReset.IsPresent) {
        Reset-PowerPlans
    }
    else {
        Write-TidyOutput -Message 'Skipping power plan reset per operator request.'
    }

    Write-TidyOutput -Message 'Performance and storage repair pack completed.'
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
    Write-TidyLog -Level Information -Message 'Performance and storage repair script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
