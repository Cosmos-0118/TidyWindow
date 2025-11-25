param(
    [Parameter(Mandatory = $true)]
    [string] $Manager,
    [switch] $Elevated,
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

function Test-TidyCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CommandName
    )

    $null -ne (Get-Command -Name $CommandName -ErrorAction SilentlyContinue)
}

function Test-TidyAdmin {
    return [bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
 
function Get-TidyScoopRootCandidates {
    $roots = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    $add = {
        param([string] $Value)

        if ([string]::IsNullOrWhiteSpace($Value)) {
            return
        }

        $trimmed = $Value.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            return
        }

        try {
            $normalized = [System.IO.Path]::GetFullPath($trimmed)
        }
        catch {
            $normalized = $trimmed
        }

        if ($seen.Add($normalized)) {
            [void]$roots.Add($normalized)
        }
    }

    foreach ($candidate in @($env:SCOOP, $env:SCOOP_GLOBAL)) {
        & $add $candidate
    }

    $programData = [Environment]::GetFolderPath('CommonApplicationData')
    if (-not [string]::IsNullOrWhiteSpace($programData)) {
        & $add (Join-Path -Path $programData -ChildPath 'scoop')
    }

    $userProfile = [Environment]::GetFolderPath('UserProfile')
    if (-not [string]::IsNullOrWhiteSpace($userProfile)) {
        & $add (Join-Path -Path $userProfile -ChildPath 'scoop')
    }

    return $roots
}

function Get-TidyPowerShellExecutable {
    if ($PSVersionTable.PSEdition -eq 'Core') {
        $pwsh = Get-Command -Name 'pwsh' -ErrorAction SilentlyContinue
        if ($pwsh) {
            return $pwsh.Source
        }
    }

    $legacy = Get-Command -Name 'powershell.exe' -ErrorAction SilentlyContinue
    if ($legacy) {
        return $legacy.Source
    }

    throw 'Unable to locate a PowerShell executable to request elevation.'
}

function Request-ChocolateyElevation {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ManagerName
    )

    $tempPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "tidywindow-choco-remove-" + ([System.Guid]::NewGuid().ToString('N')) + '.json')

    $shellPath = Get-TidyPowerShellExecutable
    $escapedScript = $callerModulePath -replace "'", "''"
    $escapedManager = $ManagerName -replace "'", "''"
    $escapedResult = $tempPath -replace "'", "''"
    $command = "& '$escapedScript' -Manager '$escapedManager' -Elevated -ResultPath '$escapedResult'"

    Write-TidyLog -Level Information -Message 'Requesting administrator approval for Chocolatey uninstall.'
    Write-TidyOutput -Message 'Requesting administrator approval. Windows may prompt for permission.'

    try {
        Start-Process -FilePath $shellPath -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $command) -Verb RunAs -WindowStyle Hidden -Wait | Out-Null
    }
    catch {
        throw 'Administrator approval was denied or cancelled.'
    }

    if (-not (Test-Path -Path $tempPath)) {
        throw 'Administrator approval was denied before the operation could start.'
    }

    try {
        $json = Get-Content -Path $tempPath -Raw -ErrorAction Stop
        $result = ConvertFrom-Json -InputObject $json -ErrorAction Stop
        if ($result -is [System.Collections.IEnumerable] -and -not ($result -is [string])) {
            $resultArray = @($result)
            if ($resultArray.Count -eq 1) {
                $result = $resultArray[0]
            }
        }
        elseif ($result -isnot [System.Collections.IDictionary] -and $result -isnot [System.Management.Automation.PSObject]) {
            $wrappedOutput = if ($null -ne $result) { @($result) } else { @() }
            $result = [pscustomobject]@{
                Success = $true
                Output  = $wrappedOutput
                Errors  = @()
            }
        }
    }
    finally {
        Remove-Item -Path $tempPath -ErrorAction SilentlyContinue
    }

    return $result
}

