param(
    [switch] $SkipShellReregister,
    [switch] $SkipStartMenuReregister,
    [switch] $SkipSearchReset,
    [switch] $SkipExplorerRecycle,
    [switch] $SkipSettingsReregister,
    [switch] $SkipTrayRefresh,
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

function Resolve-SearchIndexerDllPath {
    $candidates = @(
        (Join-Path -Path $env:SystemRoot -ChildPath 'System32\searchindexer.dll'),
        (Join-Path -Path $env:SystemRoot -ChildPath 'SysWOW64\searchindexer.dll')
    )

    foreach ($path in $candidates) {
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        $full = [System.IO.Path]::GetFullPath($path)
        if (Test-Path -LiteralPath $full) { return $full }
    }

    return $null
}

function Stop-TidyAppxProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageName,
        [string[]] $ProcessNames = @()
    )

    try {
        $families = @(Get-AppxPackage -AllUsers -Name $PackageName -ErrorAction SilentlyContinue | ForEach-Object { $_.PackageFamilyName }) | Where-Object { $_ }
        $nameSet = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)

        foreach ($n in $ProcessNames) { if (-not [string]::IsNullOrWhiteSpace($n)) { [void]$nameSet.Add($n) } }
        foreach ($fam in $families) {
            $short = $fam.Split('_')[0]
            if ($short) { [void]$nameSet.Add($short) }
        }

        if ($nameSet.Count -eq 0) { return }

        $allProcs = Get-Process -ErrorAction SilentlyContinue
        foreach ($proc in $allProcs) {
            if (-not $nameSet.Contains($proc.ProcessName)) { continue }

            try {
                Write-TidyOutput -Message ("Stopping process {0} (PID {1}) for package {2}" -f $proc.ProcessName, $proc.Id, $PackageName)
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            }
            catch {
                Write-TidyOutput -Message ("Unable to stop process {0} (PID {1}): {2}" -f $proc.ProcessName, $proc.Id, $_.Exception.Message)
            }
        }
    }
    catch {
        Write-TidyOutput -Message ("Process stop helper failed for {0}: {1}" -f $PackageName, $_.Exception.Message)
    }
}

function ReRegister-AppxPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageName,
        [string] $Label,
        [string[]] $ProcessNames = @()
    )

    $packages = Get-AppxPackage -AllUsers -Name $PackageName -ErrorAction SilentlyContinue
    if (-not $packages) {
        Write-TidyOutput -Message ("{0} package not found. Skipping." -f $Label)
        return
    }

    foreach ($package in @($packages)) {
        if ([string]::IsNullOrWhiteSpace($package.InstallLocation)) {
            Write-TidyOutput -Message ("{0} has no InstallLocation. Skipping." -f $package.PackageFullName)
            continue
        }

        $manifest = Join-Path -Path $package.InstallLocation -ChildPath 'AppXManifest.xml'
        $attempts = 0
        $maxAttempts = 2
        while ($attempts -lt $maxAttempts) {
            $attempts++

            if ($attempts -gt 1) {
                Write-TidyOutput -Message ("Retrying {0} re-registration attempt {1}/{2}." -f $Label, $attempts, $maxAttempts)
                Stop-TidyAppxProcesses -PackageName $PackageName -ProcessNames $ProcessNames
                Start-Sleep -Milliseconds 400
            }

            try {
                Write-TidyOutput -Message ("Re-registering {0} from {1}" -f $Label, $manifest)
                Invoke-TidyCommand -Command { param($path) Add-AppxPackage -DisableDevelopmentMode -Register $path -ErrorAction Stop } -Arguments @($manifest) -Description ("Re-registering {0}" -f $Label) -RequireSuccess
                break
            }
            catch {
                $message = $_.Exception.Message
                $isInUse = $message -match '0x80073D02' -or $message -match 'resources it modifies are currently in use'

                if ($attempts -lt $maxAttempts -and $isInUse) {
                    Write-TidyOutput -Message ("{0} re-registration reported in-use resources; stopping related processes and retrying." -f $Label)
                    continue
                }

                $script:OperationSucceeded = $false
                Write-TidyError -Message ("{0} re-registration failed: {1}" -f $Label, $message)
                break
            }
        }
    }
}

