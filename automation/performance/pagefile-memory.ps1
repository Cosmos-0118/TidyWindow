[CmdletBinding()]
param(
    [switch]$Detect,
    [string]$Preset = "SystemManaged",
    [string]$TargetDrive,
    [int]$InitialMB,
    [int]$MaxMB,
    [switch]$SweepWorkingSets,
    [switch]$IncludePinned,
    [switch]$PassThru
)

function Assert-Elevation {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw 'Elevation required: run as administrator to change pagefile settings.'
    }
}

function Get-PagefileState {
    $usage = Get-CimInstance -ClassName Win32_PageFileUsage -ErrorAction SilentlyContinue
    $settings = Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction SilentlyContinue

    $automatic = (Get-CimInstance -ClassName Win32_ComputerSystem -Property AutomaticManagedPagefile -ErrorAction SilentlyContinue).AutomaticManagedPagefile
    $entries = @()
    foreach ($setting in ($settings | Sort-Object Name)) {
        $usageMatch = $usage | Where-Object { $_.Name -eq $setting.Name }
        $entries += [PSCustomObject]@{
            Name        = $setting.Name
            InitialMB   = $setting.InitialSize
            MaximumMB   = $setting.MaximumSize
            CurrentMB   = $usageMatch.CurrentUsage
            PeakMB      = $usageMatch.PeakUsage
        }
    }

    return [PSCustomObject]@{
        AutomaticManaged = [bool]$automatic
        Entries          = $entries
    }
}

function Set-AutomaticManagedState {
    param(
        [bool]$Enable,
        [ref]$Warnings
    )

    $desired = [bool]$Enable

    $getState = {
        try {
            return (Get-CimInstance -ClassName Win32_ComputerSystem -Property AutomaticManagedPagefile -ErrorAction Stop).AutomaticManagedPagefile
        }
        catch {
            try {
                $mmKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
                $paging = (Get-ItemProperty -Path $mmKey -Name PagingFiles -ErrorAction Stop).PagingFiles
                if ($paging -is [array]) {
                    if ($paging.Count -eq 0) { return $false }
                    if ($paging.Count -eq 1 -and [string]::IsNullOrWhiteSpace($paging[0])) { return $true }
                    return $false
                }
                elseif ($paging -is [string]) {
                    if ([string]::IsNullOrWhiteSpace($paging)) { return $true }
                    return $false
                }
            }
            catch {
                return $null
            }
            return $null
        }
    }

    $initial = & $getState
    if ($initial -eq $desired) {
        return $true
    }

    $attempts = @(
        @{ name = "CIM"; action = {
                $cs = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
                Set-CimInstance -InputObject $cs -Property @{ AutomaticManagedPagefile = $desired } -ErrorAction Stop | Out-Null
            } },
        @{ name = "WMI"; action = {
                $cs = [wmi]"Win32_ComputerSystem.Name='$env:COMPUTERNAME'"
                $cs.AutomaticManagedPagefile = $desired
                $cs.Put() | Out-Null
            } },
        @{ name = "WMIC"; action = {
                $wmicCmd = Get-Command wmic -ErrorAction SilentlyContinue
                if (-not $wmicCmd) { throw "wmic not available" }
                $val = if ($desired) { "true" } else { "false" }
                $wmic = & wmic computersystem where name="%computername%" set AutomaticManagedPagefile=$val 2>&1
                if ($LASTEXITCODE -ne 0) {
                    throw "wmic exit ${LASTEXITCODE}: $wmic"
                }
            } },
        @{ name = "Registry"; action = {
                $mmKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
                if ($desired) {
                    # Empty the PagingFiles value to let the OS manage automatically
                    Set-ItemProperty -Path $mmKey -Name PagingFiles -Value @('') -ErrorAction Stop
                }
                else {
                    # Setting a manual pagefile will rewrite PagingFiles later; here we just ensure auto is off
                    Set-ItemProperty -Path $mmKey -Name PagingFiles -Value @() -ErrorAction Stop
                }
            } }
    )

    foreach ($attempt in $attempts) {
        try {
            & $attempt.action
            $state = & $getState
            if ($state -eq $desired) {
                return $true
            }
        }
        catch {
            $Warnings.Value += "Failed to set AutomaticManagedPagefile=$desired via $($attempt.name): $_"
        }
    }

    $final = & $getState
    return ($final -eq $desired)
}

function Set-SystemManaged {
    param([ref]$Warnings)

    try {
        $autoOk = Set-AutomaticManagedState -Enable:$true -Warnings:[ref]$Warnings
        if (-not $autoOk) {
            $Warnings.Value += "Automatic managed state could not be confirmed as enabled."
            return $false
        }

        try {
            Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction SilentlyContinue | ForEach-Object { $_ | Remove-CimInstance -ErrorAction SilentlyContinue }
        }
        catch {
            try {
                Get-WmiObject -Class Win32_PageFileSetting -ErrorAction SilentlyContinue | ForEach-Object { $_.Delete() | Out-Null }
            }
            catch {
                $Warnings.Value += "Failed to clear explicit pagefile settings: $_"
            }
        }

        return $true
    }
    catch {
        $Warnings.Value += "Failed to enable system-managed pagefile: $_"
        return $false
    }
}

