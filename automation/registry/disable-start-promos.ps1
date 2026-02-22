[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Disable Start menu promotions'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'

    if ($apply) {
        $c1 = Set-RegistryValue -Path $path -Name 'SystemPaneSuggestionsEnabled' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $c1 -Description 'Disabled Start menu suggestions.'

        $c2 = Set-RegistryValue -Path $path -Name 'SubscribedContent-338388Enabled' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $c2 -Description 'Disabled subscribed content promotions.'

        Write-RegistryOutput 'Start menu promotions disabled.'
    }
    else {
        $c1 = Set-RegistryValue -Path $path -Name 'SystemPaneSuggestionsEnabled' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $c1 -Description 'Re-enabled Start menu suggestions.'

        $c2 = Set-RegistryValue -Path $path -Name 'SubscribedContent-338388Enabled' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $c2 -Description 'Re-enabled subscribed content promotions.'

        Write-RegistryOutput 'Start menu promotions restored to defaults.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}