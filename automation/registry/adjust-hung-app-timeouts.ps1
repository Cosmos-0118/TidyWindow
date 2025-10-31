[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateRange(1000, 20000)]
    [int] $HungAppTimeout = 5000,
    [ValidateRange(1000, 20000)]
    [int] $WaitToKillAppTimeout = 5000,
    [ValidateRange(1000, 20000)]
    [int] $WaitToKillServiceTimeout = 7000,
    [switch] $Revert,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Hung app timeout tuning'

try {
    Assert-TidyAdmin

    if ($Revert.IsPresent) {
        $change1 = Remove-RegistryValue -Path 'HKCU:\Control Panel\Desktop' -Name 'HungAppTimeout'
        Register-RegistryChange -Change $change1 -Description 'Reverted HungAppTimeout setting.'

        $change2 = Remove-RegistryValue -Path 'HKCU:\Control Panel\Desktop' -Name 'WaitToKillAppTimeout'
        Register-RegistryChange -Change $change2 -Description 'Reverted WaitToKillAppTimeout setting.'

        $change3 = Remove-RegistryValue -Path 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name 'WaitToKillServiceTimeout'
        Register-RegistryChange -Change $change3 -Description 'Reverted WaitToKillServiceTimeout setting.'

        Write-RegistryOutput 'Hung app timeouts reverted to system defaults.'
    }
    else {
        Write-RegistryOutput ("Applying HungAppTimeout: {0} ms" -f $HungAppTimeout)
        $change3 = Set-RegistryValue -Path 'HKCU:\Control Panel\Desktop' -Name 'HungAppTimeout' -Value ([string]$HungAppTimeout) -Type 'String'
        Register-RegistryChange -Change $change3 -Description 'Updated HungAppTimeout.'

        Write-RegistryOutput ("Applying WaitToKillAppTimeout: {0} ms" -f $WaitToKillAppTimeout)
        $change4 = Set-RegistryValue -Path 'HKCU:\Control Panel\Desktop' -Name 'WaitToKillAppTimeout' -Value ([string]$WaitToKillAppTimeout) -Type 'String'
        Register-RegistryChange -Change $change4 -Description 'Updated WaitToKillAppTimeout.'

        Write-RegistryOutput ("Applying WaitToKillServiceTimeout: {0} ms" -f $WaitToKillServiceTimeout)
        $change5 = Set-RegistryValue -Path 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name 'WaitToKillServiceTimeout' -Value ([string]$WaitToKillServiceTimeout) -Type 'String'
        Register-RegistryChange -Change $change5 -Description 'Updated WaitToKillServiceTimeout.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
