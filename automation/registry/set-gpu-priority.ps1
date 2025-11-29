[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'GPU scheduling priority'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'Priority' -Value 8 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Raised game CPU priority to 8.'

        $change2 = Set-RegistryValue -Path $path -Name 'GPU Priority' -Value 8 -Type 'DWord'
        Register-RegistryChange -Change $change2 -Description 'Raised game GPU priority to 8.'

        Write-RegistryOutput 'Games task now uses maximum CPU/GPU scheduling priority.'
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'Priority' -Value 2 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Restored game CPU priority to 2.'

        $change2 = Set-RegistryValue -Path $path -Name 'GPU Priority' -Value 2 -Type 'DWord'
        Register-RegistryChange -Change $change2 -Description 'Restored game GPU priority to 2.'

        Write-RegistryOutput 'Games task scheduling priority reverted to default.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
