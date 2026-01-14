param(
    [switch] $SkipExecutionPolicy,
    [switch] $SkipProfileReset,
    [switch] $SkipRemotingEnable,
    [switch] $EnableRemoting,
    [switch] $RepairPsModulePath,
    [switch] $RepairSystemProfiles,
    [switch] $ClearRunspaceCaches,
    [switch] $ResetImplicitRemotingCache,
    [switch] $RepairWsmanProvider,
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

function Set-ExecutionPolicyRemoteSigned {
    try {
        $current = Get-ExecutionPolicy -Scope LocalMachine -ErrorAction SilentlyContinue
        if ($current -eq 'RemoteSigned') {
            Write-TidyOutput -Message 'Execution policy already RemoteSigned at LocalMachine scope.'
            return
        }

        Write-TidyOutput -Message "Setting execution policy to RemoteSigned at LocalMachine scope."
        Invoke-TidyCommand -Command { Set-ExecutionPolicy -Scope LocalMachine -ExecutionPolicy RemoteSigned -Force } -Description 'Setting execution policy to RemoteSigned (LocalMachine).' -RequireSuccess
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Setting execution policy failed: {0}" -f $_.Exception.Message)
    }
}

function Reset-PowerShellProfiles {
    try {
        $timestamp = Get-Date -Format 'yyyyMMddHHmmss'
        $profilePaths = @($PROFILE.CurrentUserAllHosts, $PROFILE.CurrentUserCurrentHost) | Select-Object -Unique
        $profileDirs = @()

        foreach ($path in $profilePaths) {
            if ([string]::IsNullOrWhiteSpace($path)) { continue }

            $directory = Split-Path -Parent $path
            if (-not (Test-Path -LiteralPath $directory)) {
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
            }

            $profileDirs += $directory

            if (Test-Path -LiteralPath $path) {
                $backupPath = "$path.bak.$timestamp"
                Write-TidyOutput -Message ("Backing up existing profile to {0}." -f $backupPath)
                try {
                    Move-Item -LiteralPath $path -Destination $backupPath -Force -ErrorAction Stop
                }
                catch {
                    $script:OperationSucceeded = $false
                    Write-TidyError -Message ("Failed to back up profile {0}: {1}" -f $path, $_.Exception.Message)
                    continue
                }
            }

            $content = @(
                '# PowerShell profile reset by TidyWindow',
                "# Timestamp: $(Get-Date -Format 'u')",
                '# Add customizations below'
            ) -join [Environment]::NewLine

            try {
                Set-Content -LiteralPath $path -Value $content -Encoding UTF8 -Force
                Write-TidyOutput -Message ("Created fresh profile at {0}." -f $path)
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to create profile {0}: {1}" -f $path, $_.Exception.Message)
            }
        }

        $profileDirs = $profileDirs | Select-Object -Unique
        foreach ($dir in $profileDirs) {
            try {
                $backups = Get-ChildItem -LiteralPath $dir -Filter '*.bak.*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
                if (-not $backups) { continue }

                $retentionCutoff = (Get-Date).AddDays(-14)
                $toRemove = @()

                $toRemove += $backups | Where-Object { $_.LastWriteTime -lt $retentionCutoff }

                $maxKeep = 5
                $extra = $backups | Where-Object { $_.LastWriteTime -ge $retentionCutoff } | Select-Object -Skip $maxKeep
                if ($extra) { $toRemove += $extra }

                foreach ($item in ($toRemove | Select-Object -Unique)) {
                    try {
                        Remove-Item -LiteralPath $item.FullName -Force -ErrorAction Stop
                        Write-TidyOutput -Message ("Pruned old profile backup {0}." -f $item.Name)
                    }
                    catch {
                        $script:OperationSucceeded = $false
                        Write-TidyError -Message ("Failed to prune profile backup {0}: {1}" -f $item.FullName, $_.Exception.Message)
                    }
                }
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Profile backup cleanup failed in {0}: {1}" -f $dir, $_.Exception.Message)
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Profile reset failed: {0}" -f $_.Exception.Message)
    }
}

function Enable-PowerShellRemotingSafe {
    try {
        Write-TidyOutput -Message 'Enabling PowerShell remoting (WinRM).' 
        Invoke-TidyCommand -Command { Enable-PSRemoting -Force -SkipNetworkProfileCheck } -Description 'Enable PowerShell remoting.' -RequireSuccess -AcceptableExitCodes @(0, 50)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Enable-PSRemoting failed: {0}" -f $_.Exception.Message)
    }

    try {
        $service = Get-Service -Name 'WinRM' -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            Write-TidyOutput -Message 'WinRM service not found. Skipping service configuration.'
            return
        }

        if ($service.StartType -eq 'Disabled') {
            Write-TidyOutput -Message 'Setting WinRM startup type to Automatic.'
            try {
                Set-Service -Name 'WinRM' -StartupType Automatic -ErrorAction Stop
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to set WinRM startup type: {0}" -f $_.Exception.Message)
            }
        }

        if ($service.Status -ne 'Running') {
            Write-TidyOutput -Message 'Starting WinRM service.'
            try {
                Invoke-TidyCommand -Command { Start-Service -Name 'WinRM' -ErrorAction Stop } -Description 'Starting WinRM service.' -RequireSuccess
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to start WinRM: {0}" -f $_.Exception.Message)
            }
        }

        if (-not (Wait-TidyServiceState -Name 'WinRM' -DesiredStatus 'Running' -TimeoutSeconds 12)) {
            Write-TidyOutput -Message 'WinRM did not reach Running state after start attempt.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("WinRM configuration failed: {0}" -f $_.Exception.Message)
    }
}

function Repair-PsModulePathEntries {
    try {
        $raw = $env:PSModulePath -split ';'
        $ordered = [System.Collections.Generic.List[string]]::new()
        $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

        foreach ($entry in $raw) {
            if ([string]::IsNullOrWhiteSpace($entry)) { continue }
            $full = [System.IO.Path]::GetFullPath($entry)
            if (-not (Test-Path -LiteralPath $full)) {
                Write-TidyOutput -Message ("Removing missing PSModulePath entry: {0}" -f $entry)
                continue
            }
            if ($seen.Add($full)) {
                $ordered.Add($full)
            }
        }

        if ($ordered.Count -eq 0) {
            Write-TidyOutput -Message 'PSModulePath became empty after validation; leaving existing value untouched.'
            return
        }

        $newPath = ($ordered -join ';')
        if ($newPath -ne $env:PSModulePath) {
            Write-TidyOutput -Message 'Updating PSModulePath with validated entries only.'
            $env:PSModulePath = $newPath
        }
        else {
            Write-TidyOutput -Message 'PSModulePath entries already valid; no change made.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("PSModulePath validation failed: {0}" -f $_.Exception.Message)
    }
}

function Repair-SystemProfilesSafe {
    try {
        $targets = @(
            (Join-Path -Path $PSHOME -ChildPath 'profile.ps1'),
            (Join-Path -Path $PSHOME -ChildPath 'Microsoft.PowerShell_profile.ps1')
        ) | Select-Object -Unique

        $timestamp = Get-Date -Format 'yyyyMMddHHmmss'

        foreach ($path in $targets) {
            if (-not (Test-Path -LiteralPath $path)) { continue }

            try {
                Get-Content -LiteralPath $path -ErrorAction Stop | Out-Null
                continue
            }
            catch {
                $backup = "$path.bak.$timestamp"
                Write-TidyOutput -Message ("Backing up unreadable system profile to {0}." -f $backup)
                try {
                    Move-Item -LiteralPath $path -Destination $backup -Force -ErrorAction Stop
                }
                catch {
                    Write-TidyOutput -Message ("Failed to back up {0}: {1}" -f $path, $_.Exception.Message)
                }

                $content = @(
                    '# System profile repaired by TidyWindow',
                    "# Timestamp: $(Get-Date -Format 'u')",
                    '# Original content was unreadable and was backed up.'
                ) -join [Environment]::NewLine

                try {
                    Set-Content -LiteralPath $path -Value $content -Encoding UTF8 -Force
                    Write-TidyOutput -Message ("Recreated system profile at {0}." -f $path)
                }
                catch {
                    Write-TidyError -Message ("Failed to recreate system profile {0}: {1}" -f $path, $_.Exception.Message)
                    $script:OperationSucceeded = $false
                }
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("System profile repair failed: {0}" -f $_.Exception.Message)
    }
}

function Clear-RunspaceCaches {
    try {
        $roots = @(
            (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Windows\PowerShell\Runspaces'),
            (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Windows\PowerShell\RunspaceConfiguration')
        )

        foreach ($root in $roots) {
            if (-not (Test-Path -LiteralPath $root)) { continue }
            Write-TidyOutput -Message ("Removing runspace cache at {0}." -f $root)
            try {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to clear runspace cache {0}: {1}" -f $root, $_.Exception.Message)
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Runspace cache purge failed: {0}" -f $_.Exception.Message)
    }
}

function Reset-ImplicitRemotingCache {
    try {
        $targets = @(
            (Join-Path -Path $env:APPDATA -ChildPath 'Microsoft\Windows\PowerShell\TransportConnectionCache'),
            (Join-Path -Path $env:APPDATA -ChildPath 'Microsoft\Windows\PowerShell\RemoteSessions'),
            (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Microsoft\Windows\PowerShell\TransportConnectionCache')
        )

        foreach ($path in $targets) {
            if (-not (Test-Path -LiteralPath $path)) { continue }
            Write-TidyOutput -Message ("Removing implicit remoting cache at {0}." -f $path)
            try {
                Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to clear implicit remoting cache {0}: {1}" -f $path, $_.Exception.Message)
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Implicit remoting cache reset failed: {0}" -f $_.Exception.Message)
    }
}

function Repair-WsmanProvider {
    try {
        $providerHealthy = $false
        try {
            $null = Get-PSProvider -PSProvider WSMan -ErrorAction Stop
            $providerHealthy = $true
        }
        catch {}

        if (-not $providerHealthy) {
            Write-TidyOutput -Message 'WSMan provider not healthy; re-importing Microsoft.WSMan.Management.'
            try {
                Import-Module Microsoft.WSMan.Management -Force -ErrorAction Stop
                $providerHealthy = $true
            }
            catch {}
        }

        if (-not $providerHealthy) {
            Write-TidyOutput -Message 'Reconfiguring WinRM (this will mirror Enable-PSRemoting quickconfig).'
            Invoke-TidyCommand -Command { winrm quickconfig -quiet } -Description 'Running winrm quickconfig.' -RequireSuccess -AcceptableExitCodes @(0, 50, 2150859113) | Out-Null
            Invoke-TidyCommand -Command { Enable-PSRemoting -Force -SkipNetworkProfileCheck } -Description 'Re-enabling PSRemoting to repair WSMan provider.' -RequireSuccess -AcceptableExitCodes @(0, 50)
        }

        try {
            Test-WSMan -ComputerName localhost -ErrorAction Stop | Out-Null
            Write-TidyOutput -Message 'WSMan provider and local endpoint validated.'
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("WSMan provider validation failed after repair: {0}" -f $_.Exception.Message)
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("WSMan provider repair failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'PowerShell environment repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting PowerShell environment repair pack.'

    if (-not $SkipExecutionPolicy.IsPresent) {
        Set-ExecutionPolicyRemoteSigned
    }
    else {
        Write-TidyOutput -Message 'Skipping execution policy change per operator request.'
    }

    if (-not $SkipProfileReset.IsPresent) {
        Reset-PowerShellProfiles
    }
    else {
        Write-TidyOutput -Message 'Skipping profile reset per operator request.'
    }

    if ($SkipRemotingEnable.IsPresent) {
        Write-TidyOutput -Message 'Skipping remoting enablement per operator request.'
    }
    elseif ($EnableRemoting.IsPresent) {
        Write-TidyOutput -Message 'Enabling PSRemoting is opt-in; proceeding because -EnableRemoting was specified.'
        Enable-PowerShellRemotingSafe
    }
    else {
        Write-TidyOutput -Message 'Remoting enablement skipped by default; pass -EnableRemoting to opt in (no LocalAccountTokenFilterPolicy changes applied).'
    }

    if ($RepairPsModulePath.IsPresent) {
        Repair-PsModulePathEntries
    }

    if ($RepairSystemProfiles.IsPresent) {
        Repair-SystemProfilesSafe
    }

    if ($ClearRunspaceCaches.IsPresent) {
        Clear-RunspaceCaches
    }

    if ($ResetImplicitRemotingCache.IsPresent) {
        Reset-ImplicitRemotingCache
    }

    if ($RepairWsmanProvider.IsPresent) {
        Repair-WsmanProvider
    }
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message ("PowerShell environment repair failed: {0}" -f $_.Exception.Message)
}
finally {
    Save-TidyResult
}
