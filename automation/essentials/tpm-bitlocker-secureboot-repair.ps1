param(
    [switch] $SkipTpmClear,
    [switch] $SkipBitLockerCycle,
    [switch] $SkipSecureBootGuidance,
    [switch] $SkipDeviceEncryptionPrereqs,
    [switch] $ShowTpmStatus,
    [switch] $ShowBitLockerStatus,
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
        [int] $TimeoutSeconds = 30
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

function Ensure-ServiceReady {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [string] $StartupType = 'Manual',
        [switch] $StartService
    )

    try {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            Write-TidyOutput -Message ("Service {0} not found. Skipping." -f $Name)
            return
        }

        $startTypeChanged = $false
        if ($service.StartType -eq 'Disabled') {
            Write-TidyOutput -Message ("Service {0} is Disabled; setting StartupType to {1}." -f $Name, $StartupType)
            try {
                Set-Service -Name $Name -StartupType $StartupType -ErrorAction Stop
                $startTypeChanged = $true
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to set StartupType for {0}: {1}" -f $Name, $_.Exception.Message)
            }
        }

        if ($StartService.IsPresent -and $service.Status -ne 'Running') {
            $action = if ($service.Status -eq 'Stopped') { 'Starting' } else { 'Restarting' }
            Write-TidyOutput -Message ("{0} service {1}." -f $action, $Name)
            try {
                if ($service.Status -eq 'Stopped') {
                    Invoke-TidyCommand -Command { param($svcName) Start-Service -Name $svcName -ErrorAction Stop } -Arguments @($Name) -Description ("Starting service {0}." -f $Name) -RequireSuccess
                }
                else {
                    Invoke-TidyCommand -Command { param($svcName) Restart-Service -Name $svcName -ErrorAction Stop } -Arguments @($Name) -Description ("Restarting service {0}." -f $Name) -RequireSuccess
                }

                if (-not (Wait-TidyServiceState -Name $Name -DesiredStatus 'Running' -TimeoutSeconds 15)) {
                    Write-TidyOutput -Message ("Service {0} did not reach Running state after restart." -f $Name)
                }
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to start or restart service {0}: {1}" -f $Name, $_.Exception.Message)
            }
        }
        elseif ($startTypeChanged) {
            # Refresh status after changing startup type when not asked to start now.
            $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Service prep for {0} failed: {1}" -f $Name, $_.Exception.Message)
    }
}

