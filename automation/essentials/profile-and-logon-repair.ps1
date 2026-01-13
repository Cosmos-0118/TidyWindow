param(
    [switch] $SkipStartupAudit,
    [switch] $SkipProfilePathRepair,
    [switch] $SkipProfSvcReset,
    [switch] $SkipStaleProfileCleanup,
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

function Audit-StartupRunKeys {
    $runPaths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    )

    foreach ($path in $runPaths) {
        try {
            if (-not (Test-Path -LiteralPath $path)) {
                Write-TidyOutput -Message ("Run key {0} not found. Skipping." -f $path)
                continue
            }

            $items = Get-ItemProperty -LiteralPath $path
            $valueNames = $items.PSObject.Properties | Where-Object { $_.Name -notin @('PSPath','PSParentPath','PSChildName','PSDrive','PSProvider') }
            if (-not $valueNames) {
                Write-TidyOutput -Message ("Run key {0} has no startup entries." -f $path)
                continue
            }

            $backupPath = Join-Path -Path (Split-Path -Parent $path) -ChildPath '_TidyWindow_RunBackup'
            if (-not (Test-Path -LiteralPath $backupPath)) {
                New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
            }

            foreach ($prop in $valueNames) {
                $name = $prop.Name
                $value = [string]$prop.Value

                Write-TidyOutput -Message ("Startup entry: {0} -> {1}" -f $name, $value)

                $candidatePath = $null
                if ($value -match '^["'']?([^"'']+\.exe)') {
                    $candidatePath = $Matches[1]
                }

                if ([string]::IsNullOrWhiteSpace($candidatePath) -or -not (Test-Path -LiteralPath $candidatePath)) {
                    try {
                        $backupFile = Join-Path -Path $backupPath -ChildPath ("{0}-{1}.reg.txt" -f $name, (Get-Date -Format 'yyyyMMddHHmmss'))
                        Set-Content -LiteralPath $backupFile -Value $value -Encoding UTF8
                        Remove-ItemProperty -LiteralPath $path -Name $name -ErrorAction Stop
                        Write-TidyOutput -Message ("Removed broken startup entry {0}; value backed up to {1}." -f $name, $backupFile)
                    }
                    catch {
                        $script:OperationSucceeded = $false
                        Write-TidyError -Message ("Failed to remove startup entry {0}: {1}" -f $name, $_.Exception.Message)
                    }
                }
            }
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Startup audit failed for {0}: {1}" -f $path, $_.Exception.Message)
        }
    }
}

