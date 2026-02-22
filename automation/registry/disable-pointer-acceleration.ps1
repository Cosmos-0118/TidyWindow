[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable,
    [int] $MouseSpeed = 0,
    [int] $MouseThreshold1 = 0,
    [int] $MouseThreshold2 = 0,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'Disable pointer acceleration'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent
    $path = 'HKCU:\Control Panel\Mouse'

    if ($apply) {
        $c1 = Set-RegistryValue -Path $path -Name 'MouseSpeed' -Value "$MouseSpeed" -Type 'String'
        Register-RegistryChange -Change $c1 -Description "Set MouseSpeed to $MouseSpeed."

        $c2 = Set-RegistryValue -Path $path -Name 'MouseThreshold1' -Value "$MouseThreshold1" -Type 'String'
        Register-RegistryChange -Change $c2 -Description "Set MouseThreshold1 to $MouseThreshold1."

        $c3 = Set-RegistryValue -Path $path -Name 'MouseThreshold2' -Value "$MouseThreshold2" -Type 'String'
        Register-RegistryChange -Change $c3 -Description "Set MouseThreshold2 to $MouseThreshold2."

        Write-RegistryOutput 'Pointer acceleration disabled (Enhance Pointer Precision off).'
    }
    else {
        $c1 = Set-RegistryValue -Path $path -Name 'MouseSpeed' -Value '1' -Type 'String'
        Register-RegistryChange -Change $c1 -Description 'Restored MouseSpeed to 1 (default).'

        $c2 = Set-RegistryValue -Path $path -Name 'MouseThreshold1' -Value '6' -Type 'String'
        Register-RegistryChange -Change $c2 -Description 'Restored MouseThreshold1 to 6 (default).'

        $c3 = Set-RegistryValue -Path $path -Name 'MouseThreshold2' -Value '10' -Type 'String'
        Register-RegistryChange -Change $c3 -Description 'Restored MouseThreshold2 to 10 (default).'

        Write-RegistryOutput 'Pointer acceleration restored to Windows defaults.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}