[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Nullable[bool]] $NoAutoReboot,
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Automatic restart sign-on'

try {
    Assert-TidyAdmin

    $blockRestarts = if ($PSBoundParameters.ContainsKey('NoAutoReboot')) { [bool]$NoAutoReboot } else { $Enable.IsPresent -and -not $Disable.IsPresent }
    $label = if ($blockRestarts) { 'blocked' } else { 'allowed' }

    Write-RegistryOutput ("Automatic restarts with logged-on users will be {0}." -f $label)

    $wuPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
    $change = Set-RegistryValue -Path $wuPath -Name 'NoAutoRebootWithLoggedOnUsers' -Value ($blockRestarts ? 1 : 0) -Type 'DWord'
    Register-RegistryChange -Change $change -Description 'Updated Windows Update restart policy.'

    $auPath = Join-Path -Path $wuPath -ChildPath 'AU'
    $change2 = Set-RegistryValue -Path $auPath -Name 'NoAutoRebootWithLoggedOnUsers' -Value ($blockRestarts ? 1 : 0) -Type 'DWord'
    Register-RegistryChange -Change $change2 -Description 'Updated AU restart safeguard.'

    $systemPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    $change3 = Set-RegistryValue -Path $systemPath -Name 'AutoRestartSignOn' -Value ($blockRestarts ? 0 : 1) -Type 'DWord'
    Register-RegistryChange -Change $change3 -Description 'Aligned AutoRestartSignOn setting.'

    Write-RegistryOutput 'Automatic restart safeguards applied.'
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
