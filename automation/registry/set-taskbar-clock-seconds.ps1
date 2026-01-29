# Taskbar clock seconds tweak
# NOTE: This script ONLY sets the registry value. Shell refresh is handled centrally
# by the RegistryOptimizerViewModel after all tweaks complete, to prevent other tweaks
# from resetting the clock seconds value when they modify Explorer\Advanced settings.

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Taskbar clock seconds'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
    $effectivePath = Resolve-RegistryUserPath -Path $path

    if ($apply) {
        # Get the old value for logging
        $oldValue = $null
        try {
            $oldValue = Get-ItemProperty -LiteralPath $effectivePath -Name 'ShowSecondsInSystemClock' -ErrorAction SilentlyContinue | 
                        Select-Object -ExpandProperty ShowSecondsInSystemClock -ErrorAction SilentlyContinue
        }
        catch { }
        
        Write-RegistryOutput "Enabling seconds on system clock (current value: $(if ($null -eq $oldValue) { '<not set>' } else { $oldValue }))."
        
        # Set the registry value using both methods for robustness
        try {
            if (-not (Test-Path -LiteralPath $effectivePath)) {
                New-Item -Path $effectivePath -Force | Out-Null
            }
            Set-ItemProperty -LiteralPath $effectivePath -Name 'ShowSecondsInSystemClock' -Value 1 -Type DWord -Force -ErrorAction Stop
        }
        catch {
            Write-RegistryOutput "PowerShell method failed: $_"
        }
        
        # Also use reg.exe as fallback
        & reg.exe add 'HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' /v ShowSecondsInSystemClock /t REG_DWORD /d 1 /f 2>&1 | Out-Null
        
        # Verify the value was set
        $observed = $null
        try {
            $observed = Get-ItemProperty -LiteralPath $effectivePath -Name 'ShowSecondsInSystemClock' -ErrorAction SilentlyContinue | 
                        Select-Object -ExpandProperty ShowSecondsInSystemClock -ErrorAction SilentlyContinue
        }
        catch { }
        
        if ($observed -ne 1) {
            throw "Failed to set ShowSecondsInSystemClock registry value."
        }
        
        Write-RegistryOutput "Registry value set successfully."
        
        $change = [pscustomobject]@{
            Path = $effectivePath
            Name = 'ShowSecondsInSystemClock'
            OldValue = $oldValue
            NewValue = 1
            ValueType = 'DWord'
        }
        Register-RegistryChange -Change $change -Description 'Enabled seconds on system clock.'
        Write-RegistryOutput 'Clock seconds registry value applied. Shell will refresh after all tweaks complete.'
    }
    else {
        # Disabling
        $change = Set-RegistryValue -Path $path -Name 'ShowSecondsInSystemClock' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Disabled seconds on system clock.'
        Write-RegistryOutput 'Clock seconds registry value cleared. Shell will refresh after all tweaks complete.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