function Invoke-ScoopRemoval {
    if (-not (Test-TidyCommand -CommandName 'scoop')) {
        Write-TidyOutput -Message 'Scoop is not installed.'
        return 'Scoop was already absent.'
    }

    Write-TidyLog -Level Information -Message 'Removing Scoop for the current user.'

    $scoopRoot = $null
    $rootCandidates = Get-TidyScoopRootCandidates
    foreach ($candidate in $rootCandidates) {
        if (-not $scoopRoot) {
            $scoopRoot = $candidate
        }

        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            $scoopRoot = $candidate
            break
        }
    }

    if (-not $scoopRoot) {
        $scoopRoot = $env:SCOOP
    }

    if ([string]::IsNullOrWhiteSpace($scoopRoot)) {
        $userProfile = [Environment]::GetFolderPath('UserProfile')
        if (-not [string]::IsNullOrWhiteSpace($userProfile)) {
            $scoopRoot = Join-Path -Path $userProfile -ChildPath 'scoop'
        }
    }

    $canonicalRoot = $null
    if (-not [string]::IsNullOrWhiteSpace($scoopRoot)) {
        try {
            $canonicalRoot = [System.IO.Path]::GetFullPath($scoopRoot)
        }
        catch {
            $canonicalRoot = $scoopRoot
        }
    }

    $uninstallScript = $null
    if ($canonicalRoot) {
        $candidate = Join-Path -Path $canonicalRoot -ChildPath 'apps\scoop\current\bin\uninstall.ps1'
        if (Test-Path -LiteralPath $candidate) {
            $uninstallScript = $candidate
        }
    }

    if ($uninstallScript) {
        $shell = Get-TidyPowerShellExecutable
        Invoke-TidyCommand -Command { param($exe, $scriptPath) & $exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath } -Arguments @($shell, $uninstallScript) -Description 'Running Scoop uninstall script.' -RequireSuccess | Out-Null
    }
    else {
        Invoke-TidyCommand -Command { scoop uninstall scoop } -Description 'Running Scoop self-uninstall.' -RequireSuccess | Out-Null
    }

    $cleanupTargets = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in $rootCandidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            [void]$cleanupTargets.Add($candidate)
        }
    }

    $userProfile = [Environment]::GetFolderPath('UserProfile')
    if (-not [string]::IsNullOrWhiteSpace($userProfile)) {
        [void]$cleanupTargets.Add((Join-Path -Path $userProfile -ChildPath 'scoop-global'))
    }

    $programData = [Environment]::GetFolderPath('CommonApplicationData')
    if (-not [string]::IsNullOrWhiteSpace($programData)) {
        [void]$cleanupTargets.Add((Join-Path -Path $programData -ChildPath 'scoop-global'))
    }

    foreach ($path in $cleanupTargets) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        if (-not (Test-Path -LiteralPath $path)) {
            continue
        }

        try {
            $resolved = [System.IO.Path]::GetFullPath($path)
        }
        catch {
            $resolved = $path
        }

        try {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            Write-TidyOutput -Message ("Removed Scoop directory at {0}." -f $resolved)
        }
        catch {
            Write-TidyLog -Level Warning -Message ("Failed to remove Scoop directory at {0}. {1}" -f $resolved, $_.Exception.Message)
        }
    }

    Remove-Item Env:SCOOP -ErrorAction SilentlyContinue
    Remove-Item Env:SCOOP_GLOBAL -ErrorAction SilentlyContinue

    return 'Scoop removal completed.'
}

