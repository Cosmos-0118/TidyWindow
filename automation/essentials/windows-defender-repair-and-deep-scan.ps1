param(
    [switch] $FullScan,
    [string[]] $ScanPath,
    [switch] $SkipSignatureUpdate,
    [switch] $SkipThreatScan,
    [switch] $SkipServiceHeal,
    [switch] $SkipRealtimeHeal,
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

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $timeStamp = (Get-Date).ToString('yyyyMMdd_HHmmss')
    $LogPath = Join-Path -Path $env:TEMP -ChildPath "TidyWindow_DefenderRepair_$timeStamp.json"
}
else {
    $LogPath = [System.IO.Path]::GetFullPath($LogPath)
}

$logDirectory = Split-Path -Parent $LogPath
if (-not [string]::IsNullOrWhiteSpace($logDirectory) -and -not (Test-Path -LiteralPath $logDirectory)) {
    [void](New-Item -Path $logDirectory -ItemType Directory -Force)
}

$transcriptPath = [System.IO.Path]::ChangeExtension($LogPath, '.transcript.txt')

$scanDescriptor = 'Quick'
if ($ScanPath -and $ScanPath.Count -gt 0) {
    $scanDescriptor = 'Custom'
}
elseif ($FullScan.IsPresent) {
    $scanDescriptor = 'Full'
}
if ($SkipThreatScan.IsPresent) {
    $scanDescriptor = 'Skipped'
}

$script:RunSummary = [pscustomobject]@{
    SignatureUpdateAttempted = $false
    SignatureUpdateSucceeded = $false
    SignatureUpdateError    = $null
    ServiceHealAttempted    = $false
    ServicesRestarted       = [System.Collections.Generic.List[string]]::new()
    ServiceHealFailures     = [System.Collections.Generic.List[string]]::new()
    RealTimeHealAttempted   = $false
    RealTimeHealSucceeded   = $false
    RealTimeHealError       = $null
    ScanRequested           = $scanDescriptor
    ScanCompleted           = $false
    ScanResult              = $null
    ScanError               = $null
    InitialStatus           = $null
    FinalStatus             = $null
    DryRun                  = $script:DryRunMode
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
        [switch] $RequireSuccess
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

function Get-DefenderStatusPropertyValue {
    param(
        [object] $Status,
        [string[]] $PropertyNames
    )

    if ($null -eq $Status -or -not $PropertyNames) {
        return $null
    }

    foreach ($name in $PropertyNames) {
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }

        $property = $Status.PSObject.Properties[$name]
        if ($property) {
            return $property.Value
        }
    }

    return $null
}

function Get-DefenderStatusSnapshot {
    try {
        $status = Get-MpComputerStatus -ErrorAction Stop
        if ($null -eq $status) {
            return $null
        }

        $snapshot = [ordered]@{
            AMEngineVersion               = Get-DefenderStatusPropertyValue -Status $status -PropertyNames @('AMEngineVersion')
            AntivirusEnabled              = Get-DefenderStatusPropertyValue -Status $status -PropertyNames @('AntivirusEnabled')
            RealTimeProtectionEnabled     = Get-DefenderStatusPropertyValue -Status $status -PropertyNames @('RealTimeProtectionEnabled')
            BehaviorMonitorEnabled        = Get-DefenderStatusPropertyValue -Status $status -PropertyNames @('BehaviorMonitorEnabled')
            AntivirusSignatureVersion     = Get-DefenderStatusPropertyValue -Status $status -PropertyNames @('AntivirusSignatureVersion')
            AntivirusSignatureLastUpdated = Get-DefenderStatusPropertyValue -Status $status -PropertyNames @('AntivirusSignatureLastUpdated')
            QuickScanEndTime              = Get-DefenderStatusPropertyValue -Status $status -PropertyNames @('QuickScanEndTime')
            FullScanEndTime               = Get-DefenderStatusPropertyValue -Status $status -PropertyNames @('FullScanEndTime')
            LastThreatName                = Get-DefenderStatusPropertyValue -Status $status -PropertyNames @('LastThreatName','LastThreatsFound','LastThreatDetected')
        }

        return [pscustomobject]$snapshot
    }
    catch {
        Write-TidyLog -Level Warning -Message "Failed to retrieve Defender status: $($_.Exception.Message)"
        return $null
    }
}

