[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Disable SMB1 client'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'SMB1' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Disabled SMB1 client protocol.'

        Write-RegistryOutput 'SMB1 client disabled (restart required to take effect).'
    }
    else {
        $change = Remove-RegistryValue -Path $path -Name 'SMB1'
        Register-RegistryChange -Change $change -Description 'Removed SMB1 client override (re-enables default).'

        Write-RegistryOutput 'SMB1 client setting restored to defaults.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}