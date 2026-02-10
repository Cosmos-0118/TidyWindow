param(
    [switch] $SkipActivationAttempt,
    [switch] $SkipDllReregister,
    [switch] $SkipProtectionServiceRefresh,
    [switch] $RebuildLicensingStore,
    [switch] $AttemptRearm,
    [switch] $CaptureLicenseStatus,
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

        if (($entry -is [int] -or $entry -is [long]) -and ($entry -eq $exitCode)) {
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

function Test-TidyAdmin {
    return [bool](New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-TidyServiceState {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [string] $DesiredStatus = 'Running',
        [int] $TimeoutSeconds = 30
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq $DesiredStatus) { return $true }
        Start-Sleep -Milliseconds 300
    }

    return $false
}

function Get-SlmgrPath {
    $path = Join-Path -Path $env:SystemRoot -ChildPath 'System32\slmgr.vbs'
    if (-not (Test-Path -LiteralPath $path)) {
        throw 'slmgr.vbs not found under System32. Cannot run activation commands.'
    }
    return $path
}

function Refresh-ProtectionService {
    try {
        $svc = Get-Service -Name 'sppsvc' -ErrorAction SilentlyContinue
        if ($null -eq $svc) {
            Write-TidyOutput -Message 'Software Protection (sppsvc) not found. Skipping service refresh.'
            return
        }

        $svcInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name='sppsvc'" -ErrorAction SilentlyContinue
        if ($svcInfo -and $svcInfo.StartMode -eq 'Disabled') {
            Write-TidyOutput -Message 'Software Protection service is disabled. Skipping restart to avoid policy conflicts.'
            return
        }

        if ($svc.Status -eq 'Running') {
            Write-TidyOutput -Message 'Restarting Software Protection service (sppsvc).'
            Invoke-TidyCommand -Command { Restart-Service -Name 'sppsvc' -Force -ErrorAction Stop } -Description 'Restarting sppsvc.' -AcceptableExitCodes @(0)
        }
        else {
            Write-TidyOutput -Message 'Starting Software Protection service (sppsvc).'
            Invoke-TidyCommand -Command { Start-Service -Name 'sppsvc' -ErrorAction Stop } -Description 'Starting sppsvc.' -AcceptableExitCodes @(0)
        }

        if (-not (Wait-TidyServiceState -Name 'sppsvc' -DesiredStatus 'Running' -TimeoutSeconds 15)) {
            Write-TidyOutput -Message 'sppsvc did not reach Running state after restart/start.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Software Protection service refresh failed: {0}" -f $_.Exception.Message)
    }
}

function Rebuild-LicensingStore {
    param(
        [switch] $AttemptActivation = $true
    )

    $serviceName = 'sppsvc'
    $tokensPath = Join-Path -Path $env:SystemRoot -ChildPath 'System32\spp\store\2.0\tokens.dat'

    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-TidyOutput -Message 'Licensing store rebuild skipped: Software Protection service (sppsvc) not found.'
        return
    }

    $svcInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name='sppsvc'" -ErrorAction SilentlyContinue
    if ($svcInfo -and $svcInfo.StartMode -eq 'Disabled') {
        Write-TidyOutput -Message 'Licensing store rebuild skipped: sppsvc is disabled by policy.'
        return
    }

    if (-not (Test-Path -LiteralPath $tokensPath)) {
        Write-TidyOutput -Message "Licensing store rebuild skipped: tokens.dat not found at $tokensPath."
        return
    }

    $backupName = "tokens.dat.bak-{0}" -f (Get-Date -Format 'yyyyMMddHHmmss')
    $backupPath = Join-Path -Path ([System.IO.Path]::GetDirectoryName($tokensPath)) -ChildPath $backupName

    Write-TidyOutput -Message 'Advanced: rebuilding licensing store (stop sppsvc, back up tokens.dat, restart, retry /ato).'

    try {
        Write-TidyOutput -Message 'Stopping Software Protection service for licensing store rebuild.'
        Stop-Service -Name $serviceName -Force -ErrorAction Stop
        if (-not (Wait-TidyServiceState -Name $serviceName -DesiredStatus 'Stopped' -TimeoutSeconds 20)) {
            Write-TidyOutput -Message 'sppsvc did not fully stop; continuing with caution.'
        }

        Write-TidyOutput -Message ("Backing up tokens.dat to {0}" -f $backupPath)
        Rename-Item -Path $tokensPath -NewName $backupPath -ErrorAction Stop

        Write-TidyOutput -Message 'Starting Software Protection service after licensing store rebuild.'
        Start-Service -Name $serviceName -ErrorAction Stop
        if (-not (Wait-TidyServiceState -Name $serviceName -DesiredStatus 'Running' -TimeoutSeconds 25)) {
            Write-TidyOutput -Message 'sppsvc did not reach Running state after store rebuild; activation retry may fail.'
        }

        if ($AttemptActivation.IsPresent) {
            Write-TidyOutput -Message 'Retrying activation (/ato) after licensing store rebuild.'
            Attempt-Activation
        }
        else {
            Write-TidyOutput -Message 'Activation retry suppressed by operator switch.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Licensing store rebuild failed: {0}" -f $_.Exception.Message)

        try {
            $svcCurrent = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($svcCurrent -and $svcCurrent.Status -ne 'Running') {
                Start-Service -Name $serviceName -ErrorAction SilentlyContinue | Out-Null
            }
        }
        catch { }
    }
}

function Reregister-ActivationDlls {
    $dlls = @(
        'slc.dll',
        'slwga.dll',
        'sppcomapi.dll',
        'sppuinotify.dll',
        'sppwinob.dll'
    )

    foreach ($dll in $dlls) {
        try {
            $full = Join-Path -Path $env:SystemRoot -ChildPath ("System32\{0}" -f $dll)
            if (-not (Test-Path -LiteralPath $full)) {
                Write-TidyOutput -Message ("Skipping {0}; file not found." -f $dll)
                continue
            }

            Write-TidyOutput -Message ("Re-registering {0}." -f $dll)
            Invoke-TidyCommand -Command { param($file) regsvr32.exe /s $file } -Arguments @($full) -Description ("regsvr32 {0}" -f $dll) -AcceptableExitCodes @(0)
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to re-register {0}: {1}" -f $dll, $_.Exception.Message)
        }
    }
}

function Attempt-Activation {
    try {
        $slmgr = Get-SlmgrPath
        Write-TidyOutput -Message 'Attempting online activation (slmgr /ato).'
        $exit = Invoke-TidyCommand -Command { param($path) cscript.exe //nologo $path /ato } -Arguments @($slmgr) -Description 'slmgr /ato' -RequireSuccess -AcceptableExitCodes @(0,0xC004D302)

        if ($exit -ne 0) {
            $script:OperationSucceeded = $false
            Write-TidyError -Message 'Activation returned 0xC004D302 (pending reboot or licensing service issue). Reboot and re-run status (/xpr, /dlv).'            
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Activation attempt failed: {0}" -f $_.Exception.Message)
    }
}

function Attempt-Rearm {
    try {
        $slmgr = Get-SlmgrPath
        Write-TidyOutput -Message 'Attempting license rearm (slmgr /rearm). This may consume a limited rearm count.'
        $exit = Invoke-TidyCommand -Command { param($path) cscript.exe //nologo $path /rearm } -Arguments @($slmgr) -Description 'slmgr /rearm' -RequireSuccess -AcceptableExitCodes @(0,0xC004D307,0xC004D302)

        if ($exit -eq 0xC004D302) {
            $script:OperationSucceeded = $false
            Write-TidyError -Message 'Rearm returned 0xC004D302 (pending reboot). Reboot, then re-run status (/xpr, /dlv).'            
        }
        elseif ($exit -ne 0) {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Rearm exited with code {0}." -f $exit)
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("License rearm failed: {0}" -f $_.Exception.Message)
    }
}

function Capture-LicenseStatus {
    param(
        [string] $Stage,
        [switch] $AllowPendingReboot
    )

    try {
        $slmgr = Get-SlmgrPath
        $label = if ([string]::IsNullOrWhiteSpace($Stage)) { 'License status' } else { "License status ({0})" -f $Stage }

        Write-TidyOutput -Message ("{0}: running slmgr /xpr." -f $label)
        $xprExit = Invoke-TidyCommand -Command { param($path) cscript.exe //nologo $path /xpr } -Arguments @($slmgr) -Description ("slmgr /xpr ({0})" -f $Stage) -AcceptableExitCodes @(0,0xC004D302)
        if ($AllowPendingReboot -and $xprExit -eq -1073425662) {
            Write-TidyOutput -Message 'slmgr /xpr returned 0xC004D302 (pending reboot after /rearm). Re-run status after reboot.'
        }
        elseif ($xprExit -ne 0) {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("slmgr /xpr exited with code {0}." -f $xprExit)
        }

        Write-TidyOutput -Message ("{0}: running slmgr /dlv." -f $label)
        $dlvExit = Invoke-TidyCommand -Command { param($path) cscript.exe //nologo $path /dlv } -Arguments @($slmgr) -Description ("slmgr /dlv ({0})" -f $Stage) -AcceptableExitCodes @(0,0xC004D302)
        if ($AllowPendingReboot -and $dlvExit -eq -1073425662) {
            Write-TidyOutput -Message 'slmgr /dlv returned 0xC004D302 (pending reboot after /rearm). Re-run status after reboot.'
        }
        elseif ($dlvExit -ne 0) {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("slmgr /dlv exited with code {0}." -f $dlvExit)
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("License status capture failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Activation and licensing repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting activation and licensing repair pack.'

    if ($CaptureLicenseStatus.IsPresent) {
        Capture-LicenseStatus -Stage 'before'
    }
    else {
        Write-TidyOutput -Message 'License status capture skipped; enable -CaptureLicenseStatus to log /xpr and /dlv before/after.'
    }

    if (-not $SkipDllReregister.IsPresent) {
        Reregister-ActivationDlls
    }
    else {
        Write-TidyOutput -Message 'Skipping activation DLL re-registration per operator request.'
    }

    if (-not $SkipProtectionServiceRefresh.IsPresent) {
        Refresh-ProtectionService
    }
    else {
        Write-TidyOutput -Message 'Skipping Software Protection service refresh per operator request.'
    }

    if ($RebuildLicensingStore.IsPresent) {
        Rebuild-LicensingStore -AttemptActivation:(!$SkipActivationAttempt.IsPresent)
    }
    elseif (-not $SkipActivationAttempt.IsPresent) {
        Attempt-Activation
    }
    else {
        Write-TidyOutput -Message 'Skipping online activation attempt per operator request.'
    }

    if ($AttemptRearm.IsPresent) {
        Attempt-Rearm
    }
    else {
        Write-TidyOutput -Message 'License rearm not requested; skipping /rearm to preserve remaining count.'
    }

    if ($CaptureLicenseStatus.IsPresent) {
        Capture-LicenseStatus -Stage 'after' -AllowPendingReboot:$AttemptRearm.IsPresent
    }

    Write-TidyOutput -Message 'Activation and licensing repair pack completed.'
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
    Write-TidyLog -Level Information -Message 'Activation and licensing repair script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
