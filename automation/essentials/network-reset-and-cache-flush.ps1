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

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Network reset requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting network reset and cache flush sequence.'

    Write-TidyOutput -Message 'Flushing DNS resolver cache.'
    Invoke-TidyCommand -Command { ipconfig /flushdns } -Description 'Flushing DNS cache.' -RequireSuccess | Out-Null

    Write-TidyOutput -Message 'Clearing ARP cache.'
    Invoke-TidyCommand -Command { netsh interface ip delete arpcache } -Description 'Clearing ARP cache.' -RequireSuccess | Out-Null

    Write-TidyOutput -Message 'Resetting TCP global statistics.'
    Invoke-TidyCommand -Command { netsh interface tcp reset } -Description 'Resetting TCP interface state.'  | Out-Null

    if (-not $SkipWinsockReset.IsPresent) {
        Write-TidyOutput -Message 'Resetting Winsock catalog (reboot required to complete).' 
        Invoke-TidyCommand -Command { netsh winsock reset } -Description 'Resetting Winsock catalog.' -RequireSuccess | Out-Null
    }
    else {
        Write-TidyOutput -Message 'Skipping Winsock reset per operator request.'
    }

    if (-not $SkipIpReset.IsPresent) {
        Write-TidyOutput -Message 'Resetting IP stack bindings.'
        Invoke-TidyCommand -Command { netsh int ip reset } -Description 'Resetting IP interfaces.' | Out-Null
    }
    else {
        Write-TidyOutput -Message 'Skipping IP reset per operator request.'
    }

    if ($IncludeAdapterRefresh.IsPresent) {
        Write-TidyOutput -Message 'Refreshing enabled network adapters.'
        $adapterCmd = Get-Command -Name 'Get-NetAdapter' -ErrorAction SilentlyContinue
        if ($null -eq $adapterCmd) {
            Write-TidyOutput -Message 'Get-NetAdapter is not available on this system. Skipping adapter refresh.'
        }
        else {
            $adapters = Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' }
            foreach ($adapter in $adapters) {
                Write-TidyOutput -Message ("Restarting adapter '{0}'." -f $adapter.Name)
                Invoke-TidyCommand -Command { param($name) Disable-NetAdapter -Name $name -Confirm:$false -PassThru -ErrorAction Stop } -Arguments @($adapter.Name) -Description ("Disabling adapter '{0}'." -f $adapter.Name) -RequireSuccess | Out-Null
                Start-Sleep -Seconds 2
                Invoke-TidyCommand -Command { param($name) Enable-NetAdapter -Name $name -Confirm:$false -PassThru -ErrorAction Stop } -Arguments @($adapter.Name) -Description ("Enabling adapter '{0}'." -f $adapter.Name) -RequireSuccess | Out-Null
            }
        }
    }

    if ($IncludeDhcpRenew.IsPresent) {
        Write-TidyOutput -Message 'Releasing DHCP leases.'
        Invoke-TidyCommand -Command { ipconfig /release } -Description 'Releasing DHCP leases.' | Out-Null
        Start-Sleep -Seconds 2
        Write-TidyOutput -Message 'Renewing DHCP leases.'
        Invoke-TidyCommand -Command { ipconfig /renew } -Description 'Renewing DHCP leases.' | Out-Null
    }

    Write-TidyOutput -Message 'Network reset sequence completed.'
    if (-not $SkipWinsockReset.IsPresent) {
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
