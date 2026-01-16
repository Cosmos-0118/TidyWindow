[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Recommended','DynamicTickOff','DynamicTickOn','PlatformClockOn','PlatformClockOff','TscSyncLegacy','TscSyncEnhanced','Linear57On','Linear57Off','RestoreDefaults')]
    [string] $Action = 'Recommended',
    [switch] $SkipRestorePoint,
    [switch] $PassThru
)

set-strictmode -version latest
$ErrorActionPreference = 'Stop'

function Assert-Elevation {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw 'Elevation required: run as administrator to change boot settings.'
    }
}

function New-RestorePointSafe {
    param([string] $Description)
    try {
        Checkpoint-Computer -Description $Description -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop | Out-Null
        Write-Verbose "Restore point created: $Description"
        return $true
    }
    catch {
        Write-Warning "Restore point creation failed: $($_.Exception.Message)"
        return $false
    }
}

function Invoke-BcdCommand {
    param([string[]] $Arguments)
    $output = & bcdedit @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{ Arguments = $Arguments; ExitCode = $exitCode; Output = $output }
}

Assert-Elevation

if (-not $SkipRestorePoint) {
    New-RestorePointSafe -Description 'TidyWindow KernelBoot Controls'
}

$commands = @()

switch ($Action) {
    'Recommended' {
        $commands += [pscustomobject]@{ Args = @('/set','{current}','disabledynamictick','yes'); Optional = $false }
        $commands += [pscustomobject]@{ Args = @('/set','{current}','useplatformclock','true'); Optional = $false }
        $commands += [pscustomobject]@{ Args = @('/set','{current}','tscsyncpolicy','Enhanced'); Optional = $false }
        # Attempt linearaddress57, but treat as optional to avoid blocking unsupported hardware
        $commands += [pscustomobject]@{ Args = @('/set','{current}','linearaddress57','yes'); Optional = $true }
    }
    'DynamicTickOff' { $commands += [pscustomobject]@{ Args = @('/set','{current}','disabledynamictick','yes'); Optional = $false } }
    'DynamicTickOn'  { $commands += [pscustomobject]@{ Args = @('/deletevalue','{current}','disabledynamictick'); Optional = $false } }
    'PlatformClockOn'  { $commands += [pscustomobject]@{ Args = @('/set','{current}','useplatformclock','true'); Optional = $false } }
    'PlatformClockOff' { $commands += [pscustomobject]@{ Args = @('/deletevalue','{current}','useplatformclock'); Optional = $false } }
    'TscSyncLegacy'    { $commands += [pscustomobject]@{ Args = @('/set','{current}','tscsyncpolicy','Legacy'); Optional = $false } }
    'TscSyncEnhanced'  { $commands += [pscustomobject]@{ Args = @('/set','{current}','tscsyncpolicy','Enhanced'); Optional = $false } }
    'Linear57On'       { $commands += [pscustomobject]@{ Args = @('/set','{current}','linearaddress57','yes'); Optional = $true } }
    'Linear57Off'      { $commands += [pscustomobject]@{ Args = @('/deletevalue','{current}','linearaddress57'); Optional = $true } }
    'RestoreDefaults' {
        $commands += [pscustomobject]@{ Args = @('/deletevalue','{current}','disabledynamictick'); Optional = $false }
        $commands += [pscustomobject]@{ Args = @('/deletevalue','{current}','useplatformclock'); Optional = $false }
        $commands += [pscustomobject]@{ Args = @('/deletevalue','{current}','tscsyncpolicy'); Optional = $false }
        $commands += [pscustomobject]@{ Args = @('/deletevalue','{current}','linearaddress57'); Optional = $false }
    }
}

$failures = @()
$optionalFailures = @()
foreach ($cmd in $commands) {
    $args = $cmd.Args
    $optional = $cmd.Optional
    $display = ($args -join ' ')
    if ($PSCmdlet.ShouldProcess($display, 'bcdedit')) {
        $result = Invoke-BcdCommand -Arguments $args
        $outputText = ($result.Output -join "`n")
        # Treat "Element not found" as benign when clearing values that may not exist.
        $isMissingElement = $result.ExitCode -eq 1 -and $outputText -match 'Element not found'

        if ($result.ExitCode -ne 0 -and -not $isMissingElement) {
            $entry = [pscustomobject]@{ Message = "bcdedit $display returned $($result.ExitCode): $($result.Output)"; Optional = $optional }
            if ($optional) {
                $optionalFailures += $entry
            }
            else {
                $failures += $entry
            }
        }
    }
}

if ($PassThru) {
    $payload = [pscustomobject]@{
        action = $Action
        applied = $commands.Count
        restorePointAttempted = (-not $SkipRestorePoint)
    }

    if ($failures.Count -gt 0) {
        $payload | Add-Member -NotePropertyName failures -NotePropertyValue ($failures | Select-Object -ExpandProperty Message)
    }

    $payload
}

if ($failures.Count -gt 0) {
        foreach ($entry in ($failures + $optionalFailures)) {
            Write-Warning $entry.Message
        }
        if ($failures.Count -gt 0) {
            throw "One or more boot configuration commands failed. See warnings for details."
        }
}