function Cycle-BitLockerProtectors {
    param(
        [string] $MountPoint = $env:SystemDrive
    )

    try {
        $volume = Get-BitLockerVolume -MountPoint $MountPoint -ErrorAction Stop
        if ($null -eq $volume) {
            Write-TidyOutput -Message ("BitLocker volume not found for {0}." -f $MountPoint)
            return
        }

        if ($volume.ProtectionStatus -eq 'Off') {
            Write-TidyOutput -Message ("BitLocker is not protecting {0}; skipping suspend/resume." -f $MountPoint)
            return
        }

        Write-TidyOutput -Message ("Suspending BitLocker protectors on {0} for one reboot." -f $MountPoint)
        try {
            Suspend-BitLocker -MountPoint $MountPoint -RebootCount 1 -ErrorAction Stop | Out-Null
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to suspend BitLocker on {0}: {1}" -f $MountPoint, $_.Exception.Message)
            return
        }

        Write-TidyOutput -Message ("Resuming BitLocker protectors on {0}." -f $MountPoint)
        try {
            Resume-BitLocker -MountPoint $MountPoint -ErrorAction Stop | Out-Null
            Write-TidyOutput -Message ("BitLocker suspend/resume completed on {0}." -f $MountPoint)
        }
        catch {
            $script:OperationSucceeded = $false
            Write-TidyError -Message ("Failed to resume BitLocker on {0}: {1}" -f $MountPoint, $_.Exception.Message)
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("BitLocker cycle failed on {0}: {1}" -f $MountPoint, $_.Exception.Message)
    }
}

function Request-TpmClear {
    try {
        $tpm = Get-Tpm -ErrorAction Stop
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Get-Tpm failed: {0}" -f $_.Exception.Message)
        return
    }

    if (-not $tpm.TpmPresent) {
        Write-TidyOutput -Message 'No TPM detected on this system. Skipping TPM clear.'
        return
    }

    if ($tpm.LockedOut) {
        Write-TidyOutput -Message 'TPM is locked out; clear request may require cooldown or firmware unlock.'
    }

    if ($tpm.OwnerClearDisabled) {
        Write-TidyOutput -Message 'Owner clear is disabled in firmware. Enable it in UEFI to proceed.'
        return
    }

    if (-not $tpm.TpmReady) {
        Write-TidyOutput -Message 'TPM is not fully provisioned/ready; skipping Clear-Tpm to avoid failing the run. Initialize TPM in firmware, then retry if needed.'
        return
    }

    Write-TidyOutput -Message 'Requesting TPM clear. A reboot and owner confirmation may be required; backup recovery keys first.'

    try {
        $result = Clear-Tpm -ErrorAction Stop

        $clearRequested = $false
        if ($null -ne $result -and $result -is [psobject]) {
            $prop = $result.PSObject.Properties['ClearTpmRequested']
            if ($prop) {
                $clearRequested = [bool]$prop.Value
            }
        }

        $clearStatus = if ($clearRequested) { 'Clear request submitted.' } else { 'Clear command issued.' }
        Write-TidyOutput -Message $clearStatus
    }
    catch {
        $message = $_.Exception.Message
        if ($message -match '0x80290401' -or $message -match 'Platform Crypto Device is currently not ready') {
            Write-TidyOutput -Message ('TPM clear skipped: TPM not ready/provisioned ({0}). Initialize TPM in firmware, then retry.' -f $message)
            return
        }

        $script:OperationSucceeded = $false
        Write-TidyError -Message ("TPM clear failed: {0}" -f $message)
    }
}

function Show-TpmStatusVerbose {
    try {
        $tpm = Get-Tpm -ErrorAction Stop
    }
    catch {
        Write-TidyOutput -Message ("Unable to query TPM status: {0}" -f $_.Exception.Message)
        return
    }

    Write-TidyOutput -Message ("TPM Present: {0}; Ready: {1}; LockedOut: {2}; OwnerClearDisabled: {3}" -f $tpm.TpmPresent, $tpm.TpmReady, $tpm.LockedOut, $tpm.OwnerClearDisabled)

    if ($tpm.ManagedAuthLevel) {
        Write-TidyOutput -Message ("ManagedAuthLevel: {0}" -f $tpm.ManagedAuthLevel)
    }

    $manufacturer = $tpm | Select-Object -First 1 -Property ManufacturerIdTxt, SpecVersion
    if ($manufacturer) {
        $mfg = if ($manufacturer.ManufacturerIdTxt) { $manufacturer.ManufacturerIdTxt } else { 'Unknown' }
        $spec = if ($manufacturer.PSObject.Properties['SpecVersion']) { $manufacturer.SpecVersion } else { 'Unknown' }
        Write-TidyOutput -Message ("Manufacturer: {0}; SpecVersion: {1}" -f $mfg, $spec)
    }

    if ($tpm.PSObject.Properties['ActivePcrBanks'] -and $tpm.ActivePcrBanks) {
        $banks = ($tpm.ActivePcrBanks -join ', ')
        Write-TidyOutput -Message ("Active PCR banks: {0}" -f $banks)
        if ($tpm.ActivePcrBanks -contains 'SHA1') {
            Write-TidyOutput -Message 'Warning: SHA1 PCR bank is active; prefer SHA256 when available.'
        }
    }

    if (-not $tpm.TpmReady) {
        Write-TidyOutput -Message 'TPM is present but not ready/provisioned; initialize in firmware/Windows before requesting a clear.'
    }
}

function Show-BitLockerStatus {
    param(
        [string] $MountPoint = $env:SystemDrive
    )

    try {
        Write-TidyOutput -Message ("Querying BitLocker status for {0}." -f $MountPoint)
        $output = & manage-bde -status $MountPoint 2>&1
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            Write-TidyOutput -Message ("manage-bde -status returned exit code {0}." -f $exitCode)
        }
    }
    catch {
        Write-TidyOutput -Message ("Unable to query BitLocker status: {0}" -f $_.Exception.Message)
        return
    }

    if ($null -eq $output) { return }

    $parsed = [ordered]@{}
    $collectorKeys = @('Conversion Status', 'Percentage Encrypted', 'Protection Status', 'Lock Status', 'Key Protectors')
    $currentKey = $null
    $protectors = @()

    foreach ($line in $output) {
        if (-not $line) { continue }

        foreach ($key in $collectorKeys) {
            $pattern = "^\s*{0}:\s*(.+)$" -f [regex]::Escape($key)
            if ($line -match $pattern) {
                $value = $Matches[1].Trim()
                if ($key -eq 'Key Protectors') {
                    $currentKey = 'Key Protectors'
                    continue
                }

                $parsed[$key] = $value
                $currentKey = $null
                break
            }
        }

        if ($currentKey -eq 'Key Protectors') {
            if ($line -match '^\s+([A-Za-z].+)$') {
                $protectors += $Matches[1].Trim()
            }
        }
    }

    if ($protectors.Count -gt 0) {
        $parsed['Key Protectors'] = ($protectors -join '; ')
    }

    if ($parsed.Count -eq 0) {
        Write-TidyOutput -Message 'BitLocker status query returned no parsable data.'
        return
    }

    foreach ($kvp in $parsed.GetEnumerator()) {
        Write-TidyOutput -Message ("BitLocker {0}: {1}" -f $kvp.Key, $kvp.Value)
    }

    if ($parsed.Contains('Protection Status') -and $parsed['Protection Status'] -match 'Off') {
        Write-TidyOutput -Message 'BitLocker protection is off/suspended on the target volume.'
    }
    if ($parsed.Contains('Conversion Status') -and $parsed['Conversion Status'] -notmatch 'Fully Encrypted') {
        Write-TidyOutput -Message 'BitLocker conversion is not fully encrypted; investigate to ensure protection is complete.'
    }
}