function Reset-SearchIndexer {
    try {
        $svc = Get-Service -Name 'WSearch' -ErrorAction SilentlyContinue
        if ($null -eq $svc) {
            Write-TidyOutput -Message 'WSearch service not found. Skipping search reset.'
            return
        }

        $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name='WSearch'" -ErrorAction SilentlyContinue
        if ($serviceInfo -and $serviceInfo.StartMode -eq 'Disabled') {
            Write-TidyOutput -Message 'WSearch service is disabled. Skipping search reset.'
            return
        }

        $actionDescription = if ($svc.Status -eq 'Stopped') { 'Starting Windows Search service.' } else { 'Restarting Windows Search service.' }
        Write-TidyOutput -Message $actionDescription

        try {
            if ($svc.Status -eq 'Stopped') {
                Invoke-TidyCommand -Command { Start-Service -Name 'WSearch' -ErrorAction Stop } -Description $actionDescription -RequireSuccess
            }
            else {
                Invoke-TidyCommand -Command { Restart-Service -Name 'WSearch' -Force -ErrorAction Stop } -Description $actionDescription -RequireSuccess
            }
        }
        catch {
            Write-TidyOutput -Message ("Primary start/restart for WSearch failed or was blocked: {0}" -f $_.Exception.Message)
            try {
                Invoke-TidyCommand -Command { Start-Service -Name 'WSearch' -ErrorAction Stop } -Description 'Ensuring WSearch is running.' -RequireSuccess
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Search service restart failed: {0}" -f $_.Exception.Message)
                return
            }
        }

        if (-not (Wait-TidyServiceState -Name 'WSearch' -DesiredStatus 'Running' -TimeoutSeconds 15)) {
            $script:OperationSucceeded = $false
            Write-TidyError -Message 'WSearch did not reach Running state after restart.'
            return
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Search service restart failed: {0}" -f $_.Exception.Message)
        return
    }

    try {
        $dllPath = Resolve-SearchIndexerDllPath
        if (-not $dllPath) {
            Write-TidyOutput -Message 'searchindexer.dll not found; skipping rundll32 reset to avoid user-facing error. Service restart completed.'
            return
        }

        Write-TidyOutput -Message ("Triggering indexer reset via rundll32 ({0},Reset)." -f $dllPath)
        Invoke-TidyCommand -Command {
            param($dll)
            $exe = Join-Path -Path $env:SystemRoot -ChildPath 'System32\rundll32.exe'
            $args = ("{0},Reset" -f $dll)
            $process = Start-Process -FilePath $exe -ArgumentList $args -PassThru -WindowStyle Hidden
            $process.WaitForExit()
            return $process.ExitCode
        } -Arguments @($dllPath) -Description 'Resetting search indexer.' -AcceptableExitCodes @(0)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Indexer reset failed: {0}" -f $_.Exception.Message)
    }
}

function Recycle-Explorer {
    try {
        Write-TidyOutput -Message 'Restarting explorer shell.'
        Invoke-TidyCommand -Command { Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue } -Description 'Stopping explorer.'
        Start-Sleep -Milliseconds 500
        Invoke-TidyCommand -Command { Start-Process explorer.exe } -Description 'Starting explorer.' -RequireSuccess
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Explorer recycle failed: {0}" -f $_.Exception.Message)
    }
}

function Resolve-ShellExperienceHostExecutable {
    $candidates = [System.Collections.Generic.List[string]]::new()

    $packages = @(Get-AppxPackage -AllUsers -Name 'Microsoft.Windows.ShellExperienceHost' -ErrorAction SilentlyContinue | Sort-Object -Property Version -Descending)
    foreach ($package in $packages) {
        if (-not [string]::IsNullOrWhiteSpace($package.InstallLocation)) {
            $candidates.Add((Join-Path -Path $package.InstallLocation -ChildPath 'ShellExperienceHost.exe'))
        }

        if (-not [string]::IsNullOrWhiteSpace($package.PackageFamilyName)) {
            $systemApps = Join-Path -Path $env:SystemRoot -ChildPath 'SystemApps'
            $candidates.Add((Join-Path -Path $systemApps -ChildPath ("{0}\ShellExperienceHost.exe" -f $package.PackageFamilyName)))
        }
    }

    # Fallback to the canonical SystemApps location used by most builds.
    $defaultSystemApps = Join-Path -Path $env:SystemRoot -ChildPath 'SystemApps'
    $candidates.Add((Join-Path -Path $defaultSystemApps -ChildPath 'ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe'))

    foreach ($path in $candidates) {
        if ([string]::IsNullOrWhiteSpace($path)) { continue }

        $fullPath = [System.IO.Path]::GetFullPath($path)
        if (Test-Path -Path $fullPath) {
            return $fullPath
        }
    }

    return $null
}

function Wait-TidyProcessStart {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [int] $TimeoutSeconds = 5
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $process = Get-Process -Name $Name -ErrorAction SilentlyContinue
        if ($process) {
            return $true
        }

        Start-Sleep -Milliseconds 250
    }

    return $false
}

