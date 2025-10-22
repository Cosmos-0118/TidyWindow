param(
    [switch] $ResetStoreCache,
    [switch] $ReRegisterStore,
    [switch] $ReRegisterAppInstaller,
    [switch] $ReRegisterPackages,
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

function Invoke-TidyWsReset {
    $wsreset = Get-Command -Name 'wsreset.exe' -ErrorAction SilentlyContinue
    if ($null -eq $wsreset) {
        Write-TidyOutput -Message 'wsreset.exe not found. Skipping store cache reset.'
        return
    }

    Write-TidyOutput -Message 'Resetting Microsoft Store cache (wsreset.exe).'
    Invoke-TidyCommand -Command { param($path) Start-Process -FilePath $path -Wait -WindowStyle Hidden } -Arguments @($wsreset.Path) -Description 'Running wsreset.exe.' | Out-Null
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
        return
    }

    foreach ($package in @($packages)) {
        if ([string]::IsNullOrWhiteSpace($package.InstallLocation)) {
            Write-TidyOutput -Message ("Package '{0}' has no install location. Skipping re-registration." -f $package.PackageFullName)
            continue
        }

        $manifest = Join-Path -Path $package.InstallLocation -ChildPath 'AppXManifest.xml'
        if (-not (Test-Path -LiteralPath $manifest)) {
            Write-TidyOutput -Message ("Manifest not found for package '{0}'." -f $package.PackageFullName)
            continue
        }

        Write-TidyOutput -Message ("Re-registering {0}" -f $package.PackageFullName)
        Invoke-TidyCommand -Command {
            param($path)

            try {
                Add-AppxPackage -DisableDevelopmentMode -ForceApplicationShutdown -Register $path -ErrorAction Stop
            }
            catch [System.Runtime.InteropServices.COMException] {
                $hresult = $_.Exception.HResult
                $messageText = $_.Exception.Message
                if ($hresult -eq -2147009274 -or ($messageText -and $messageText -like '*higher version*')) {
                    Write-TidyOutput -Message 'Package already registered at equal or higher version. Skipping re-registration.'
                    return
                }

                throw
            }
        } -Arguments @($manifest) -Description ("Add-AppxPackage {0}" -f $package.PackageFullName) | Out-Null
    }
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

function Invoke-LicensingRepair {
    if (-not $ConfigureLicensingServices.IsPresent) {
        return
    }

    Write-TidyOutput -Message 'Restarting Store licensing services.'
    $services = @('ClipSVC', 'AppXSvc', 'LicenseManager', 'WinStoreSvc')
    foreach ($service in $services) {
        $svc = Get-Service -Name $service -ErrorAction SilentlyContinue
        if ($null -eq $svc) {
            continue
        }

        if ($svc.Status -eq 'Running') {
            Invoke-TidyCommand -Command { param($name) Stop-Service -Name $name -Force -ErrorAction SilentlyContinue } -Arguments @($service) -Description ("Stop service {0}" -f $service) | Out-Null
        }

        Start-Sleep -Seconds 2
        Invoke-TidyCommand -Command { param($name) Start-Service -Name $name -ErrorAction SilentlyContinue } -Arguments @($service) -Description ("Start service {0}" -f $service) | Out-Null
    }

    Write-TidyOutput -Message 'Resetting Store licensing cache.'
    $licensePath = Join-Path -Path $env:ProgramData -ChildPath 'Microsoft\Windows\ClipSVC\TokenStore'
    if (Test-Path -LiteralPath $licensePath) {
        try {
            Remove-Item -LiteralPath $licensePath -Recurse -Force -ErrorAction Stop
            Write-TidyOutput -Message ("Removed ClipSVC token cache at {0}." -f $licensePath)
        }
        catch {
            Write-TidyError -Message ("Failed to clear licensing cache: {0}" -f $_.Exception.Message)
        }
    }
}

$script:IncludeAllUsers = -not $CurrentUserOnly.IsPresent

if (-not ($ResetStoreCache -or $ReRegisterStore -or $ReRegisterAppInstaller -or $ReRegisterPackages -or $IncludeFrameworks -or $ConfigureLicensingServices)) {
    $ResetStoreCache = $true
    $ReRegisterStore = $true
    $ReRegisterAppInstaller = $true
    $ReRegisterPackages = $true
    $IncludeFrameworks = $true
    $ConfigureLicensingServices = $true
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

try {
    if (-not (Test-TidyAdmin)) {
        throw 'App repair helper requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting Microsoft Store and AppX repair sequence.'

    if ($ResetStoreCache.IsPresent) {
        Invoke-TidyWsReset
    }

    if ($ReRegisterStore.IsPresent) {
        Write-TidyOutput -Message 'Re-registering Microsoft Store and dependencies.'
        Invoke-StoreReRegistration
    }

    if ($ReRegisterAppInstaller.IsPresent) {
        Write-TidyOutput -Message 'Repairing App Installer integration.'
        Invoke-AppInstallerRepair
    }

    if ($ReRegisterPackages.IsPresent -and $PackageNames) {
        foreach ($name in $PackageNames) {
            Invoke-AppxReRegistration -PackageName $name -AllUsers:$script:IncludeAllUsers
        }
    }

    Invoke-AppxFrameworkRefresh
    Invoke-LicensingRepair

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