function Restart-DefenderService {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ServiceName,
        [Parameter(Mandatory = $true)]
        [string] $FriendlyName
    )

    try {
        $service = Get-Service -Name $ServiceName -ErrorAction Stop
    }
    catch {
        Write-TidyLog -Level Warning -Message "Service '$ServiceName' not found. Skipping restart."
        return $false
    }

    $description = "Repairing $FriendlyName service ($ServiceName)."
    $exit = Invoke-TidyCommand -Command {
        param($svcName)
        try {
            Set-Service -Name $svcName -StartupType Automatic -ErrorAction SilentlyContinue
        }
        catch {
            # It is safe to continue if policy blocks startup type changes.
        }

        $current = Get-Service -Name $svcName -ErrorAction Stop
        if ($current.Status -ne 'Running') {
            try {
                Start-Service -Name $svcName -ErrorAction Stop | Out-Null
            }
            catch {
                Write-Output "Failed to start ${svcName}: $($_.Exception.Message)"
            }
        }
        elseif (-not $current.CanStop) {
            Write-Output "Service $svcName already running; restart not supported by the Service Control Manager."
        }
        else {
            try {
                Restart-Service -Name $svcName -ErrorAction Stop | Out-Null
            }
            catch {
                Write-Output "Restart attempt for $svcName failed: $($_.Exception.Message)"
            }
        }

        Start-Sleep -Seconds 2
        $updated = Get-Service -Name $svcName -ErrorAction Stop
        "Service $svcName state: $($updated.Status)"
    } -Arguments @($ServiceName) -Description $description

    if ($script:DryRunMode) {
        return ($exit -eq 0)
    }

    try {
        $final = Get-Service -Name $ServiceName -ErrorAction Stop
        if ($final.Status -eq 'Running') {
            return $true
        }

        Write-TidyLog -Level Warning -Message "$FriendlyName ($ServiceName) is not running after remediation attempt."
        return $false
    }
    catch {
        Write-TidyLog -Level Warning -Message "Unable to verify $FriendlyName ($ServiceName) state: $($_.Exception.Message)"
        return $false
    }
}

function Enable-DefenderRealtimeProtection {
    $description = 'Ensuring Microsoft Defender real-time protection is enabled.'
    $exit = Invoke-TidyCommand -Command {
        Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction Stop
    } -Description $description -RequireSuccess
    return ($exit -eq 0)
}

