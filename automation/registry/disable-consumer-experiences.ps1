[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Disable consumer experiences'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent'

    if ($apply) {
        $c1 = Set-RegistryValue -Path $path -Name 'DisableWindowsConsumerFeatures' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $c1 -Description 'Disabled Windows consumer features.'

        $c2 = Set-RegistryValue -Path $path -Name 'DisableWindowsSpotlightFeatures' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $c2 -Description 'Disabled Windows Spotlight features.'

        Write-RegistryOutput 'Consumer experiences and Spotlight disabled.'
    }
    else {
        $c1 = Remove-RegistryValue -Path $path -Name 'DisableWindowsConsumerFeatures'
        Register-RegistryChange -Change $c1 -Description 'Removed consumer features override.'

        $c2 = Remove-RegistryValue -Path $path -Name 'DisableWindowsSpotlightFeatures'
        Register-RegistryChange -Change $c2 -Description 'Removed Spotlight features override.'

        Write-RegistryOutput 'Consumer experiences restored to defaults.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}