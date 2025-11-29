[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Power throttling policy'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling'

    if ($apply) {
        $change = Set-RegistryValue -Path $path -Name 'PowerThrottlingOff' -Value 1 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Disabled Windows power throttling.'
        Write-RegistryOutput 'Background power throttling disabled (best performance).'
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'PowerThrottlingOff' -Value 0 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Re-enabled Windows power throttling.'
        Write-RegistryOutput 'Power throttling returned to system default.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