try {
    try {
        Start-Transcript -Path $transcriptPath -Force -ErrorAction SilentlyContinue | Out-Null
    }
    catch {
        # Non-fatal if transcript cannot be created (e.g., already active).
    }

    Write-TidyLog -Level Information -Message 'Starting Microsoft Defender repair and deep scan toolkit.'

    if (-not (Test-TidyAdmin)) {
        $elevated = Ensure-Elevation -AllowNoElevate:$false
        if (-not $elevated) {
            throw 'Defender repair requires elevated privileges and elevation was disabled.'
        }
    }

    $updateCmd = $null
    if (-not $SkipSignatureUpdate.IsPresent) {
        $updateCmd = Get-Command -Name 'Update-MpSignature' -ErrorAction SilentlyContinue
        if (-not $updateCmd) {
            throw 'Update-MpSignature cmdlet not available. Microsoft Defender may be disabled or removed.'
        }
    }

    $scanCmd = $null
    if (-not $SkipThreatScan.IsPresent) {
        $scanCmd = Get-Command -Name 'Start-MpScan' -ErrorAction SilentlyContinue
        if (-not $scanCmd) {
            throw 'Start-MpScan cmdlet not available. Microsoft Defender may be disabled or removed.'
        }
    }

    $script:RunSummary.InitialStatus = Get-DefenderStatusSnapshot
    if ($script:RunSummary.InitialStatus) {
        Write-TidyOutput -Message 'Initial Defender status snapshot:'
        foreach ($prop in $script:RunSummary.InitialStatus.PSObject.Properties) {
            Write-TidyOutput -Message ("  {0}: {1}" -f $prop.Name, $prop.Value)
        }
    }

    if (-not $SkipServiceHeal.IsPresent) {
        $script:RunSummary.ServiceHealAttempted = $true
        $services = @(
            @{ Name = 'WinDefend'; Friendly = 'Microsoft Defender Antivirus' },
            @{ Name = 'WdNisSvc'; Friendly = 'Microsoft Defender Network Inspection' },
            @{ Name = 'SecurityHealthService'; Friendly = 'Windows Security Health' }
        )

        foreach ($svc in $services) {
            $serviceName = $svc.Name
            $friendly = $svc.Friendly

            Write-TidyOutput -Message ("Repairing {0} service." -f $friendly)
            $success = $false
            try {
                $success = Restart-DefenderService -ServiceName $serviceName -FriendlyName $friendly
            }
            catch {
                $success = $false
            }

            if ($success) {
                $label = if ($script:DryRunMode) { "$friendly ($serviceName) [dry-run]" } else { "$friendly ($serviceName)" }
                [void]$script:RunSummary.ServicesRestarted.Add($label)
            }
            else {
                $message = "$friendly ($serviceName) restart failed or was skipped."
                [void]$script:RunSummary.ServiceHealFailures.Add($message)
                if (-not $script:DryRunMode) {
                    Write-TidyLog -Level Warning -Message $message
                }
            }
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping Defender service heal per operator request.'
    }

    if (-not $SkipSignatureUpdate.IsPresent) {
        $script:RunSummary.SignatureUpdateAttempted = $true
        Write-TidyOutput -Message 'Updating Microsoft Defender signatures.'
        try {
            $sigExit = Invoke-TidyCommand -Command {
                Update-MpSignature -ErrorAction Stop
            } -Description 'Updating Defender signatures.' -RequireSuccess
            if ($sigExit -eq 0) {
                $script:RunSummary.SignatureUpdateSucceeded = $true
                if ($script:DryRunMode) {
                    $script:RunSummary.SignatureUpdateError = 'Dry-run only; command not executed.'
                }
            }
        }
        catch {
            $script:RunSummary.SignatureUpdateError = $_.Exception.Message
            throw
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping signature update per operator request.'
    }

    if (-not $SkipRealtimeHeal.IsPresent) {
        $setPreferenceCmd = Get-Command -Name 'Set-MpPreference' -ErrorAction SilentlyContinue
        if (-not $setPreferenceCmd) {
            Write-TidyOutput -Message 'Set-MpPreference cmdlet not available; skipping real-time protection remediation.'
            $script:RunSummary.RealTimeHealError = 'Set-MpPreference cmdlet unavailable.'
        }
        else {
            $script:RunSummary.RealTimeHealAttempted = $true
            Write-TidyOutput -Message 'Validating Defender real-time protection state.'
            try {
                if (Enable-DefenderRealtimeProtection) {
                    $script:RunSummary.RealTimeHealSucceeded = -not $script:DryRunMode
                    if ($script:DryRunMode) {
                        $script:RunSummary.RealTimeHealError = 'Dry-run only; command not executed.'
                    }
                }
            }
            catch {
                $script:RunSummary.RealTimeHealError = $_.Exception.Message
                Write-TidyLog -Level Warning -Message "Failed to force real-time protection on: $($_.Exception.Message)"
            }
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping real-time protection remediation per operator request.'
    }

    if (-not $SkipThreatScan.IsPresent) {
        if ($ScanPath -and $ScanPath.Count -gt 0) {
            $resolvedPaths = [System.Collections.Generic.List[string]]::new()
            foreach ($path in $ScanPath) {
                try {
                    $resolved = Resolve-Path -Path $path -ErrorAction Stop
                    foreach ($candidate in $resolved) {
                        [void]$resolvedPaths.Add($candidate.ProviderPath)
                    }
                }
                catch {
                    throw "Scan path '$path' could not be resolved: $($_.Exception.Message)"
                }
            }

            Write-TidyOutput -Message 'Running Microsoft Defender custom path scan.'
            try {
                $scanExit = Invoke-TidyCommand -Command {
                    param($paths)
                    Start-MpScan -ScanPath $paths -ErrorAction Stop | Out-Null
                } -Arguments @([string[]]$resolvedPaths.ToArray()) -Description 'Running Defender custom scan.' -RequireSuccess
                if ($scanExit -eq 0) {
                    $script:RunSummary.ScanCompleted = -not $script:DryRunMode
                    $script:RunSummary.ScanResult = if ($script:DryRunMode) { 'Custom scan planned (dry-run).' } else { 'Custom scan completed.' }
                }
            }
            catch {
                $script:RunSummary.ScanError = $_.Exception.Message
                throw
            }
        }
        elseif ($FullScan.IsPresent) {
            Write-TidyOutput -Message 'Running Microsoft Defender full system scan (this can take a while).'
            try {
                $scanExit = Invoke-TidyCommand -Command {
                    Start-MpScan -ScanType FullScan -ErrorAction Stop | Out-Null
                } -Description 'Running Defender full scan.' -RequireSuccess
                if ($scanExit -eq 0) {
                    $script:RunSummary.ScanCompleted = -not $script:DryRunMode
                    $script:RunSummary.ScanResult = if ($script:DryRunMode) { 'Full scan planned (dry-run).' } else { 'Full scan completed.' }
                }
            }
            catch {
                $script:RunSummary.ScanError = $_.Exception.Message
                throw
            }
        }
        else {
            Write-TidyOutput -Message 'Running Microsoft Defender quick scan.'
            try {
                $scanExit = Invoke-TidyCommand -Command {
                    Start-MpScan -ScanType QuickScan -ErrorAction Stop | Out-Null
                } -Description 'Running Defender quick scan.' -RequireSuccess
                if ($scanExit -eq 0) {
                    $script:RunSummary.ScanCompleted = -not $script:DryRunMode
                    $script:RunSummary.ScanResult = if ($script:DryRunMode) { 'Quick scan planned (dry-run).' } else { 'Quick scan completed.' }
                }
            }
            catch {
                $script:RunSummary.ScanError = $_.Exception.Message
                throw
            }
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping Microsoft Defender scan per operator request.'
        $script:RunSummary.ScanResult = 'Scan skipped.'
    }

    $script:RunSummary.FinalStatus = Get-DefenderStatusSnapshot
    if ($script:RunSummary.FinalStatus) {
        Write-TidyOutput -Message 'Final Defender status snapshot:'
        foreach ($prop in $script:RunSummary.FinalStatus.PSObject.Properties) {
            Write-TidyOutput -Message ("  {0}: {1}" -f $prop.Name, $prop.Value)
        }
    }

    Write-TidyOutput -Message 'Microsoft Defender repair and scan routine completed.'
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
            Time                     = (Get-Date).ToString('o')
            Success                  = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
            SignatureUpdateAttempted = $script:RunSummary.SignatureUpdateAttempted
            SignatureUpdateSucceeded = $script:RunSummary.SignatureUpdateSucceeded
            SignatureUpdateError     = $script:RunSummary.SignatureUpdateError
            ServiceHealAttempted     = $script:RunSummary.ServiceHealAttempted
            ServicesRestarted        = @($script:RunSummary.ServicesRestarted)
            ServiceHealFailures      = @($script:RunSummary.ServiceHealFailures)
            RealTimeHealAttempted    = $script:RunSummary.RealTimeHealAttempted
            RealTimeHealSucceeded    = $script:RunSummary.RealTimeHealSucceeded
            RealTimeHealError        = $script:RunSummary.RealTimeHealError
            ScanRequested            = $script:RunSummary.ScanRequested
            ScanCompleted            = $script:RunSummary.ScanCompleted
            ScanResult               = $script:RunSummary.ScanResult
            ScanError                = $script:RunSummary.ScanError
            InitialStatus            = $script:RunSummary.InitialStatus
            FinalStatus              = $script:RunSummary.FinalStatus
            DryRun                   = $script:RunSummary.DryRun
            TranscriptPath           = $transcriptPath
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