function Refresh-TrayShell {
    try {
        Write-TidyOutput -Message 'Refreshing ShellExperienceHost to reload tray/UI components.'
        Invoke-TidyCommand -Command { Stop-Process -Name ShellExperienceHost -Force -ErrorAction SilentlyContinue } -Description 'Stopping ShellExperienceHost.'
        Start-Sleep -Milliseconds 500

        $hostExe = Resolve-ShellExperienceHostExecutable
        $launched = $false

        if ($hostExe -and (Test-Path -Path $hostExe)) {
            $workingDir = Split-Path -Path $hostExe -Parent
            Write-TidyOutput -Message ("Starting ShellExperienceHost from '{0}'." -f $hostExe)
            try {
                Invoke-TidyCommand -Command { param($path, $wd) Start-Process -FilePath $path -WorkingDirectory $wd } -Arguments @($hostExe, $workingDir) -Description 'Starting ShellExperienceHost (exe).' -RequireSuccess
                $launched = $true
            }
            catch {
                Write-TidyOutput -Message ("Direct launch failed, will retry via AppsFolder: {0}" -f $_.Exception.Message)
            }
        }

        if (-not $launched) {
            $pkg = Get-AppxPackage -AllUsers -Name 'Microsoft.Windows.ShellExperienceHost' -ErrorAction SilentlyContinue | Sort-Object -Property Version -Descending | Select-Object -First 1
            $family = if ($pkg) { $pkg.PackageFamilyName } else { 'Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy' }
            $shellUri = "shell:AppsFolder\$family!App"
            Write-TidyOutput -Message ("ShellExperienceHost.exe not found or direct start failed. Launching via AppsFolder: {0}" -f $shellUri)
            Invoke-TidyCommand -Command { param($uri) Start-Process -FilePath 'explorer.exe' -ArgumentList $uri -WindowStyle Hidden } -Arguments @($shellUri) -Description 'Starting ShellExperienceHost (AppsFolder).' -RequireSuccess
        }

        if (-not (Wait-TidyProcessStart -Name 'ShellExperienceHost' -TimeoutSeconds 8)) {
            throw 'ShellExperienceHost did not start within the expected time window.'
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Tray refresh failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Shell and UI repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting shell and UI repair pack.'

    if (-not $SkipShellReregister.IsPresent) {
        ReRegister-AppxPackage -PackageName 'Microsoft.Windows.ShellExperienceHost' -Label 'ShellExperienceHost' -ProcessNames @('ShellExperienceHost')
    }
    else {
        Write-TidyOutput -Message 'Skipping ShellExperienceHost re-register per operator request.'
    }

    if (-not $SkipStartMenuReregister.IsPresent) {
        ReRegister-AppxPackage -PackageName 'Microsoft.Windows.StartMenuExperienceHost' -Label 'StartMenuExperienceHost' -ProcessNames @('StartMenuExperienceHost')
    }
    else {
        Write-TidyOutput -Message 'Skipping StartMenuExperienceHost re-register per operator request.'
    }

    if (-not $SkipSearchReset.IsPresent) {
        Reset-SearchIndexer
    }
    else {
        Write-TidyOutput -Message 'Skipping search indexer reset per operator request.'
    }

    if (-not $SkipExplorerRecycle.IsPresent) {
        Recycle-Explorer
    }
    else {
        Write-TidyOutput -Message 'Skipping explorer recycle per operator request.'
    }

    if (-not $SkipSettingsReregister.IsPresent) {
        ReRegister-AppxPackage -PackageName 'windows.immersivecontrolpanel' -Label 'Settings (ImmersiveControlPanel)' -ProcessNames @('SystemSettings')
    }
    else {
        Write-TidyOutput -Message 'Skipping settings app re-register per operator request.'
    }

    if (-not $SkipTrayRefresh.IsPresent) {
        Refresh-TrayShell
    }
    else {
        Write-TidyOutput -Message 'Skipping tray refresh per operator request.'
    }

    Write-TidyOutput -Message 'Shell and UI repair pack completed.'
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
    Write-TidyLog -Level Information -Message 'Shell and UI repair script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}