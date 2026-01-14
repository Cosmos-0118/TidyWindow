param(
    [switch] $ResetStoreCache,
    [switch] $ReRegisterStore,
    [switch] $ReRegisterAppInstaller,
    [switch] $ReRegisterPackages,
    [switch] $ReRegisterProvisioned,
    [switch] $RestartStoreServices,
    [switch] $ReinstallStoreIfMissing,
    [switch] $ResetCapabilityAccess,
    [switch] $RepairWslState,
    [string[]] $PackageNames,
    [switch] $IncludeFrameworks,
    [switch] $ConfigureLicensingServices,
    [switch] $CurrentUserOnly,
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
$script:RepairedPackages = [System.Collections.Generic.List[string]]::new()
$script:SkippedPackages = [System.Collections.Generic.List[string]]::new()
$script:FailedPackages = [System.Collections.Generic.List[string]]::new()
$script:RestartedServices = [System.Collections.Generic.List[string]]::new()
$script:WsResetAttempted = $false
$script:WsResetSucceeded = $false
$script:LicensingCacheCleared = $false
$script:CapabilityAccessReset = $false

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

    # Reset $LASTEXITCODE before invoking to avoid sticky non-zero values from earlier native calls.
    if (Test-Path -Path 'variable:LASTEXITCODE') {
        $global:LASTEXITCODE = 0
    }

    $output = & $Command @Arguments 2>&1

    $exitCode = 0
    if (Test-Path -Path 'variable:LASTEXITCODE') {
        $exitCode = $LASTEXITCODE
    }

    # If a PowerShell command returned a numeric exit code directly and LASTEXITCODE stayed 0, respect the returned value.
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

function Restart-StoreServiceSafe {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-TidyOutput -Message ("Service {0} not found. Skipping." -f $Name)
        return
    }

    $description = if ($svc.Status -eq 'Running') { "Restarting service {0}." -f $Name } else { "Starting service {0}." -f $Name }
    Write-TidyOutput -Message $description

    try {
        $command = if ($svc.Status -eq 'Running') { { param($serviceName) Restart-Service -Name $serviceName -Force -ErrorAction Stop } } else { { param($serviceName) Start-Service -Name $serviceName -ErrorAction Stop } }
        Invoke-TidyCommand -Command $command -Arguments @($Name) -Description $description -RequireSuccess | Out-Null
    }
    catch {
        Write-TidyError -Message ("Service {0} restart/start failed: {1}" -f $Name, $_.Exception.Message)
        $script:OperationSucceeded = $false
        return
    }

    if (-not (Wait-TidyServiceState -Name $Name -DesiredStatus 'Running' -TimeoutSeconds 12)) {
        Write-TidyOutput -Message ("Service {0} did not reach Running state after restart." -f $Name)
    }

    if (-not $script:RestartedServices.Contains($Name)) {
        $script:RestartedServices.Add($Name)
    }
}

function Restart-StoreServices {
    if (-not $RestartStoreServices.IsPresent) {
        return
    }

    $targets = @('UwpSvc', 'InstallService')
    foreach ($svc in $targets) {
        Restart-StoreServiceSafe -Name $svc
    }
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

function Parse-TidyAppxInUsePackages {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    $results = [System.Collections.Generic.List[string]]::new()
    if ([string]::IsNullOrWhiteSpace($Message)) { return $results }

    $pattern = '([A-Za-z0-9\.]+_[0-9A-Za-z\.]+__\w+)'  # matches package family names from error text
    $matches = [System.Text.RegularExpressions.Regex]::Matches($Message, $pattern)
    foreach ($m in $matches) {
        $val = $m.Groups[1].Value
        if (-not [string]::IsNullOrWhiteSpace($val) -and -not $results.Contains($val)) {
            [void]$results.Add($val)
        }
    }

    return $results
}

function Stop-TidyAppxProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageName,
        [string[]] $ProcessNames = @(),
        [string[]] $PackageFamilies = @()
    )

    try {
        $families = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)

        foreach ($fam in $PackageFamilies) {
            if (-not [string]::IsNullOrWhiteSpace($fam)) { [void]$families.Add($fam) }
        }

        $pkgResults = @(Get-AppxPackage -AllUsers -Name $PackageName -ErrorAction SilentlyContinue)
        foreach ($pkg in $pkgResults) {
            if ($pkg.PackageFamilyName) { [void]$families.Add($pkg.PackageFamilyName) }
        }

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
                Write-TidyOutput -Message ("Stopping process {0} (PID {1}) related to {2}" -f $proc.ProcessName, $proc.Id, $PackageName)
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

