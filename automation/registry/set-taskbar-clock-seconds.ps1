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

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'ShowSecondsInSystemClock' -Value 1 -Type 'DWord'
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
