param(
    [string] $TargetHost = '8.8.8.8',
    [int] $LatencySamples = 8,
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

function Test-TidyIpAddress {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    [System.Net.IPAddress] $parsed = $null
    return [System.Net.IPAddress]::TryParse($Value, [ref]$parsed)
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

    if (-not $DiagnosticsOnly.IsPresent) {
        Write-TidyOutput -Message 'Clearing ARP cache.'
        Invoke-TidyCommand -Command { arp -d * } -Description 'Clearing ARP table.' -RequireSuccess | Out-Null

        Write-TidyOutput -Message 'Reloading NetBIOS name cache.'
        Invoke-TidyCommand -Command { nbtstat -R } -Description 'nbtstat -R' -RequireSuccess | Out-Null

        Write-TidyOutput -Message 'Re-registering NetBIOS names.'
        Invoke-TidyCommand -Command { nbtstat -RR } -Description 'nbtstat -RR' -RequireSuccess | Out-Null

        Write-TidyOutput -Message 'Resetting IPv4 neighbor cache.'
        Invoke-TidyCommand -Command { netsh interface ip delete arpcache } -Description 'netsh interface ip delete arpcache' -RequireSuccess | Out-Null

        Write-TidyOutput -Message 'Resetting TCP global heuristics to defaults.'
        Invoke-TidyCommand -Command { netsh interface tcp set heuristics disabled } -Description 'Disable TCP heuristics.' | Out-Null
        Invoke-TidyCommand -Command { netsh interface tcp set global autotuninglevel=normal } -Description 'Restore TCP auto-tuning.' | Out-Null

        if (-not $SkipDnsRegistration.IsPresent) {
            Write-TidyOutput -Message 'Registering DNS records with DHCP server.'
            Invoke-TidyCommand -Command { ipconfig /registerdns } -Description 'ipconfig /registerdns' | Out-Null
        }
        else {
            Write-TidyOutput -Message 'Skipping DNS registration per operator request.'
        }
    }

    Write-TidyOutput -Message 'Capturing adapter link statistics.'
    Invoke-TidyCommand -Command { Get-NetAdapterStatistics -IncludeHidden } -Description 'Adapter statistics snapshot.' | Out-Null

    if ($dnsName) {
        Write-TidyOutput -Message ("Resolving DNS for {0}" -f $dnsName)
        Invoke-TidyCommand -Command { param($name) Resolve-DnsName -Name $name -Type A,AAAA -ErrorAction SilentlyContinue } -Arguments @($dnsName) -Description 'Resolve DNS records.' | Out-Null
    }

    Write-TidyOutput -Message ("Testing connection to {0}." -f $TargetHost)
    Invoke-TidyCommand -Command { param($computerName) Test-NetConnection -ComputerName $computerName -InformationLevel Detailed } -Arguments @($TargetHost) -Description 'Test-NetConnection probe.' | Out-Null

    Write-TidyOutput -Message ("Running latency sample ({0} pings)." -f $LatencySamples)
    Invoke-TidyCommand -Command { param($computerName, $count) ping.exe -n $count $computerName } -Arguments @($TargetHost, [Math]::Max(1, $LatencySamples)) -Description 'ping sweep.' -RequireSuccess | Out-Null

    if (-not $SkipTraceroute.IsPresent) {
        Write-TidyOutput -Message 'Tracing network route.'
        Invoke-TidyCommand -Command { param($computerName) tracert.exe $computerName } -Arguments @($TargetHost) -Description 'tracert execution.' | Out-Null
    }
    else {
        Write-TidyOutput -Message 'Skipping traceroute per operator request.'
    }

    if (-not $SkipPathPing.IsPresent) {
        Write-TidyOutput -Message 'Running pathping for loss analysis (this can take several minutes).'
        Invoke-TidyCommand -Command { param($computerName) pathping.exe $computerName } -Arguments @($TargetHost) -Description 'pathping execution.' | Out-Null
    }
    else {
        Write-TidyOutput -Message 'Skipping pathping per operator request.'
    }

    Write-TidyOutput -Message 'Dumping refreshed ARP table.'
    Invoke-TidyCommand -Command { arp -a } -Description 'arp -a snapshot.' | Out-Null

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