function Test-StoreConnectivity {
    $storePresent = $false
    try {
        $pkg = Get-AppxPackage -Name 'Microsoft.WindowsStore' -ErrorAction SilentlyContinue | Select-Object -First 1
        $storePresent = [bool]$pkg
    }
    catch {
        Write-TidyOutput -Message 'Unable to query Microsoft Store package presence.'
    }

    $netOk = $false
    $probe = Test-NetConnection -ComputerName 'www.microsoft.com' -Port 443 -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
    if ($probe -and $probe.PSObject.Properties['TcpTestSucceeded']) {
        $netOk = [bool]$probe.TcpTestSucceeded
    }

    if (-not $netOk) {
        Write-TidyOutput -Message 'Network connectivity to www.microsoft.com:443 unavailable. Store repair may fail; check WAN/firewall.'
    }

    if (-not $storePresent) {
        Write-TidyOutput -Message 'Microsoft Store package not detected. Will attempt reinstall from WindowsApps payload if available.'
    }
}

function Invoke-TidyWsReset {
    $wsreset = Get-Command -Name 'wsreset.exe' -ErrorAction SilentlyContinue
    if ($null -eq $wsreset) {
        Write-TidyOutput -Message 'wsreset.exe not found. Skipping store cache reset.'
        return
    }

    $script:WsResetAttempted = $true
    Write-TidyOutput -Message 'Resetting Microsoft Store cache (wsreset.exe).'
    try {
        $exitCode = Invoke-TidyCommand -Command {
            param($path)

            $process = Start-Process -FilePath $path -PassThru -WindowStyle Hidden
            $process.WaitForExit()
            return $process.ExitCode
        } -Arguments @($wsreset.Path) -Description 'Running wsreset.exe.'

        if ($exitCode -eq 0) {
            $script:WsResetSucceeded = $true
            Write-TidyOutput -Message 'Microsoft Store cache reset completed successfully.'
        }
        else {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("wsreset.exe exited with code {0}. Review Store cache permissions." -f $exitCode)
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("wsreset.exe failed: {0}" -f $_.Exception.Message)
    }
}

function Invoke-StoreReRegistration {
    $targets = @(
        'Microsoft.WindowsStore',
        'Microsoft.StorePurchaseApp',
        'Microsoft.DesktopAppInstaller'
    )

    foreach ($name in $targets) {
        Invoke-AppxReRegistration -PackageName $name -AllUsers:$script:IncludeAllUsers
    }
}

function Invoke-AppxReRegistration {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageName,
        [switch] $AllUsers
    )

    $lookupParams = @{ Name = $PackageName; ErrorAction = 'SilentlyContinue' }
    if ($AllUsers.IsPresent) {
        $lookupParams['AllUsers'] = $true
    }

    $packages = Get-AppxPackage @lookupParams
    if (-not $packages) {
        Write-TidyOutput -Message ("Package '{0}' was not found. Skipping." -f $PackageName)
        if (-not $script:SkippedPackages.Contains($PackageName)) {
            $script:SkippedPackages.Add($PackageName)
        }
        return
    }

    foreach ($package in @($packages)) {
        if ([string]::IsNullOrWhiteSpace($package.InstallLocation)) {
            Write-TidyOutput -Message ("Package '{0}' has no install location. Skipping re-registration." -f $package.PackageFullName)
            if (-not $script:SkippedPackages.Contains($package.PackageFullName)) {
                $script:SkippedPackages.Add($package.PackageFullName)
            }
            continue
        }

        $manifest = Join-Path -Path $package.InstallLocation -ChildPath 'AppXManifest.xml'
        if (-not (Test-Path -LiteralPath $manifest)) {
            Write-TidyOutput -Message ("Manifest not found for package '{0}'." -f $package.PackageFullName)
            if (-not $script:SkippedPackages.Contains($package.PackageFullName)) {
                $script:SkippedPackages.Add($package.PackageFullName)
            }
            continue
        }

        $attempts = 0
        $maxAttempts = 2

        while ($attempts -lt $maxAttempts) {
            $attempts++
            if ($attempts -gt 1) {
                Write-TidyOutput -Message ("Retrying re-registration for {0} (attempt {1}/{2})." -f $package.PackageFullName, $attempts, $maxAttempts)
                $familiesFromError | Out-Null
            }

            try {
                Write-TidyOutput -Message ("Re-registering {0}" -f $package.PackageFullName)
                Add-AppxPackage -DisableDevelopmentMode -ForceApplicationShutdown -Register $manifest -ErrorAction Stop
                Write-TidyOutput -Message ("Re-registration succeeded for {0}." -f $package.PackageFullName)
                if (-not $script:RepairedPackages.Contains($package.PackageFullName)) {
                    $script:RepairedPackages.Add($package.PackageFullName)
                }
                break
            }
            catch {
                $exception = $_.Exception
                if (Test-TidyAppxRegistrationBenignFailure -Exception $exception) {
                    Write-TidyOutput -Message ("{0} already present at an equal or newer version." -f $package.PackageFullName)
                    if (-not $script:SkippedPackages.Contains($package.PackageFullName)) {
                        $script:SkippedPackages.Add($package.PackageFullName)
                    }
                    break
                }

                $message = $exception.Message
                $isInUse = $message -match '0x80073D02' -or $message -match 'resources it modifies are currently in use' -or $message -match 'need to be closed'

                if ($attempts -lt $maxAttempts -and $isInUse) {
                    $familiesFromError = Parse-TidyAppxInUsePackages -Message $message
                    Stop-TidyAppxProcesses -PackageName $PackageName -PackageFamilies $familiesFromError
                    Start-Sleep -Milliseconds 500
                    continue
                }

                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to re-register {0}: {1}" -f $package.PackageFullName, $message)
                if (-not $script:FailedPackages.Contains($package.PackageFullName)) {
                    $script:FailedPackages.Add($package.PackageFullName)
                }
                break
            }
        }
    }
}

