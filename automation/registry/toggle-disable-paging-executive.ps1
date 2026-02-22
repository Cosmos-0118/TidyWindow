[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Disable Paging Executive'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'DisablePagingExecutive' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Kernel drivers and system code will remain in physical memory.'

        Write-RegistryOutput 'DisablePagingExecutive enabled (restart required to take effect).'
    }
    else {
        $change = Remove-RegistryValue -Path $path -Name 'DisablePagingExecutive'
        Register-RegistryChange -Change $change -Description 'Restored default paging executive behavior.'

        Write-RegistryOutput 'DisablePagingExecutive removed (restart required to take effect).'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}