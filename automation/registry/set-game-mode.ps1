[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Game Mode'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SOFTWARE\Microsoft\GameBar'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'AllowAutoGameMode' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Enabled Game Mode auto-detection.'

        Write-RegistryOutput 'Game Mode is now enabled.'
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'AllowAutoGameMode' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Disabled Game Mode auto-detection.'

        Write-RegistryOutput 'Game Mode has been disabled.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}