function Invoke-ProvisionedAppxReRegistration {
    if (-not $ReRegisterProvisioned.IsPresent) {
        return
    }

    Write-TidyOutput -Message 'Re-registering provisioned AppX packages (all users baseline).'

    $provisioned = Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue
    if (-not $provisioned) {
        Write-TidyOutput -Message 'No provisioned packages found to re-register.'
        return
    }

    foreach ($pkg in $provisioned) {
        $family = if ($pkg.PSObject.Properties['PackageFamilyName']) { $pkg.PackageFamilyName } else { $pkg.PackageName }
        $manifest = $null

        $installed = Get-AppxPackage -AllUsers -Name $pkg.PackageName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($installed -and $installed.InstallLocation) {
            $manifest = Join-Path -Path $installed.InstallLocation -ChildPath 'AppxManifest.xml'
        }

        if (-not $manifest) {
            $root = "$env:ProgramFiles\WindowsApps"
            $candidate = Get-ChildItem -Path $root -Filter "$family*" -Directory -ErrorAction SilentlyContinue | Sort-Object -Property Name -Descending | Select-Object -First 1
            if ($candidate) {
                $manifest = Join-Path -Path $candidate.FullName -ChildPath 'AppxManifest.xml'
            }
        }

        if (-not $manifest -or -not (Test-Path -LiteralPath $manifest)) {
            Write-TidyOutput -Message ("Provisioned package {0} manifest not found. Skipping." -f $family)
            if (-not $script:SkippedPackages.Contains($family)) {
                $script:SkippedPackages.Add($family)
            }
            continue
        }

        $attempts = 0
        $maxAttempts = 2
        while ($attempts -lt $maxAttempts) {
            $attempts++
            if ($attempts -gt 1) {
                Write-TidyOutput -Message ("Retrying provisioned package {0} re-registration (attempt {1}/{2})." -f $family, $attempts, $maxAttempts)
            }

            try {
                Write-TidyOutput -Message ("Re-registering provisioned package {0}." -f $family)
                Add-AppxPackage -DisableDevelopmentMode -Register $manifest -ErrorAction Stop
                if (-not $script:RepairedPackages.Contains($family)) {
                    $script:RepairedPackages.Add($family)
                }
                break
            }
            catch {
                if (Test-TidyAppxRegistrationBenignFailure -Exception $_.Exception) {
                    Write-TidyOutput -Message ("Provisioned package {0} already present at equal/newer version." -f $family)
                    if (-not $script:SkippedPackages.Contains($family)) {
                        $script:SkippedPackages.Add($family)
                    }
                    break
                }

                $message = $_.Exception.Message
                $isInUse = $message -match '0x80073D02' -or $message -match 'resources it modifies are currently in use' -or $message -match 'need to be closed'

                if ($attempts -lt $maxAttempts -and $isInUse) {
                    $familiesFromError = Parse-TidyAppxInUsePackages -Message $message
                    Stop-TidyAppxProcesses -PackageName $family -PackageFamilies $familiesFromError
                    Start-Sleep -Milliseconds 500
                    continue
                }

                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Provisioned package {0} failed re-registration: {1}" -f $family, $message)
                if (-not $script:FailedPackages.Contains($family)) {
                    $script:FailedPackages.Add($family)
                }
                break
            }
        }
    }
}


