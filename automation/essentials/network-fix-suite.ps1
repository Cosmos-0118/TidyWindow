param(
    [string] $TargetHost = '8.8.8.8',
    [int] $LatencySamples = 8,
    [int] $TcpPort,
    [switch] $SkipTraceroute,
    [switch] $SkipPathPing,
    [switch] $DiagnosticsOnly,
    [switch] $SkipDnsRegistration,
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
$script:ActionsPerformed = [System.Collections.Generic.List[string]]::new()
$script:ActionsFailed = [System.Collections.Generic.List[string]]::new()
$script:ActionsSkipped = [System.Collections.Generic.List[string]]::new()
$script:ActionsPerformedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$script:ActionsFailedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$script:ActionsSkippedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$script:PingExitCode = $null
$script:TracerouteExitCode = $null
$script:PathpingExitCode = $null
$script:LatencySamplesEffective = [Math]::Max(1, $LatencySamples)
$script:DiagnosticsSummary = [System.Collections.Generic.List[string]]::new()
$script:AdapterSnapshotCaptured = $false
$script:TestConnectionSucceeded = $false
$script:TargetResolvedAddresses = [System.Collections.Generic.List[string]]::new()
$script:TargetHostLabel = $TargetHost
$script:EffectiveTcpPort = $null
$script:ExecutionStart = Get-Date
$script:SummaryEmitted = $false

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

    # Clear sticky non-zero LASTEXITCODE from prior native calls.
    if (Test-Path -Path 'variable:LASTEXITCODE') {
        $global:LASTEXITCODE = 0
    }

    $output = & $Command @Arguments 2>&1
    $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { $LASTEXITCODE } else { 0 }

    # If scriptblock emitted a numeric exit code while LASTEXITCODE stayed 0, honor it.
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
        throw "$Description failed with exit code $exitCode."
    }

    return $exitCode
}

function Test-TidyAdmin {
    return [bool](New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-TidyIpAddress {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    [System.Net.IPAddress] $parsed = $null
    return [System.Net.IPAddress]::TryParse($Value, [ref]$parsed)
}

function Register-TidyAction {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Success', 'Failed', 'Skipped')]
        [string] $Status,
        [string] $Details
    )

    switch ($Status) {
        'Success' {
            if ($script:ActionsPerformedSet.Add($Name)) {
                $script:ActionsPerformed.Add($Name)
            }
        }
        'Failed' {
            if ($script:ActionsFailedSet.Add($Name)) {
                $script:ActionsFailed.Add(($Details) ? "${Name}: $Details" : $Name)
            }

            # Ensure overall status is marked unsuccessful when any action fails.
            $script:OperationSucceeded = $false
        }
        'Skipped' {
            if ($script:ActionsSkippedSet.Add($Name)) {
                $script:ActionsSkipped.Add(($Details) ? "${Name}: $Details" : $Name)
            }
        }
    }
}

function Resolve-TidyCommandResult {
    param(
        [Parameter(Mandatory = $false)]
        [object] $InputObject
    )

    $result = [pscustomobject]@{
        ExitCode      = $null
        Message       = $null
        Indeterminate = $false
    }

    if ($null -eq $InputObject) {
        return $result
    }

    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $items = @($InputObject)
        if ($items.Count -eq 0) {
            return $result
        }

        return Resolve-TidyCommandResult -InputObject $items[$items.Count - 1]
    }

    if ($InputObject -is [int] -or $InputObject -is [long]) {
        $result.ExitCode = [int]$InputObject
        return $result
    }

    if ($InputObject -is [double] -or $InputObject -is [decimal]) {
        $result.ExitCode = [int][Math]::Round([double]$InputObject)
        return $result
    }

    $text = Convert-TidyLogMessage -InputObject $InputObject
    if ([string]::IsNullOrWhiteSpace($text)) {
        $result.ExitCode = 0
        return $result
    }

    $trimmed = $text.Trim()
    $lower = $trimmed.ToLowerInvariant()

    $failureTokens = @('error', 'fail', 'failed', 'denied', 'cannot', 'not recognized', 'not recognised', 'not found', 'unrecognized', 'unrecognised', 'refused')
    foreach ($token in $failureTokens) {
        if ($lower.Contains($token)) {
            $result.ExitCode = 1
            $result.Message = $trimmed
            return $result
        }
    }

    $numericMatch = [System.Text.RegularExpressions.Regex]::Match($trimmed, '(-?\d+)\s*$')
    if ($numericMatch.Success) {
        $parsed = 0
        if ([int]::TryParse($numericMatch.Groups[1].Value, [ref]$parsed)) {
            $result.ExitCode = $parsed
            if ($trimmed.Length -gt $numericMatch.Groups[1].Index) {
                $result.Message = $trimmed
            }
            return $result
        }
    }

    $successTokens = @('ok', 'success', 'successful', 'sucessfully', 'completed', 'complete', 'windows ip configuration', 'purge and preload')
    foreach ($token in $successTokens) {
        if ($lower.Contains($token)) {
            $result.ExitCode = 0
            $result.Message = $trimmed
            return $result
        }
    }

    $result.ExitCode = 0
    $result.Message = $trimmed
    $result.Indeterminate = $true
    return $result
}

