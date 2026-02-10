param(
    [switch] $SkipOneDriveReset,
    [switch] $SkipSyncServicesRestart,
    [switch] $SkipKfmMappingRepair,
    [switch] $SkipAutorunTaskRecreate,
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
        [int] $TimeoutSeconds = 15
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq $DesiredStatus) {
            return $true
        }

        Start-Sleep -Milliseconds 350
    }

    return $false
}

function Resolve-OneDriveExecutable {
    $raw = @(
        (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\OneDrive\OneDrive.exe'),
        (Join-Path -Path ${env:ProgramFiles} -ChildPath 'Microsoft OneDrive\OneDrive.exe'),
        (Join-Path -Path ${env:ProgramFiles(x86)} -ChildPath 'Microsoft OneDrive\OneDrive.exe')
    )

    $candidates = $raw | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($path in $candidates) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }

    return $null
}

function Reset-OneDriveClient {
    try {
        $exe = Resolve-OneDriveExecutable
        if (-not $exe) {
            Write-TidyOutput -Message 'OneDrive executable not found; skipping reset.'
            return
        }

        Write-TidyOutput -Message 'Stopping existing OneDrive processes before reset.'
        Invoke-TidyCommand -Command { Stop-Process -Name OneDrive -Force -ErrorAction SilentlyContinue } -Description 'Stopping existing OneDrive processes.'

        Write-TidyOutput -Message ("Resetting OneDrive via {0} /reset (with timeout)." -f $exe)
        $resetSucceeded = $false
        try {
            $resetExit = Invoke-TidyCommand -Command {
                param($path)
                $global:LASTEXITCODE = 0
                $p = Start-Process -FilePath $path -ArgumentList '/reset' -WindowStyle Hidden -PassThru
                if (-not ($p | Wait-Process -Timeout 60 -ErrorAction SilentlyContinue)) {
                    try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {}
                    $global:LASTEXITCODE = 408
                    return
                }

                $global:LASTEXITCODE = $p.ExitCode
            } -Arguments @($exe) -Description 'Issuing OneDrive reset.' -AcceptableExitCodes @(0,408)

            if ($resetExit -eq 0) {
                $resetSucceeded = $true
            }
            elseif ($resetExit -eq 408) {
                Write-TidyOutput -Message 'OneDrive /reset timed out at 60s; process was terminated and will continue with restart.'
            }
        }
        catch {
            Write-TidyOutput -Message ("OneDrive /reset threw an exception: {0}" -f $_.Exception.Message)
        }

        Start-Sleep -Seconds 3

        Write-TidyOutput -Message 'Starting OneDrive client in background.'
        Invoke-TidyCommand -Command { param($path) Start-Process -FilePath $path -ArgumentList '/background' -WindowStyle Hidden } -Arguments @($exe) -Description 'Starting OneDrive client.'

        if (-not $resetSucceeded) {
            Write-TidyOutput -Message 'OneDrive reset may have been partial or timed out; client restart was still issued.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("OneDrive reset failed: {0}" -f $_.Exception.Message)
    }
}

function Restart-SyncServices {
    $serviceGroups = @(
        @{ BaseName = 'OneSyncSvc'; Pattern = 'OneSyncSvc*' },
        @{ BaseName = 'FileSyncProvider'; Pattern = 'FileSyncProvider' },
        @{ BaseName = 'FileSyncSvc'; Pattern = 'FileSyncSvc' }
    )

    foreach ($group in $serviceGroups) {
        try {
            $candidates = Get-Service -Name $group.Pattern -ErrorAction SilentlyContinue | Sort-Object -Property Name -Unique
            if (-not $candidates) {
                Write-TidyOutput -Message ("Service {0} not found. Skipping." -f $group.BaseName)
                continue
            }

            foreach ($service in $candidates) {
                $svcName = $service.Name

                $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name='$svcName'" -ErrorAction SilentlyContinue
                if ($serviceInfo -and $serviceInfo.StartMode -eq 'Disabled') {
                    Write-TidyOutput -Message ("Service {0} ({1}) is disabled. Skipping restart." -f $group.BaseName, $svcName)
                    continue
                }

                $actionDescription = if ($service.Status -eq 'Stopped') { ("Starting sync service {0} ({1})." -f $group.BaseName, $svcName) } else { ("Restarting sync service {0} ({1})." -f $group.BaseName, $svcName) }
                Write-TidyOutput -Message $actionDescription

                $started = $false
                try {
                    if ($service.Status -eq 'Stopped') {
                        Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($svcName) -Description $actionDescription -RequireSuccess
                    }
                    else {
                        Invoke-TidyCommand -Command { param($name) Restart-Service -Name $name -ErrorAction Stop } -Arguments @($svcName) -Description $actionDescription -RequireSuccess
                    }
                    $started = $true
                }
                catch {
                    Write-TidyOutput -Message ("Primary restart/start for {0} ({1}) failed or was blocked: {2}" -f $group.BaseName, $svcName, $_.Exception.Message)
                    try {
                        Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($svcName) -Description ("Ensuring sync service {0} ({1}) is running." -f $group.BaseName, $svcName)
                        $started = $true
                    }
                    catch {
                        $script:OperationSucceeded = $false
                        Write-TidyError -Message ("Failed to restart service {0} ({1}): {2}" -f $group.BaseName, $svcName, $_.Exception.Message)
                    }
                }

                if ($started -and -not (Wait-TidyServiceState -Name $svcName -DesiredStatus 'Running' -TimeoutSeconds 15)) {
                    $script:OperationSucceeded = $false
                    Write-TidyError -Message ("Service {0} ({1}) did not reach Running state after restart attempt." -f $group.BaseName, $svcName)
                }
            }
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to restart service {0}: {1}" -f $group.BaseName, $_.Exception.Message)
        }
    }
}

function Repair-KfmMappings {
    try {
        $userProfile = $env:USERPROFILE
        $shellKey = 'HKCU:SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders'
        $defaults = @{
            'Desktop'     = "$userProfile\Desktop"
            'Personal'    = "$userProfile\Documents"
            'My Pictures' = "$userProfile\Pictures"
            'My Music'    = "$userProfile\Music"
            'My Video'    = "$userProfile\Videos"
        }

        foreach ($name in $defaults.Keys) {
            $current = (Get-ItemProperty -Path $shellKey -Name $name -ErrorAction SilentlyContinue).$name
            $target = $defaults[$name]

            if (-not [string]::IsNullOrWhiteSpace($current) -and ($current -like '*OneDrive*')) {
                Write-TidyOutput -Message ("Resetting {0} mapping from '{1}' to '{2}'." -f $name, $current, $target)
                Set-ItemProperty -Path $shellKey -Name $name -Value $target -ErrorAction Stop
            }
            else {
                Write-TidyOutput -Message ("{0} mapping is already local or unset. Current: '{1}'" -f $name, $current)
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("KFM mapping repair failed: {0}" -f $_.Exception.Message)
    }
}

function Ensure-OneDriveAutorun {
    try {
        $exe = Resolve-OneDriveExecutable
        if (-not $exe) {
            Write-TidyOutput -Message 'OneDrive executable not found; cannot create autorun entry.'
            return
        }

        $runKey = 'HKCU:SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
        $valueName = 'OneDrive'
        $desired = '"{0}" /background' -f $exe
        $current = (Get-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue).$valueName

        if ($current -and ($current -ieq $desired)) {
            Write-TidyOutput -Message 'OneDrive autorun entry already present.'
            return
        }

        Write-TidyOutput -Message 'Creating OneDrive autorun entry.'
        Set-ItemProperty -Path $runKey -Name $valueName -Value $desired -ErrorAction Stop
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Autorun/task recreate failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'OneDrive and cloud repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting OneDrive and cloud sync repair pack.'

    if (-not $SkipOneDriveReset.IsPresent) {
        Reset-OneDriveClient
    }
    else {
        Write-TidyOutput -Message 'Skipping OneDrive reset per operator request.'
    }

    if (-not $SkipSyncServicesRestart.IsPresent) {
        Restart-SyncServices
    }
    else {
        Write-TidyOutput -Message 'Skipping sync service restart per operator request.'
    }

    if (-not $SkipKfmMappingRepair.IsPresent) {
        Repair-KfmMappings
    }
    else {
        Write-TidyOutput -Message 'Skipping KFM mapping repair per operator request.'
    }

    if (-not $SkipAutorunTaskRecreate.IsPresent) {
        Ensure-OneDriveAutorun
    }
    else {
        Write-TidyOutput -Message 'Skipping autorun/task recreate per operator request.'
    }

    Write-TidyOutput -Message 'OneDrive and cloud sync repair pack completed.'
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
    Write-TidyLog -Level Information -Message 'OneDrive and cloud sync repair script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