function Test-TidyAppxRegistrationBenignFailure {
    param(
        [Parameter(Mandatory = $true)]
        [System.Exception] $Exception
    )

    $knownHResults = @(
        -2147009274,  # 0x80073D06: higher version already installed
        -2147009286   # 0x80073CFA: package already installed
    )

    $hresult = $null
    if ($Exception.PSObject.Properties['HResult']) {
        $hresult = [int]$Exception.HResult
    }

    if ($null -ne $hresult -and $knownHResults -contains $hresult) {
        return $true
    }

    $message = $Exception.Message
    if ($message -and ($message -like '*higher version*' -or $message -like '*already installed*')) {
        return $true
    }

    return $false
}

function Invoke-AppInstallerRepair {
    Invoke-AppxReRegistration -PackageName 'Microsoft.DesktopAppInstaller' -AllUsers:$script:IncludeAllUsers
}

function Invoke-AppxFrameworkRefresh {
    if (-not $IncludeFrameworks.IsPresent) {
        return
    }

    Write-TidyOutput -Message 'Refreshing critical app frameworks (Microsoft.NET.Native, VCLibs, UI.Xaml).'
    $filters = @(
        'Microsoft.NET.Native.Runtime.*',
        'Microsoft.NET.Native.Framework.*',
        'Microsoft.VCLibs.*',
        'Microsoft.UI.Xaml.*'
    )

    foreach ($filter in $filters) {
        Invoke-AppxReRegistration -PackageName $filter -AllUsers:$script:IncludeAllUsers
    }
}

