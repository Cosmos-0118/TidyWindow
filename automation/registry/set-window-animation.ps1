[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Disable', 'Enable')]
    [string] $AnimationState = 'Disable',
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Window animation policy'

try {
    Assert-TidyAdmin

    $isEnabled = $AnimationState -eq 'Enable'
    Write-RegistryOutput ("Animations will be {0}." -f ($AnimationState.ToLowerInvariant()))

    $minAnimate = $isEnabled ? '1' : '0'
    $change1 = Set-RegistryValue -Path 'HKCU:\Control Panel\Desktop' -Name 'MinAnimate' -Value $minAnimate -Type 'String'
    Register-RegistryChange -Change $change1 -Description 'Updated window minimize/restore animation setting.'

    $taskbarAnimations = $isEnabled ? 1 : 0
    $change2 = Set-RegistryValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name 'TaskbarAnimations' -Value $taskbarAnimations -Type 'DWord'
    Register-RegistryChange -Change $change2 -Description 'Updated taskbar animation toggle.'

    Write-RegistryOutput 'Window animation preferences applied.'
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