function Invoke-ChocolateyRemoval {
    if ((-not (Test-TidyAdmin)) -and (-not $Elevated.IsPresent)) {
        $elevationResult = Request-ChocolateyElevation -ManagerName $Manager

        if ($null -eq $elevationResult) {
            throw 'Failed to capture the elevated Chocolatey result.'
        }

        $outputValue = $elevationResult.Output
        if ($null -eq $outputValue) {
            $outputValue = Get-TidyResultProperty -Result $elevationResult -Name 'Output'
        }

        $errorValue = $elevationResult.Errors
        if ($null -eq $errorValue) {
            $errorValue = Get-TidyResultProperty -Result $elevationResult -Name 'Errors'
        }

        $successValue = $elevationResult.Success
        if ($null -eq $successValue) {
            $successValue = Get-TidyResultProperty -Result $elevationResult -Name 'Success'
        }

        $outputLines = @()
        foreach ($line in @($outputValue)) {
            if ($null -ne $line -and -not [string]::IsNullOrWhiteSpace([string]$line)) {
                $outputLines += [string]$line
            }
        }

        $errorLines = @()
        foreach ($line in @($errorValue)) {
            if ($null -ne $line -and -not [string]::IsNullOrWhiteSpace([string]$line)) {
                $errorLines += [string]$line
            }
        }

        foreach ($line in $outputLines) {
            Write-TidyOutput -Message $line
        }

        foreach ($line in $errorLines) {
            Write-TidyError -Message $line
        }

        $elevatedSucceeded = $true
        if ($successValue -is [bool]) {
            $elevatedSucceeded = $successValue
        }
        elseif ($null -ne $successValue) {
            try {
                $elevatedSucceeded = [System.Convert]::ToBoolean($successValue)
            }
            catch {
                $elevatedSucceeded = $true
            }
        }

        if (-not $elevatedSucceeded) {
            throw 'Chocolatey uninstall failed when running with administrator privileges.'
        }

        if ($outputLines.Count -gt 0) {
            return $outputLines[$outputLines.Count - 1]
        }

        return 'Chocolatey uninstall completed with administrator privileges.'
    }

    if (-not (Test-TidyCommand -CommandName 'choco')) {
        Write-TidyOutput -Message 'Chocolatey is not installed.'
        return 'Chocolatey was already absent.'
    }

    Write-TidyLog -Level Information -Message 'Removing Chocolatey via choco uninstall.'
    Invoke-TidyCommand -Command { choco uninstall chocolatey -y --remove-dependencies } -Description 'Running Chocolatey uninstall.' -RequireSuccess | Out-Null

    $installRoot = $env:ChocolateyInstall
    if ([string]::IsNullOrWhiteSpace($installRoot)) {
        $installRoot = 'C:\ProgramData\chocolatey'
    }

    if (Test-Path -LiteralPath $installRoot) {
        try {
            Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction Stop
            Write-TidyOutput -Message ("Removed Chocolatey directory at {0}." -f $installRoot)
        }
        catch {
            Write-TidyLog -Level Warning -Message ("Failed to remove Chocolatey directory at {0}. {1}" -f $installRoot, $_.Exception.Message)
        }
    }

    Remove-Item Env:ChocolateyInstall -ErrorAction SilentlyContinue

    return 'Chocolatey removal completed.'
}