function Ensure-StorePresence {
    if (-not $ReinstallStoreIfMissing.IsPresent) {
        return
    }

    $store = Get-AppxPackage -AllUsers -Name 'Microsoft.WindowsStore' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($store -and $store.InstallLocation) {
        Write-TidyOutput -Message 'Microsoft Store is present. Skipping reinstall step.'
        return
    }

    Write-TidyOutput -Message 'Microsoft Store not detected; attempting reinstall from WindowsApps payload.'
    $root = "$env:ProgramFiles\WindowsApps"
    $candidate = Get-ChildItem -Path $root -Filter 'Microsoft.WindowsStore_*_8wekyb3d8bbwe' -Directory -ErrorAction SilentlyContinue | Sort-Object -Property Name -Descending | Select-Object -First 1

    if (-not $candidate) {
        Write-TidyOutput -Message 'No Store payload found under WindowsApps. Reinstall not attempted.'
        return
    }

    $manifest = Join-Path -Path $candidate.FullName -ChildPath 'AppxManifest.xml'
    if (-not (Test-Path -LiteralPath $manifest)) {
        Write-TidyOutput -Message ("Store manifest not found at {0}. Reinstall not attempted." -f $manifest)
        return
    }

    try {
        Add-AppxPackage -DisableDevelopmentMode -Register $manifest -ErrorAction Stop
        Write-TidyOutput -Message 'Microsoft Store reinstall attempt completed.'
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Microsoft Store reinstall failed: {0}" -f $_.Exception.Message)
    }
}

function Invoke-LicensingHealthCheck {
    $slmgr = Join-Path -Path $env:WINDIR -ChildPath 'System32\slmgr.vbs'
    if (-not (Test-Path -LiteralPath $slmgr)) {
        Write-TidyOutput -Message 'Licensing health check skipped: slmgr.vbs not found.'
        return
    }

    try {
        $exitCode = Invoke-TidyCommand -Command { param($path) cscript.exe //Nologo $path '/dlv' } -Arguments @($slmgr) -Description 'Checking licensing health (slmgr /dlv).' -AcceptableExitCodes @(0)
        Write-TidyOutput -Message ("Licensing health check exit code: {0}" -f $exitCode)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Licensing health check failed: {0}" -f $_.Exception.Message)
    }
}

function Invoke-LicensingRepair {
    if (-not $ConfigureLicensingServices.IsPresent) {
        return
    }

    Invoke-LicensingHealthCheck

    Write-TidyOutput -Message 'Restarting Store licensing services.'
    $services = @('ClipSVC', 'AppXSvc', 'LicenseManager', 'WinStoreSvc')
    $stoppedServices = [System.Collections.Generic.List[string]]::new()
    foreach ($service in $services) {
        $svc = Get-Service -Name $service -ErrorAction SilentlyContinue
        if ($null -eq $svc) {
            continue
        }

        if ($svc.Status -eq 'Running') {
            Invoke-TidyCommand -Command { param($name) Stop-Service -Name $name -Force -ErrorAction SilentlyContinue } -Arguments @($service) -Description ("Stop service {0}" -f $service) | Out-Null
            Start-Sleep -Seconds 2
            [void]$stoppedServices.Add($service)
        }
    }

    Write-TidyOutput -Message 'Resetting Store licensing cache.'
    $licensePath = Join-Path -Path $env:ProgramData -ChildPath 'Microsoft\Windows\ClipSVC\TokenStore'
    if (Test-Path -LiteralPath $licensePath) {
        try {
            Remove-Item -LiteralPath $licensePath -Recurse -Force -ErrorAction Stop
            Write-TidyOutput -Message ("Removed ClipSVC token cache at {0}." -f $licensePath)
            $script:LicensingCacheCleared = $true
        }
        catch {
            Write-TidyError -Message ("Failed to clear licensing cache: {0}" -f $_.Exception.Message)
            $script:OperationSucceeded = $false
        }
    }

    foreach ($service in $stoppedServices) {
        try {
            Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction Stop } -Arguments @($service) -Description ("Start service {0}" -f $service) | Out-Null
            if (-not $script:RestartedServices.Contains($service)) {
                $script:RestartedServices.Add($service)
            }
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to restart service {0}: {1}" -f $service, $_.Exception.Message)
        }
    }
}