function Set-ManualPagefile {
    param(
        [string]$Drive,
        [int]$Initial,
        [int]$Maximum,
        [ref]$Warnings
    )

    try {
        if (-not $Drive.EndsWith(":\\")) {
            $Drive = $Drive.TrimEnd('\\').TrimEnd(':') + ':\\'
        }
        $path = "$Drive\\pagefile.sys"

        $autoOk = Set-AutomaticManagedState -Enable:$false -Warnings:$Warnings
        if (-not $autoOk) {
            $Warnings.Value += "Automatic managed state could not be confirmed as disabled; continuing to apply manual settings."
        }

        try {
            Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction SilentlyContinue | ForEach-Object { $_ | Remove-CimInstance -ErrorAction SilentlyContinue }
        }
        catch {
            try {
                Get-WmiObject -Class Win32_PageFileSetting -ErrorAction SilentlyContinue | ForEach-Object { $_.Delete() | Out-Null }
            }
            catch {
                $Warnings.Value += "Failed to clear existing pagefile settings: $_"
            }
        }

        $applyRegistry = {
            try {
                $mmKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
                if (-not (Test-Path $mmKey)) { throw "Registry key $mmKey missing" }
                $pagingValue = "$path $Initial $Maximum"
                if (Get-ItemProperty -Path $mmKey -Name PagingFiles -ErrorAction SilentlyContinue) {
                    Set-ItemProperty -Path $mmKey -Name PagingFiles -Value @($pagingValue) -ErrorAction Stop
                }
                else {
                    New-ItemProperty -Path $mmKey -Name PagingFiles -PropertyType MultiString -Value @($pagingValue) -Force -ErrorAction Stop | Out-Null
                }

                if (Get-ItemProperty -Path $mmKey -Name ExistingPageFiles -ErrorAction SilentlyContinue) {
                    Set-ItemProperty -Path $mmKey -Name ExistingPageFiles -Value @($path) -ErrorAction Stop
                }
                else {
                    New-ItemProperty -Path $mmKey -Name ExistingPageFiles -PropertyType MultiString -Value @($path) -Force -ErrorAction Stop | Out-Null
                }

                Set-ItemProperty -Path $mmKey -Name TempPageFile -Value 0 -ErrorAction SilentlyContinue
                return $true
            }
            catch {
                $Warnings.Value += "Failed to set manual pagefile via registry: $_"
                return $false
            }
        }

        try {
            $created = Invoke-WmiMethod -Class Win32_PageFileSetting -Name Create -ArgumentList $path, $Initial, $Maximum -ErrorAction Stop
            if ($created.ReturnValue -ne 0) {
                $Warnings.Value += "Pagefile create returned code $($created.ReturnValue); attempting registry fallback."
                return (& $applyRegistry)
            }
        }
        catch {
            $Warnings.Value += "Failed to set manual pagefile via WMI: $_; attempting registry fallback."
            return (& $applyRegistry)
        }

        return $true
    }
    catch {
        $Warnings.Value += "Failed to set manual pagefile: $_"
        return $false
    }
}

function Invoke-WorkingSetSweep {
    param(
        [bool]$IncludePinned,
        [ref]$Warnings
    )

    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class PsapiNative
{
    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);
}
"@ -ErrorAction SilentlyContinue

    $processes = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'Idle' -and $_.Name -ne 'System' }
    if (-not $IncludePinned) {
        $processes = $processes | Where-Object { $_.MinWorkingSet -lt $_.WorkingSet64 }
    }

    $swept = 0
    foreach ($proc in $processes) {
        try {
            $ok = [PsapiNative]::EmptyWorkingSet($proc.Handle)
            if ($ok) { $swept++ }
        }
        catch {
            $Warnings.Value += "Sweep failed for $($proc.ProcessName): $_"
        }
    }

    return $swept
}

$warnings = @()
$actions = @()

$stateBefore = Get-PagefileState

if ($Detect) {
    $actions += "Detect"
}
elseif ($SweepWorkingSets -or $PSBoundParameters.ContainsKey('SweepWorkingSets')) {
    $actions += "SweepWorkingSets"
}
else {
    $actions += "Apply"
}

if ($SweepWorkingSets) {
    $sweptCount = Invoke-WorkingSetSweep -IncludePinned:$IncludePinned -Warnings:[ref]$warnings
    $actions += "Swept=$sweptCount"
}

if (-not $Detect -and -not $SweepWorkingSets) {
    Assert-Elevation
    $preset = if ([string]::IsNullOrWhiteSpace($Preset)) { "SystemManaged" } else { $Preset }
    switch ($preset.ToLowerInvariant()) {
        "systemmanaged" {
            if (Set-SystemManaged -Warnings:[ref]$warnings) {
                $actions += "SystemManaged"
            }
        }
        default {
            $drive = if ([string]::IsNullOrWhiteSpace($TargetDrive)) { $env:SystemDrive } else { $TargetDrive }
            $initialSize = if ($InitialMB -gt 0) { $InitialMB } else { 4096 }
            $maxSize = if ($MaxMB -gt 0 -and $MaxMB -ge $initialSize) { $MaxMB } else { [Math]::Max($initialSize * 3, 12288) }

            $warningsRef = [ref]$warnings
            if (Set-ManualPagefile -Drive:$drive -Initial:$initialSize -Maximum:$maxSize -Warnings:$warningsRef) {
                $actions += "Preset=$preset@$drive"; $actions += "InitialMB=$initialSize"; $actions += "MaxMB=$maxSize"
            }
            else {
                $actions += "PresetFailed=$preset"
            }
        }
    }
}

$stateAfter = Get-PagefileState

if ($PassThru) {
    [PSCustomObject]@{
        actions         = ($actions -join ", ")
        automaticManaged= $stateAfter.AutomaticManaged
        entries         = $stateAfter.Entries
        sweepRan        = $SweepWorkingSets
        warnings        = $warnings
        beforeAutomatic = $stateBefore.AutomaticManaged
        beforeEntries   = $stateBefore.Entries
    }
}
