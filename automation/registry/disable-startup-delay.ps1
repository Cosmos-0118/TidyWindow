[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Startup delay policy'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'StartupDelayInMSec' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Set StartupDelayInMSec to 0.'

        $change2 = Set-RegistryValue -Path $path -Name 'WaitForIdleState' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $change2 -Description 'Set WaitForIdleState to 0.'

        Write-RegistryOutput 'Startup delay disabled for signin apps.'
    }
    else {
        $change = Remove-RegistryValue -Path $path -Name 'StartupDelayInMSec'
        Register-RegistryChange -Change $change -Description 'Removed StartupDelayInMSec override.'

        $change2 = Remove-RegistryValue -Path $path -Name 'WaitForIdleState'
        Register-RegistryChange -Change $change2 -Description 'Removed WaitForIdleState override.'

        Write-RegistryOutput 'Startup delay restored to Windows defaults.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