function Reset-CapabilityAccessPolicies {
    if (-not $ResetCapabilityAccess.IsPresent) {
        return
    }

    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore'
    if (-not (Test-Path -LiteralPath $path)) {
        Write-TidyOutput -Message 'CapabilityAccessManager ConsentStore not present. Nothing to reset.'
        return
    }

    $timestamp = Get-Date -Format 'yyyyMMddHHmmss'
    $parent = Split-Path -Parent $path
    $backupName = "ConsentStore.bak.$timestamp"
    $backup = Join-Path -Path $parent -ChildPath $backupName

    try {
        Write-TidyOutput -Message ("Resetting capability access policies by backing up {0}." -f $path)
        Copy-Item -LiteralPath $path -Destination $backup -Recurse -Force -ErrorAction Stop
        Write-TidyOutput -Message ("Original consent store backed up to {0}." -f $backup)
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
        Write-TidyOutput -Message 'Consent store removed; defaults will be recreated by apps as needed.'
        $script:CapabilityAccessReset = $true
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Failed to reset capability access policies: {0}" -f $_.Exception.Message)
    }
}

function Repair-WslState {
    if (-not $RepairWslState.IsPresent) {
        return
    }

    $wsl = Get-Command -Name 'wsl.exe' -ErrorAction SilentlyContinue
    if (-not $wsl) {
        Write-TidyOutput -Message 'WSL not installed; skipping WSL state repair.'
        return
    }

    try {
        $statusExit = Invoke-TidyCommand -Command { wsl --status } -Description 'Checking WSL status.' -AcceptableExitCodes @(0) -SkipLog
        Write-TidyOutput -Message ("WSL status exit code: {0}" -f $statusExit)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("WSL status check failed: {0}" -f $_.Exception.Message)
    }

    try {
        Invoke-TidyCommand -Command { wsl --shutdown } -Description 'Refreshing WSL by shutdown.' -AcceptableExitCodes @(0) -SkipLog | Out-Null
        Write-TidyOutput -Message 'WSL shutdown issued to refresh state.'
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("WSL shutdown failed: {0}" -f $_.Exception.Message)
    }
}

function Write-TidyRepairSummary {
    Write-TidyOutput -Message '--- Repair summary ---'

    if ($script:WsResetAttempted) {
        $status = if ($script:WsResetSucceeded) { 'Success' } else { 'Failed' }
        Write-TidyOutput -Message ("Store cache reset: {0}" -f $status)
    }

    if ($script:RepairedPackages.Count -gt 0) {
        Write-TidyOutput -Message ("Re-registered packages ({0}):" -f $script:RepairedPackages.Count)
        foreach ($entry in $script:RepairedPackages | Sort-Object) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $entry)
        }
    }

    if ($script:SkippedPackages.Count -gt 0) {
        Write-TidyOutput -Message ("Skipped packages ({0}):" -f $script:SkippedPackages.Count)
        foreach ($entry in $script:SkippedPackages | Sort-Object -Unique) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $entry)
        }
    }

    if ($script:FailedPackages.Count -gt 0) {
        Write-TidyOutput -Message ("Failed packages ({0}):" -f $script:FailedPackages.Count)
        foreach ($entry in $script:FailedPackages | Sort-Object -Unique) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $entry)
        }
    }

    if ($ConfigureLicensingServices.IsPresent) {
        $cacheStatus = if ($script:LicensingCacheCleared) { 'Cleared' } else { 'Not modified' }
        Write-TidyOutput -Message ("Licensing cache: {0}" -f $cacheStatus)
    }

    if ($script:RestartedServices.Count -gt 0) {
        Write-TidyOutput -Message ("Services restarted ({0}):" -f $script:RestartedServices.Count)
        foreach ($service in $script:RestartedServices | Sort-Object -Unique) {
            Write-TidyOutput -Message ("  ↳ {0}" -f $service)
        }
    }

    if ($ResetCapabilityAccess.IsPresent) {
        $status = if ($script:CapabilityAccessReset) { 'Reset (backed up previous consent store)' } else { 'Not modified' }
        Write-TidyOutput -Message ("Capability access policies: {0}" -f $status)
    }
}

