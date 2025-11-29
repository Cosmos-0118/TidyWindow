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
        Write-RegistryOutput 'Explorer clock will show seconds.'
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'ShowSecondsInSystemClock' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Disabled seconds on system clock.'
        Write-RegistryOutput 'Explorer clock seconds hidden.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
