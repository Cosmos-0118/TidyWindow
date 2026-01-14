param(
    [switch] $IncludeAdapterRefresh,
    [switch] $IncludeDhcpRenew,
    [switch] $SkipWinsockReset,
    [switch] $SkipIpReset,
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
$script:DiagnosticsSummary = [System.Collections.Generic.List[string]]::new()
$script:ExecutionStart = Get-Date
$script:SummaryEmitted = $false
$script:DnsFlushSucceeded = $false
$script:ArpFlushSucceeded = $false
$script:TcpResetSucceeded = $false
$script:WinsockResetAttempted = $false
$script:WinsockResetSucceeded = $false
$script:WinsockResetStatus = 'Not attempted'
$script:IpResetAttempted = $false
$script:IpResetSucceeded = $false
$script:IpResetStatus = 'Not attempted'
$script:IpResetLogPath = $null
$script:RebootRecommended = $false
$script:AdaptersRestarted = [System.Collections.Generic.List[string]]::new()
$script:AdaptersFailed = [System.Collections.Generic.List[string]]::new()
$script:DhcpReleaseExitCode = $null
$script:DhcpRenewExitCode = $null
$script:AdapterRefreshRequested = [bool]$IncludeAdapterRefresh
$script:AdapterRefreshAttempted = $false
$script:AdapterRefreshSupported = $false

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
        [switch] $RequireSuccess
    )

    Write-TidyLog -Level Information -Message $Description

    # Prevent sticky $LASTEXITCODE values from earlier native calls from leaking into this invocation.
    if (Test-Path -Path 'variable:LASTEXITCODE') {
        $global:LASTEXITCODE = 0
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

function Register-TidyResetAction {
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

            # Mark overall run as not fully successful when a step fails.
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

    $successTokens = @('ok', 'success', 'successful', 'sucessfully', 'completed', 'complete', 'windows ip configuration')
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

function Invoke-TidyResetAction {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Action,
        [Parameter(Mandatory = $true)]
        [scriptblock] $Command,
        [object[]] $Arguments = @(),
        [string] $Description = 'Executing network remediation command.',
        [switch] $RequireSuccess,
        [int[]] $AdditionalSuccessExitCodes = @()
    )

    try {
        $rawResult = Invoke-TidyCommand -Command $Command -Arguments $Arguments -Description $Description -RequireSuccess:$RequireSuccess
    }
    catch {
        $message = $_.Exception.Message
        Register-TidyResetAction -Name $Action -Status Failed -Details $message
        $script:OperationSucceeded = $false

        if ($RequireSuccess) {
            throw
        }

        return $null
    }

    $resolved = Resolve-TidyCommandResult -InputObject $rawResult
    $exitCode = if ($null -eq $resolved.ExitCode) { 0 } else { [int]$resolved.ExitCode }
    $detailMessage = $resolved.Message

    if ($exitCode -ne 0 -and -not ($AdditionalSuccessExitCodes -contains $exitCode)) {
        $detailText = if ([string]::IsNullOrWhiteSpace($detailMessage)) { "Exit code $exitCode" } else { $detailMessage }
        Register-TidyResetAction -Name $Action -Status Failed -Details $detailText
        $script:OperationSucceeded = $false

        if ($RequireSuccess) {
            $errorText = if ([string]::IsNullOrWhiteSpace($detailMessage)) { "exit code $exitCode" } else { $detailMessage }
            throw "$Description failed: $errorText"
        }
    }
    else {
        Register-TidyResetAction -Name $Action -Status Success

        if ($resolved.Indeterminate -and -not [string]::IsNullOrWhiteSpace($detailMessage)) {
            $script:DiagnosticsSummary.Add("${Action}: $detailMessage")
        }
    }

    return $exitCode
}

function Write-TidyResetSummary {
    if ($script:SummaryEmitted) {
        return
    }

    $script:SummaryEmitted = $true
    $elapsed = (Get-Date) - $script:ExecutionStart

    Write-TidyOutput -Message '--- Network reset summary ---'
    Write-TidyOutput -Message ("Elapsed time: {0:g}" -f $elapsed)

    Write-TidyOutput -Message ("DNS cache flush: {0}" -f $(if ($script:DnsFlushSucceeded) { 'Success' } else { 'See diagnostic notes' }))
    Write-TidyOutput -Message ("ARP cache clear: {0}" -f $(if ($script:ArpFlushSucceeded) { 'Success' } else { 'See diagnostic notes' }))
    Write-TidyOutput -Message ("TCP tuning reset: {0}" -f $(if ($script:TcpResetSucceeded) { 'Success' } else { 'See diagnostic notes' }))
    Write-TidyOutput -Message ("Winsock reset: {0}" -f $script:WinsockResetStatus)
    Write-TidyOutput -Message ("IP stack reset: {0}" -f $script:IpResetStatus)

    if ($script:AdapterRefreshRequested) {
        $supportLabel = if ($script:AdapterRefreshSupported) { 'Available' } else { 'Unavailable' }
        Write-TidyOutput -Message ("Adapter refresh support: {0}" -f $supportLabel)
        if ($script:AdapterRefreshAttempted) {
            if ($script:AdaptersRestarted.Count -gt 0) {
                Write-TidyOutput -Message ("Adapters restarted ({0}):" -f $script:AdaptersRestarted.Count)
                foreach ($name in $script:AdaptersRestarted) {
                    Write-TidyOutput -Message ("  ↳ {0}" -f $name)
                }
            }

            if ($script:AdaptersFailed.Count -gt 0) {
                Write-TidyOutput -Message ("Adapters failed to restart ({0}):" -f $script:AdaptersFailed.Count)
                foreach ($name in $script:AdaptersFailed) {
                    Write-TidyOutput -Message ("  ↳ {0}" -f $name)
                }
            }
        }
    }

    if ($IncludeDhcpRenew) {
        $releaseLabel = if ($null -eq $script:DhcpReleaseExitCode) { 'Not attempted' } elseif ($script:DhcpReleaseExitCode -eq 0) { 'Success' } else { "Exit code $($script:DhcpReleaseExitCode)" }
        $renewLabel = if ($null -eq $script:DhcpRenewExitCode) { 'Not attempted' } elseif ($script:DhcpRenewExitCode -eq 0) { 'Success' } else { "Exit code $($script:DhcpRenewExitCode)" }
        Write-TidyOutput -Message ("DHCP release: {0}" -f $releaseLabel)
        Write-TidyOutput -Message ("DHCP renew: {0}" -f $renewLabel)
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

    if ($script:DiagnosticsSummary.Count -gt 0) {
        Write-TidyOutput -Message 'Diagnostic notes:'
        foreach ($note in $script:DiagnosticsSummary) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $note)
        }
    }

    if ($script:RebootRecommended) {
        Write-TidyOutput -Message 'Reboot recommended: Winsock reset requires a restart to fully apply.'
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Network reset requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting network reset and cache flush sequence.'

    Write-TidyOutput -Message 'Flushing DNS resolver cache.'
    $dnsExit = Invoke-TidyResetAction -Action 'Flush DNS cache' -Command { ipconfig /flushdns } -Description 'Flushing DNS cache.'
    if ($dnsExit -eq 0) {
        $script:DnsFlushSucceeded = $true
    }
    elseif ($null -ne $dnsExit) {
        $script:DiagnosticsSummary.Add('DNS cache flush reported a non-zero exit code.')
    }
    else {
        $script:DiagnosticsSummary.Add('DNS cache flush failed due to an exception. Review error logs above.')
    }

    Write-TidyOutput -Message 'Clearing ARP cache.'
    $arpExit = Invoke-TidyResetAction -Action 'Clear ARP cache' -Command { netsh interface ip delete arpcache } -Description 'Clearing ARP cache.'
    if ($arpExit -eq 0) {
        $script:ArpFlushSucceeded = $true
    }
    elseif ($null -ne $arpExit) {
        $script:DiagnosticsSummary.Add('ARP cache clear returned a non-zero exit code.')
    }
    else {
        $script:DiagnosticsSummary.Add('ARP cache clear failed due to an exception.')
    }

    Write-TidyOutput -Message 'Normalizing TCP auto-tuning and heuristics.'
    $tcpExit = Invoke-TidyResetAction -Action 'Normalize TCP stack' -Command {
        netsh interface tcp set heuristics disabled | Out-Null
        netsh interface tcp set global autotuninglevel=normal | Out-Null
    } -Description 'Normalizing TCP global settings.'
    if ($tcpExit -eq 0) {
        $script:TcpResetSucceeded = $true
    }
    elseif ($null -ne $tcpExit) {
        $script:DiagnosticsSummary.Add('TCP tuning normalization returned a non-zero exit code.')
    }
    else {
        $script:DiagnosticsSummary.Add('TCP tuning normalization failed due to an exception.')
    }

    if ($SkipWinsockReset.IsPresent) {
        Write-TidyOutput -Message 'Skipping Winsock reset per operator request.'
        Register-TidyResetAction -Name 'Winsock reset' -Status Skipped -Details 'SkipWinsockReset flag set.'
        $script:WinsockResetStatus = 'Skipped via switch'
    }
    else {
        Write-TidyOutput -Message 'Resetting Winsock catalog (reboot required to complete).'
        $script:WinsockResetAttempted = $true
        $winsockExit = Invoke-TidyResetAction -Action 'Winsock reset' -Command { netsh winsock reset } -Description 'Resetting Winsock catalog.'
        if ($winsockExit -eq 0) {
            $script:WinsockResetSucceeded = $true
            $script:WinsockResetStatus = 'Success'
            $script:RebootRecommended = $true
        }
        elseif ($null -ne $winsockExit) {
            $script:WinsockResetStatus = "Failed (exit code $winsockExit)"
            $script:DiagnosticsSummary.Add("Winsock reset failed with exit code $winsockExit." )
        }
        else {
            $script:WinsockResetStatus = 'Failed (exception reported)'
            $script:DiagnosticsSummary.Add('Winsock reset failed due to an exception.')
        }
    }

    if ($SkipIpReset.IsPresent) {
        Write-TidyOutput -Message 'Skipping IP reset per operator request.'
        Register-TidyResetAction -Name 'IP stack reset' -Status Skipped -Details 'SkipIpReset flag set.'
        $script:IpResetStatus = 'Skipped via switch'
    }
    else {
        Write-TidyOutput -Message 'Resetting IP stack bindings.'
        $script:IpResetAttempted = $true
        $script:IpResetLogPath = Join-Path -Path $env:TEMP -ChildPath 'tidy-ip-reset.log'
        $ipExit = Invoke-TidyResetAction -Action 'IP stack reset' -Command { param($logPath) netsh int ip reset $logPath } -Arguments @($script:IpResetLogPath) -Description 'Resetting IP interfaces.' -AdditionalSuccessExitCodes @(1)
        if ($ipExit -eq 0 -or $ipExit -eq 1) {
            $script:IpResetSucceeded = $true
            $script:IpResetStatus = if ($ipExit -eq 0) { 'Success' } else { 'Success (exit code 1; review log and reboot recommended)' }
            if ($ipExit -eq 1) {
                $script:DiagnosticsSummary.Add("IP stack reset returned exit code 1; registry entries may have been in use. Review the log file at $script:IpResetLogPath and reboot if issues persist.")
                $script:RebootRecommended = $true
            }
        }
        elseif ($null -ne $ipExit) {
            $script:IpResetStatus = "Failed (exit code $ipExit)"
            $script:DiagnosticsSummary.Add("IP stack reset returned exit code $ipExit.")
        }
        else {
            $script:IpResetStatus = 'Failed (exception reported)'
            $script:DiagnosticsSummary.Add('IP stack reset failed due to an exception.')
        }
    }

    if ($IncludeAdapterRefresh.IsPresent) {
        Write-TidyOutput -Message 'Refreshing enabled network adapters.'
        $script:AdapterRefreshAttempted = $true
        $adapterCmd = Get-Command -Name 'Get-NetAdapter' -ErrorAction SilentlyContinue
        if ($null -eq $adapterCmd) {
            Write-TidyOutput -Message 'Get-NetAdapter is not available on this system. Skipping adapter refresh.'
            Register-TidyResetAction -Name 'Adapter refresh' -Status Skipped -Details 'Get-NetAdapter unavailable.'
            $script:DiagnosticsSummary.Add('Adapter refresh skipped because Get-NetAdapter is unavailable on this system.')
        }
        else {
            $script:AdapterRefreshSupported = $true
            try {
                $adapters = Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' }
            }
            catch {
                $script:OperationSucceeded = $false
                $script:DiagnosticsSummary.Add('Failed to enumerate adapters: ' + $_.Exception.Message)
                Register-TidyResetAction -Name 'Adapter refresh' -Status Failed -Details $_.Exception.Message
                $adapters = @()
            }

            if (-not $adapters -or $adapters.Count -eq 0) {
                Write-TidyOutput -Message 'No active physical adapters detected. Skipping adapter restart.'
                if (-not $script:ActionsFailedSet.Contains('Adapter refresh')) {
                    Register-TidyResetAction -Name 'Adapter refresh' -Status Skipped -Details 'No active adapters found.'
                }
            }
            else {
                foreach ($adapter in $adapters) {
                    $actionLabel = "Adapter restart ($($adapter.Name))"
                    $restartExit = Invoke-TidyResetAction -Action $actionLabel -Command {
                        param($name)
                        Disable-NetAdapter -Name $name -Confirm:$false -PassThru -ErrorAction Stop | Out-Null
                        Start-Sleep -Seconds 2
                        Enable-NetAdapter -Name $name -Confirm:$false -PassThru -ErrorAction Stop | Out-Null
                        if (Test-Path -Path 'variable:LASTEXITCODE') { $global:LASTEXITCODE = 0 }
                    } -Arguments @($adapter.Name) -Description ("Restarting adapter '{0}'." -f $adapter.Name)

                    if ($restartExit -eq 0) {
                        $script:AdaptersRestarted.Add($adapter.Name)
                    }
                    else {
                        $script:AdaptersFailed.Add($adapter.Name)
                        if ($null -eq $restartExit) {
                            $script:DiagnosticsSummary.Add("Adapter restart ($($adapter.Name)) failed due to an exception.")
                        }
                        else {
                            $script:DiagnosticsSummary.Add("Adapter restart ($($adapter.Name)) returned exit code $restartExit.")
                        }
                    }
                }
            }
        }
    }

    if ($IncludeDhcpRenew) {
        Write-TidyOutput -Message 'Releasing DHCP leases.'
        $script:DhcpReleaseExitCode = Invoke-TidyResetAction -Action 'DHCP release' -Command { ipconfig /release } -Description 'Releasing DHCP leases.'
        if ($script:DhcpReleaseExitCode -ne 0 -and $null -ne $script:DhcpReleaseExitCode) {
            $script:DiagnosticsSummary.Add("DHCP release returned exit code $($script:DhcpReleaseExitCode).")
        }
        elseif ($null -eq $script:DhcpReleaseExitCode) {
            $script:DiagnosticsSummary.Add('DHCP release failed due to an exception.')
        }

        Start-Sleep -Seconds 2
        Write-TidyOutput -Message 'Renewing DHCP leases.'
        $script:DhcpRenewExitCode = Invoke-TidyResetAction -Action 'DHCP renew' -Command { ipconfig /renew } -Description 'Renewing DHCP leases.'
        if ($script:DhcpRenewExitCode -ne 0 -and $null -ne $script:DhcpRenewExitCode) {
            $script:DiagnosticsSummary.Add("DHCP renew returned exit code $($script:DhcpRenewExitCode).")
        }
        elseif ($null -eq $script:DhcpRenewExitCode) {
            $script:DiagnosticsSummary.Add('DHCP renew failed due to an exception.')
        }
    }

    Write-TidyResetSummary

    Write-TidyOutput -Message 'Network reset sequence completed.'
    if ($script:RebootRecommended -and -not $script:SummaryEmitted) {
        Write-TidyOutput -Message 'Reboot recommended to finalize Winsock reset.'
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
    Write-TidyResetSummary
    Save-TidyResult
    Write-TidyLog -Level Information -Message 'Network reset script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

