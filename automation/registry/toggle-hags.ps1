[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Hardware-accelerated GPU scheduling'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'HwSchMode' -Value 2 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Enabled hardware-accelerated GPU scheduling.'

        Write-RegistryOutput 'HAGS enabled (restart required to take effect).'
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'HwSchMode' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Disabled hardware-accelerated GPU scheduling.'

        Write-RegistryOutput 'HAGS disabled (restart required to take effect).'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}