function Invoke-TidyNetworkAction {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Action,
        [Parameter(Mandatory = $true)]
        [scriptblock] $Command,
        [object[]] $Arguments = @(),
        [string] $Description = 'Running network command.',
        [switch] $RequireSuccess
    )

    try {
        $rawResult = Invoke-TidyCommand -Command $Command -Arguments $Arguments -Description $Description
    }
    catch {
        $message = $_.Exception.Message
        Register-TidyAction -Name $Action -Status Failed -Details $message

        if ($RequireSuccess) {
            throw
        }

        if (-not [string]::IsNullOrWhiteSpace($message)) {
            Write-TidyError -Message ("{0} failed: {1}" -f $Action, $message)
        }

        return $null
    }

    $resolved = Resolve-TidyCommandResult -InputObject $rawResult
    $exitCode = if ($null -eq $resolved.ExitCode) { 0 } else { [int]$resolved.ExitCode }
    $detailMessage = $resolved.Message

    if ($exitCode -ne 0) {
        $detailText = if ([string]::IsNullOrWhiteSpace($detailMessage)) { "Exit code $exitCode" } else { $detailMessage }
        Register-TidyAction -Name $Action -Status Failed -Details $detailText

        if ($RequireSuccess) {
            $errorText = if ([string]::IsNullOrWhiteSpace($detailMessage)) { "exit code $exitCode" } else { $detailMessage }
            throw "$Description failed: $errorText"
        }
    }
    else {
        Register-TidyAction -Name $Action -Status Success

        if ($resolved.Indeterminate -and -not [string]::IsNullOrWhiteSpace($detailMessage)) {
            $script:DiagnosticsSummary.Add("${Action}: $detailMessage")
        }
    }

    return $exitCode
}