function Repair-ProfileImagePath {
    try {
        $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $sid = $currentUser.User.Value
        $profileKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid"

        if (-not (Test-Path -LiteralPath $profileKey)) {
            Write-TidyOutput -Message ("ProfileList key for SID {0} not found. Skipping ProfileImagePath repair." -f $sid)
            return
        }

        $userProfilePath = $env:USERPROFILE
        if ([string]::IsNullOrWhiteSpace($userProfilePath)) {
            Write-TidyOutput -Message 'USERPROFILE is empty; cannot validate ProfileImagePath.'
            return
        }

        $currentPath = (Get-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath -ErrorAction SilentlyContinue).ProfileImagePath
        if ($currentPath -and ($currentPath -ieq $userProfilePath)) {
            Write-TidyOutput -Message 'ProfileImagePath already matches USERPROFILE. No change needed.'
        }
        else {
            Write-TidyOutput -Message ("Setting ProfileImagePath for SID {0} to {1}." -f $sid, $userProfilePath)
            Set-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath -Value $userProfilePath -Force -ErrorAction Stop
        }

        $profSvc = Get-Service -Name 'ProfSvc' -ErrorAction SilentlyContinue
        if ($profSvc -and $profSvc.StartType -ne 'Automatic') {
            Write-TidyOutput -Message 'Setting ProfSvc startup type to Automatic.'
            Set-Service -Name 'ProfSvc' -StartupType Automatic -ErrorAction Stop
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("ProfileImagePath repair failed: {0}" -f $_.Exception.Message)
    }
}

function Reset-ProfSvcAndUserinit {
    try {
        $service = Get-Service -Name 'ProfSvc' -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            Write-TidyOutput -Message 'User Profile Service (ProfSvc) not found. Skipping restart.'
        }
        else {
            $serviceName = $service.Name
            if ($service.Status -ne 'Running') {
                Write-TidyOutput -Message 'ProfSvc is not running; attempting start.'
                try {
                    Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($serviceName) -Description 'Starting ProfSvc.' -RequireSuccess
                }
                catch {
                    $script:OperationSucceeded = $false
                    Write-TidyError -Message ("ProfSvc start failed: {0}" -f $_.Exception.Message)
                }
            }
            else {
                Write-TidyOutput -Message 'Restarting User Profile Service (ProfSvc).'
                try {
                    Invoke-TidyCommand -Command { param($name) Restart-Service -Name $name -ErrorAction Stop } -Arguments @($serviceName) -Description 'Restarting ProfSvc.' -RequireSuccess
                }
                catch {
                    Write-TidyOutput -Message ("Restart was blocked or timed out; ensuring service remains running: {0}" -f $_.Exception.Message)
                }
            }

            if (-not (Wait-TidyServiceState -Name $serviceName -DesiredStatus 'Running' -TimeoutSeconds 15)) {
                try {
                    Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($serviceName) -Description 'Ensuring ProfSvc is running.'
                }
                catch {
                    $script:OperationSucceeded = $false
                    Write-TidyError -Message ("ProfSvc not running after retry: {0}" -f $_.Exception.Message)
                }
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("ProfSvc restart failed: {0}" -f $_.Exception.Message)
    }

    $userinitPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    try {
        $expected = 'C:\\Windows\\system32\\userinit.exe,'
        $current = (Get-ItemProperty -LiteralPath $userinitPath -Name Userinit -ErrorAction SilentlyContinue).Userinit
        if ($current -and ($current -ieq $expected)) {
            Write-TidyOutput -Message 'Userinit registry value is correct.'
        }
        else {
            Write-TidyOutput -Message ("Setting Userinit to {0}." -f $expected)
            Set-ItemProperty -LiteralPath $userinitPath -Name Userinit -Value $expected -Force -ErrorAction Stop
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Userinit check/reset failed: {0}" -f $_.Exception.Message)
    }
}

function Cleanup-StaleProfiles {
    $profilesRoot = 'C:\\Users'
    $backupRoot = Join-Path -Path $profilesRoot -ChildPath 'TidyWindow.ProfileBackups'

    try {
        if (-not (Test-Path -LiteralPath $profilesRoot)) {
            Write-TidyOutput -Message 'C:\\Users not found. Skipping stale profile cleanup.'
            return
        }

        if (-not (Test-Path -LiteralPath $backupRoot)) {
            New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        }

        $candidates = Get-ChildItem -LiteralPath $profilesRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '\\.(000|bak)$' -or $_.Name -ieq 'Temp' }

        if (-not $candidates -or $candidates.Count -eq 0) {
            Write-TidyOutput -Message 'No stale profile directories detected.'
            return
        }

        foreach ($dir in $candidates) {
            try {
                $timestamp = Get-Date -Format 'yyyyMMddHHmmss'
                $destination = Join-Path -Path $backupRoot -ChildPath ("{0}-{1}" -f $dir.Name, $timestamp)
                Write-TidyOutput -Message ("Moving stale profile {0} to {1}." -f $dir.FullName, $destination)
                Move-Item -LiteralPath $dir.FullName -Destination $destination -Force -ErrorAction Stop
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to move stale profile {0}: {1}" -f $dir.FullName, $_.Exception.Message)
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Stale profile cleanup failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Profile and logon repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting profile and logon repair pack.'

    if (-not $SkipStartupAudit.IsPresent) {
        Audit-StartupRunKeys
    }
    else {
        Write-TidyOutput -Message 'Skipping startup audit/trim per operator request.'
    }

    if (-not $SkipProfilePathRepair.IsPresent) {
        Repair-ProfileImagePath
    }
    else {
        Write-TidyOutput -Message 'Skipping ProfileImagePath repair per operator request.'
    }

    if (-not $SkipProfSvcReset.IsPresent) {
        Reset-ProfSvcAndUserinit
    }
    else {
        Write-TidyOutput -Message 'Skipping ProfSvc restart/userinit check per operator request.'
    }

    if (-not $SkipStaleProfileCleanup.IsPresent) {
        Cleanup-StaleProfiles
    }
    else {
        Write-TidyOutput -Message 'Skipping stale profile cleanup per operator request.'
    }

    Write-TidyOutput -Message 'Profile and logon repair pack completed.'
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
    Write-TidyLog -Level Information -Message 'Profile and logon repair script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}
