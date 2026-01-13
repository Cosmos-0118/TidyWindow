param(
    [switch] $SkipTimeZoneSync,
    [switch] $SkipLocaleReset,
    [switch] $SkipTimeServiceRepair,
    [switch] $UseFallbackNtpPeers,
    [switch] $ReportClockOffset,
    [switch] $SkipNtpReachabilityCheck,
    [string[]] $PreferredNtpPeers = @('time.windows.com,0x9'),
    [string] $TimeZoneId,
    [string] $Locale = 'en-US',
    [string] $Language = 'en-US',
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

function Invoke-TidyCommand {
    param(
        [Parameter(Mandatory = $true)][scriptblock] $Command,
        [string] $Description = 'Running command.',
        [object[]] $Arguments = @(),
        [switch] $RequireSuccess,
        [int[]] $AcceptableExitCodes = @(),
        [switch] $SkipLog
    )

    if (-not $SkipLog) {
        Write-TidyLog -Level Information -Message $Description
    }

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

function Invoke-TimeSync {
    param(
        [string[]] $Peers,
        [switch] $AllowFallback,
        [switch] $SkipReachabilityCheck
    )

    if (-not (Get-Variable -Name 'TestNetConnectionSupportsUdp' -Scope Script -ErrorAction SilentlyContinue)) {
        $script:TestNetConnectionSupportsUdp = $false
        $cmd = Get-Command -Name Test-NetConnection -ErrorAction SilentlyContinue
        if ($cmd -and $cmd.Parameters.ContainsKey('UdpPort')) {
            $script:TestNetConnectionSupportsUdp = $true
        }
    }

    if (-not (Get-Variable -Name 'ReportedUdpCheckDowngrade' -Scope Script -ErrorAction SilentlyContinue)) {
        $script:ReportedUdpCheckDowngrade = $false
    }

    $peerSets = @()
    $primaryPeers = if ($Peers -and $Peers.Count -gt 0) { $Peers } else { @('time.windows.com,0x9') }
    $peerSets += ,$primaryPeers

    if ($AllowFallback.IsPresent) {
        $peerSets += ,@('time.google.com,0x9')
        $peerSets += ,@('pool.ntp.org,0x9')
        $peerSets += ,@('time.nist.gov,0x9')
    }

    $synced = $false
    foreach ($set in $peerSets) {
        $peerList = ($set -join ' ')

        if (-not $SkipReachabilityCheck.IsPresent) {
            $reachable = @()
            foreach ($peer in $set) {
                $peerHost = ($peer -split ',')[0]
                $probe = $null
                if ($script:TestNetConnectionSupportsUdp) {
                    $probe = Test-NetConnection -ComputerName $peerHost -UdpPort 123 -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
                }
                else {
                    if (-not $script:ReportedUdpCheckDowngrade) {
                        Write-TidyOutput -Message 'UDP reachability probe unavailable on this PowerShell; falling back to TCP port 123 test.'
                        $script:ReportedUdpCheckDowngrade = $true
                    }
                    $probe = Test-NetConnection -ComputerName $peerHost -Port 123 -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
                }
                $udpOk = $false
                if ($probe -and $probe.PSObject.Properties['UdpTestSucceeded']) {
                    $udpOk = [bool]$probe.UdpTestSucceeded
                }

                $tcpOk = $false
                if ($probe -and $probe.PSObject.Properties['TcpTestSucceeded']) {
                    $tcpOk = [bool]$probe.TcpTestSucceeded
                }

                if ($udpOk -or $tcpOk) {
                    $reachable += $peerHost
                }
            }

            if (-not $reachable -or $reachable.Count -eq 0) {
                Write-TidyOutput -Message ("NTP port 123 appears blocked to peers [{0}]. Attempting sync anyway; check firewall/router for UDP 123." -f ($set -join ', '))
            }
        }

        Write-TidyOutput -Message ("Configuring NTP peers: {0}" -f $peerList)
        try {
            Invoke-TidyCommand -Command { param($peers) w32tm /config /update /manualpeerlist:$peers /syncfromflags:manual } -Arguments @($peerList) -Description 'Configuring NTP peer list.' -RequireSuccess -SkipLog | Out-Null
            Invoke-TidyCommand -Command { w32tm /resync /force } -Description 'Forcing time sync.' -RequireSuccess -AcceptableExitCodes @(0,5,13868,13874) -SkipLog | Out-Null
            $synced = $true
            break
        }
        catch {
            Write-TidyOutput -Message ("Time sync attempt failed for peers [{0}]: {1}" -f $peerList, $_.Exception.Message)
            try {
                Write-TidyOutput -Message 'Retrying time sync with rediscover.'
                Invoke-TidyCommand -Command { w32tm /resync /rediscover } -Description 'Rediscover time sources.' -RequireSuccess -AcceptableExitCodes @(0,5,13868,13874) -SkipLog | Out-Null
                $synced = $true
                break
            }
            catch {
                Write-TidyOutput -Message ("Rediscover attempt failed for peers [{0}]: {1}" -f $peerList, $_.Exception.Message)
            }
        }
    }

    if (-not $synced) {
        $script:OperationSucceeded = $false
        Write-TidyError -Message 'Time sync failed for all configured NTP peers. Verify network and firewall allow UDP/TCP 123 to public NTP servers.'
        return $false
    }

    return $true
}

function Report-ClockOffset {
    param(
        [string] $Peer = 'time.windows.com',
        [string] $Label = 'Clock offset'
    )

    try {
        Write-TidyOutput -Message ("Measuring {0} against {1}." -f $Label, $Peer)
        Invoke-TidyCommand -Command { param($p) w32tm /stripchart /computer:$p /samples:3 /dataonly } -Arguments @($Peer) -Description 'Clock offset stripchart.' -RequireSuccess -AcceptableExitCodes @(0,5) -SkipLog | Out-Null
    }
    catch {
        Write-TidyOutput -Message ("Clock offset check failed: {0}" -f $_.Exception.Message)
    }
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

function Ensure-TimeServiceReady {
    try {
        $service = Get-Service -Name 'W32Time' -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            Write-TidyOutput -Message 'Registering Windows Time service (W32Time).'
            Invoke-TidyCommand -Command { w32tm /register } -Description 'Registering W32Time service.' -RequireSuccess | Out-Null
            $service = Get-Service -Name 'W32Time' -ErrorAction SilentlyContinue
            if ($null -eq $service) {
                throw 'W32Time service unavailable after registration attempt.'
            }
        }

        if ($service.StartType -eq 'Disabled') {
            Write-TidyOutput -Message 'Setting W32Time startup type to Automatic.'
            Set-Service -Name 'W32Time' -StartupType Automatic -ErrorAction Stop
        }

        if ($service.Status -ne 'Running') {
            Write-TidyOutput -Message 'Starting Windows Time service.'
            Invoke-TidyCommand -Command { Start-Service -Name 'W32Time' -ErrorAction Stop } -Description 'Starting W32Time service.' -RequireSuccess | Out-Null
            if (-not (Wait-TidyServiceState -Name 'W32Time' -DesiredStatus 'Running' -TimeoutSeconds 15)) {
                Write-TidyOutput -Message 'W32Time did not reach Running state after start attempt.'
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Failed to prepare Windows Time service: {0}" -f $_.Exception.Message)
    }
}

function Set-SystemTimeZoneAndResync {
    try {
        Ensure-TimeServiceReady

        $targetZoneId = if (-not [string]::IsNullOrWhiteSpace($TimeZoneId)) { $TimeZoneId } else { (Get-TimeZone -ErrorAction Stop).Id }
        $availableZone = Get-TimeZone -ListAvailable | Where-Object { $_.Id -eq $targetZoneId } | Select-Object -First 1
        if ($null -eq $availableZone) {
            throw ("Time zone '{0}' not found on this system." -f $targetZoneId)
        }

        $currentZone = Get-TimeZone -ErrorAction Stop
        if ($currentZone.Id -ne $availableZone.Id) {
            Write-TidyOutput -Message ("Setting time zone to {0}." -f $availableZone.Id)
            Set-TimeZone -Id $availableZone.Id -ErrorAction Stop
        }
        else {
            Write-TidyOutput -Message ("Time zone already set to {0}; reapplying NTP sync." -f $availableZone.Id)
        }

        $syncSucceeded = Invoke-TimeSync -Peers $PreferredNtpPeers -AllowFallback:$UseFallbackNtpPeers -SkipReachabilityCheck:$SkipNtpReachabilityCheck
        if (-not $syncSucceeded) {
            $script:OperationSucceeded = $false
            Write-TidyError -Message 'NTP sync did not complete successfully.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Time zone/NTP resync failed: {0}" -f $_.Exception.Message)
    }
}

function Reset-LocaleAndLanguage {
    try {
        $targetLocale = if (-not [string]::IsNullOrWhiteSpace($Locale)) { $Locale } else { 'en-US' }
        $targetLanguage = if (-not [string]::IsNullOrWhiteSpace($Language)) { $Language } else { 'en-US' }

        Write-TidyOutput -Message ("Setting system locale and culture to {0}." -f $targetLocale)
        Set-WinSystemLocale -SystemLocale $targetLocale -ErrorAction Stop
        Set-Culture -CultureInfo $targetLocale -ErrorAction Stop

        Write-TidyOutput -Message ("Resetting user language list to {0}." -f $targetLanguage)
        $languageList = New-WinUserLanguageList -Language $targetLanguage
        Set-WinUserLanguageList -LanguageList $languageList -Force -ErrorAction Stop

        try {
            Set-WinUILanguageOverride -Language $targetLanguage -ErrorAction Stop
        }
        catch {
            Write-TidyOutput -Message ("UI language override not applied: {0}" -f $_.Exception.Message)
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Locale/language reset failed: {0}" -f $_.Exception.Message)
    }
}

function Repair-W32TimeService {
    try {
        Ensure-TimeServiceReady

        Write-TidyOutput -Message 'Configuring Windows Time peers and flags.'
        $syncSucceeded = Invoke-TimeSync -Peers $PreferredNtpPeers -AllowFallback:$UseFallbackNtpPeers -SkipReachabilityCheck:$SkipNtpReachabilityCheck
        if (-not $syncSucceeded) {
            $script:OperationSucceeded = $false
            Write-TidyError -Message 'NTP sync did not complete successfully during Windows Time service repair.'
        }

        Write-TidyOutput -Message 'Restarting Windows Time service.'
        Invoke-TidyCommand -Command { Restart-Service -Name 'W32Time' -Force -ErrorAction Stop } -Description 'Restarting W32Time service.' -RequireSuccess | Out-Null
        if (-not (Wait-TidyServiceState -Name 'W32Time' -DesiredStatus 'Running' -TimeoutSeconds 20)) {
            Write-TidyOutput -Message 'W32Time did not reach Running state after restart.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Windows Time repair failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Time and region repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting Time and Region repair pack.'

    $offsetPeer = if ($PreferredNtpPeers -and $PreferredNtpPeers.Count -gt 0) { ($PreferredNtpPeers[0] -split ',')[0] } else { 'time.windows.com' }
    if ($ReportClockOffset.IsPresent) {
        Report-ClockOffset -Peer $offsetPeer -Label 'Clock offset (before repair)'
    }

    if (-not $SkipTimeZoneSync.IsPresent) {
        Set-SystemTimeZoneAndResync
    }
    else {
        Write-TidyOutput -Message 'Skipping time zone and NTP sync per operator request.'
    }

    if (-not $SkipLocaleReset.IsPresent) {
        Reset-LocaleAndLanguage
    }
    else {
        Write-TidyOutput -Message 'Skipping locale and language reset per operator request.'
    }

    if (-not $SkipTimeServiceRepair.IsPresent) {
        Repair-W32TimeService
    }
    else {
        Write-TidyOutput -Message 'Skipping Windows Time service repair per operator request.'
    }

    if ($ReportClockOffset.IsPresent) {
        Report-ClockOffset -Peer $offsetPeer -Label 'Clock offset (after repair)'
    }

    Write-TidyOutput -Message 'Time and region repair completed.'
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message ("Time and region repair failed: {0}" -f $_.Exception.Message)
}
finally {
    Save-TidyResult
}
