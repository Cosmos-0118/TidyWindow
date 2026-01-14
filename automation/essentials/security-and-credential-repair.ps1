param(
    [switch] $SkipFirewallReset,
    [switch] $SkipSecurityUiReregister,
    [switch] $SkipCredentialVaultRebuild,
    [switch] $SkipEnableLuaEnforcement,
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

function Wait-TidyServiceState {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [string] $DesiredStatus = 'Running',
        [int] $TimeoutSeconds = 12
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq $DesiredStatus) {
            return $true
        }

        Start-Sleep -Milliseconds 300
    }

    return $false
}

function Reset-WindowsFirewall {
    try {
        Write-TidyOutput -Message 'Resetting Windows Firewall to defaults.'
        Invoke-TidyCommand -Command { netsh advfirewall reset } -Description 'Resetting Windows Firewall to defaults.' -RequireSuccess

        Write-TidyOutput -Message 'Re-enabling all firewall profiles.'
        Invoke-TidyCommand -Command { netsh advfirewall set allprofiles state on } -Description 'Enabling firewall profiles after reset.' -RequireSuccess

        Write-TidyOutput -Message 'Firewall reset completed.'
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Firewall reset failed: {0}" -f $_.Exception.Message)
    }
}

function Reregister-SecurityHealthUi {
    try {
        $service = Get-Service -Name 'SecurityHealthService' -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            Write-TidyOutput -Message 'SecurityHealthService not found. Skipping service restart.'
        }
        else {
            $serviceName = $service.Name
            $waited = $false

            function Wait-TidyServiceRunning {
                param(
                    [Parameter(Mandatory = $true)]
                    [string] $Name,
                    [int] $TimeoutSeconds = 10
                )

                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
                    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
                    if ($svc -and $svc.Status -eq 'Running') { return $true }
                    Start-Sleep -Milliseconds 300
                }

                return $false
            }

            if ($service.Status -eq 'Stopped') {
                Write-TidyOutput -Message 'SecurityHealthService is stopped; attempting start instead of restart.'
                try {
                    Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($serviceName) -Description 'Starting SecurityHealthService.' -RequireSuccess
                }
                catch {
                    $script:OperationSucceeded = $false
                    Write-TidyError -Message ("Failed to start SecurityHealthService: {0}" -f $_.Exception.Message)
                }
            }
            else {
                Write-TidyOutput -Message 'Restarting SecurityHealthService.'
                try {
                    Invoke-TidyCommand -Command { param($name) Restart-Service -Name $name -Force -ErrorAction Stop } -Arguments @($serviceName) -Description 'Restarting SecurityHealthService.' -RequireSuccess
                }
                catch {
                    Write-TidyOutput -Message ("Restart was blocked; verifying service state instead: {0}" -f $_.Exception.Message)
                }
            }

            if (-not (Wait-TidyServiceRunning -Name $serviceName -TimeoutSeconds 12)) {
                # Try one more start if the restart path didn't yield running state.
                try {
                    Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($serviceName) -Description 'Ensuring SecurityHealthService is running.'
                }
                catch {
                    $script:OperationSucceeded = $false
                    Write-TidyError -Message ("SecurityHealthService not running after retry: {0}" -f $_.Exception.Message)
                }
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Failed to restart SecurityHealthService: {0}" -f $_.Exception.Message)
    }

    try {
        $packages = @(Get-AppxPackage -AllUsers -Name 'Microsoft.SecHealthUI' -ErrorAction SilentlyContinue)
        if (-not $packages -or $packages.Count -eq 0) {
            Write-TidyOutput -Message 'Windows Security app package not found. Skipping re-registration.'
            return
        }

        foreach ($pkg in $packages) {
            $manifest = Join-Path -Path $pkg.InstallLocation -ChildPath 'AppXManifest.xml'
            if (-not (Test-Path -LiteralPath $manifest)) {
                Write-TidyOutput -Message ("Manifest not found for package {0}. Skipping." -f $pkg.PackageFullName)
                continue
            }

            Write-TidyOutput -Message ("Re-registering Windows Security app for package {0}." -f $pkg.PackageFullName)
            Invoke-TidyCommand -Command { param($path) Add-AppxPackage -DisableDevelopmentMode -Register $path } -Arguments @($manifest) -Description 'Re-registering Windows Security app.' -RequireSuccess
        }

        Write-TidyOutput -Message 'Windows Security app re-registration completed.'
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Windows Security app re-registration failed: {0}" -f $_.Exception.Message)
    }
}