function Write-TidyNetworkSummary {
    if ($script:SummaryEmitted) {
        return
    }

    $script:SummaryEmitted = $true
    $elapsed = (Get-Date) - $script:ExecutionStart
    Write-TidyOutput -Message '--- Network remediation summary ---'
    Write-TidyOutput -Message ("Target host: {0}" -f $script:TargetHostLabel)
    Write-TidyOutput -Message ("Diagnostics duration: {0:g}" -f $elapsed)

    if ($script:TargetResolvedAddresses.Count -gt 0) {
        Write-TidyOutput -Message ("Resolved addresses ({0}):" -f $script:TargetResolvedAddresses.Count)
        foreach ($addr in $script:TargetResolvedAddresses | Sort-Object -Unique) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $addr)
        }
    }

    if ($script:ActionsPerformed.Count -gt 0) {
        Write-TidyOutput -Message ("Completed actions ({0}):" -f $script:ActionsPerformed.Count)
        foreach ($action in $script:ActionsPerformed) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $action)
        }
    }

    if ($script:ActionsSkipped.Count -gt 0) {
        Write-TidyOutput -Message ("Skipped actions ({0}):" -f $script:ActionsSkipped.Count)
        foreach ($action in $script:ActionsSkipped) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $action)
        }
    }

    if ($script:ActionsFailed.Count -gt 0) {
        Write-TidyOutput -Message ("Failures ({0}):" -f $script:ActionsFailed.Count)
        foreach ($action in $script:ActionsFailed) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $action)
        }
    }

    $adapterStatus = if ($script:AdapterSnapshotCaptured) { 'Captured' } else { 'Unavailable' }
    Write-TidyOutput -Message ("Adapter statistics: {0}" -f $adapterStatus)

    $connectStatus = if ($script:TestConnectionSucceeded) { 'TCP probe succeeded' } else { 'No TCP response' }
    $portLabel = if ($script:EffectiveTcpPort) { $script:EffectiveTcpPort } else { 'n/a' }
    Write-TidyOutput -Message ("Connectivity probe: {0} (port {1})" -f $connectStatus, $portLabel)

    $pingLabel = if ($null -eq $script:PingExitCode) { 'Not attempted' } elseif ($script:PingExitCode -eq 0) { 'Success' } else { "Exit code $($script:PingExitCode)" }
    Write-TidyOutput -Message ("Ping status: {0} ({1} samples)" -f $pingLabel, $script:LatencySamplesEffective)

    $traceLabel = if ($SkipTraceroute.IsPresent) { 'Skipped via switch' } elseif ($null -eq $script:TracerouteExitCode) { 'Not attempted' } elseif ($script:TracerouteExitCode -eq 0) { 'Success' } else { "Exit code $($script:TracerouteExitCode)" }
    Write-TidyOutput -Message ("Traceroute status: {0}" -f $traceLabel)

    $pathPingLabel = if ($SkipPathPing.IsPresent) { 'Skipped via switch' } elseif ($null -eq $script:PathpingExitCode) { 'Not attempted' } elseif ($script:PathpingExitCode -eq 0) { 'Success' } else { "Exit code $($script:PathpingExitCode)" }
    Write-TidyOutput -Message ("PathPing status: {0}" -f $pathPingLabel)

    if ($script:DiagnosticsSummary.Count -gt 0) {
        Write-TidyOutput -Message 'Diagnostic notes:'
        foreach ($item in $script:DiagnosticsSummary) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $item)
        }
    }
}

$TargetHost = if ($null -ne $TargetHost) { $TargetHost.Trim() } else { $null }
if ([string]::IsNullOrWhiteSpace($TargetHost)) {
    throw 'TargetHost cannot be empty.'
}

$maxSamples = 100
if ($LatencySamples -lt 1 -or $LatencySamples -gt $maxSamples) {
    $script:DiagnosticsSummary.Add(("LatencySamples adjusted from {0} to within 1-{1}." -f $LatencySamples, $maxSamples))
}

$LatencySamples = [Math]::Max(1, [Math]::Min($LatencySamples, $maxSamples))
$script:LatencySamplesEffective = $LatencySamples
$script:TargetHostLabel = $TargetHost
$script:EffectiveTcpPort = if ($TcpPort -gt 0) { $TcpPort } else { $null }