function Publish-SecureBootGuidance {
    try {
        $secureBootEnabled = $null
        try {
            $secureBootEnabled = Confirm-SecureBootUEFI
        }
        catch {
            $secureBootEnabled = $null
        }

        if ($secureBootEnabled -eq $true) {
            Write-TidyOutput -Message 'Secure Boot is enabled.'
        }
        elseif ($secureBootEnabled -eq $false) {
            Write-TidyOutput -Message 'Secure Boot is disabled or unsupported. Enable it in firmware for enforcement.'
        }
        else {
            Write-TidyOutput -Message 'Secure Boot state could not be queried (BIOS or permission limitation).'
        }

        Write-TidyOutput -Message 'To reseed Secure Boot keys, use firmware setup (often called "Restore Factory Keys"), then save and reboot.'
        Write-TidyOutput -Message "Shortcut to firmware menus: run 'shutdown /r /fw /t 0' in an elevated terminal (does not execute automatically here)."
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Secure Boot guidance failed: {0}" -f $_.Exception.Message)
    }
}

function Enable-DeviceEncryptionPrereqs {
    try {
        $paths = @(
            @{ Path = 'HKLM:SOFTWARE\Microsoft\Windows\CurrentVersion\DeviceEncryption'; Name = 'DisableDeviceEncryption'; Value = 0 },
            @{ Path = 'HKLM:SOFTWARE\Policies\Microsoft\FVE'; Name = 'DisableDeviceEncryption'; Value = 0 },
            @{ Path = 'HKLM:SYSTEM\CurrentControlSet\Control\BitLocker'; Name = 'PreventDeviceEncryption'; Value = 0 }
        )

        foreach ($entry in $paths) {
            if (-not (Test-Path -Path $entry.Path)) {
                New-Item -Path $entry.Path -Force | Out-Null
            }

            Write-TidyOutput -Message ("Setting {0}\{1} to {2}." -f $entry.Path, $entry.Name, $entry.Value)
            try {
                Set-ItemProperty -Path $entry.Path -Name $entry.Name -Value $entry.Value -Type DWord -Force -ErrorAction Stop
            }
            catch {
                $script:OperationSucceeded = $false
                Write-TidyError -Message ("Failed to set {0}\{1}: {2}" -f $entry.Path, $entry.Name, $_.Exception.Message)
            }
        }
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Device encryption registry prep failed: {0}" -f $_.Exception.Message)
    }

    $services = @(
        @{ Name = 'BDESVC'; Start = 'Manual'; StartService = $true },
        @{ Name = 'KeyIso'; Start = 'Manual'; StartService = $true },
        @{ Name = 'DeviceInstall'; Start = 'Manual'; StartService = $true },
        @{ Name = 'PlugPlay'; Start = 'Manual'; StartService = $true }
    )

    foreach ($svc in $services) {
        Ensure-ServiceReady -Name $svc.Name -StartupType $svc.Start -StartService:$svc.StartService
    }

    Write-TidyOutput -Message 'Device encryption prerequisites refreshed.'
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'TPM/BitLocker/Secure Boot repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting TPM, BitLocker, and Secure Boot repair pack.'

    if ($ShowTpmStatus.IsPresent) {
        Show-TpmStatusVerbose
    }

    if ($ShowBitLockerStatus.IsPresent) {
        Show-BitLockerStatus
    }

    if (-not $SkipBitLockerCycle.IsPresent) {
        Cycle-BitLockerProtectors
    }
    else {
        Write-TidyOutput -Message 'Skipping BitLocker suspend/resume per operator request.'
    }

    if (-not $SkipTpmClear.IsPresent) {
        Request-TpmClear
    }
    else {
        Write-TidyOutput -Message 'Skipping TPM clear per operator request.'
    }

    if (-not $SkipSecureBootGuidance.IsPresent) {
        Publish-SecureBootGuidance
    }
    else {
        Write-TidyOutput -Message 'Skipping Secure Boot guidance per operator request.'
    }

    if (-not $SkipDeviceEncryptionPrereqs.IsPresent) {
        Enable-DeviceEncryptionPrereqs
    }
    else {
        Write-TidyOutput -Message 'Skipping device encryption prerequisite refresh per operator request.'
    }
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message ("TPM/BitLocker/Secure Boot repair failed: {0}" -f $_.Exception.Message)
}
finally {
    Save-TidyResult
}