function Invoke-WingetRemoval {
    Write-TidyLog -Level Information -Message 'Removing winget (App Installer).'

    $packages = @()
    try {
        $packages = @(Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' -ErrorAction Stop)
    }
    catch {
        $packages = @()
    }

    $removedAny = $false
    $isAdmin = Test-TidyAdmin

    if ($packages.Count -eq 0) {
        Write-TidyOutput -Message 'App Installer is not installed for the current user.'
    }
    else {
        foreach ($package in @($packages)) {
            $fullName = $package.PackageFullName
            $description = if ($isAdmin) {
                "Removing App Installer package '$fullName' for all users."
            }
            else {
                "Removing App Installer package '$fullName' for the current user."
            }

            if ($isAdmin) {
                Invoke-TidyCommand -Command { param($name) Remove-AppxPackage -Package $name -AllUsers -ErrorAction Stop } -Arguments @($fullName) -Description $description -RequireSuccess | Out-Null
            }
            else {
                Invoke-TidyCommand -Command { param($name) Remove-AppxPackage -Package $name -ErrorAction Stop } -Arguments @($fullName) -Description $description -RequireSuccess | Out-Null
            }

            $removedAny = $true
        }
    }

    try {
        $provisioned = @()
        if ($isAdmin) {
            $provisioned = @(Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq 'Microsoft.DesktopAppInstaller' })
            foreach ($entry in @($provisioned)) {
                Invoke-TidyCommand -Command { param($packageName) Remove-AppxProvisionedPackage -Online -PackageName $packageName -ErrorAction Stop } -Arguments @($entry.PackageName) -Description ("Removing provisioned App Installer package '{0}'." -f $entry.PackageName) -RequireSuccess | Out-Null
            }
        }
        else {
            $provisioned = @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq 'Microsoft.DesktopAppInstaller' })
            if ($provisioned.Count -gt 0) {
                Write-TidyOutput -Message 'Provisioned App Installer package detected. Run removal again from an elevated session to remove it for all users.'
            }
        }
    }
    catch {
        Write-TidyLog -Level Warning -Message ("Provisioned App Installer cleanup encountered an error. {0}" -f $_.Exception.Message)
    }

    if ($removedAny) {
        Write-TidyOutput -Message 'App Installer removal completed. Windows may reinstall it during servicing updates.'
        return 'winget removal completed.'
    }

    return 'winget was already absent.'
}

function Get-TidyResultProperty {
    param(
        [Parameter(Mandatory = $true)]
        $Result,
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($null -eq $Result) {
        return $null
    }

    if ($Result -is [System.Collections.IDictionary]) {
        if ($Result.Contains($Name)) {
            return $Result[$Name]
        }

        if ($Result.ContainsKey($Name)) {
            return $Result[$Name]
        }

        return $null
    }

    try {
        $value = $Result | Select-Object -ExpandProperty $Name -ErrorAction Stop
        return $value
    }
    catch {
        return $null
    }
}

$normalized = ($Manager ?? '').Trim().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($normalized)) {
    throw 'Manager name must be provided.'
}

try {
    Write-TidyLog -Level Information -Message ("Uninstall requested for manager '{0}'. Elevated flag: {1}" -f $Manager, $Elevated.IsPresent)
    switch ($normalized) {
        'scoop' {
            $message = Invoke-ScoopRemoval
            Write-TidyOutput -Message $message
        }
        'scoop package manager' {
            $message = Invoke-ScoopRemoval
            Write-TidyOutput -Message $message
        }
        'choco' {
            $message = Invoke-ChocolateyRemoval
            Write-TidyOutput -Message $message
        }
        'chocolatey' {
            $message = Invoke-ChocolateyRemoval
            Write-TidyOutput -Message $message
        }
        'chocolatey cli' {
            $message = Invoke-ChocolateyRemoval
            Write-TidyOutput -Message $message
        }
        'winget' {
            $message = Invoke-WingetRemoval
            Write-TidyOutput -Message $message
        }
        default {
            throw "Package manager '$Manager' is not supported by the uninstaller."
        }
    }

    $commandLookupName = switch ($normalized) {
        'chocolatey' { 'choco' }
        'chocolatey cli' { 'choco' }
        'scoop package manager' { 'scoop' }
        default { $normalized }
    }

    $commandPath = Get-TidyCommandPath -CommandName $commandLookupName
    if ([string]::IsNullOrWhiteSpace($commandPath)) {
        Write-TidyOutput -Message ("'{0}' no longer resolves on PATH after the uninstall attempt." -f $Manager)
    }
    else {
        Write-TidyLog -Level Warning -Message ("'{0}' still resolves to {1} after uninstall." -f $Manager, $commandPath)
        Write-TidyOutput -Message ("'{0}' still resolves on PATH at {1}. Restart your shell or remove the folder manually if needed." -f $Manager, $commandPath)
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
    Write-TidyLog -Level Information -Message ("Uninstall flow finished for manager '{0}'." -f $Manager)
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