if (-not $script:EffectiveTcpPort) {
    $wellKnownDnsResolvers = @(
        '8.8.8.8', '8.8.4.4',
        '1.1.1.1', '1.0.0.1',
        '9.9.9.9', '149.112.112.112',
        '208.67.222.222', '208.67.220.220'
    )

    if (Test-TidyIpAddress -Value $TargetHost -and $wellKnownDnsResolvers -contains $TargetHost) {
        $script:EffectiveTcpPort = 53
        $script:DiagnosticsSummary.Add('Connectivity probe port auto-set to 53 for DNS resolver targets.')
    }
    else {
        $script:EffectiveTcpPort = 443
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Network fix suite requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message ("Starting advanced network remediation for target '{0}'." -f $TargetHost)

    $dnsName = $TargetHost
    if (Test-TidyIpAddress -Value $TargetHost) {
        $dnsName = $null
    }

    if ($DiagnosticsOnly.IsPresent) {
        Write-TidyOutput -Message 'Diagnostics-only mode: remediation steps skipped.'
        $script:DiagnosticsSummary.Add('Diagnostics-only mode enabled; remediation commands were not executed.')
        foreach ($action in @(
                'ARP cache reset',
                'NetBIOS cache reload',
                'NetBIOS re-registration',
                'IPv4 neighbor cache reset',
                'TCP heuristics reset',
                'TCP auto-tuning normalization',
                'DNS registration')) {
            Register-TidyAction -Name $action -Status Skipped -Details 'DiagnosticsOnly flag set.'
        }
    }
    else {
        Write-TidyOutput -Message 'Clearing ARP cache.'
        [void](Invoke-TidyNetworkAction -Action 'ARP cache reset' -Command { arp -d * } -Description 'Clearing ARP table.' -RequireSuccess)

        Write-TidyOutput -Message 'Reloading NetBIOS name cache.'
        [void](Invoke-TidyNetworkAction -Action 'NetBIOS cache reload' -Command { nbtstat -R } -Description 'nbtstat -R' -RequireSuccess)

        Write-TidyOutput -Message 'Re-registering NetBIOS names.'
        [void](Invoke-TidyNetworkAction -Action 'NetBIOS re-registration' -Command { nbtstat -RR } -Description 'nbtstat -RR' -RequireSuccess)

        Write-TidyOutput -Message 'Resetting IPv4 neighbor cache.'
        [void](Invoke-TidyNetworkAction -Action 'IPv4 neighbor cache reset' -Command { netsh interface ip delete arpcache } -Description 'netsh interface ip delete arpcache' -RequireSuccess)

        Write-TidyOutput -Message 'Resetting TCP global heuristics to defaults.'
        [void](Invoke-TidyNetworkAction -Action 'TCP heuristics reset' -Command { netsh interface tcp set heuristics disabled } -Description 'Disable TCP heuristics.')
        [void](Invoke-TidyNetworkAction -Action 'TCP auto-tuning normalization' -Command { netsh interface tcp set global autotuninglevel=normal } -Description 'Restore TCP auto-tuning.')

        if (-not $SkipDnsRegistration.IsPresent) {
            Write-TidyOutput -Message 'Registering DNS records with DHCP server.'
            $dnsExit = Invoke-TidyNetworkAction -Action 'DNS registration' -Command { ipconfig /registerdns } -Description 'ipconfig /registerdns'
            if ($dnsExit -ne 0) {
                $script:DiagnosticsSummary.Add('DNS registration encountered errors; review ipconfig output.')
            }
        }
        else {
            Write-TidyOutput -Message 'Skipping DNS registration per operator request.'
            Register-TidyAction -Name 'DNS registration' -Status Skipped -Details 'SkipDnsRegistration flag set.'
        }
    }

    Write-TidyOutput -Message 'Capturing adapter link statistics.'
    $adapterExit = Invoke-TidyNetworkAction -Action 'Adapter statistics snapshot' -Command { Get-NetAdapterStatistics -IncludeHidden } -Description 'Adapter statistics snapshot.'
    if ($null -eq $adapterExit -or $adapterExit -eq 0) {
        $script:AdapterSnapshotCaptured = $true
    }
    else {
        $script:DiagnosticsSummary.Add('Unable to capture adapter statistics.')
    }

    if ($dnsName) {
        Write-TidyOutput -Message ("Resolving DNS for {0}" -f $dnsName)
        try {
            $records = Resolve-DnsName -Name $dnsName -Type A,AAAA -ErrorAction Stop
            if ($records) {
                foreach ($record in $records) {
                    if ($record.IPAddress) {
                        $script:TargetResolvedAddresses.Add($record.IPAddress)
                    }

                    $recordText = $record | Format-List | Out-String
                    foreach ($line in ($recordText -split '\r?\n')) {
                        if (-not [string]::IsNullOrWhiteSpace($line)) {
                            Write-TidyOutput -Message $line
                        }
                    }
                }
            }
            else {
                Write-TidyOutput -Message 'No A/AAAA records returned.'
                $script:DiagnosticsSummary.Add("DNS query returned no host records for $dnsName.")
            }

            Register-TidyAction -Name 'DNS resolution' -Status Success
        }
        catch {
            $message = $_.Exception.Message
            Write-TidyError -Message ("DNS resolution failed: {0}" -f $message)
            Register-TidyAction -Name 'DNS resolution' -Status Failed -Details $message
            $script:DiagnosticsSummary.Add("DNS resolution failed: $message")
        }
    }
    else {
        Register-TidyAction -Name 'DNS resolution' -Status Skipped -Details 'Target specified as IP address.'
    }

    Write-TidyOutput -Message ("Testing connection to {0} on TCP port {1}." -f $TargetHost, $script:EffectiveTcpPort)
    try {
        $testResult = Test-NetConnection -ComputerName $TargetHost -Port $script:EffectiveTcpPort -InformationLevel Detailed -ErrorAction Stop
        $formatted = ($testResult | Format-List | Out-String)
        foreach ($line in ($formatted -split '\r?\n')) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                Write-TidyOutput -Message $line
            }
        }

        if ($testResult.TcpTestSucceeded) {
            $script:TestConnectionSucceeded = $true
        }
        else {
            $script:DiagnosticsSummary.Add('TCP connectivity test did not succeed.')
        }

        Register-TidyAction -Name 'Connectivity test' -Status Success
    }
    catch {
        $message = $_.Exception.Message
        Write-TidyError -Message ("Test-NetConnection failed: {0}" -f $message)
        Register-TidyAction -Name 'Connectivity test' -Status Failed -Details $message
        $script:DiagnosticsSummary.Add("Connectivity test failed: $message")
    }

    Write-TidyOutput -Message ("Running latency sample ({0} pings)." -f $LatencySamples)
    $script:PingExitCode = Invoke-TidyNetworkAction -Action 'Latency sweep (ping)' -Command { param($computerName, $count) ping.exe -n $count $computerName } -Arguments @($TargetHost, $LatencySamples) -Description 'ping sweep.'
    if ($null -eq $script:PingExitCode) {
        $script:PingExitCode = -1
    }
    if ($script:PingExitCode -ne 0 -and $null -ne $script:PingExitCode) {
        Write-TidyLog -Level Warning -Message ("Ping sweep to {0} returned exit code {1}." -f $TargetHost, $script:PingExitCode)
        $script:DiagnosticsSummary.Add("Ping sweep to $TargetHost failed (exit code $($script:PingExitCode)).")
    }

    if (-not $SkipTraceroute.IsPresent) {
        Write-TidyOutput -Message 'Tracing network route.'
        $tracerouteArgs = @('-d', '-h', 15, '-w', 2000, $TargetHost)
        $script:TracerouteExitCode = Invoke-TidyNetworkAction -Action 'Traceroute' -Command { param([string[]] $args) tracert.exe @args } -Arguments $tracerouteArgs -Description 'tracert execution with bounded hops and timeouts.'
        if ($null -eq $script:TracerouteExitCode) {
            $script:TracerouteExitCode = -1
        }
        if ($script:TracerouteExitCode -ne 0 -and $null -ne $script:TracerouteExitCode) {
            $script:DiagnosticsSummary.Add('Traceroute reported errors; inspect hop details above.')
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping traceroute per operator request.'
        Register-TidyAction -Name 'Traceroute' -Status Skipped -Details 'SkipTraceroute flag set.'
    }

    if (-not $SkipPathPing.IsPresent) {
        Write-TidyOutput -Message 'Running pathping for loss analysis (this can take several minutes).'
        $pathPingArgs = @('-d', '-h', 15, '-w', 2000, $TargetHost)
        $script:PathpingExitCode = Invoke-TidyNetworkAction -Action 'PathPing' -Command { param([string[]] $args) pathping.exe @args } -Arguments $pathPingArgs -Description 'pathping execution with bounded hops and timeouts.'
        if ($null -eq $script:PathpingExitCode) {
            $script:PathpingExitCode = -1
        }
        if ($script:PathpingExitCode -ne 0 -and $null -ne $script:PathpingExitCode) {
            $script:DiagnosticsSummary.Add('PathPing reported transmission loss or errors.')
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping pathping per operator request.'
        Register-TidyAction -Name 'PathPing' -Status Skipped -Details 'SkipPathPing flag set.'
    }

    Write-TidyOutput -Message 'Dumping refreshed ARP table.'
    [void](Invoke-TidyNetworkAction -Action 'ARP table snapshot' -Command { arp -a } -Description 'arp -a snapshot.')

    Write-TidyNetworkSummary

    Write-TidyOutput -Message 'Advanced network routine completed.'
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
    if (-not $script:SummaryEmitted) {
        Write-TidyNetworkSummary
    }

    Save-TidyResult
    Write-TidyLog -Level Information -Message 'Network fix suite finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