function Rebuild-CredentialVault {
    $services = @('VaultSvc', 'Schedule')
    foreach ($svc in $services) {
        try {
            $service = Get-Service -Name $svc -ErrorAction SilentlyContinue
            if ($null -eq $service) {
                Write-TidyOutput -Message ("Service {0} not found. Skipping." -f $svc)
                continue
            }

            $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name='$svc'" -ErrorAction SilentlyContinue
            if ($serviceInfo -and $serviceInfo.StartMode -eq 'Disabled') {
                Write-TidyOutput -Message ("Service {0} is disabled. Skipping restart." -f $svc)
                continue
            }

            $actionDescription = if ($service.Status -eq 'Stopped') { ("Starting service {0}." -f $svc) } else { ("Restarting service {0}." -f $svc) }
            Write-TidyOutput -Message $actionDescription

            $started = $false
            try {
                if ($service.Status -eq 'Stopped') {
                    Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($svc) -Description $actionDescription -RequireSuccess
                }
                else {
                    Invoke-TidyCommand -Command { param($name) Restart-Service -Name $name -ErrorAction Stop } -Arguments @($svc) -Description $actionDescription -RequireSuccess
                }
                $started = $true
            }
            catch {
                Write-TidyOutput -Message ("Primary restart/start for {0} failed or was blocked: {1}" -f $svc, $_.Exception.Message)
                try {
                    Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($svc) -Description ("Ensuring service {0} is running." -f $svc)
                    $started = $true
                }
                catch {
                    $script:OperationSucceeded = $false
                    Write-TidyError -Message ("Failed to restart service {0}: {1}" -f $svc, $_.Exception.Message)
                }
            }

            if ($started -and -not (Wait-TidyServiceState -Name $svc -DesiredStatus 'Running' -TimeoutSeconds 15)) {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Service {0} did not reach Running state after restart attempt." -f $svc)
            }
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to restart service {0}: {1}" -f $svc, $_.Exception.Message)
        }
    }

    try {
        if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
            throw 'LOCALAPPDATA is not defined. Cannot rebuild credential cache.'
        }

        $credentialPath = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Credentials'
        $timestamp = Get-Date -Format 'yyyyMMddHHmmss'

        if (Test-Path -LiteralPath $credentialPath) {
            $backupPath = "$credentialPath.bak.$timestamp"
            Write-TidyOutput -Message ("Backing up existing credential cache to {0}." -f $backupPath)
            Move-Item -LiteralPath $credentialPath -Destination $backupPath -Force -ErrorAction Stop
        }
        else {
            Write-TidyOutput -Message 'Credential cache not found. Creating a fresh directory.'
        }

        Write-TidyOutput -Message 'Creating new credential cache directory.'
        New-Item -ItemType Directory -Path $credentialPath -Force -ErrorAction Stop | Out-Null

        $folder = Get-Item -LiteralPath $credentialPath -ErrorAction Stop
        $folder.Attributes = $folder.Attributes -bor [System.IO.FileAttributes]::Hidden

        Write-TidyOutput -Message 'Credential cache rebuilt successfully.'
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Credential vault rebuild failed: {0}" -f $_.Exception.Message)
    }
}

function Enforce-EnableLua {
    $path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    try {
        $current = Get-ItemProperty -Path $path -Name EnableLUA -ErrorAction SilentlyContinue
        $currentValue = if ($null -ne $current -and ($current.PSObject.Properties['EnableLUA'])) { [int]$current.EnableLUA } else { $null }

        if ($currentValue -eq 1) {
            Write-TidyOutput -Message 'EnableLUA already set to 1. No change required.'
            return
        }

        Write-TidyOutput -Message 'Enforcing EnableLUA=1 for UAC prompts.'
        Set-ItemProperty -Path $path -Name EnableLUA -Value 1 -Type DWord -Force -ErrorAction Stop
        Write-TidyOutput -Message 'EnableLUA set to 1. A reboot or logoff may be required for prompts to return.'
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("EnableLUA enforcement failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Security and credentials repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting security and credentials repair pack.'

    if (-not $SkipFirewallReset.IsPresent) {
        Reset-WindowsFirewall
    }
    else {
        Write-TidyOutput -Message 'Skipping firewall reset per operator request.'
    }

    if (-not $SkipSecurityUiReregister.IsPresent) {
        Reregister-SecurityHealthUi
    }
    else {
        Write-TidyOutput -Message 'Skipping Windows Security app re-registration per operator request.'
    }

    if (-not $SkipCredentialVaultRebuild.IsPresent) {
        Rebuild-CredentialVault
    }
    else {
        Write-TidyOutput -Message 'Skipping credential vault rebuild per operator request.'
    }

    if (-not $SkipEnableLuaEnforcement.IsPresent) {
        Enforce-EnableLua
    }
    else {
        Write-TidyOutput -Message 'Skipping EnableLUA enforcement per operator request.'
    }

    Write-TidyOutput -Message 'Security and credentials repair pack completed.'
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
    Write-TidyLog -Level Information -Message 'Security and credentials repair script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
