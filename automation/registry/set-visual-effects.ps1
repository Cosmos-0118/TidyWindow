[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Balanced', 'Performance', 'Appearance', 'Default')]
    [string] $Profile = 'Balanced',
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Visual effects preset'

try {
    Assert-TidyAdmin

    $visualFxSetting = switch ($Profile) {
        'Performance' { 2 }
        'Appearance' { 1 }
        'Default' { 0 }
        default { 3 }
    }

    Write-RegistryOutput ("Applying visual effects profile: {0}" -f $Profile)

    $change = Set-RegistryValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' -Name 'VisualFXSetting' -Value $visualFxSetting -Type 'DWord'
    Register-RegistryChange -Change $change -Description 'Updated VisualFXSetting profile flag.'

    if ($Profile -eq 'Balanced') {
        $change1 = Set-RegistryValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name 'ListviewAlphaSelect' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change1 -Description 'Enabled translucent selection rectangles.'

        $change2 = Set-RegistryValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name 'ListviewShadow' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change2 -Description 'Kept list view shadow effects.'

        $change3 = Set-RegistryValue -Path 'HKCU:\Control Panel\Desktop' -Name 'DragFullWindows' -Value '1' -Type 'String'
        Register-RegistryChange -Change $change3 -Description 'Retained full window dragging for context awareness.'
    }

    Write-RegistryOutput 'Visual effects profile applied.'
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
