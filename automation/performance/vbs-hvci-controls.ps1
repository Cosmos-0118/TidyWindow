[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Detect,
    [switch] $Disable,
    [switch] $RestoreDefaults,
    [switch] $PassThru
)

set-strictmode -version latest
$ErrorActionPreference = 'Stop'

function Assert-Elevation {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw 'Elevation required: run as administrator to change VBS/HVCI settings.'
    }
}

function Get-DeviceGuardState {
    $dg = Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction SilentlyContinue
    $hvciConfigured = $false
    $hvciRunning = $false
    $vbsConfigured = $false

    if ($dg) {
        $hvciConfigured = $dg.SecurityServicesConfigured -contains 1
        $hvciRunning = $dg.SecurityServicesRunning -contains 1
        $vbsConfigured = ($dg.VirtualizationBasedSecurityStatus -eq 2)
    }

    $regValue = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -ErrorAction SilentlyContinue
    if ($regValue -and $regValue.PSObject.Properties['Enabled']) {
        $hvciConfigured = ($regValue.Enabled -eq 1)
    }

    return [pscustomobject]@{
        HvciConfigured = $hvciConfigured
        HvciRunning    = $hvciRunning
        VbsConfigured  = $vbsConfigured
    }
}

function Get-HypervisorLaunchType {
    $output = & bcdedit /enum {current} 2>&1
    $line = $output | Where-Object { $_ -match '^hypervisorlaunchtype' }
    if (-not $line) { return 'Auto (default)' }
    $parts = $line -split '\s+' | Where-Object { $_ }
    if ($parts.Length -ge 2) { return $parts[-1] }
    return $line.Trim()
}

function Ensure-RegistryKey {
    param([string] $Path)
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
}

$intent = 'Detect'
if ($Disable) { $intent = 'Disable' }
elseif ($RestoreDefaults) { $intent = 'RestoreDefaults' }
elseif ($Detect) { $intent = 'Detect' }

Assert-Elevation

$state = Get-DeviceGuardState
$hyperType = Get-HypervisorLaunchType
$changes = @()
$failures = @()

switch ($intent) {
    'Disable' {
        Ensure-RegistryKey -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity'
        try {
            Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -Name Enabled -Value 0 -Type DWord -Force
            $changes += 'HVCI disabled (registry)'
        }
        catch {
            $failures += "Failed to disable HVCI: $($_.Exception.Message)"
        }

        $cmd = @('/set','{current}','hypervisorlaunchtype','off')
        if ($PSCmdlet.ShouldProcess('bcdedit', ($cmd -join ' '))) {
            $output = & bcdedit @cmd 2>&1
            if ($LASTEXITCODE -ne 0) {
                $failures += "bcdedit hypervisorlaunchtype off returned $($LASTEXITCODE): $output"
            }
            else {
                $changes += 'hypervisorlaunchtype set to off'
            }
        }
    }
    'RestoreDefaults' {
        try {
            if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity') {
                Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -Name Enabled -ErrorAction SilentlyContinue
                $changes += 'HVCI registry flag cleared'
            }
        }
        catch {
            $failures += "Failed to clear HVCI registry flag: $($_.Exception.Message)"
        }

        $cmd = @('/deletevalue','{current}','hypervisorlaunchtype')
        if ($PSCmdlet.ShouldProcess('bcdedit', ($cmd -join ' '))) {
            $output = & bcdedit @cmd 2>&1
            $outputText = ($output -join "`n")
            $isMissingElement = $LASTEXITCODE -eq 1 -and $outputText -match 'Element not found'

            if ($LASTEXITCODE -eq 0) {
                $changes += 'hypervisorlaunchtype cleared (default auto)'
            }
            elseif ($isMissingElement) {
                # Treat already-default state as success when no value exists to delete.
                $changes += 'hypervisorlaunchtype already at default'
            }
            else {
                # Fallback to auto only when delete genuinely fails for another reason.
                $fallback = & bcdedit /set {current} hypervisorlaunchtype auto 2>&1
                if ($LASTEXITCODE -ne 0) {
                    $failures += "bcdedit restore hypervisorlaunchtype failed: $fallback"
                }
                else {
                    $changes += 'hypervisorlaunchtype set to auto'
                }
            }
        }
    }
    default {
        # Detect only
    }
}

# Refresh hypervisor launch type after any changes so PassThru reflects post-operation state
$hyperType = Get-HypervisorLaunchType

$payload = [pscustomobject]@{
    action = $intent
    hvciConfigured = $state.HvciConfigured
    hvciRunning = $state.HvciRunning
    vbsConfigured = $state.VbsConfigured
    hypervisorLaunchType = $hyperType
    rebootRequired = ($intent -ne 'Detect')
}

if ($changes.Count -gt 0) {
    $payload | Add-Member -NotePropertyName changes -NotePropertyValue $changes
}

if ($failures.Count -gt 0) {
    $payload | Add-Member -NotePropertyName failures -NotePropertyValue $failures
}

if ($PassThru) {
    $payload
}

if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Warning $f }
    throw 'One or more VBS/HVCI operations failed.'
}

