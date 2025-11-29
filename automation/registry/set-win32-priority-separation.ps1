[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Win32 priority separation'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'Win32PrioritySeparation' -Value 0x26 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Favoring foreground responsiveness (0x26).'
        Write-RegistryOutput 'Foreground processes now receive additional CPU priority.'
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'Win32PrioritySeparation' -Value 0x2 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Restored default Win32 priority separation (0x2).'
        Write-RegistryOutput 'CPU priority scheduling restored to default.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
