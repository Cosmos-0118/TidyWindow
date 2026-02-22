[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Disable shared experiences'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $machinePath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'
    $userPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CDP'

    if ($apply) {
        $c1 = Set-RegistryValue -Path $machinePath -Name 'EnableCdp' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $c1 -Description 'Disabled Connected Devices Platform policy.'

        $c2 = Set-RegistryValue -Path $userPath -Name 'CdpSessionUserAuthzPolicy' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $c2 -Description 'Disabled CDP session user authorization.'

        Write-RegistryOutput 'Shared experiences (CDP) disabled.'
    }
    else {
        $c1 = Remove-RegistryValue -Path $machinePath -Name 'EnableCdp'
        Register-RegistryChange -Change $c1 -Description 'Removed CDP policy override.'

        $c2 = Remove-RegistryValue -Path $userPath -Name 'CdpSessionUserAuthzPolicy'
        Register-RegistryChange -Change $c2 -Description 'Removed CDP session authorization override.'

        Write-RegistryOutput 'Shared experiences restored to defaults.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}