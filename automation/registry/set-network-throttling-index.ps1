[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Network throttling index'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'

    if ($apply) {
        $maxThrottle = [uint32]::MaxValue
        $change = Set-RegistryValue -Path $path -Name 'NetworkThrottlingIndex' -Value $maxThrottle -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Disabled multimedia network throttling.'
        Write-RegistryOutput 'Network throttling disabled (0xFFFFFFFF).'
    }
    else {
        $change = Set-RegistryValue -Path $path -Name 'NetworkThrottlingIndex' -Value 10 -Type 'DWord'
        Register-RegistryChange -Change $change -Description 'Restored default network throttling index.'
        Write-RegistryOutput 'Network throttling restored to default value (10).'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
