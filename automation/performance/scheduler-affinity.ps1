[CmdletBinding()]
param(
    [switch]$Detect,
    [string]$Preset = "Balanced",
    [string[]]$ProcessNames,
    [switch]$RestoreDefaults,
    [switch]$PassThru
)

function Get-FullMask {
    $count = [Environment]::ProcessorCount
    if ($count -ge 63) {
        return [int64]::MaxValue
    }

    return ([int64]1 -shl $count) - 1
}

function Get-HalfMask {
    $count = [Environment]::ProcessorCount
    $half = [Math]::Max([int][Math]::Ceiling($count / 2.0), 1)
    return ([int64]1 -shl $half) - 1
}

function Apply-Preset {
    param(
        [string]$Preset,
        [System.Diagnostics.Process[]]$Targets,
        [ref]$Warnings,
        [ref]$Actions
    )

    $maskFull = Get-FullMask
    $maskHalf = Get-HalfMask

    switch ($Preset.ToLowerInvariant()) {
        "balanced" {
            $mask = $maskFull; $priority = 'Normal'; $presetName = 'Balanced'
        }
        "latencyboost" {
            $mask = $maskFull; $priority = 'High'; $presetName = 'LatencyBoost'
        }
        "efficiency" {
            $mask = $maskHalf; $priority = 'BelowNormal'; $presetName = 'Efficiency'
        }
        default {
            $mask = $maskFull; $priority = 'AboveNormal'; $presetName = $Preset
        }
    }

    foreach ($proc in $Targets) {
        try {
            $proc.ProcessorAffinity = $mask
            if ($priority) {
                $proc.PriorityClass = $priority
            }
            $Actions.Value += "Applied $presetName to $($proc.ProcessName) (mask=$mask, priority=$priority)"
        }
        catch {
            $Warnings.Value += "Failed to apply $presetName to $($proc.ProcessName): $_"
        }
    }

    return $presetName, $mask, $priority
}

$warnings = @()
$actions = @()
$names = @()
if ($ProcessNames) {
    $names = $ProcessNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

if ($Detect) {
    $state = [PSCustomObject]@{
        preset     = $Preset
        cpuCount   = [Environment]::ProcessorCount
        fullMask   = Get-FullMask
        halfMask   = Get-HalfMask
        processHint= $names
        actions    = "Detect"
        warnings   = $warnings
    }

    if ($PassThru) { $state }
    return
}

if ($RestoreDefaults) {
    $targets = @()
    if ($names.Count -gt 0) {
        $targets = Get-Process -Name $names -ErrorAction SilentlyContinue
    }

    $mask = Get-FullMask
    foreach ($proc in $targets) {
        try {
            $proc.ProcessorAffinity = $mask
            $proc.PriorityClass = 'Normal'
            $actions += "Restored default mask for $($proc.ProcessName)"
        }
        catch {
            $warnings += "Failed to restore for $($proc.ProcessName): $_"
        }
    }

    if ($names.Count -eq 0) {
        $warnings += "No process names provided; nothing restored."
    }

    if ($PassThru) {
        [PSCustomObject]@{
            preset     = 'RestoreDefaults'
            mask       = $mask
            actions    = $actions
            warnings   = $warnings
        }
    }
    return
}

$targetsToApply = @()
if ($names.Count -gt 0) {
    $targetsToApply = Get-Process -Name $names -ErrorAction SilentlyContinue
    if ($targetsToApply.Count -eq 0) {
        $warnings += "No running processes matched: $($names -join ', ')"
    }
}
else {
    $warnings += "No process names provided; preset logged only."
}

$presetResult = Apply-Preset -Preset:$Preset -Targets:$targetsToApply -Warnings:[ref]$warnings -Actions:[ref]$actions

if ($PassThru) {
    [PSCustomObject]@{
        presetApplied = $presetResult[0]
        mask          = $presetResult[1]
        priority      = $presetResult[2]
        processCount  = $targetsToApply.Count
        actions       = $actions
        warnings      = $warnings
    }
}
