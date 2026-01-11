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

function Set-SystemManaged {
    param([ref]$Warnings)

    try {
        Set-CimInstance -ClassName Win32_ComputerSystem -Property @{ AutomaticManagedPagefile = $true } -ErrorAction Stop | Out-Null
        # Remove explicit pagefile settings so Windows controls the size.
        Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction SilentlyContinue | ForEach-Object { $_ | Remove-CimInstance -ErrorAction SilentlyContinue }
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

        Set-CimInstance -ClassName Win32_ComputerSystem -Property @{ AutomaticManagedPagefile = $false } -ErrorAction Stop | Out-Null
        Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction SilentlyContinue | ForEach-Object { $_ | Remove-CimInstance -ErrorAction SilentlyContinue }

        $arguments = @{ Name = $path; InitialSize = $Initial; MaximumSize = $Maximum }
        $created = Invoke-CimMethod -ClassName Win32_PageFileSetting -MethodName Create -Arguments $arguments -ErrorAction Stop
        if ($created.ReturnValue -ne 0) {
            $Warnings.Value += "Pagefile create returned code $($created.ReturnValue)"
            return $false
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

            if (Set-ManualPagefile -Drive:$drive -Initial:$initialSize -Maximum:$maxSize -Warnings:[ref]$warnings) {
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
