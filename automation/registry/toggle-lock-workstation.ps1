[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Lock workstation shortcut toggle'

try {
    Assert-TidyAdmin

    $state = $Enable.IsPresent -and -not $Disable.IsPresent
    $value = if ($state) { 0 } else { 1 }
    $label = if ($state) { 'enabled' } else { 'blocked' }

    Write-RegistryOutput ("Lock workstation shortcut will be {0}." -f $label)

    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System'
    $change = Set-RegistryValue -Path $path -Name 'DisableLockWorkstation' -Value $value -Type 'DWord'
    Register-RegistryChange -Change $change -Description 'Updated DisableLockWorkstation policy.'

    Write-RegistryOutput 'Lock workstation policy applied.'
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
