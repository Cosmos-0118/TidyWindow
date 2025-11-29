[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Disable = $true,
    [string] $ResultPath
)

. "$PSScriptRoot\registry-common.ps1"

Initialize-RegistryScript -Cmdlet $PSCmdlet -ResultPath $ResultPath -OperationName 'CPU core parking policy'

try {
    Assert-TidyAdmin

    $apply = $Enable.IsPresent -and -not $Disable.IsPresent

    $targets = @(
        'HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\0cc5b647-c1df-4637-891a-dec35c318583',
        'HKLM:\SYSTEM\ControlSet001\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\0cc5b647-c1df-4637-891a-dec35c318583'
    )

    foreach ($target in $targets) {
        if ($apply) {
            $change = Set-RegistryValue -Path $target -Name 'ValueMax' -Value 0 -Type 'DWord'
            Register-RegistryChange -Change $change -Description ("Set ValueMax to 0 at {0}" -f $target)
        }
        else {
            $change = Set-RegistryValue -Path $target -Name 'ValueMax' -Value 64 -Type 'DWord'
            Register-RegistryChange -Change $change -Description ("Restored ValueMax to 64 at {0}" -f $target)
        }
    }

    if ($apply) {
        Write-RegistryOutput 'CPU core parking disabled (all cores stay available).'
    }
    else {
        Write-RegistryOutput 'CPU core parking restored to Windows default.'
    }
}
catch {
    Write-RegistryError $_
}
finally {
    Complete-RegistryScript
}