$script:IncludeAllUsers = -not $CurrentUserOnly.IsPresent

if (-not ($ResetStoreCache -or $ReRegisterStore -or $ReRegisterAppInstaller -or $ReRegisterPackages -or $ReRegisterProvisioned -or $RestartStoreServices -or $ReinstallStoreIfMissing -or $ResetCapabilityAccess -or $RepairWslState -or $IncludeFrameworks -or $ConfigureLicensingServices)) {
    $ResetStoreCache = $true
    $ReRegisterStore = $true
    $ReRegisterAppInstaller = $true
    $ReRegisterPackages = $true
    $ReRegisterProvisioned = $true
    $RestartStoreServices = $true
    $ReinstallStoreIfMissing = $true
    $IncludeFrameworks = $true
    $ConfigureLicensingServices = $true
    $ResetCapabilityAccess = $true
    $RepairWslState = $true
}

if (-not $PackageNames -and $ReRegisterPackages.IsPresent) {
    $PackageNames = @(
            'Microsoft.DesktopAppInstaller',
            'Microsoft.WindowsStore',
            'Microsoft.WindowsCalculator',
            'Microsoft.WindowsCamera',
            'Microsoft.Windows.Photos',
            'Microsoft.WindowsSoundRecorder',
            'Microsoft.WindowsNotepad',
            'Microsoft.WindowsCommunicationApps',
            'Microsoft.WindowsTerminal',
            'Microsoft.ZuneVideo',
            'Microsoft.ZuneMusic'
    )
}

if ($PackageNames) {
    $unique = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $ordered = [System.Collections.Generic.List[string]]::new()

    foreach ($entry in $PackageNames) {
        if ([string]::IsNullOrWhiteSpace($entry)) {
            continue
        }

        $trimmed = $entry.Trim()
        if ($unique.Add($trimmed)) {
            [void]$ordered.Add($trimmed)
        }
    }

    $PackageNames = $ordered.ToArray()
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'App repair helper requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting Microsoft Store and AppX repair sequence.'

    Test-StoreConnectivity

    if ($ResetStoreCache.IsPresent) {
        Invoke-TidyWsReset
    }

    if ($RestartStoreServices.IsPresent) {
        Write-TidyOutput -Message 'Restarting Store deployment services (UwpSvc, InstallService).'
        Restart-StoreServices
    }

    if ($ReRegisterStore.IsPresent) {
        Write-TidyOutput -Message 'Re-registering Microsoft Store and dependencies.'
        Invoke-StoreReRegistration
    }

    if ($ReRegisterAppInstaller.IsPresent) {
        Write-TidyOutput -Message 'Repairing App Installer integration.'
        Invoke-AppInstallerRepair
    }

    if ($ReRegisterProvisioned.IsPresent) {
        Invoke-ProvisionedAppxReRegistration
    }

    if ($ReRegisterPackages.IsPresent -and $PackageNames) {
        foreach ($name in $PackageNames) {
            Invoke-AppxReRegistration -PackageName $name -AllUsers:$script:IncludeAllUsers
        }
    }

    Invoke-AppxFrameworkRefresh
    Invoke-LicensingRepair

    if ($ReinstallStoreIfMissing.IsPresent) {
        Ensure-StorePresence
    }

    if ($RepairWslState.IsPresent) {
        Repair-WslState
    }

    if ($ResetCapabilityAccess.IsPresent) {
        Reset-CapabilityAccessPolicies
    }

    Write-TidyRepairSummary

    Write-TidyOutput -Message 'App repair routine completed.'
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
    Write-TidyLog -Level Information -Message 'App repair helper finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

